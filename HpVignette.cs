using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace noWickyXIV;

// Screen-edge vignette driven by the local player's HP state.
//
// Three modes:
//   1. Alive, low HP            — red gradient, intensity scales with HP
//                                 below the threshold.
//   2. Dead (HP = 0)            — flat dark-grey gradient at full alpha
//                                 (calmer than red, signals "you're out").
//   3. Just resurrected         — short white-flash that fades to
//                                 transparent over REZ_FADE_SECONDS,
//                                 marking the rez moment.
//
// Drawn on the foreground draw list as four edge-anchored rectangles
// with a 4-corner gradient (transparent toward center). RGBA is packed
// directly so ImGui's Style.Alpha doesn't dim the overlay further.
public static class HpVignette
{
    private const double REZ_FADE_SECONDS = 1.6;
    private const float  DEAD_ALPHA       = 0.65f;
    // Single-frame HP gain (as a fraction of MaxHp) that counts as a
    // "real heal" worth flashing. 0.06 catches Cure/Adloquium-class
    // heals and bigger; sits above typical regen-tick magnitudes so
    // passive ticks don't pulse the screen.
    private const float  HEAL_GAIN_FRAC   = 0.06f;

    // Fall-damage vignette timing. Quick punch-in, slow decay so it
    // reads as "you just ate dirt, now you're catching your breath."
    private const float  FALL_FADE_IN_S   = 0.15f;
    private const float  FALL_FADE_OUT_S  = 1.8f;
    private const float  FALL_MAX_ALPHA   = 0.75f;
    // Grace window after a landing edge during which any HP drop is
    // attributed to fall damage. Most landings deal damage on the same
    // frame the Jumping flag clears, but the engine sometimes applies
    // it several frames later — this absorbs that lag. Widened from
    // 0.4 → 0.7 because the older value was missing some landings.
    private const double FALL_GRACE_S     = 0.7;
    // Minimum HP loss (fraction of MaxHp) that counts as fall damage
    // for the landing-edge path. Lowered to 0.01 so small drops still
    // register; the OOC fallback below uses a higher floor to avoid
    // mistaking tiny status ticks for falls.
    private const float  FALL_MIN_LOSS    = 0.01f;
    // Secondary fallback: any single-frame HP loss this large while
    // not in combat is almost certainly fall damage, regardless of
    // whether the Jumping flag transitioned cleanly. Catches the cases
    // where the condition flag's timing diverges from the damage tick.
    private const float  FALL_OOC_MIN_LOSS = 0.04f;

    // Anticipatory pre-impact darkening. While the player is airborne,
    // we ramp a dimmer version of the vignette in once they've been
    // off the ground long enough that a long fall is plausible. The
    // ramp tops out at AIR_ANTICIPATION_ALPHA, well below the FALL
    // punch-in alpha, so the impact still reads as a hit.
    private const double AIR_ANTICIPATION_DELAY_S = 0.20;  // hold-off before any darken
    private const double AIR_ANTICIPATION_MAX_S   = 1.20;  // airtime at which ramp tops out
    private const float  AIR_ANTICIPATION_ALPHA   = 0.30f; // cap during anticipation
    private const float  AIR_FADE_RATE            = 8f;    // exp-lerp 1/s for smoothing
    // Actor-anchored avfx fired the moment fall damage is detected.
    // Both play together (stacked impact); each runs its own timeline
    // and self-cleans via the engine. Paths verbatim from the user.
    private static readonly string[] FALL_VFX_PATHS =
    {
        "vfx/rrp/2km_ws_s13/eff/2km_ws13_c5x.avfx",
        "vfx/mks/chk_ws_s17/eff/chk_ws17_c1c.avfx",
    };

    // True last frame the player was at HP = 0.
    private static bool   _wasDead;
    // True last frame the player's HP was below the vignette threshold.
    private static bool   _wasLowHp;
    // Wall time the rez/heal fade ends. <= 0 means no fade in flight.
    private static double _rezFadeUntilS;
    // True last frame ConditionFlag.Jumping (or Jumping61) was set.
    private static bool   _wasJumping;
    // Wall time the post-landing grace window closes. While now is in
    // [landed, landed+FALL_GRACE_S] and HP drops, we attribute the
    // drop to fall damage.
    private static double _landingGraceUntilS;
    // Wall time the fall-vignette started. <= 0 means no fall fade in
    // flight. Drives both the fade-in (clamp to FALL_FADE_IN_S after
    // start) and the fade-out (decays through FALL_FADE_OUT_S).
    private static double _fallStartS;
    // Wall time the current airborne arc started. 0 = on ground.
    private static double _airborneStartS;
    // Smoothed anticipation alpha — ramps up over the airborne arc,
    // decays smoothly to 0 after landing if no fall vignette fired.
    private static float  _airAlpha;
    // Smoothed alpha for fade-out when HP climbs above threshold.
    private static float  _smoothAlpha;
    // Previous frame's CurrentHp for delta-based heal detection.
    // uint.MaxValue = sentinel "no prior sample" (e.g., feature just
    // enabled, zoning) so we don't fake a huge heal on the first frame.
    private static uint   _prevCurrentHp = uint.MaxValue;

