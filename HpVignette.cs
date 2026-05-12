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

    // True last frame the player was at HP = 0.
    private static bool   _wasDead;
    // True last frame the player's HP was below the vignette threshold.
    private static bool   _wasLowHp;
    // Wall time the rez/heal fade ends. <= 0 means no fade in flight.
    private static double _rezFadeUntilS;
    // Smoothed alpha for fade-out when HP climbs above threshold.
    private static float  _smoothAlpha;

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

            // Heal transition: HP was below threshold and just crossed above.
            float threshold = MathF.Max(0.01f, c.HpVignetteThreshold);
            bool isLowHp = !isDead && hpFrac < threshold;
            if (!isLowHp && _wasLowHp && !isDead && _rezFadeUntilS <= 0)
            {
                // Healed above threshold — trigger same white-fade as rez.
                _rezFadeUntilS = now + REZ_FADE_SECONDS;
            }
            _wasLowHp = isLowHp;

            float r, g, b, alpha;

            if (isDead)
            {
                // Mode 2: dead — flat dark grey.
                r = 0.18f; g = 0.18f; b = 0.18f;
                alpha = DEAD_ALPHA;
            }
            else if (_rezFadeUntilS > 0 && now < _rezFadeUntilS)
            {
                // Mode 3: rez/heal fade — white that decays to transparent
                // over REZ_FADE_SECONDS from full DEAD_ALPHA.
                float remain = (float)((_rezFadeUntilS - now) / REZ_FADE_SECONDS);
                if (remain < 0f) remain = 0f;
                if (remain > 1f) remain = 1f;
                r = 1f; g = 1f; b = 1f;
                alpha = DEAD_ALPHA * remain;
            }
            else
            {
                // Clear any expired rez fade so we don't keep checking.
                if (_rezFadeUntilS > 0 && now >= _rezFadeUntilS) _rezFadeUntilS = 0;

                // Mode 1: alive, HP-driven red with smooth fade-out.
                const float FADE_IN_BAND = 0.10f;
                float fadeInStart = threshold + FADE_IN_BAND;
                float targetAlpha;
                if (hpFrac >= fadeInStart)
                    targetAlpha = 0f;
                else if (hpFrac >= threshold)
                {
                    float band = (fadeInStart - hpFrac) / FADE_IN_BAND;
                    targetAlpha = 0.5f * band;
                }
                else
                {
                    float belowFrac = 1f - (hpFrac / threshold);
                    targetAlpha = 0.5f + 0.5f * belowFrac;
                }
                float maxAlpha = MathF.Max(0f, MathF.Min(1f, c.HpVignetteMaxAlpha));
                targetAlpha *= maxAlpha;

                // Smooth lerp so red fades out gradually instead of cutting.
                float dt = ImGui.GetIO().DeltaTime;
                float speed = targetAlpha >= _smoothAlpha ? 12f : 3f; // fast in, gentle out
                _smoothAlpha += (targetAlpha - _smoothAlpha) * MathF.Min(1f, speed * dt);
                if (MathF.Abs(_smoothAlpha - targetAlpha) < 0.002f) _smoothAlpha = targetAlpha;

                r = c.HpVignetteR; g = c.HpVignetteG; b = c.HpVignetteB;
                alpha = _smoothAlpha;
            }

            if (alpha <= 0.005f) return;

            var io = ImGui.GetIO();
            var disp = io.DisplaySize;
            if (disp.X <= 0 || disp.Y <= 0) return;

            float thickness = MathF.Max(20f, MathF.Min(disp.X, disp.Y) * c.HpVignetteThickness);

            uint edge  = PackRgba(r, g, b, alpha);
            uint inner = PackRgba(r, g, b, 0f);

            var dl = ImGui.GetForegroundDrawList();
            // AddRectFilledMultiColor: (min, max, ul, ur, br, bl).
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
        catch { /* defensive — IO / ObjectTable can be transient */ }
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
