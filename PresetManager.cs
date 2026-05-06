using System;
using System.Linq;

namespace noWickyXIV;

public static class PresetManager
{
    public static CameraConfigPreset CurrentPreset
    {
        get => PresetOverride ?? ActivePreset ?? DefaultPreset;
        set
        {
            ApplyPreset(PresetOverride = value);
            if (value == null)
                ActivePreset = null;
            try
            {
                noWickyXIV.Config.LastActivePresetName = value?.Name ?? "";
                noWickyXIV.Config.Save();
            }
            catch { /* defensive */ }
        }
    }

    public static void RestoreLastActivePreset()
    {
        try
        {
            var name = noWickyXIV.Config.LastActivePresetName;
            if (string.IsNullOrEmpty(name)) return;
            var preset = noWickyXIV.Config.Presets.FirstOrDefault(p => p.Name == name);
            if (preset == null) return;
            ApplyPreset(PresetOverride = preset, isLoggingIn: true);
        }
        catch { /* defensive */ }
    }

    public static CameraConfigPreset DefaultPreset { get; set; } = new();
    public static CameraConfigPreset ActivePreset { get; private set; }
    public static CameraConfigPreset PresetOverride { get; private set; }
    // Public read so CameraDynamics can bypass its zoom/rotation
    // smoothing while a transition is in flight — without this, both
    // PresetManager.Update (writing the lerped zoom) and
    // CameraDynamics.Update (re-targeting to whatever the lerp just
    // wrote) compete on the same value every frame, producing the
    // stepped/jerky zoom feel during preset swaps.
    public static bool IsTransitionActive => _txActive;

    // ---- Effective values ----
    // Game.cs detours read these instead of preset.X directly so the
    // detours have a single source of truth. During a preset
    // transition (auto-condition swap, manual click) these values
    // smoothstep from their previous values toward the new preset's
    // over Config.PresetTransitionSeconds — only the position-shaped
    // offsets (Height/Side/LookAtHeight). Zoom/FoV/min-max bounds
    // still snap, since lerping them fights user wheel/yaw input.
    public static float EffectiveTilt              { get; private set; }
    public static float EffectiveLookAtHeightOffset{ get; private set; }
    public static float EffectiveHeightOffset      { get; private set; }
    public static float EffectiveSideOffset        { get; private set; }