    public static void Update() { /* render does the state work */ }

    public static void Draw()
    {
        var c = noWickyXIV.Config;
        if (!c.EnableHpVignette)
        {
            _wasDead = false;
            _wasLowHp = false;
            _rezFadeUntilS = 0;
            _smoothAlpha = 0f;
            _prevCurrentHp = uint.MaxValue;
            _wasJumping = false;
            _landingGraceUntilS = 0;
            _fallStartS = 0;
            _airborneStartS = 0;
            _airAlpha = 0f;
            return;
        }

        try
        {
            var p = DalamudApi.ObjectTable.LocalPlayer;
            if (p is not Dalamud.Game.ClientState.Objects.Types.IBattleChara bc) return;
            if (bc.MaxHp <= 0) return;

            float hpFrac = (float)bc.CurrentHp / bc.MaxHp;
            if (hpFrac < 0f) hpFrac = 0f;
            if (hpFrac > 1f) hpFrac = 1f;
            bool isDead = bc.CurrentHp <= 0;
            double now = NowSeconds();

            // Death/rez transition tracking.
            if (isDead && !_wasDead)
            {
                // Transition into death — clear any pending rez fade.
                _rezFadeUntilS = 0;
            }
            else if (!isDead && _wasDead)
            {
                // Just came back alive — start the white-fade.
                _rezFadeUntilS = now + REZ_FADE_SECONDS;
            }
            _wasDead = isDead;

            // Heal detection — two complementary triggers, both arm the
            // same white-fade. Either can fire each frame; restarting an
            // in-flight fade just refreshes its end time (visually a
            // continued saturated flash, not a stutter).
            //
            //  (a) Threshold-cross: HP was below the user's "low" line
            //      last frame and is no longer below it now. Catches the
            //      slow-recovery case where a single heal bumps the bar
            //      from 30% → 55% across one frame.
            //  (b) Delta gain: CurrentHp jumped up by >= HEAL_GAIN_FRAC
            //      of MaxHp in one frame. Catches "big heal landed even
            //      though I was at 80%" — the old impl missed this
            //      because the player never crossed the low threshold.
            //
            // The old `_rezFadeUntilS <= 0` gate is gone — without it,
            // a heal during an active rez/heal fade can still re-arm,
            // which is what the user wants ("for sure fire all the time").
            float threshold = MathF.Max(0.01f, c.HpVignetteThreshold);
            bool isLowHp = !isDead && hpFrac < threshold;

            bool healFire = false;
            if (!isDead && _wasLowHp && !isLowHp)
                healFire = true;                                  // (a)
            if (!isDead && _prevCurrentHp != uint.MaxValue
                && bc.CurrentHp > _prevCurrentHp)
            {
                float gainFrac = (bc.CurrentHp - _prevCurrentHp) / (float)bc.MaxHp;
                if (gainFrac >= HEAL_GAIN_FRAC)
                    healFire = true;                              // (b)
            }
            // Suppress the heal flash while a fall vignette is still
            // in flight. Recovering after a hard landing visually is
            // the dark→red→nothing transition below; pulsing white on
            // top of that read as a glitch ("did the game crash?").
            // Once the fall fully decays (_fallStartS clears in the
            // render branch below), heal flashes resume.
            if (healFire && _fallStartS <= 0)
                _rezFadeUntilS = now + REZ_FADE_SECONDS;

            _wasLowHp = isLowHp;

            // Fall-damage detection.
            //
            // FFXIV sets ConditionFlag.Jumping (or Jumping61 on some
            // patches) for the entire airborne arc — jumps, drops,
            // ledge-walks, falls. The falling edge of that flag is
            // the moment the player's feet touch the ground. We open
            // a short grace window starting at that moment; any HP
            // loss inside that window is attributed to fall damage.
            // Normal jumps don't trigger because they don't subtract
            // HP, so the window expires harmlessly.
            //
            // FALL_MIN_LOSS filters out 1-HP regen/buff jitter that
            // can land coincidentally on the same frame as a landing.
            bool isJumping = false;
            try
            {
                var cond = DalamudApi.Condition;
                isJumping = cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping]
                         || cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping61];
            }
            catch { }

            if (isJumping && !_wasJumping)
            {
                // Just left the ground — start the airborne arc timer
                // for the anticipation fade.
                _airborneStartS = now;
            }
            if (_wasJumping && !isJumping && !isDead)
            {
                // Just landed. Open the grace window for HP-drop
                // attribution; clear the airborne start.
                _landingGraceUntilS = now + FALL_GRACE_S;
                _airborneStartS = 0;
            }
            _wasJumping = isJumping;

