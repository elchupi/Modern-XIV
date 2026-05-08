using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace noWickyXIV;

// Standalone HP-driven ring overlay. Separate from JobAura's ring layer.
//
// Visual:
//   - A single circle outline anchored at a configurable screen position.
//   - Pulses on a sine wave between a base alpha and a pulse-peak alpha.
//   - At FULL HP: slow pulse, base alpha low (e.g. 0.5), peak alpha 1.0.
//   - At LOW HP : fast pulse, base alpha higher (e.g. 0.8), peak 1.0,
//                 radius shrinks toward HpRingLowHpRadiusFactor (e.g. 0.7).
//
// Linear interpolation between full-HP and zero-HP across the entire
// HP range, so the visual smoothly transitions as HP drops.
public static unsafe class HpRing
{
    private static double _phase;       // accumulated pulse phase (radians)
    private static float  _hpPctSmooth; // 0..1, smoothed for stability

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableHpRing) return;

        float hpPct = 1f;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp != null && lp.MaxHp > 0)
                hpPct = MathF.Max(0f, MathF.Min(1f, lp.CurrentHp / (float)lp.MaxHp));
        }
        catch { }

        // Smooth HP toward target so a sudden jump doesn't make the ring
        // jerk between size/pulse rates.
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        if (dt <= 0f) dt = 0.016f;
        const float HP_SMOOTH_RATE = 6f; // higher = snappier
        float k = 1f - MathF.Exp(-HP_SMOOTH_RATE * dt);
        _hpPctSmooth += (hpPct - _hpPctSmooth) * k;

        // Pulse rate lerps with HP: slowHz at full, fastHz at zero.
        // Hz = cycles per second.
        float slowHz = noWickyXIV.Config.HpRingSlowPulseHz;
        float fastHz = noWickyXIV.Config.HpRingFastPulseHz;
        float t = 1f - _hpPctSmooth; // 0 = full HP, 1 = empty
        float hz = slowHz + (fastHz - slowHz) * t;

        _phase += hz * dt * 2.0 * Math.PI;
        if (_phase > 1e9) _phase -= 1e9;
    }

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnableHpRing) return;

        // Advance shared throb / emanating-pulse phases for this frame
        // before any actor draws — keeps player / target / party rings
        // beating in unison.
        TickPulses();

        var dl = ImGui.GetForegroundDrawList();

        // Player ring — uses smoothed HP for stability.
        DrawRingForActor(dl,
            DalamudApi.ObjectTable.LocalPlayer,
            _hpPctSmooth,
            anchorToBone: noWickyXIV.Config.HpRingAnchorToBone,
            screenX: noWickyXIV.Config.HpRingScreenX,
            screenY: noWickyXIV.Config.HpRingScreenY);

        // Enemy rings — only enemies the player is engaged with: the
        // current target (if hostile BattleNpc) plus everyone on the
        // hater list (anything currently aggroed onto the player).
        // No bone lookup; ring sits at actor.Position + 1.8 m Y so it
        // floats around the head consistently regardless of skeleton.
        // Skip the active target when JobAura's already drawing its
        // ring there to avoid duplicating that one.
        if (noWickyXIV.Config.EnableHpRingOnTarget)
        {
            bool jobAuraOnTarget = noWickyXIV.Config.EnableJobAura
                                && noWickyXIV.Config.JobAuraAnchorToTarget;
            ulong skipId = 0UL;
            if (jobAuraOnTarget)
            {
                try { skipId = DalamudApi.TargetManager?.Target?.GameObjectId ?? 0UL; }
                catch { }
            }
            ulong selfId = DalamudApi.ObjectTable?.LocalPlayer?.GameObjectId ?? 0UL;

            // Hater list ∪ current target — entities currently aggroed
            // onto the local player PLUS whatever's explicitly targeted.
            // Anything outside that union (random passive mobs, NPCs,
            // etc.) gets nothing.
            var seen = new System.Collections.Generic.HashSet<ulong>();
            var diagNames = new System.Collections.Generic.List<string>();

            // Current targets (hard + soft + mouseover). D-pad cycle
            // uses SoftTarget while highlighting the next enemy
            // before the user locks it in; mouse-hover uses
            // MouseOverTarget. Including all three so the ring
            // follows whichever target slot the user is actively
            // driving.
            void TryAddTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject t, string label)
            {
                if (t is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn) return;
                if (!bn.IsTargetable) return;
                if (bn.CurrentHp <= 0 || bn.MaxHp <= 0) return;
                if (bn.GameObjectId == selfId) return;
                if (bn.GameObjectId == skipId) return;
                if (!seen.Add(bn.GameObjectId)) return;
                float pct = MathF.Min(1f, MathF.Max(0f, bn.CurrentHp / (float)bn.MaxHp));
                DrawRingsAtActorHead(dl, bn, pct);
                try { diagNames.Add($"{label}:{bn.Name?.TextValue}"); } catch { }
            }
            try { TryAddTarget(DalamudApi.TargetManager?.Target,         "target"); } catch { }
            try { TryAddTarget(DalamudApi.TargetManager?.SoftTarget,     "soft"); } catch { }
            try { TryAddTarget(DalamudApi.TargetManager?.MouseOverTarget,"mouse"); } catch { }

            // Hater list (engine's enmity table for the local player).
            // Filter on Enmity > 0 — the raw Haters list includes any
            // aggressive NPC that's flagged the player as a hater (i.e.
            // would attack them on detection), even passive prowling
            // mobs the player hasn't engaged. Requiring positive enmity
            // means the player must have actually generated threat
            // (took or dealt damage) against the entity.
            try
            {
                var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
                if (ui != null)
                {
                    var haters = ui->Hater.Haters;
                    for (int i = 0; i < haters.Length; i++)
                    {
                        uint eid = haters[i].EntityId;
                        if (eid == 0 || eid == 0xE0000000) continue;
                        if (haters[i].Enmity <= 0) continue;
                        var obj = DalamudApi.ObjectTable.SearchById(eid);
                        if (obj == null || !obj.IsValid()) continue;
                        if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn) continue;
                        if (!bn.IsTargetable) continue;
                        if (bn.CurrentHp <= 0 || bn.MaxHp <= 0) continue;
                        if (bn.GameObjectId == selfId) continue;
                        if (bn.GameObjectId == skipId) continue;
                        if (!seen.Add(bn.GameObjectId)) continue;
                        float pct = MathF.Min(1f, MathF.Max(0f, bn.CurrentHp / (float)bn.MaxHp));
                        DrawRingsAtActorHead(dl, bn, pct);
                        try { diagNames.Add($"hater:{bn.Name?.TextValue}"); } catch { }
                    }
                }
            }
            catch { }

            var nowDiag = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            if (nowDiag - _lastEnemyHb > 2.0)
            {
                _lastEnemyHb = nowDiag;
                try { DalamudApi.PluginLog.Information(
                    $"[noWickyXIV] HpRing enemies drawn ({diagNames.Count}): "
                    + string.Join(", ", diagNames)); } catch { }
            }
        }

        // Party rings — iterate non-self party members.
        if (noWickyXIV.Config.EnableHpRingOnParty)
        {
            try
            {
                var party = DalamudApi.PartyList;
                ulong selfId = DalamudApi.ObjectTable?.LocalPlayer?.GameObjectId ?? 0UL;
                if (party != null)
                {
                    foreach (var pm in party)
                    {
                        if (pm == null) continue;
                        var go = pm.GameObject;
                        if (go == null || !go.IsValid()) continue;
                        if (go.GameObjectId == selfId) continue;
                        if (pm.MaxHP <= 0) continue;
                        float pct = MathF.Min(1f, MathF.Max(0f, pm.CurrentHP / (float)pm.MaxHP));
                        DrawRingForActor(dl, go, pct,
                            anchorToBone: true,
                            screenX: 0f, screenY: 0f);
                    }
                }
            }
            catch { }
        }
    }

    // Pulse phases for the JobAura-style ring layers. Shared across
    // every actor we draw so player / target / party rings beat in
    // unison and the visual is consistent.
    private static double _bornAt = -1;
    private static double _hpPulsePhase;

    private static void TickPulses()
    {
        if (_bornAt < 0) _bornAt = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        float dtr = 0.016f;
        try { dtr = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        if (dtr <= 0f) dtr = 0.016f;
        // Period locked to the lowest-HP actor we drew last frame so
        // an emergency ally pulses fast. Defaulted to mid-range when
        // no actor has driven it yet.
        float pctSafe = MathF.Max(0.05f, _lastSeenLowestPct);
        double pulsePeriod = 0.18 + (1.0 - 0.18) * pctSafe;
        _hpPulsePhase += dtr / pulsePeriod;
        if (_hpPulsePhase >= 1.0) _hpPulsePhase -= Math.Floor(_hpPulsePhase);
        _lastSeenLowestPct = 1f; // reset; this frame's draws will set it
    }

    private static float _lastSeenLowestPct = 1f;
    private static double _lastEnemyHb;

    // Shared ring-drawing helper. Pull the per-actor alpha + radius math
    // out so we can reuse it for the player, current target, and each
    // party member without duplicating the logic. Visual style mirrors
    // JobAura.DrawHpRingsAt: a backdrop ring, a sine-modulated throb at
    // the backdrop edge, an HP-driven inner core, and a pulse ring
    // emanating outward from the backdrop edge. All driven by the
    // JobAuraHp* config fields so a single tuning applies across both
    // the SAM Kenki overlay and these standalone rings.
    private static void DrawRingForActor(
        ImDrawListPtr dl,
        Dalamud.Game.ClientState.Objects.Types.IGameObject actor,
        float hpPct,
        bool anchorToBone,
        float screenX,
        float screenY)
    {
        if (actor == null) return;
        var cfg = noWickyXIV.Config;

        float pct = MathF.Min(1f, MathF.Max(0f, hpPct));
        if (pct < _lastSeenLowestPct) _lastSeenLowestPct = pct;

        float baseR = cfg.HpRingRadius;

        // ---- Resolve screen-space center ----
        float cx, cy;
        if (anchorToBone)
        {
            var go = (GameObject*)actor.Address;
            if (go == null || go->DrawObject == null) return;

            Vector3 boneWorld = new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z);
            try
            {
                if (Hypostasis.Game.Common.getWorldBonePosition.IsValid)
                {
                    var bw = Hypostasis.Game.Common.GetBoneWorldPosition(
                        go, (uint)Math.Max(0, cfg.HpRingBoneIndex));
                    if (bw != Vector3.Zero) boneWorld = bw;
                }
            }
            catch { /* fall back to root position */ }

            float yaw = actor.Rotation;
            float c = MathF.Cos(yaw);
            float s = MathF.Sin(yaw);
            float r = cfg.HpRingOffsetRight;
            float u = cfg.HpRingOffsetUp;
            float f = cfg.HpRingOffsetForward;
            float worldX = boneWorld.X + r * c - f * s;
            float worldZ = boneWorld.Z - r * s - f * c;
            float worldY = boneWorld.Y + u;

            if (!DalamudApi.GameGui.WorldToScreen(new Vector3(worldX, worldY, worldZ), out var screen))
                return;
            cx = screen.X;
            cy = screen.Y;
        }
        else
        {
            var io = ImGui.GetIO();
            cx = io.DisplaySize.X * MathF.Max(0f, MathF.Min(1f, screenX));
            cy = io.DisplaySize.Y * MathF.Max(0f, MathF.Min(1f, screenY));
        }
        var center = new Vector2(cx, cy);

        // Delegate to the shared screen-space renderer so player /
        // party / enemy all run through the same alpha-by-HP slider
        // logic. Without this, only the enemy path used the sliders
        // and the player/party rings ignored the full-HP fade.
        DrawRingsAtScreen(dl, center, pct);
    }

    // Project the actor's head-height world position to screen and
    // draw the JobAura HP-ring layers there. No bone lookup, no
    // skeleton-specific config — works for any IGameObject. Used for
    // enemies; HP-alpha gate disabled so they show at full visibility
    // whenever the foreach picks them up (aggro list / current target).
    private static void DrawRingsAtActorHead(
        ImDrawListPtr dl,
        Dalamud.Game.ClientState.Objects.Types.IGameObject actor,
        float hpPct)
    {
        if (actor == null) return;
        var headWorld = new Vector3(
            actor.Position.X,
            actor.Position.Y + 1.8f,
            actor.Position.Z);
        if (!DalamudApi.GameGui.WorldToScreen(headWorld, out var screen))
            return;
        DrawRingsAtScreen(dl, screen, hpPct, applyHpAlphaGate: false);
    }

    // Renders the layered HP-ring visual at a fixed screen position.
    // Mirrors JobAura.DrawHpRingsAt: backdrop (filled) + sine-modulated
    // throb + HP-driven inner core + emanating pulse. All driven by
    // the JobAuraHp* config fields.
    //
    // Overall alpha gated by HpRing*HpBaseAlpha (persistent layers)
    // and HpRing*HpPeakAlpha (pulse-modulated layers), lerped between
    // their full-HP and low-HP values. With the user's FullHp=0 /
    // LowHp=1 setting, full-HP actors are invisible and the ring
    // ramps in as HP drops.
    private static void DrawRingsAtScreen(ImDrawListPtr dl, Vector2 center, float hpPct,
        bool applyHpAlphaGate = true)
    {
        var cfg = noWickyXIV.Config;
        float pct = MathF.Min(1f, MathF.Max(0f, hpPct));
        if (pct < _lastSeenLowestPct) _lastSeenLowestPct = pct;

        // HP-driven alpha gate from HpRing*HpBaseAlpha / *HpPeakAlpha
        // sliders — applied to player/party so the ring fades in as
        // their HP drops. NOT applied to enemies: their rings show
        // based on aggro/target presence (the foreach in Draw filters
        // by hater list), not by HP — they should be fully visible
        // whenever drawn.
        float baseAlphaMul = 1f;
        float peakAlphaMul = 1f;
        if (applyHpAlphaGate)
        {
            float t = 1f - pct; // 0 at full HP, 1 at empty
            baseAlphaMul = cfg.HpRingFullHpBaseAlpha
                         + (cfg.HpRingLowHpBaseAlpha - cfg.HpRingFullHpBaseAlpha) * t;
            peakAlphaMul = cfg.HpRingFullHpPeakAlpha
                         + (cfg.HpRingLowHpPeakAlpha - cfg.HpRingFullHpPeakAlpha) * t;
            if (baseAlphaMul < 0.005f && peakAlphaMul < 0.005f) return;
        }

        float baseR = cfg.HpRingRadius;

        // Backdrop ring — persistent.
        float backdropR = baseR * cfg.JobAuraHpBackdropRadiusFactor;
        float backdropA = cfg.JobAuraHpBackdropAlpha * baseAlphaMul;
        if (backdropA >= 0.005f)
        {
            var backdrop = new Vector4(
                cfg.JobAuraHpBackdropColorR, cfg.JobAuraHpBackdropColorG, cfg.JobAuraHpBackdropColorB,
                backdropA);
            dl.AddCircleFilled(center, backdropR, ImGui.GetColorU32(backdrop), 64);
        }

        // Outer throb ring at the backdrop edge.
        float pctSafeT = MathF.Max(0.05f, pct);
        double throbPeriod = 0.20 + (1.00 - 0.20) * pctSafeT;
        double tph = ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond - _bornAt) % throbPeriod;
        float throb = 0.5f + 0.5f * MathF.Sin((float)(tph * Math.PI * 2.0 / throbPeriod));
        float throbR = backdropR * (1.02f + 0.06f * throb);
        float throbA = (0.35f + 0.65f * throb) * cfg.JobAuraHpPulseAlpha * peakAlphaMul;
        if (throbA >= 0.005f)
        {
            var throbCol = new Vector4(
                cfg.JobAuraHpPulseColorR, cfg.JobAuraHpPulseColorG, cfg.JobAuraHpPulseColorB,
                throbA);
            dl.AddCircle(center, throbR, ImGui.GetColorU32(throbCol),
                96, cfg.JobAuraHpPulseThickness);
        }

        // Inner core — radius + alpha scale with HP.
        float coreR = baseR * cfg.JobAuraHpInnerRadiusFactor * pct;
        float coreA = cfg.JobAuraHpInnerAlpha * MathF.Max(0.05f, pct) * baseAlphaMul;
        if (coreR > 0.5f && coreA >= 0.005f)
        {
            var core = new Vector4(
                cfg.JobAuraHpInnerColorR, cfg.JobAuraHpInnerColorG, cfg.JobAuraHpInnerColorB,
                coreA);
            dl.AddCircleFilled(center, coreR, ImGui.GetColorU32(core), 64);
        }

        // Emanating pulse ring — period from shared HP phase.
        float pulseT = (float)_hpPulsePhase;
        float pulseRStart = backdropR;
        float pulseREnd   = backdropR * cfg.JobAuraHpPulseExpandFactor;
        float pulseR = pulseRStart + (pulseREnd - pulseRStart) * pulseT;
        float pulseA = (1f - pulseT) * cfg.JobAuraHpPulseAlpha * peakAlphaMul;
        if (pulseA >= 0.005f)
        {
            var pCol = new Vector4(
                cfg.JobAuraHpPulseColorR, cfg.JobAuraHpPulseColorG, cfg.JobAuraHpPulseColorB,
                pulseA);
            dl.AddCircle(center, pulseR, ImGui.GetColorU32(pCol),
                96, cfg.JobAuraHpPulseThickness);
        }
    }

    private static uint ColorU32(float r, float g, float b, float a)
    {
        byte ri = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, r)) * 255f);
        byte gi = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, g)) * 255f);
        byte bi = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, b)) * 255f);
        byte ai = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, a)) * 255f);
        return ((uint)ai << 24) | ((uint)bi << 16) | ((uint)gi << 8) | ri;
    }
}
