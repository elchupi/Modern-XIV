using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Hypostasis.Game;
using Hypostasis.Game.Structures;

namespace noWickyXIV;

// Per-frame writer for the dynamic-feel layer. Math ports verbatim from
// Wicked's PlayerCameraPatch / WickedTPS where indicated; gating + struct
// field choices match FFXIV's GameCamera (Hypostasis/Game/Structures/GameCamera.cs).
//
// Runs from noWickyXIV.Update() (Framework.Update). All writes happen AFTER
// the game's per-frame camera update, so we get the "last word" on tilt /
// lookAtHeightOffset. PositionFloat is exposed via GetPositionFloatOffset()
// so Game.GetCameraPositionDetour can add it during the position-compute call.
public static unsafe class CameraDynamics
{
    // --- RollTilt state ---
    private static float _rollCurrent;          // current roll in degrees
    private static float _rollSmoothedYawVel;
    private static float _rollPrevYaw;
    private static bool  _rollInit;

    // --- PitchTilt state ---
    private static float _pitchTiltCurrent;
    // Tracks the offset we LAST WROTE into cam->lookAtHeightOffset, so we
    // can subtract it before adding the new value. Without this, += each
    // frame produces a runaway accumulator (field grows unboundedly until
    // the game's own UpdateLookAtHeightOffset call resets it — which
    // happens infrequently). The accumulator was the actual cause of the
    // 'camera stuck looking up' bug + the 'persists after disable' bug.
    private static float _pitchTiltLastApplied;

    // --- PositionFloat state ---
    // Offset is applied to the camera's LOOK-AT point (cam->lookAtX/Y/Z),
    // not the camera position. Offsetting only camera position leaves the
    // look-at glued to the player → camera angle silently re-centers the
    // character. Offsetting look-at instead lets the character drift
    // off-center within the frame as they strafe — the actual "discreet
    // float" feel.
    //
    // Subtract-previous-then-add pattern (same as PitchTilt) so we don't
    // accumulate frame-over-frame.
    private static Vector3 _floatOffset = Vector3.Zero;
    private static Vector3 _floatVelocity = Vector3.Zero;
    private static Vector3 _lastPlayerPos = Vector3.Zero;
    private static bool    _floatInit;
    private static Vector3 _floatLastApplied = Vector3.Zero;

    // --- ADS state ---
    private static float _adsBaseFoV;     // baseline captured on RMB press
    private static float _adsBaseZoom;
    private static bool  _adsActive;

    // --- Combat zoom state ---
    private static float _combatZoomBase;      // zoom captured on combat enter
    private static bool  _combatZoomActive;    // currently lerping to combat zoom
    private static bool  _combatZoomWasInCombat; // last-frame condition for edge detect

    // --- Auto-shoulder swap state ---
    private static float _shoulderStart;     // SideOffset value at lerp start
    private static float _shoulderTarget;    // SideOffset value at lerp end (= -start)
    private static float _shoulderDisplay;   // currently-applied SideOffset (lerped)
    private static float _shoulderLerpT;     // 0..1 progress
    private static bool  _shoulderLerping;
    private static float _lastSwapCheckTime;

    // --- SwivelOnMove state ---
    private static Vector3 _swivelLastPos;
    private static float   _swivelMoveTimer;
    private static bool    _swivelInit;

    // --- Sensitivity scaling state ---
    private static float _sensLastHrot;
    private static float _sensLastVrot;
    private static bool  _sensInit;