            // Single helper — fires the fall vignette + vfx exactly once
            // per landing event so the two detection paths below can't
            // double-spawn the avfx.
            bool fallFiredThisFrame = false;
            void FireFall()
            {
                if (fallFiredThisFrame || _fallStartS > 0) return;
                fallFiredThisFrame = true;
                _fallStartS = now;
                _landingGraceUntilS = 0;
                for (int i = 0; i < FALL_VFX_PATHS.Length; i++)
                {
                    try { VfxBridge.Create(FALL_VFX_PATHS[i]); } catch { }
                }
            }

            // Path A — landing edge + HP drop inside the grace window.
            // This is the clean signal for most falls.
            if (!isDead && _landingGraceUntilS > 0 && now < _landingGraceUntilS
                && _prevCurrentHp != uint.MaxValue
                && bc.CurrentHp < _prevCurrentHp)
            {
                float lossFrac = (_prevCurrentHp - bc.CurrentHp) / (float)bc.MaxHp;
                if (lossFrac >= FALL_MIN_LOSS) FireFall();
            }

            // Path B — out-of-combat fallback. If a single frame loses
            // a lot of HP while we're not in combat, it's almost
            // certainly fall damage even if the Jumping condition flag
            // timing didn't line up. Gated on FALL_OOC_MIN_LOSS so
            // status ticks / minor stamps don't trigger.
            if (!isDead && !fallFiredThisFrame && _fallStartS <= 0
                && _prevCurrentHp != uint.MaxValue
                && bc.CurrentHp < _prevCurrentHp)
            {
                bool inCombat = false;
                try { inCombat = DalamudApi.Condition[
                    Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]; }
                catch { }
                if (!inCombat)
                {
                    float lossFrac = (_prevCurrentHp - bc.CurrentHp) / (float)bc.MaxHp;
                    if (lossFrac >= FALL_OOC_MIN_LOSS) FireFall();
                }
            }

            // Track previous HP for the next frame's delta check.
            // While dead we reset to the sentinel so the rez itself
            // (0 → full HP) doesn't read as a "heal" — the dead→alive
            // transition above already arms the rez fade.
            _prevCurrentHp = isDead ? uint.MaxValue : bc.CurrentHp;

            // Compute fall-vignette alpha first so we can decide
            // whether it takes priority over the rez/heal flash.
            // Profile: fast linear ramp UP over FALL_FADE_IN_S, then
            // linear ramp DOWN over FALL_FADE_OUT_S as the player
            // "recovers." Active = inside [_fallStartS, fallEnd].
            float fallAlpha = 0f;
            if (_fallStartS > 0)
            {
                double dt   = now - _fallStartS;
                double tot  = FALL_FADE_IN_S + FALL_FADE_OUT_S;
                if (dt < 0 || dt >= tot)
                {
                    _fallStartS = 0;
                }
                else if (dt < FALL_FADE_IN_S)
                {
                    fallAlpha = FALL_MAX_ALPHA * (float)(dt / FALL_FADE_IN_S);
                }
                else
                {
                    fallAlpha = FALL_MAX_ALPHA *
                        (float)(1.0 - (dt - FALL_FADE_IN_S) / FALL_FADE_OUT_S);
                }
            }

            // Anticipatory pre-impact alpha. Target ramps linearly with
            // airtime past AIR_ANTICIPATION_DELAY_S, capped at
            // AIR_ANTICIPATION_ALPHA. Once grounded the target snaps to
            // 0 and the smoothed value decays at AIR_FADE_RATE, giving
            // a graceful "false alarm" fade-out when the player jumps
            // safely without taking damage.
            float airTarget = 0f;
            if (isJumping && _airborneStartS > 0)
            {
                double airtime = now - _airborneStartS;
                if (airtime >= AIR_ANTICIPATION_DELAY_S)
                {
                    double span = AIR_ANTICIPATION_MAX_S - AIR_ANTICIPATION_DELAY_S;
                    float t = (float)((airtime - AIR_ANTICIPATION_DELAY_S) / span);
                    if (t < 0f) t = 0f;
                    if (t > 1f) t = 1f;
                    airTarget = AIR_ANTICIPATION_ALPHA * t;
                }
            }
            {
                float dtF = ImGui.GetIO().DeltaTime;
                float k = 1f - MathF.Exp(-AIR_FADE_RATE * dtF);
                _airAlpha += (airTarget - _airAlpha) * k;
                if (_airAlpha < 0.002f) _airAlpha = 0f;
            }

            float darkAlpha = MathF.Max(fallAlpha, _airAlpha);