    // ---- Transition state ----
    // Captured at ApplyPreset(transition=true) time. While _txActive,
    // PresetManager.Update lerps Effective* from these starts toward
    // _txTarget's preset values. After elapsed >= duration, falls
    // back to direct passthrough from CurrentPreset.
    private static bool   _txActive;
    private static double _txStartT;
    private static float  _txStartLookAt;
    private static float  _txStartHeight;
    private static float  _txStartSide;
    // Zoom/FoV captured at transition start so the engine's
    // currentZoom/currentFoV can lerp visibly toward the target
    // preset's StartZoom/StartFoV. User scroll input cancels the
    // transition so we never fight live wheel adjustments.
    private static float  _txStartZoom;
    private static float  _txStartFoV;
    private static float  _txTargetZoom;
    private static float  _txTargetFoV;
    private static CameraConfigPreset _txTarget;

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    // Called by InputHandler when the user adjusts position offsets via
    // Ctrl/Alt+scroll. If a transition is mid-flight, snap it to its
    // target so user input doesn't fight the lerp's residual motion —
    // otherwise scrolling DOWN to lower height while transitioning UP
    // produces a tug-of-war where the camera barely moves (or even
    // moves the wrong way) until the transition completes.
    public static unsafe void CancelTransitionToTarget()
    {
        if (!_txActive || _txTarget == null) return;
        EffectiveLookAtHeightOffset = _txTarget.LookAtHeightOffset;
        EffectiveHeightOffset       = _txTarget.HeightOffset;
        EffectiveSideOffset         = _txTarget.SideOffset;
        EffectiveTilt               = _txTarget.Tilt;
        try
        {
            var cam = Common.CameraManager->worldCamera;
            if (cam != null)
            {
                cam->currentZoom = _txTargetZoom;
                cam->currentFoV  = _txTargetFoV;
            }
        }
        catch { /* defensive */ }
        try { CameraDynamics.SnapOffsets(); } catch { /* defensive */ }
        _txActive = false;
        _txTarget = null;
    }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    // Ken Perlin's smootherstep — 5th-order Hermite, flatter
    // derivative at both ends than cubic smoothstep, so the
    // beginning and end of the transition feel less abrupt while
    // the middle moves more decisively. Replaces the previous
    // 3t² - 2t³ which had a noticeably "stepped" feel on zoom
    // because its endpoint derivative is non-zero relative to a
    // perceptually-flat baseline.
    private static float Smoothstep(float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    public static unsafe void ApplyPreset(CameraConfigPreset preset, bool isLoggingIn = false)
        => ApplyPreset(preset, isLoggingIn, transition: false);

    public static unsafe void ApplyPreset(CameraConfigPreset preset, bool isLoggingIn, bool transition)
    {
        if (preset == null) return;

        var camera = Common.CameraManager->worldCamera;
        if (camera == null) return;

        // ---- Bounds + invariants snap immediately ----
        camera->minZoom = preset.MinZoom;
        camera->maxZoom = preset.MaxZoom;
        camera->minFoV  = preset.MinFoV;
        camera->maxFoV  = preset.MaxFoV;
        Game.FoVDelta   = preset.FoVDelta;
        camera->minVRotation = preset.MinVRotation;
        camera->maxVRotation = preset.MaxVRotation;

        // Resolve target zoom/FoV (clamped to preset's min/max).
        float targetZoom = preset.StartZoom > 0f
            ? Math.Min(Math.Max(preset.StartZoom, preset.MinZoom), preset.MaxZoom)
            : Math.Min(Math.Max(camera->currentZoom, preset.MinZoom), preset.MaxZoom);

        float targetFoV  = preset.StartFoV  > 0f
            ? Math.Min(Math.Max(preset.StartFoV,  preset.MinFoV), preset.MaxFoV)
            : Math.Min(Math.Max(camera->currentFoV, preset.MinFoV), preset.MaxFoV);

        if (transition && !isLoggingIn
            && noWickyXIV.Config.PresetTransitionSeconds > 0.06f)
        {
            // Capture the CURRENT values as the lerp's start point.
            // Mid-transition switches pick up smoothly from wherever
            // the lerp is, not a stale point before this transition
            // began. Camera struct fields (zoom/FoV) are also part
            // of the lerp now — snapping them was the dominant
            // visual change in a preset swap, and snapping them
            // overpowered the position-offset lerp making the
            // transition slider feel like a no-op.
            _txStartLookAt = EffectiveLookAtHeightOffset;
            _txStartHeight = EffectiveHeightOffset;
            _txStartSide   = EffectiveSideOffset;
            _txStartZoom   = camera->currentZoom;
            _txStartFoV    = camera->currentFoV;
            _txTargetZoom  = targetZoom;
            _txTargetFoV   = targetFoV;
            _txTarget      = preset;
            _txStartT      = NowSec();
            _txActive      = true;
            // Tilt snaps — it's not visually significant enough to
            // warrant its own lerp axis, and CameraDynamics rewrites
            // camera->tilt every frame from _rollCurrent anyway.
            camera->tilt   = preset.Tilt;
            EffectiveTilt  = preset.Tilt;
            // Don't write currentZoom/FoV/lookAtHeightOffset — Update
            // will lerp them and the per-frame detour
            // (UpdateLookAtHeightOffsetDetour) reads
            // EffectiveLookAtHeightOffset which Update lerps.
            // Don't call SnapOffsets — let CameraDynamics smoothing
            // ride the lerp on top.
        }
        else
        {
            // ---- No transition: snap everything immediately. ----
            _txActive = false;
            _txTarget = null;
            camera->currentZoom        = targetZoom;
            camera->currentFoV         = targetFoV;
            camera->tilt               = preset.Tilt;
            camera->lookAtHeightOffset = preset.LookAtHeightOffset;
            EffectiveTilt               = preset.Tilt;
            EffectiveLookAtHeightOffset = preset.LookAtHeightOffset;
            EffectiveHeightOffset       = preset.HeightOffset;
            EffectiveSideOffset         = preset.SideOffset;
            try { CameraDynamics.SnapOffsets(); } catch { /* defensive */ }
        }
    }

    public static void CheckCameraConditionSets(bool isLoggingIn)
    {
        // Match priority:
        //   1. First preset whose CONDITIONAL trigger matches
        //      (built-in game state OR a QoL Bar set), in list order
        //   2. Otherwise, first unconditional ("None") preset
        // Walking the list once with two slots avoids the bug where a
        // None-preset earlier in the list always shadowed every
        // conditional one below it.
        CameraConfigPreset conditionalMatch = null;
        CameraConfigPreset unconditionalMatch = null;
        foreach (var p in noWickyXIV.Config.Presets)
        {
            bool hasCondition =
                p.Condition != BuiltinPresetCondition.None || p.ConditionSet >= 0;

            if (hasCondition)
            {
                if (conditionalMatch == null && p.CheckConditionSet())
                    conditionalMatch = p;
            }
            else
            {
                if (unconditionalMatch == null)
                    unconditionalMatch = p;
            }
        }

        var preset = conditionalMatch ?? unconditionalMatch;
        if (preset == null || preset == ActivePreset) return;

        // Auto-condition swaps use the smoothstep transition unless
        // we're in the middle of a login restore (login should snap
        // — the user expects their preset already loaded, not
        // gliding in over 5 seconds).
        ApplyPreset(preset, isLoggingIn, transition: !isLoggingIn);
        ActivePreset = preset;
    }

    public static unsafe void Update()
    {
        if (_txActive && _txTarget != null)
        {
            // Smoothstep all transition axes toward the target.
            float dur = MathF.Max(0.05f, noWickyXIV.Config.PresetTransitionSeconds);
            float t = (float)((NowSec() - _txStartT) / dur);
            if (t >= 1f) { t = 1f; _txActive = false; }
            float s = Smoothstep(t);

            // Pivot semantics: lerp LookAtHeightOffset on a HEAVILY
            // backloaded cubic curve (t^3) while HeightOffset uses
            // the normal symmetric smoothstep. Effect: for most of
            // the transition, the look-at point barely moves — so
            // the camera ORBITS around the original focus point as
            // its height changes (player stays framed). LookAt then
            // catches up quickly in the last ~30% of the window and
            // lands cleanly at the target by t=1, no end snap.
            // Without the curve split, both Height and LookAt rose
            // in parallel and the framing dragged upward as the
            // camera ascended — the user reported it as "the
            // center axis shifts up instead of pivoting around
            // what we're looking at". Holding LookAt constant the
            // whole way fixed the pivot but caused a visible reset
            // at the end; the cubic curve gets both right.
            float sLookAt = t * t * t;
            EffectiveLookAtHeightOffset = Lerp(_txStartLookAt, _txTarget.LookAtHeightOffset, sLookAt);
            EffectiveHeightOffset       = Lerp(_txStartHeight, _txTarget.HeightOffset,       s);
            EffectiveSideOffset         = Lerp(_txStartSide,   _txTarget.SideOffset,         s);
            EffectiveTilt               = _txTarget.Tilt;

            // Lerp camera struct fields directly. User wheel input
            // cancels the transition (CancelTransitionToTarget) so
            // these writes never fight the user — by the time their
            // scroll arrives, _txActive is false.
            try
            {
                var cam = Common.CameraManager->worldCamera;
                if (cam != null)
                {
                    cam->currentZoom = Lerp(_txStartZoom, _txTargetZoom, s);
                    cam->currentFoV  = Lerp(_txStartFoV,  _txTargetFoV,  s);
                }
            }
            catch { /* defensive */ }
        }
        else
        {
            // Direct passthrough — all four Effective* values mirror
            // the active preset every frame so live edits in the
            // editor take effect immediately.
            var preset = CurrentPreset;
            if (preset != null)
            {
                EffectiveTilt               = preset.Tilt;
                EffectiveLookAtHeightOffset = preset.LookAtHeightOffset;
                EffectiveHeightOffset       = preset.HeightOffset;
                EffectiveSideOffset         = preset.SideOffset;
            }
        }

        // Auto-snapshot: when the user adjusts zoom/FoV via wheel
        // input, write the new values back into the active preset so
        // the preset "remembers" the tweak — no need to manually
        // re-save. We only run this OUTSIDE of transitions (lerping
        // values aren't user intent) and only when there's a real
        // preset to write to (DefaultPreset is a singleton fallback,
        // not a user-saved preset).
        if (!_txActive)
            AutoSnapshotCameraIntoActivePreset();

        // Auto-swap by condition. Manual override (PresetOverride set
        // when the user clicks a preset) suppresses condition swaps —
        // the user is taking explicit control.
        if (!DalamudApi.ClientState.IsLoggedIn || FreeCam.Enabled || PresetOverride != null) return;
        CheckCameraConditionSets(false);
    }

    // Throttled save state for auto-snapshot. We coalesce wheel-tick
    // changes that arrive across many frames into a single Save()
    // every ~0.5 s so we don't disk-thrash on a fast scroll burst.
    private static double _lastAutoSaveAt;
    private static bool   _autoSaveDirty;

    private static unsafe void AutoSnapshotCameraIntoActivePreset()
    {
        // Pick the preset the user is actually using. PresetOverride
        // = manually-clicked preset; ActivePreset = condition-driven.
        // Skip when neither is set (DefaultPreset is the live fallback
        // and isn't a saved preset the user can edit).
        var preset = PresetOverride ?? ActivePreset;
        if (preset == null) return;

        var cam = Common.CameraManager->worldCamera;
        if (cam == null) return;

        // Compare camera state against preset's recorded values.
        // Only Zoom/FoV are auto-tracked here — Tilt/LookAtHeight
        // get rewritten by CameraDynamics every frame from input
        // smoothing state, so they'd appear to "change" constantly
        // and trigger save churn. Height/Side are already per-preset
        // and updated explicitly by the wheel handlers.
        const float epsZoom = 0.01f;
        const float epsFoV  = 0.001f;

        bool dirty = false;
        if (MathF.Abs(cam->currentZoom - preset.StartZoom) > epsZoom)
        {
            preset.StartZoom = cam->currentZoom;
            // Auto-set the toggle so re-applying the preset uses
            // this zoom (otherwise StartZoom is ignored on apply).
            preset.UseStartZoom = true;
            dirty = true;
        }
        if (MathF.Abs(cam->currentFoV - preset.StartFoV) > epsFoV)
        {
            preset.StartFoV = cam->currentFoV;
            preset.UseStartFoV = true;
            dirty = true;
        }

        if (dirty) _autoSaveDirty = true;

        // Throttled save — at most twice a second. Avoids thrash
        // during a continuous wheel-zoom burst.
        if (_autoSaveDirty)
        {
            double now = NowSec();
            if (now - _lastAutoSaveAt > 0.5)
            {
                _lastAutoSaveAt = now;
                _autoSaveDirty = false;
                try { noWickyXIV.Config.Save(); } catch { }
            }
        }
    }

    public static void DisableCameraPresets()
    {
        ActivePreset = null;
        PresetOverride = null;
    }
}