    // --- Always-on mouselook state (FPS-style camera lock) ---
    // Default = TRUE (cursor released, UI mode). This means even if the
    // user has EnableMouseLookAlways=true saved in their config,
    // mouselook does NOT engage on plugin start — they have to press
    // the CursorReleaseHotkey (default F7) to grab the cursor first.
    // Prevents the unwanted "camera flinch on plugin load" where the
    // first mouse movement after startup yanks the camera somewhere.
    private static bool    _cursorReleased = true;
    private static bool    _mouseLookInit;
    // One-frame guard after init/toggle: discard the next frame's
    // delta and just refresh prev-pos. Without this, cursor-pos
    // updates between Plugin.Update's SetCursorPos call and ImGui's
    // io.MousePos snapshot could leak a single big phantom delta
    // (oldPos − center) on the frame right after re-enable, which
    // jumped the camera angle instead of "just hiding the mouse".
    private static bool    _mouseLookSkipNextDelta;
    private static Vector2 _mouseLookPrevPos;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Real-time RMB read. ImGui IO MouseDown is only reliable when an ImGui
    // window is forcing the input pump; from plain Framework.Update it stays
    // 0 unless a panel is open. Win32 read is OS-level → always live.
    private const int VK_RBUTTON = 0x02;
    private static bool RmbHeldNow => (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    // Track our own hide state so we don't pump ShowCursor every frame
    // (the Win32 counter keeps state across calls; we manage it idempotently).
    private static bool _osCursorHidden;

    private static void HideOsCursor()
    {
        if (_osCursorHidden) return;
        // ShowCursor returns the new counter. Cursor is shown when counter >= 0;
        // hidden when counter < 0. Decrement until hidden.
        try
        {
            int safety = 16;
            while (ShowCursor(false) >= 0 && --safety > 0) { }
            _osCursorHidden = true;
        }
        catch { /* defensive */ }
    }

    private static void ShowOsCursor()
    {
        if (!_osCursorHidden) return;
        try
        {
            int safety = 16;
            while (ShowCursor(true) < 0 && --safety > 0) { }
            _osCursorHidden = false;
        }
        catch { /* defensive */ }
    }

    /// <summary>Toggle by F7 (or whatever the user binds via Configuration.CursorReleaseHotkey).</summary>
    public static void ToggleCursorRelease()
    {
        _cursorReleased = !_cursorReleased;
        // Re-init delta tracking on toggle so we don't apply a phantom delta
        // accumulated during the released period. Also burn the next
        // frame's delta — see _mouseLookSkipNextDelta comment.
        _mouseLookInit = false;
        _mouseLookSkipNextDelta = true;
    }

    public static bool IsMouseLookActive
        => noWickyXIV.Config.EnableMouseLookAlways && !_cursorReleased;

    private static bool _instantModeNoteLogged;

    public static Vector3 GetPositionFloatOffset() => _floatOffset;

    /// <summary>Current roll value in radians, ready to write into cam->tilt
    /// from Game.SetCameraLookAtDetour (inline). Direct writes to cam->tilt
    /// from Framework.Update get overwritten by the game's per-frame camera
    /// setup before render — that's why the camera tilt was invisible. The
    /// detour pattern was the working version (commit 1326cbb) before its
    /// accidental revert as collateral on a PositionFloat fix.</summary>
    public static float GetCurrentRollRadians() => _rollCurrent * (MathF.PI / 180f);

    /// <summary>Current character-roll angle in degrees, consumed by
    /// CharacterRollHook to bake the lean into the per-frame transform
    /// matrix. Separate from camera RollTilt's _rollCurrent so the
    /// two roll effects can be tuned independently.</summary>
    public static float GetCharacterRollCurrentDegrees() => _charRollCurrent;

    // Returns the SideOffset value Game.GetCameraPositionDetour should apply
    // for this frame. During an auto-swap lerp, this returns the lerped
    // _shoulderDisplay; otherwise the preset's static SideOffset.
    public static float GetActiveSideOffset(float presetSideOffset)
        => _shoulderLerping ? _shoulderDisplay : presetSideOffset;

    public static void Update()
    {
        if (FreeCam.Enabled) { ResetState(); return; }

        var cm = Common.CameraManager;
        if (cm == null) return;
        var cam = cm->worldCamera;
        if (cam == null) return;

        float dt;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { dt = 0.016f; }
        if (dt <= 0f) return;
        if (dt > 0.1f) dt = 0.1f;  // cap after long stalls (loading screens etc.)

        bool tps = cam->mode == 1;

        // Close-zoom pitch cap. Tightens the camera's minVRotation
        // (and clamps currentVRotation to match) when zoom is below
        // the configured threshold, preventing the camera from ending
        // up overhead-looking-down at extreme close distance. The
        // cap relaxes back to the preset's normal MinVRotation as
        // the user zooms out past the threshold.
        UpdateCloseZoomPitchCap(cam);

        // Sensitivity FIRST so subsequent writes (Swivel, etc.) operate on
        // the corrected H/V rotation and don't get inadvertently scaled by
        // the next-frame delta-replay.
        UpdateSensitivity(cam, tps);

        // Mouselook BEFORE Swivel so its yaw write is the final word the
        // user feels, but after Sensitivity so it operates on un-scaled deltas.
        UpdateMouseLook(cam, tps);

        // Yaw-velocity tracker runs BEFORE both roll features so they
        // share the same smoothed input. Without this split, CharacterRoll
        // would see _rollSmoothedYawVel=0 whenever camera RollTilt is
        // disabled (the velocity tracker used to live inside RollTilt).
        UpdateYawVelocity(cam, tps, dt);
        UpdateRollTilt(cam, tps, dt);
        UpdateCharacterRoll(cam, tps, dt);
        UpdatePitchTilt(cam, tps, dt);
        UpdatePositionFloat(cam, dt);
        UpdateAds(cam, tps, dt);
        UpdateCombatZoom(cam, tps, dt);
        UpdateAutoShoulderSwap(cam, tps, dt);
        UpdateSwivelOnMove(cam, tps, dt);
        // Input smoothing runs LAST so it observes the final-state
        // currentZoom / currentHRotation / currentVRotation produced
        // by every other writer this frame, exp-lerps toward those
        // targets, and writes the smoothed values back.
        UpdateInputSmoothing(cam, tps, dt);
        // Position-offset smoothing — lerps Display*Offset getters
        // toward PresetManager.Effective* + Config.GlobalHeightOffset.
        // The Game.cs camera-position detour reads these getters.
        UpdateOffsetSmoothing(dt);
        UpdateInstantModeNote();
    }

    // ---- Sensitivity + Y-inversion: Phase E ----
    // Delta-replay approach: track previous-frame H/V rotations, compute
    // this-frame delta, scale by user multiplier, write the scaled value
    // back. Identity at multiplier=1 + InvertY=false.
    //
    // Pitch cap that activates when currentZoom is below the
    // configured threshold. The default MinVRotation is around -85°
    // (camera can pitch nearly straight down) which combined with
    // close zoom puts the camera above the player looking at the
    // ground. We tighten the floor to a much shallower angle
    // (default ~-23°) when zoom is close. As the user zooms out
    // past the threshold the cap relaxes back to the preset's
    // configured MinVRotation. Currently-applied currentVRotation
    // is also pushed up to the new floor so the user doesn't have
    // to manually un-pitch after the cap engages.
    // Smoothed pitch-floor state. Lerps toward the active target
    // (preset's MinVRotation outside the cap zone, or the cap floor
    // when inside) over CAP_FLOOR_LERP_RATE so engaging/leaving the
    // cap zone — including via a preset transition that crosses the
    // zoom threshold mid-flight — doesn't snap the camera.
    private static float _smoothedPitchFloor;
    private static bool  _smoothedPitchFloorInit;
    private const float  CAP_FLOOR_LERP_RATE = 4f; // ~175 ms halflife

    private static void UpdateCloseZoomPitchCap(GameCamera* cam)
    {
        var cfg = noWickyXIV.Config;
        var preset = PresetManager.CurrentPreset;
        if (!cfg.EnableCloseZoomPitchCap || preset == null)
        {
            _smoothedPitchFloorInit = false;
            return;
        }

        // Pick the target floor: the cap floor when zoomed past the
        // threshold, otherwise the preset's normal MinVRotation. The
        // cap only ever tightens — if the preset's MinVRotation is
        // already above the cap floor, preset wins.
        bool inCap = cam->currentZoom < cfg.CloseZoomPitchCapZoom;
        float target = preset.MinVRotation;
        if (inCap)
        {
            float capFloor = cfg.CloseZoomPitchCapMinRad;
            if (preset.MinVRotation > capFloor) capFloor = preset.MinVRotation;
            target = capFloor;
        }

        if (!_smoothedPitchFloorInit)
        {
            _smoothedPitchFloor = target;
            _smoothedPitchFloorInit = true;
        }
        else
        {
            float dt;
            try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { dt = 0.016f; }
            if (dt <= 0f) dt = 0.016f;
            float k = 1f - MathF.Exp(-CAP_FLOOR_LERP_RATE * dt);
            _smoothedPitchFloor += (target - _smoothedPitchFloor) * k;
        }

        // Set minVRotation to the smoothed floor so the engine's own
        // clamp logic enforces it on user input. Don't snap
        // currentVRotation — the engine will clamp it naturally as
        // minVRotation moves up, producing a smooth glide instead of
        // the visible "spring" the immediate-snap version produced
        // (which fired when a preset transition crossed the cap zoom
        // threshold mid-flight).
        cam->minVRotation = _smoothedPitchFloor;
    }

    // Single multiplier (Config.MouseSensitivityMul) used for ALL input —
    // gamepad-vs-mouse differentiation requires a per-frame source check
    // that's deferred. Y-inversion uses InvertMouseY (sole vertical knob).
    private static void UpdateSensitivity(GameCamera* cam, bool tps)
    {
        if (!tps) { _sensInit = false; return; }

        float curH = cam->currentHRotation;
        float curV = cam->currentVRotation;

        if (!_sensInit)
        {
            _sensLastHrot = curH;
            _sensLastVrot = curV;
            _sensInit = true;
            return;
        }

        float mul = MathF.Max(0.1f, noWickyXIV.Config.MouseSensitivityMul);
        bool invertY = noWickyXIV.Config.InvertMouseY;
        bool identity = MathF.Abs(mul - 1f) < 0.001f && !invertY;

        if (identity)
        {
            _sensLastHrot = curH;
            _sensLastVrot = curV;
            return;
        }

        float dH = AngleDelta(_sensLastHrot, curH);
        float dV = curV - _sensLastVrot;
        if (invertY) dV = -dV;

        float newH = _sensLastHrot + dH * mul;
        // Wrap to [-π, π] like cam->currentHRotation expects.
        while (newH >  MathF.PI) newH -= 2f * MathF.PI;
        while (newH < -MathF.PI) newH += 2f * MathF.PI;

        float newV = _sensLastVrot + dV * mul;
        // Clamp to camera's V range.
        if (newV < cam->minVRotation) newV = cam->minVRotation;
        if (newV > cam->maxVRotation) newV = cam->maxVRotation;

        cam->currentHRotation = newH;
        cam->currentVRotation = newV;

        _sensLastHrot = newH;
        _sensLastVrot = newV;
    }

    // ---- Camera position smoothing ----
    // Smoothed copies of HeightOffset / SideOffset / GlobalHeightOffset.
    // The Game.cs detour reads these via the Display* getters when
    // EnableCameraPositionSmoothing is on, so slider drags and
    // Ctrl+scroll height adjustments lerp into place instead of
    // snapping. PresetManager.ApplyPreset calls SnapOffsets() so a
    // preset switch doesn't get caught in the lerp window.
    private static float _smoothedHeightOffset;
    private static float _smoothedSideOffset;
    private static float _smoothedGlobalHeightOffset;
    private static bool  _smoothedOffsetsInit;

    // Position smoothing follows EITHER toggle — its dedicated
    // checkbox OR the broader "Smooth zoom / yaw / pitch input" one
    // — so users who only flip the more obvious input-smoothing
    // toggle still get smoothed offsets.
    private static bool PositionSmoothingActive
        => noWickyXIV.Config.EnableCameraPositionSmoothing
        || noWickyXIV.Config.EnableInputSmoothing;

    public static float DisplayHeightOffset
        => PositionSmoothingActive
            ? _smoothedHeightOffset
            : PresetManager.EffectiveHeightOffset;

    public static float DisplaySideOffset
        => PositionSmoothingActive
            ? _smoothedSideOffset
            : PresetManager.EffectiveSideOffset;

    public static float DisplayGlobalHeightOffset
        => PositionSmoothingActive
            ? _smoothedGlobalHeightOffset
            : LiveHeightTarget;

    // The "live height adjustment" is now per-preset (preset.LiveHeightOffset)
    // so user tuning travels with the preset across condition swaps
    // instead of stacking globally. Falls back to legacy
    // Config.GlobalHeightOffset for the DefaultPreset (which has no
    // saved adjustment) so existing user setups don't lose their saved
    // tweak when this change ships.
    private static float LiveHeightTarget
    {
        get
        {
            var p = PresetManager.CurrentPreset;
            if (p != null && p != PresetManager.DefaultPreset) return p.LiveHeightOffset;
            return noWickyXIV.Config.GlobalHeightOffset;
        }
    }

    // Snaps every smoothed offset to its current target. Called by
    // PresetManager.ApplyPreset so a preset switch doesn't visually
    // ride a lerp out of the previous preset's offsets.
    public static void SnapOffsets()
    {
        _smoothedHeightOffset       = PresetManager.EffectiveHeightOffset;
        _smoothedSideOffset         = PresetManager.EffectiveSideOffset;
        _smoothedGlobalHeightOffset = LiveHeightTarget;
        _smoothedOffsetsInit = true;
    }

    private static void UpdateOffsetSmoothing(float dt)
    {
        if (!PositionSmoothingActive)
        {
            _smoothedOffsetsInit = false;
            return;
        }

        if (!_smoothedOffsetsInit)
        {
            SnapOffsets();
            return;
        }

        // Use the dedicated CameraPositionSmoothingRate when its
        // toggle is on; otherwise fall back to the input rotate rate
        // so the offset feel matches the rest of the smoothing the
        // user already enabled.
        float rate = noWickyXIV.Config.EnableCameraPositionSmoothing
            ? MathF.Max(0.5f, noWickyXIV.Config.CameraPositionSmoothingRate)
            : MathF.Max(0.5f, noWickyXIV.Config.InputSmoothingRotateRate);
        float k = 1f - MathF.Exp(-rate * dt);

        float hT = PresetManager.EffectiveHeightOffset;
        float sT = PresetManager.EffectiveSideOffset;
        float gT = LiveHeightTarget;

        _smoothedHeightOffset       += (hT - _smoothedHeightOffset)       * k;
        _smoothedSideOffset         += (sT - _smoothedSideOffset)         * k;
        _smoothedGlobalHeightOffset += (gT - _smoothedGlobalHeightOffset) * k;

        // Snap-on-arrival so we don't asymptote forever.
        if (MathF.Abs(hT - _smoothedHeightOffset)       < 0.0005f) _smoothedHeightOffset       = hT;
        if (MathF.Abs(sT - _smoothedSideOffset)         < 0.0005f) _smoothedSideOffset         = sT;
        if (MathF.Abs(gT - _smoothedGlobalHeightOffset) < 0.0005f) _smoothedGlobalHeightOffset = gT;
    }

    // ---- Input smoothing: exp-lerp on zoom/yaw/pitch ----
    // Detects external writes by comparing the current camera value
    // against what we wrote on the previous frame. When they differ
    // (user scrolled the wheel, mouse-moved, or another writer like
    // CombatZoom changed it), update the per-axis target. The
    // smoothed value lerps toward target each frame at a config
    // rate. Yaw uses AngleDelta so wrap-around at ±π takes the
    // shortest arc.
    private static bool  _smoothInit;
    private static float _smoothZoomLast,  _smoothZoomTarget;
    private static float _smoothHrotLast,  _smoothHrotTarget;
    private static float _smoothVrotLast,  _smoothVrotTarget;

    private static void UpdateInputSmoothing(GameCamera* cam, bool tps, float dt)
    {
        if (!noWickyXIV.Config.EnableInputSmoothing || !tps)
        {
            _smoothInit = false;
            return;
        }

        float curZoom = cam->currentZoom;
        float curH    = cam->currentHRotation;
        float curV    = cam->currentVRotation;

        if (!_smoothInit)
        {
            _smoothZoomLast = _smoothZoomTarget = curZoom;
            _smoothHrotLast = _smoothHrotTarget = curH;
            _smoothVrotLast = _smoothVrotTarget = curV;
            _smoothInit = true;
            return;
        }

        // ROTATION smoothing is bypassed while RMB is held — the game's
        // native RMB-drag does its own delta accumulation per poll;
        // layering our lerp on top makes it feel sticky/jittery.
        bool rmbHeld = RmbHeldNow;
        // ZOOM smoothing is bypassed while another writer is actively
        // driving currentZoom: CombatZoom, ADS, or a preset
        // transition. Without these bypasses, two writers compete on
        // the same value each frame — they both write currentZoom,
        // each smoothing the other's output, producing the stepped
        // / jerky feel during preset swaps. While bypassed the
        // smoother just tracks the engine value so it can resume
        // cleanly when the other writer releases.
        bool zoomBypassed = _combatZoomActive || _adsActive
                         || PresetManager.IsTransitionActive;

        if (!zoomBypassed
            && MathF.Abs(curZoom - _smoothZoomLast) > 0.0005f)
            _smoothZoomTarget = curZoom;
        if (MathF.Abs(AngleDelta(_smoothHrotLast, curH)) > 0.00005f)
            _smoothHrotTarget = curH;
        if (MathF.Abs(curV - _smoothVrotLast) > 0.00005f)
            _smoothVrotTarget = curV;

        float zoomRate = MathF.Max(0.5f, noWickyXIV.Config.InputSmoothingZoomRate);
        float rotRate  = MathF.Max(0.5f, noWickyXIV.Config.InputSmoothingRotateRate);
        float kZ = 1f - MathF.Exp(-zoomRate * dt);
        float kR = 1f - MathF.Exp(-rotRate  * dt);

        float newZoom;
        if (zoomBypassed)
        {
            // Track the engine's current value so the smoother resumes
            // cleanly when CombatZoom/ADS releases the value.
            newZoom = curZoom;
            _smoothZoomTarget = curZoom;
        }
        else
        {
            newZoom = _smoothZoomLast + (_smoothZoomTarget - _smoothZoomLast) * kZ;
            if (MathF.Abs(_smoothZoomTarget - newZoom) < 0.001f) newZoom = _smoothZoomTarget;
        }

        float newH, newV;
        if (rmbHeld)
        {
            newH = curH;
            newV = curV;
            _smoothHrotTarget = curH;
            _smoothVrotTarget = curV;
        }
        else
        {
            float dH = AngleDelta(_smoothHrotLast, _smoothHrotTarget);
            newH = _smoothHrotLast + dH * kR;
            while (newH >  MathF.PI) newH -= 2f * MathF.PI;
            while (newH < -MathF.PI) newH += 2f * MathF.PI;
            if (MathF.Abs(AngleDelta(newH, _smoothHrotTarget)) < 0.0005f) newH = _smoothHrotTarget;

            newV = _smoothVrotLast + (_smoothVrotTarget - _smoothVrotLast) * kR;
            if (MathF.Abs(_smoothVrotTarget - newV) < 0.0005f) newV = _smoothVrotTarget;
            if (newV < cam->minVRotation) newV = cam->minVRotation;
            if (newV > cam->maxVRotation) newV = cam->maxVRotation;
        }

        // Only write back what we actually computed (don't stomp on
        // CombatZoom/ADS's zoom or the engine's RMB-drag rotation).
        if (!zoomBypassed) cam->currentZoom = newZoom;
        if (!rmbHeld)
        {
            cam->currentHRotation = newH;
            cam->currentVRotation = newV;
        }

        _smoothZoomLast = newZoom;
        _smoothHrotLast = newH;
        _smoothVrotLast = newV;
    }

    // ---- Always-on mouselook (FPS-style camera lock) ----
    // When enabled and cursor is "grabbed" (default; F7 toggles to release):
    //   - Read mouse delta vs our tracked previous position
    //   - Apply to currentHRotation (yaw) + currentVRotation (pitch)
    //   - Recenter cursor via Win32 SetCursorPos so the cursor never reaches
    //     a screen edge. We track the recentered position internally so the
    //     next frame's delta is user-input only (not our own recenter move).
    //
    // Bypassed while RMB is held — let FFXIV's own mouselook take over so
    // we don't double-rotate. Bypassed when an ImGui window has captured
    // the mouse so the user can still click panels.
    private static void UpdateMouseLook(GameCamera* cam, bool tps)
    {
        if (!noWickyXIV.Config.EnableMouseLookAlways || !tps || _cursorReleased)
        {
            _mouseLookInit = false;
            _mouseLookSkipNextDelta = true; // re-enable should burn the
                                             // first delta, regardless
                                             // of which path turned us off.
            ShowOsCursor();
            return;
        }

        ImGuiIOPtr io;
        try { io = ImGui.GetIO(); } catch { ShowOsCursor(); return; }

        // Don't fight the game's own RMB-held mouselook — skip OUR drive but
        // KEEP the cursor hidden. Prior code called ShowOsCursor() here which
        // produced a brief cursor flash whenever the user pressed RMB during
        // mouselook mode. We only need to stop our delta application; the
        // cursor stays hidden because the user is still in mouselook intent.
        bool rmbHeld = RmbHeldNow;
        if (rmbHeld) { _mouseLookInit = false; HideOsCursor(); return; }

        // Don't fight ImGui — if a panel captured the mouse, leave it alone.
        bool wantCaptureMouse;
        try { wantCaptureMouse = io.WantCaptureMouse; } catch { wantCaptureMouse = false; }
        if (wantCaptureMouse) { _mouseLookInit = false; ShowOsCursor(); return; }

        // Past all the bail-outs — we're in mouselook. Hide the cursor.
        HideOsCursor();

        Vector2 curPos;
        try { curPos = io.MousePos; } catch { return; }

        if (!_mouseLookInit)
        {
            _mouseLookPrevPos = curPos;
            _mouseLookInit = true;
        }
        else if (_mouseLookSkipNextDelta)
        {
            // Burn the first post-init delta — see field comment.
            _mouseLookPrevPos = curPos;
            _mouseLookSkipNextDelta = false;
        }
        else
        {
            var delta = curPos - _mouseLookPrevPos;
            if (MathF.Abs(delta.X) > 0.01f || MathF.Abs(delta.Y) > 0.01f)
            {
                float sens = MathF.Max(0.0001f, noWickyXIV.Config.MouseLookSensitivity);
                float xSign = noWickyXIV.Config.MouseLookInvertX ? +1f : -1f; // default negate: matches FFXIV's RMB-drag direction
                float ySign = noWickyXIV.Config.MouseLookInvertY ? +1f : -1f;

                // FFXIV: positive currentVRotation = looking up. Mouse Y up
                // = negative delta. So default mapping: mouse-up → look-up.
                float newH = cam->currentHRotation + delta.X * sens * xSign;
                while (newH >  MathF.PI) newH -= 2f * MathF.PI;
                while (newH < -MathF.PI) newH += 2f * MathF.PI;

                float newV = cam->currentVRotation + delta.Y * sens * ySign;
                if (newV < cam->minVRotation) newV = cam->minVRotation;
                if (newV > cam->maxVRotation) newV = cam->maxVRotation;

                cam->currentHRotation = newH;
                cam->currentVRotation = newV;
            }
            _mouseLookPrevPos = curPos;
        }

        // Recenter cursor for next-frame delta tracking. Sync our tracked
        // prev-pos to the new center so the next read sees pure user delta
        // (not the recenter move).
        if (noWickyXIV.Config.MouseLookCenterCursor)
        {
            try
            {
                var vp = ImGui.GetMainViewport();
                var center = new Vector2(vp.Pos.X + vp.Size.X * 0.5f,
                                         vp.Pos.Y + vp.Size.Y * 0.5f);
                if (SetCursorPos((int)center.X, (int)center.Y))
                    _mouseLookPrevPos = center;
            }
            catch { /* defensive */ }
        }
    }

    // ---- SwivelOnMove: auto-center camera behind player after delay ----
    // Mirrors PlayerCameraPatch.cs:606-620: when player moves, after a delay,
    // ease the camera yaw toward the player's facing direction at SwivelSpeed.
    private static void UpdateSwivelOnMove(GameCamera* cam, bool tps, float dt)
    {
        if (!noWickyXIV.Config.SwivelOnMove || !tps || FreeCam.Enabled)
        {
            _swivelMoveTimer = 0f;
            return;
        }

        // Defensive (same reasoning as UpdatePositionFloat): LocalPlayer
        // access can throw during transient client states even when the
        // wrapper isn't null. No-op for the frame and re-try next.
        Vector3 pos;
        float lpRotation;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            pos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);
            lpRotation = lp.Rotation;
        }
        catch { return; }

