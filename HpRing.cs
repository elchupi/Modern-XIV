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

        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();
        // (ImDrawListPtr is a struct wrapper — the != null check has
        // ambiguous operators in this Dalamud binding, so we just trust
        // the call and rely on AddCircle's internal null guard.)

        // Base / pulse alpha and radius interpolate across HP%.
        float t = 1f - _hpPctSmooth; // 0 = full, 1 = empty

        float baseAlphaFull  = noWickyXIV.Config.HpRingFullHpBaseAlpha;
        float baseAlphaLow   = noWickyXIV.Config.HpRingLowHpBaseAlpha;
        float peakAlphaFull  = noWickyXIV.Config.HpRingFullHpPeakAlpha;
        float peakAlphaLow   = noWickyXIV.Config.HpRingLowHpPeakAlpha;
        float baseAlpha = baseAlphaFull + (baseAlphaLow - baseAlphaFull) * t;
        float peakAlpha = peakAlphaFull + (peakAlphaLow - peakAlphaFull) * t;

        // Pulse 0..1 from sine. (sin+1)/2 gives 0..1.
        float pulse = 0.5f * (1f + MathF.Sin((float)_phase));
        float alpha = baseAlpha + (peakAlpha - baseAlpha) * pulse;

        // Outer ring: stays at base radius (the full slider value),
        // pulsing alpha. Inner filled circle: radius scales with HP
        // (full = baseRadius, empty = 0). LowHpRadiusFactor still
        // applies to the OUTER ring so users who'd configured the
        // ring to grow at low HP still get that visual cue.
        float baseRadius = noWickyXIV.Config.HpRingRadius;
        float lowFactor  = noWickyXIV.Config.HpRingLowHpRadiusFactor;
        float outerRadius = baseRadius * (1f + (lowFactor - 1f) * t);
        float innerRadius = baseRadius * _hpPctSmooth;

        // ---- Position ----
        // Two modes:
        //   1. Screen-anchored: cx/cy from viewport-fraction sliders.
        //   2. Bone-anchored: resolve a player bone's world position,
        //      add a player-LOCAL offset (rotated by yaw so "forward"
        //      stays in front and "back" stays behind as the player
        //      turns), project to screen each frame.
        float cx, cy;
        if (noWickyXIV.Config.HpRingAnchorToBone)
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var go = (GameObject*)lp.Address;
            if (go == null || go->DrawObject == null) return;

            Vector3 boneWorld = lp.Position;
            try
            {
                if (Hypostasis.Game.Common.getWorldBonePosition.IsValid)
                {
                    var bw = Hypostasis.Game.Common.GetBoneWorldPosition(
                        go, (uint)Math.Max(0, noWickyXIV.Config.HpRingBoneIndex));
                    if (bw != Vector3.Zero) boneWorld = bw;
                }
            }
            catch { /* fall back to root position */ }

            // Rotate the right/forward offset by player yaw so the
            // back-of-player offset stays behind regardless of facing.
            float yaw = lp.Rotation;
            float c = MathF.Cos(yaw);
            float s = MathF.Sin(yaw);
            float r = noWickyXIV.Config.HpRingOffsetRight;
            float u = noWickyXIV.Config.HpRingOffsetUp;
            float f = noWickyXIV.Config.HpRingOffsetForward;
            // FFXIV: rotation 0 faces -Z (south). Positive yaw rotates CCW
            // looking from above. Forward (+player Z) maps to (-sin·yaw, ?, -cos·yaw).
            // Right (+player X) maps to (cos·yaw, ?, -sin·yaw). Mirroring
            // the same convention CameraDynamics / JobAura use.
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
            cx = io.DisplaySize.X * MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.HpRingScreenX));
            cy = io.DisplaySize.Y * MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.HpRingScreenY));
        }

        var center = new Vector2(cx, cy);

        // Outer pulse ring — alpha modulates with the sine pulse.
        uint outerCol = ColorU32(
            noWickyXIV.Config.HpRingColorR,
            noWickyXIV.Config.HpRingColorG,
            noWickyXIV.Config.HpRingColorB,
            alpha);
        dl.AddCircle(center, outerRadius, outerCol,
            noWickyXIV.Config.HpRingSegments,
            noWickyXIV.Config.HpRingThickness);

        // Inner filled circle — radius shows current HP. Slightly
        // higher alpha (peak of the pulse range) for a stable readout
        // that doesn't blend into the background as HP drops.
        if (innerRadius > 0.5f)
        {
            uint innerCol = ColorU32(
                noWickyXIV.Config.HpRingColorR,
                noWickyXIV.Config.HpRingColorG,
                noWickyXIV.Config.HpRingColorB,
                peakAlpha);
            dl.AddCircleFilled(center, innerRadius, innerCol,
                noWickyXIV.Config.HpRingSegments);
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
