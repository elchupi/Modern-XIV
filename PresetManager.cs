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
            // Set ActivePreset (green = auto-active) rather than
            // PresetOverride (blue = manual override). The override
            // semantics suppress condition-driven swaps; on plugin
            // start the user wants conditions to keep working AND
            // the last preset to be the seed in the active slot.
            ApplyPreset(preset, isLoggingIn: true);
            ActivePreset = preset;
            PresetOverride = null;
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
    // Per-preset live height nudge — also lerped during transitions so
    // it doesn't jump when the active preset flips. Without this, a
    // preset swap would snap LiveHeightOffset to the new preset's value
    // instantly while the camera-position smoothing chased it over
    // ~500 ms, producing the "camera starts high and slowly drifts
    // down" / "dips below floor and floats back up" glitch on
    // default→combat / NPC-dialogue swaps.
    public static float EffectiveLiveHeightOffset  { get; private set; }

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
    private static float  _txStartLive;
    private static float  _txStartMinVRot;
    private static float  _txStartMaxVRot;
    private static float  _txStartMinZoom;
    private static float  _txStartMaxZoom;
    private static float  _txStartMinFoV;
    private static float  _txStartMaxFoV;
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
        EffectiveLiveHeightOffset   = _txTarget.LiveHeightOffset;
        EffectiveTilt               = _txTarget.Tilt;
        try
        {
            var cam = Common.CameraManager->worldCamera;
            if (cam != null)
            {
                cam->currentZoom = _txTargetZoom;
                cam->currentFoV  = _txTargetFoV;
                cam->minZoom      = _txTarget.MinZoom;
                cam->maxZoom      = _txTarget.MaxZoom;
                cam->minFoV       = _txTarget.MinFoV;
                cam->maxFoV       = _txTarget.MaxFoV;
                cam->minVRotation = _txTarget.MinVRotation;
                cam->maxVRotation = _txTarget.MaxVRotation;
            }
        }
        catch { /* defensive */ }
        try { CameraDynamics.SnapOffsets(); } catch { /* defensive */ }
        // Apply any pending Dynamics now (cancellation collapses the
        // transition to its target, including feature toggles).
        if (_pendingDynamicsApply != null)
        {
            try
            {
                if (_pendingDynamicsApply.Dynamics != null)
                    PresetDynamicsState.ApplyStateToConfig(_pendingDynamicsApply.Dynamics, noWickyXIV.Config);
            }
            catch { /* defensive */ }
            _pendingDynamicsApply = null;
        }
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

    // The preset whose Dynamics state corresponds to the values
    // currently sitting in noWickyXIV.Config. On preset swap we
    // snapshot Config back into THIS preset's Dynamics before
    // applying the new preset, so any UI edits the user made while
    // it was active get persisted.
    private static CameraConfigPreset _liveDynamicsPreset;

    // Push the current global Config's per-preset fields into the
    // active preset's Dynamics state. Called from the UI helpers on
    // every slider / checkbox edit so the active preset captures
    // the change immediately — without this, edits live only in
    // Config until the next preset switch and get stomped if Config
    // is overwritten (e.g. clicking the active preset again, plugin
    // reload, etc.).
    public static void SyncConfigToActivePresetDynamics()
    {
        try
        {
            var preset = _liveDynamicsPreset;
            if (preset == null) return;
            preset.Dynamics ??= new PresetDynamicsState();
            PresetDynamicsState.ApplyConfigToState(noWickyXIV.Config, preset.Dynamics);
        }
        catch { /* defensive */ }
    }

    // When a transitioned preset switch is in flight, the incoming
    // preset's Dynamics is parked here until the transition completes.
    // Applying it at frame 0 caused features like PitchTilt to
    // instantly disable (else branch undoing accumulated contribution),
    // dropping the camera by the cached offset — the "dip on
    // transition start" the user reported. Applied in Update() the
    // moment the position lerp finishes.
    private static CameraConfigPreset _pendingDynamicsApply;

    public static unsafe void ApplyPreset(CameraConfigPreset preset, bool isLoggingIn, bool transition)
    {
        if (preset == null) return;

        var camera = Common.CameraManager->worldCamera;
        if (camera == null) return;

        // ---- Per-preset Dynamics swap ----
        // 1. Snapshot live Config into the OUTGOING preset's Dynamics
        //    (captures any UI edits the user made while it was active).
        // 2. Lazy-migrate INCOMING preset's Dynamics from current Config
        //    if it's null (preset saved before this field existed).
        // 3. Apply incoming preset's Dynamics — IMMEDIATELY for snap
        //    swaps and login, but DEFERRED to transition end for
        //    transitioned swaps (so feature toggles like PitchTilt
        //    don't flip mid-transition and drop the camera by the
        //    accumulated contribution).
        bool willTransition = transition && !isLoggingIn
            && noWickyXIV.Config.PresetTransitionSeconds > 0.06f;
        if (!isLoggingIn)
        {
            try
            {
                if (_liveDynamicsPreset != null && _liveDynamicsPreset != preset)
                {
                    _liveDynamicsPreset.Dynamics ??= new PresetDynamicsState();
                    PresetDynamicsState.ApplyConfigToState(noWickyXIV.Config, _liveDynamicsPreset.Dynamics);
                }
                preset.Dynamics ??= PresetDynamicsState.SnapshotFrom(noWickyXIV.Config);
                if (willTransition)
                {
                    // Defer apply — Update() will write incoming
                    // Dynamics to Config when the position lerp
                    // completes. Until then, Config keeps the
                    // outgoing preset's settings so PitchTilt /
                    // PositionFloat / etc. keep their accumulated
                    // contributions across the transition.
                    _pendingDynamicsApply = preset;
                }
                else
                {
                    PresetDynamicsState.ApplyStateToConfig(preset.Dynamics, noWickyXIV.Config);
                    _pendingDynamicsApply = null;
                }
            }
            catch { /* defensive */ }
        }
        else
        {
            // On login, lazy-migrate but DO NOT overwrite Config
            // (keeps the user's saved global state as the source of
            // truth for the very first preset to activate).
            try { preset.Dynamics ??= PresetDynamicsState.SnapshotFrom(noWickyXIV.Config); }
            catch { /* defensive */ }
        }
        _liveDynamicsPreset = preset;

        // ---- Invariants snap immediately ----
        // FoVDelta only affects user wheel input, no visible motion.
        // Zoom/FoV/VRotation bounds DO NOT snap — they lerp during a
        // transition (see below). Snapping them clamped currentZoom
        // and currentFoV on frame 0 (engine clips to new bounds the
        // same frame the bound shrinks), producing the "initial
        // height shift burst" the user reported. Lerping bounds lets
        // the engine clamp gradually as bounds tighten.
        Game.FoVDelta = preset.FoVDelta;

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
            // Tell CameraDynamics the lerp is about to start so it can
            // cleanly remove its additive contributions (PitchTilt,
            // PositionFloat) from the camera fields. Without this, those
            // accumulated offsets distort the lerp target — visible as
            // the camera dipping below the floor or starting at a high
            // angle and slowly drifting down on default→combat /
            // NPC-dialogue swaps.
            try { CameraDynamics.OnPresetTransitionStart(camera); } catch { }

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
            _txStartLive   = EffectiveLiveHeightOffset;
            _txStartZoom   = camera->currentZoom;
            _txStartFoV    = camera->currentFoV;
            _txStartMinVRot = camera->minVRotation;
            _txStartMaxVRot = camera->maxVRotation;
            _txStartMinZoom = camera->minZoom;
            _txStartMaxZoom = camera->maxZoom;
            _txStartMinFoV  = camera->minFoV;
            _txStartMaxFoV  = camera->maxFoV;
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
            camera->minZoom            = preset.MinZoom;
            camera->maxZoom            = preset.MaxZoom;
            camera->minFoV             = preset.MinFoV;
            camera->maxFoV             = preset.MaxFoV;
            camera->currentZoom        = targetZoom;
            camera->currentFoV         = targetFoV;
            camera->tilt               = preset.Tilt;
            camera->lookAtHeightOffset = preset.LookAtHeightOffset;
            camera->minVRotation       = preset.MinVRotation;
            camera->maxVRotation       = preset.MaxVRotation;
            EffectiveTilt               = preset.Tilt;
            EffectiveLookAtHeightOffset = preset.LookAtHeightOffset;
            EffectiveHeightOffset       = preset.HeightOffset;
            EffectiveSideOffset         = preset.SideOffset;
            EffectiveLiveHeightOffset   = preset.LiveHeightOffset;
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

        // Persist the picked preset's name so RestoreLastActivePreset
        // can re-apply it next session even when the user never
        // manually clicks a preset (auto-condition picks were never
        // saved before, leaving LastActivePresetName empty and the
        // restore path falling back to a default that didn't match
        // the user's last-seen height).
        try
        {
            if (!string.IsNullOrEmpty(preset.Name)
                && noWickyXIV.Config.LastActivePresetName != preset.Name)
            {
                noWickyXIV.Config.LastActivePresetName = preset.Name;
                noWickyXIV.Config.Save();
            }
        }
        catch { /* defensive */ }
    }

    public static unsafe void Update()
    {
        if (_txActive && _txTarget != null)
        {
            // Smoothstep all transition axes toward the target.
            float dur = MathF.Max(0.05f, noWickyXIV.Config.PresetTransitionSeconds);
            float t = (float)((NowSec() - _txStartT) / dur);
            bool justEnded = false;
            if (t >= 1f) { t = 1f; _txActive = false; justEnded = true; }
            float s = Smoothstep(t);

            // Transition just completed — apply the pending Dynamics
            // (deferred from ApplyPreset so feature toggles don't flip
            // mid-transition and drop the camera). Position offsets
            // have settled at the new preset's values, so any feature-
            // toggle change here lands on the already-correct camera
            // pose without a visible jump.
            if (justEnded && _pendingDynamicsApply != null)
            {
                try
                {
                    var pending = _pendingDynamicsApply;
                    _pendingDynamicsApply = null;
                    if (pending.Dynamics != null)
                        PresetDynamicsState.ApplyStateToConfig(pending.Dynamics, noWickyXIV.Config);
                }
                catch { /* defensive */ }
            }

            // All four position axes (LookAt height, height, side,
            // live height) share the SAME smoothstep curve so they
            // start, accelerate, and finish in lockstep — the
            // earlier cubic-on-LookAt curve made the look-at lag
            // ~70% of the transition, which the user perceived as
            // "horizontal axis adjusts first, then vertical, then
            // height" — a staggered, jarring sequence. Unified
            // smoothstep produces a single smooth pose change.
            EffectiveLookAtHeightOffset = Lerp(_txStartLookAt, _txTarget.LookAtHeightOffset, s);
            EffectiveHeightOffset       = Lerp(_txStartHeight, _txTarget.HeightOffset,       s);
            EffectiveSideOffset         = Lerp(_txStartSide,   _txTarget.SideOffset,         s);
            EffectiveLiveHeightOffset   = Lerp(_txStartLive,   _txTarget.LiveHeightOffset,   s);
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
                    // Lerp ALL bounds (zoom, FoV, VRotation) so a
                    // tighter new preset doesn't snap-clamp current*
                    // values on the first transition frame. Engine
                    // clamps each frame to whatever the bounds are
                    // now, so the visible value eases instead of
                    // jolting at t=0 (the "initial burst").
                    cam->minZoom      = Lerp(_txStartMinZoom, _txTarget.MinZoom,      s);
                    cam->maxZoom      = Lerp(_txStartMaxZoom, _txTarget.MaxZoom,      s);
                    cam->minFoV       = Lerp(_txStartMinFoV,  _txTarget.MinFoV,       s);
                    cam->maxFoV       = Lerp(_txStartMaxFoV,  _txTarget.MaxFoV,       s);
                    cam->minVRotation = Lerp(_txStartMinVRot, _txTarget.MinVRotation, s);
                    cam->maxVRotation = Lerp(_txStartMaxVRot, _txTarget.MaxVRotation, s);
                    // DO NOT write cam->lookAtHeightOffset every frame
                    // here. PitchTilt uses subtract-prev-add on that
                    // field and relies on it PERSISTING between the
                    // engine's UpdateLookAtHeightOffset calls (which
                    // are intentionally NOT every frame — that's why
                    // PitchTilt's accumulator pattern works at all).
                    // Writing every frame forces PitchTilt's
                    // accumulated contribution to vanish, which on
                    // frame 0 of a transition dropped the camera by
                    // the PitchTilt amount — visible as "camera
                    // pushed down at start of transition".
                    // UpdateLookAtHeightOffsetDetour already writes
                    // EffectiveLookAtHeightOffset whenever the engine
                    // refreshes the field, which is sufficient.
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
                EffectiveLiveHeightOffset   = preset.LiveHeightOffset;
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

        // Mark dirty for the global debounced-save tick. The shared
        // debouncer batches all profile edits across the session into
        // a single write once the user is idle.
        if (dirty) noWickyXIV.Config.SaveDebounced();
    }

    public static void DisableCameraPresets()
    {
        ActivePreset = null;
        PresetOverride = null;
    }
}