            // Alive-mode (red, low-HP) target — computed every frame
            // whether or not a fall is in flight, so _smoothAlpha is
            // always at the right level when the dark layer decays
            // and the red layer becomes visible underneath. Without
            // this, after a fall the red would need its own ramp-up
            // and the user would see a visible re-fade-in instead of
            // a clean dark→red dissolve.
            float redTarget;
            {
                const float FADE_IN_BAND = 0.10f;
                float fadeInStart = threshold + FADE_IN_BAND;
                if (hpFrac >= fadeInStart)
                    redTarget = 0f;
                else if (hpFrac >= threshold)
                {
                    float band = (fadeInStart - hpFrac) / FADE_IN_BAND;
                    redTarget = 0.5f * band;
                }
                else
                {
                    float belowFrac = 1f - (hpFrac / threshold);
                    redTarget = 0.5f + 0.5f * belowFrac;
                }
                float maxAlpha = MathF.Max(0f, MathF.Min(1f, c.HpVignetteMaxAlpha));
                redTarget *= maxAlpha;

                float dtF = ImGui.GetIO().DeltaTime;
                float speed = redTarget >= _smoothAlpha ? 12f : 3f;
                _smoothAlpha += (redTarget - _smoothAlpha) * MathF.Min(1f, speed * dtF);
                if (MathF.Abs(_smoothAlpha - redTarget) < 0.002f) _smoothAlpha = redTarget;
            }
            float redAlpha = _smoothAlpha;

            // Clear any expired rez fade so we don't keep checking.
            if (_rezFadeUntilS > 0 && now >= _rezFadeUntilS) _rezFadeUntilS = 0;

            var io = ImGui.GetIO();
            var disp = io.DisplaySize;
            if (disp.X <= 0 || disp.Y <= 0) return;
            float thickness = MathF.Max(20f, MathF.Min(disp.X, disp.Y) * c.HpVignetteThickness);
            var dl = ImGui.GetForegroundDrawList();

            // Render order — back to front so the dark fall layer can
            // dissolve over the red underneath:
            //   1. Dead grey (overrides everything — single layer)
            //   2. Red low-HP (underlay, persistent)
            //   3. Rez/heal white flash (mid, suppressed during fall)
            //   4. Dark fall+anticipation (top — fades to reveal red)
            if (isDead)
            {
                DrawVignette(dl, disp, thickness, 0.18f, 0.18f, 0.18f, DEAD_ALPHA);
            }
            else
            {
                if (redAlpha > 0.005f)
                    DrawVignette(dl, disp, thickness,
                        c.HpVignetteR, c.HpVignetteG, c.HpVignetteB, redAlpha);

                if (_rezFadeUntilS > 0 && now < _rezFadeUntilS && _fallStartS <= 0)
                {
                    float remain = (float)((_rezFadeUntilS - now) / REZ_FADE_SECONDS);
                    if (remain < 0f) remain = 0f;
                    if (remain > 1f) remain = 1f;
                    DrawVignette(dl, disp, thickness,
                        1f, 1f, 1f, DEAD_ALPHA * remain);
                }

                if (darkAlpha > 0.005f)
                    DrawVignette(dl, disp, thickness,
                        0.04f, 0.04f, 0.07f, darkAlpha);
            }
        }
        catch { /* defensive — IO / ObjectTable can be transient */ }
    }

    // Draws one full-screen edge vignette with the given color/alpha.
    // Four AddRectFilledMultiColor calls — one per edge, fading to
    // transparent toward the center.
    private static void DrawVignette(ImDrawListPtr dl, Vector2 disp,
                                      float thickness, float r, float g, float b, float a)
    {
        if (a <= 0.005f) return;
        uint edge  = PackRgba(r, g, b, a);
        uint inner = PackRgba(r, g, b, 0f);
        dl.AddRectFilledMultiColor(
            new Vector2(0f, 0f),
            new Vector2(disp.X, thickness),
            edge, edge, inner, inner);
        dl.AddRectFilledMultiColor(
            new Vector2(0f, disp.Y - thickness),
            new Vector2(disp.X, disp.Y),
            inner, inner, edge, edge);
        dl.AddRectFilledMultiColor(
            new Vector2(0f, 0f),
            new Vector2(thickness, disp.Y),
            edge, inner, inner, edge);
        dl.AddRectFilledMultiColor(
            new Vector2(disp.X - thickness, 0f),
            new Vector2(disp.X, disp.Y),
            inner, edge, edge, inner);
    }

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

    private static uint PackRgba(float r, float g, float b, float a)
    {
        byte br = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, r)) * 255f);
        byte bg = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, g)) * 255f);
        byte bb = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, b)) * 255f);
        byte ba = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, a)) * 255f);
        return ((uint)ba << 24) | ((uint)bb << 16) | ((uint)bg << 8) | br;
    }
}