        if (!_swivelInit)
        {
            _swivelLastPos = pos;
            _swivelInit = true;
            return;
        }

        float speed = (pos - _swivelLastPos).Length() / dt; // m/s
        _swivelLastPos = pos;

        if (speed < noWickyXIV.Config.SwivelMoveThreshold)
        {
            _swivelMoveTimer = 0f;
            return;
        }

        _swivelMoveTimer += dt;
        if (_swivelMoveTimer < noWickyXIV.Config.SwivelDelay) return;

        // Camera yaw to look at player from behind: player.Rotation + π,
        // wrapped to [-π, π] like cam->currentHRotation.
        float targetYaw = lpRotation + MathF.PI;
        while (targetYaw >  MathF.PI) targetYaw -= 2f * MathF.PI;
        while (targetYaw < -MathF.PI) targetYaw += 2f * MathF.PI;

        float yawRateRad = noWickyXIV.Config.SwivelSpeed * (MathF.PI / 180f);
        float diff = AngleDelta(cam->currentHRotation, targetYaw);
        float maxStep = yawRateRad * dt;
        float step = diff;
        if (step >  maxStep) step =  maxStep;
        if (step < -maxStep) step = -maxStep;
        cam->currentHRotation += step;
    }

    // ---- Auto-shoulder swap: Phase C ----
    // State machine ports WickedTPS.cs:1956-1991 (probe + cubic-bezier lerp).
    // The actual collision PROBE is a TODO — the active FFXIVClientStructs
    // version's BGCollisionModule.Raycast signature isn't documented in the
    // shipped XML, so we ship the lerp infrastructure and a hook (TryProbeWall)
    // that returns false today. Wire the real raycast when the API shape is
    // confirmed; everything else (smooth lerp, frequency throttle, gating) is
    // already in place.
    private static void UpdateAutoShoulderSwap(GameCamera* cam, bool tps, float dt)
    {
        // Always advance an in-progress lerp, regardless of toggle state, so
        // toggling off mid-swap settles cleanly instead of snapping.
        if (_shoulderLerping)
        {
            float dur = MathF.Max(0.05f, noWickyXIV.Config.ShoulderLerpDuration);
            _shoulderLerpT += dt / dur;
            if (_shoulderLerpT >= 1f)
            {
                _shoulderLerpT = 1f;
                _shoulderDisplay = _shoulderTarget;
                _shoulderLerping = false;
                // Persist the swap on the active preset so subsequent renders
                // pick up the new shoulder via the normal path.
                var p = PresetManager.CurrentPreset;
                if (p != null) { p.SideOffset = _shoulderTarget; try { noWickyXIV.Config.Save(); } catch { } }
            }
            else
            {
                // Cubic-bezier easing (P0=0, P1=0.25, P2=1.0, P3=1) at t.
                float t = _shoulderLerpT;
                float eased = CubicBezier(t, 0.25f, 1.0f);
                _shoulderDisplay = _shoulderStart + (_shoulderTarget - _shoulderStart) * eased;
            }
        }

        if (!noWickyXIV.Config.EnableAutoShoulderSwap || !tps || FreeCam.Enabled) return;

        var preset = PresetManager.CurrentPreset;
        if (preset == null) return;
        float side = preset.SideOffset;
        if (MathF.Abs(side) < 0.01f) return;  // no shoulder offset → nothing to swap

        // Throttle probes
        float now = (float)(DateTime.UtcNow - _epoch).TotalSeconds;
        float interval = 1f / MathF.Max(1f, noWickyXIV.Config.ShoulderSwapCheckHz);
        if (now - _lastSwapCheckTime < interval) return;
        _lastSwapCheckTime = now;

        if (_shoulderLerping) return;  // already swapping; let it finish

        // Build the probe ray: from the camera position outward along the
        // shoulder-side direction (camera right, signed by current SideOffset).
        var origin = new Vector3(cam->viewX, cam->viewY, cam->viewZ);
        float yaw = cam->currentHRotation;
        // FFXIV camera right vector (yaw=0 looks toward -Z is typical; this
        // matches Cammy's existing SideOffset compute in Game.cs:101-103).
        const float halfPI = MathF.PI / 2f;
        float a = yaw - halfPI;
        var right = new Vector3(-MathF.Sin(a), 0f, -MathF.Cos(a));
        var dir = right * MathF.Sign(side);
        float maxDist = MathF.Abs(side) + noWickyXIV.Config.ShoulderSwapSafetyMargin;

        if (TryProbeWall(origin, dir, maxDist, out _))
        {
            // Hit on the active shoulder side → swap.
            _shoulderStart = _shoulderDisplay = side;
            _shoulderTarget = -side;
            _shoulderLerpT = 0f;
            _shoulderLerping = true;
        }
    }

    // STUB: world-collision raycast probe. Returns false today (no swap).
    // To wire: query Framework.Instance()->BGCollisionModule with the active
    // ClientStructs shape (likely Raycast(RaycastHit*, ulong, RaycastParams*)
    // per the XML doc convention seen on Collider subtypes).
    private static bool TryProbeWall(Vector3 origin, Vector3 direction, float maxDist, out float hitDist)
    {
        hitDist = maxDist;
        // TODO: replace with real raycast call once API shape verified.
        return false;
    }

    private static readonly DateTime _epoch = DateTime.UtcNow;

    // Cubic Bezier with P0=0, P3=1, given P1 / P2 (control y-coords). Approx
    // ease-out style. Matches the Wicked AutoShoulderSwap easing.
    private static float CubicBezier(float t, float p1, float p2)
    {
        float omt = 1f - t;
        return 3f * omt * omt * t * p1 + 3f * omt * t * t * p2 + t * t * t;
    }

    // ---- ADS zoom on RMB: ports PlayerCameraPatch.cs:683-698 ----
    // Held RMB → cam currentFoV/currentZoom lerp toward base/factor; release →
    // lerp back to baseline. Baseline captured on rising-edge press so we
    // restore exactly what the user had before the hold.
    private static void UpdateAds(GameCamera* cam, bool tps, float dt)
    {
        if (!noWickyXIV.Config.EnableAdsOnRmb || !tps)
        {
            _adsActive = false;
            return;
        }

        bool held = RmbHeldNow;

        float k = 1f - MathF.Exp(-noWickyXIV.Config.AdsTransitionSpeed * dt);
        float factor = MathF.Max(1.01f, noWickyXIV.Config.AdsZoomFactor);

        if (held)
        {
            if (!_adsActive)
            {
                _adsBaseFoV  = cam->currentFoV;
                _adsBaseZoom = cam->currentZoom;
                _adsActive = true;
            }
            float targetFoV  = MathF.Max(cam->minFoV,  _adsBaseFoV  / factor);
            float targetZoom = MathF.Max(cam->minZoom, _adsBaseZoom / factor);
            cam->currentFoV  = cam->currentFoV  + (targetFoV  - cam->currentFoV)  * k;
            cam->currentZoom = cam->currentZoom + (targetZoom - cam->currentZoom) * k;
        }
        else if (_adsActive)
        {
            cam->currentFoV  = cam->currentFoV  + (_adsBaseFoV  - cam->currentFoV)  * k;
            cam->currentZoom = cam->currentZoom + (_adsBaseZoom - cam->currentZoom) * k;
            // Settle: once we're close to baseline, stop overriding so the
            // user's normal zoom controls work again.
            if (MathF.Abs(cam->currentFoV - _adsBaseFoV) < 0.0005f &&
                MathF.Abs(cam->currentZoom - _adsBaseZoom) < 0.005f)
            {
                _adsActive = false;
            }
        }
    }

    // ---- Combat zoom: auto-pull-back when in combat ----
    // Watches ConditionFlag.InCombat. On rising edge, capture baseline zoom
    // and lerp toward CombatZoomDistance. On falling edge, lerp back to
    // baseline. Bypassed while ADS is active (RMB held) so ADS owns zoom.
    private static void UpdateCombatZoom(GameCamera* cam, bool tps, float dt)
    {
        // Hard-disabled paths: reset state. If we were mid-effect when
        // the toggle flipped off (e.g. zoom currently lerped to the
        // combat distance), SNAP BACK TO BASELINE so the camera
        // returns to where it was before combat zoom kicked in. Prior
        // to this, the early-return cleared _combatZoomActive but
        // never restored zoom — the camera sat at CombatZoomDistance
        // and looked like the feature was still on.
        if (!noWickyXIV.Config.EnableCombatZoom || !tps)
        {
            if (_combatZoomActive && cam != null)
            {
                cam->currentZoom = MathF.Max(cam->minZoom,
                                   MathF.Min(cam->maxZoom, _combatZoomBase));
            }
            _combatZoomActive = false;
            _combatZoomWasInCombat = false;
            return;
        }

        // ADS owns zoom briefly while RMB is held. SUSPEND combat zoom — no
        // writes — but DO NOT reset edge-detect state. Otherwise a transition
        // out of combat that happens during ADS gets dropped (rising-edge state
        // wiped, falling-edge lerp never runs, camera stuck at combat zoom).
        if (_adsActive) return;

        bool inCombat;
        try { inCombat = DalamudApi.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]; }
        catch { return; }

        // Rising edge: combat just started → capture baseline
        if (inCombat && !_combatZoomWasInCombat)
        {
            _combatZoomBase = cam->currentZoom;
            _combatZoomActive = true;
        }
        _combatZoomWasInCombat = inCombat;

        if (!_combatZoomActive) return;

        float k = 1f - MathF.Exp(-MathF.Max(0.1f, noWickyXIV.Config.CombatZoomTransitionSpeed) * dt);

        if (inCombat)
        {
            float target = MathF.Max(cam->minZoom,
                           MathF.Min(cam->maxZoom, noWickyXIV.Config.CombatZoomDistance));
            cam->currentZoom = cam->currentZoom + (target - cam->currentZoom) * k;
        }
        else
        {
            // Out of combat — lerp back to baseline
            cam->currentZoom = cam->currentZoom + (_combatZoomBase - cam->currentZoom) * k;
            if (MathF.Abs(cam->currentZoom - _combatZoomBase) < 0.01f)
            {
                _combatZoomActive = false;
            }
        }
    }

    // Yaw-velocity tracker shared by RollTilt + CharacterRoll. Runs
    // each frame in TPS regardless of either feature's enable flag so
    // a camera-tilt-off / character-bank-on configuration still has a
    // valid yaw-velocity signal to drive the model lean.
    // Raw, unfiltered yaw rate this frame (rad/s). Direct joystick
    // proxy for character bank — lets the character lean track the
    // joystick magnitude without the input-smoothing tail that
    // _rollSmoothedYawVel introduces.
    private static float _rawYawVel;

    private static void UpdateYawVelocity(GameCamera* cam, bool tps, float dt)
    {
        if (!tps)
        {
            _rollSmoothedYawVel = 0f;
            _rawYawVel = 0f;
            _rollInit = false;
            return;
        }
        float yaw = cam->currentHRotation;
        if (!_rollInit) { _rollPrevYaw = yaw; _rollInit = true; return; }
        float yawDelta = AngleDelta(_rollPrevYaw, yaw);
        _rollPrevYaw = yaw;
        float yawVel = yawDelta / dt;
        _rawYawVel = yawVel;
        _rollSmoothedYawVel = ExpDecay(_rollSmoothedYawVel, yawVel, 10f, dt);
    }

    // ---- RollTilt: ports WickedTPS.cs:2051-2088 ----
    // Camera tilt driven by the shared yaw-velocity signal. Asymmetric
    // on/off rates give a snappy onset + gentle recovery feel.
    //
    // COMPUTE-ONLY HERE. The cam->tilt write happens INLINE from
    // Game.SetCameraLookAtDetour using GetCurrentRollRadians() —
    // direct writes from Framework.Update are silently overwritten
    // by the engine's per-frame camera setup before render, which
    // is why the visible tilt used to be zero. This was the fix
    // landed in commit 1326cbb.
    // Player position tracker — used by UpdateRollTilt's movement
    // gate so the camera tilt recovers when the player stops
    // moving (even if the camera is still being swung).
    private static System.Numerics.Vector3 _rollLastPlayerPos;
    private static bool _rollPosInit;
    private static void UpdateRollTilt(GameCamera* cam, bool tps, float dt)
    {
        if (noWickyXIV.Config.EnableRollTilt && tps)
        {
            // Maintain the player-speed tracker — UpdateCharacterRoll
            // (and any other future consumer) reads
            // _charRollSmoothedSpeed. Camera RollTilt itself no
            // longer gates on it.
            try
            {
                var lp = DalamudApi.ObjectTable.LocalPlayer;
                if (lp != null && dt > 0f)
                {
                    var pos = new System.Numerics.Vector3(
                        lp.Position.X, lp.Position.Y, lp.Position.Z);
                    if (_rollPosInit)
                    {
                        float dx = pos.X - _rollLastPlayerPos.X;
                        float dz = pos.Z - _rollLastPlayerPos.Z;
                        float raw = MathF.Sqrt(dx * dx + dz * dz) / dt;
                        float k = 1f - MathF.Exp(-8f * dt);
                        _charRollSmoothedSpeed += (raw - _charRollSmoothedSpeed) * k;
                    }
                    _rollLastPlayerPos = pos;
                    _rollPosInit = true;
                }
            }
            catch { }

            // Camera roll: pure yaw-velocity-driven, smooth lerp
            // recovery via configured On/Off rates. No movement gate,
            // no forced fast rate, no snap-to-zero. When the user
            // releases the camera, _rollSmoothedYawVel decays
            // naturally → target → 0 → camera lerps back to level
            // smoothly on RollTiltOffRate (default 1.0 = ~700 ms
            // halflife, 1-2 s perceived recovery).
            float maxAngle = noWickyXIV.Config.RollTiltMaxAngle;
            float target = Clamp(
                -_rollSmoothedYawVel * noWickyXIV.Config.RollTiltSensitivity,
                -maxAngle, maxAngle);
            float rate = MathF.Abs(target) > MathF.Abs(_rollCurrent)
                ? noWickyXIV.Config.RollTiltOnRate
                : noWickyXIV.Config.RollTiltOffRate;
            _rollCurrent = ExpDecay(_rollCurrent, target, rate, dt);
        }
        else if (MathF.Abs(_rollCurrent) > 0.001f)
        {
            // Decay toward zero when disabled / out of TPS so re-
            // enabling doesn't snap from a stale value.
            _rollCurrent = ExpDecay(_rollCurrent, 0f, 4f, dt);
        }
        else
        {
            _rollCurrent = 0f;
        }
    }

    // CharacterRoll — banks the player MODEL into turns by writing
    // a roll component into the DrawObject's rotation quaternion.
    // Currently INERT — the drawObj->Rotation write didn't produce
    // a visible bank in this game version (engine overwrites the
    // field per-frame). Kept as a stub to preserve the toggle/
    // sliders so user config doesn't break; needs a different
    // injection point (skeleton-level rotation, or a render-pass
    // hook) to actually move the model. Use RollTilt for visible
    // lean in the meantime.
    private static float _charRollCurrent;
    // Used by UpdateRollTilt's movement-gate; kept here so both roll
    // updates can share the same speed reading.
    private static float _charRollSmoothedSpeed;
    private const float CharRollMotionThreshold = 0.4f; // m/s

    private static unsafe void UpdateCharacterRoll(GameCamera* cam, bool tps, float dt)
    {
        if (!noWickyXIV.Config.EnableCharacterRoll || !tps)
        {
            _charRollCurrent = 0f;
            return;
        }
        var cfg = noWickyXIV.Config;
        float maxAngle = cfg.CharacterRollMaxAngle;

        // Movement gate — character only banks while the player is
        // actually moving. Standing still and panning the camera
        // shouldn't tilt the rider; only mounted/running motion
        // should produce a body lean.
        bool moving = _charRollSmoothedSpeed > CharRollMotionThreshold;

        // Smoothed-input target (same _rollSmoothedYawVel the camera
        // uses) — raw yawDelta jitters frame-to-frame because the
        // game's HRotation isn't sampled every Framework.Update,
        // which would make target oscillate 0 → high → 0 visibly
        // during a continuous turn.
        float target = moving
            ? Clamp(-_rollSmoothedYawVel * cfg.CharacterRollSensitivity,
                    -maxAngle, maxAngle)
            : 0f;

        // Fast forced output rate — 12/s = ~58 ms half-life. With
        // the 10/s input smoothing that's ~165 ms total settle on
        // joystick release, no slider-driven OffRate. The previous
        // user-slider OffRate at 1.5/s gave a 460 ms output tail
        // which was the visible "stuck" feel.
        _charRollCurrent = ExpDecay(_charRollCurrent, target, 12f, dt);
    }

    // ---- PitchTilt: ports PlayerCameraPatch.cs:384-398 ----
    // When pitched DOWN (looking down), nudge lookAtHeightOffset UP so
    // the framing widens; identity at full pitch-up.
    //
    // CRITICAL: write must SUBTRACT the previous frame's contribution
    // before adding the new one. Otherwise the += compounds frame-over-
    // frame because cam->lookAtHeightOffset isn't reset by the game every
    // frame — only when the game itself invokes UpdateLookAtHeightOffset.
    private static void UpdatePitchTilt(GameCamera* cam, bool tps, float dt)
    {
        if (noWickyXIV.Config.EnablePitchTilt && tps)
        {
            float pitch = cam->currentVRotation;
            float minP = cam->minVRotation;
            float maxP = cam->maxVRotation;
            float range = maxP - minP;
            float pNorm = MathF.Abs(range) > 1e-5f ? Clamp((pitch - minP) / range, 0f, 1f) : 0f;

            float targetOffset = noWickyXIV.Config.PitchTiltMaxOffset * (1f - pNorm);
            _pitchTiltCurrent = ExpDecay(_pitchTiltCurrent, targetOffset,
                                         noWickyXIV.Config.PitchTiltSmoothRate, dt);

            // Undo previous, apply new — net contribution per frame = current.
            cam->lookAtHeightOffset -= _pitchTiltLastApplied;
            cam->lookAtHeightOffset += _pitchTiltCurrent;
            _pitchTiltLastApplied = _pitchTiltCurrent;
        }
        else
        {
            // Disabled (or not in TPS): undo any leftover contribution and stop.
            // This is what guarantees the "camera unsticks" the moment the user
            // toggles the feature off.
            if (MathF.Abs(_pitchTiltLastApplied) > 0.0001f)
            {
                cam->lookAtHeightOffset -= _pitchTiltLastApplied;
                _pitchTiltLastApplied = 0f;
            }
            _pitchTiltCurrent = 0f;
        }
    }

    // ---- PositionFloat: discreet float behind player ----
    // Tracks player velocity, computes a small opposite offset, smooth-damps
    // it. Applied additively in Game.GetCameraPositionDetour.
    private static bool _positionFloatErrorLogged;
    private static void UpdatePositionFloat(GameCamera* cam, float dt)
    {
        // Compute the offset only. Application is in Game.SetCameraLookAtDetour
        // which is the inline detour the game calls each frame to set up the
        // look-at position. Writing the offset there guarantees it sticks
        // through to the render — writes to cam->lookAtX/Y/Z from
        // Framework.Update were silently overwritten by the game's per-frame
        // camera setup before render.
        if (!noWickyXIV.Config.EnablePositionFloat)
        {
            if (_floatOffset.LengthSquared() > 1e-5f)
                _floatOffset = ExpDecayV(_floatOffset, Vector3.Zero, 6f, dt);
            else
                _floatOffset = Vector3.Zero;
            _floatVelocity = Vector3.Zero;
            _floatInit = false;
            return;
        }

        Vector3 pos;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) { _floatInit = false; return; }
            pos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);
        }
        catch (Exception ex)
        {
            _floatInit = false;
            if (!_positionFloatErrorLogged)
            {
                _positionFloatErrorLogged = true;
                try { DalamudApi.PluginLog.Warning($"[noWickyXIV] PositionFloat: LocalPlayer access threw ({ex.GetType().Name}: {ex.Message}). Suppressing further logs."); } catch { }
            }
            return;
        }
        if (!_floatInit)
        {
            _lastPlayerPos = pos;
            _floatInit = true;
            return;
        }

        var vel = (pos - _lastPlayerPos) / dt;
        _lastPlayerPos = pos;

        // Target = velocity * lagFactor in motion direction. Look-at "leads"
        // the player slightly when they move → camera pivots toward the lead
        // point → character drifts off-center opposite to motion within the
        // frame. Springs back when the player stops.
        var target = vel * noWickyXIV.Config.PositionFloatLagFactor;
        float rate = 1f / MathF.Max(0.01f, noWickyXIV.Config.PositionFloatSmoothTime);
        _floatOffset = ExpDecayV(_floatOffset, target, rate, dt);

        // Magnitude clamp.
        if (_floatOffset.Length() > 1.5f)
            _floatOffset = Vector3.Normalize(_floatOffset) * 1.5f;
    }

    // ---- InstantMode: documented no-op for now ----
    // FFXIV's GameCamera struct (Hypostasis/Game/Structures/GameCamera.cs)
    // does NOT expose the smoothing-rate fields Wicked zeroes
    // (CharacterPositionSmoothSpeed, PositionSmoothBlendSpeed, etc.). The
    // toggle stays in the panel for future use; current implementation has
    // no effect. Logged once per session so the user knows.
    private static void UpdateInstantModeNote()
    {
        if (noWickyXIV.Config.InstantMode && !_instantModeNoteLogged)
        {
            _instantModeNoteLogged = true;
            try { DalamudApi.PluginLog.Information(
                "[noWickyXIV] InstantMode is enabled but currently a no-op — FFXIV's camera struct does not expose smoothing-rate fields. Toggle has no effect.");
            } catch { }
        }
        if (!noWickyXIV.Config.InstantMode) _instantModeNoteLogged = false;
    }

    private static void ResetState()
    {
        _rollCurrent = 0f;
        _rollSmoothedYawVel = 0f;
        _rollInit = false;
        _pitchTiltCurrent = 0f;
        // Note: not subtracting _pitchTiltLastApplied here because cam pointer
        // isn't trusted in the FreeCam-active path; the next UpdatePitchTilt
        // tick (with FreeCam off) will handle the undo via its else branch.
        _pitchTiltLastApplied = 0f;
        _floatOffset = Vector3.Zero;
        _floatVelocity = Vector3.Zero;
        _floatInit = false;
    }

    // ---- Math helpers ----
    private static float ExpDecay(float current, float target, float rate, float dt)
        => current + (target - current) * (1f - MathF.Exp(-rate * dt));

    private static Vector3 ExpDecayV(Vector3 current, Vector3 target, float rate, float dt)
    {
        float k = 1f - MathF.Exp(-rate * dt);
        return current + (target - current) * k;
    }

    private static float AngleDelta(float prev, float curr)
    {
        float d = curr - prev;
        while (d >  MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    private static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);
}
