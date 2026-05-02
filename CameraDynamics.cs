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
    private static bool    _cursorReleased; // true = cursor free for UI; false = mouse drives camera
    private static bool    _mouseLookInit;
    private static Vector2 _mouseLookPrevPos;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

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
        // accumulated during the released period.
        _mouseLookInit = false;
        try { DalamudApi.PluginLog.Debug($"[noWickyXIV] Cursor release toggled -> {(_cursorReleased ? "RELEASED (UI mode)" : "GRABBED (mouselook)")}"); } catch { }
    }

    public static bool IsMouseLookActive
        => noWickyXIV.Config.EnableMouseLookAlways && !_cursorReleased;

    private static bool _instantModeNoteLogged;

    public static Vector3 GetPositionFloatOffset() => _floatOffset;

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

        // Sensitivity FIRST so subsequent writes (Swivel, etc.) operate on
        // the corrected H/V rotation and don't get inadvertently scaled by
        // the next-frame delta-replay.
        UpdateSensitivity(cam, tps);

        // Mouselook BEFORE Swivel so its yaw write is the final word the
        // user feels, but after Sensitivity so it operates on un-scaled deltas.
        UpdateMouseLook(cam, tps);

        UpdateRollTilt(cam, tps, dt);
        UpdatePitchTilt(cam, tps, dt);
        UpdatePositionFloat(cam, dt);
        UpdateAds(cam, tps, dt);
        UpdateCombatZoom(cam, tps, dt);
        UpdateAutoShoulderSwap(cam, tps, dt);
        UpdateSwivelOnMove(cam, tps, dt);
        UpdateInstantModeNote();
    }

    // ---- Sensitivity + Y-inversion: Phase E ----
    // Delta-replay approach: track previous-frame H/V rotations, compute
    // this-frame delta, scale by user multiplier, write the scaled value
    // back. Identity at multiplier=1 + InvertY=false.
    //
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
            ShowOsCursor();
            return;
        }

        ImGuiIOPtr io;
        try { io = ImGui.GetIO(); } catch { ShowOsCursor(); return; }

        // Don't fight the game's own RMB-held mouselook
        bool rmbHeld;
        try { rmbHeld = io.MouseDown[1]; } catch { rmbHeld = false; }
        if (rmbHeld) { _mouseLookInit = false; ShowOsCursor(); return; }

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

        bool held;
        try { held = ImGui.GetIO().MouseDown[1]; } catch { return; }

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
        if (!noWickyXIV.Config.EnableCombatZoom || !tps || _adsActive)
        {
            // Reset edge state so a re-enable doesn't capture a wrong baseline
            // mid-combat — first frame after re-enable will re-capture.
            _combatZoomActive = false;
            _combatZoomWasInCombat = false;
            return;
        }

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

    // ---- RollTilt: ports WickedTPS.cs:2051-2088 ----
    // Yaw velocity drives a target roll angle; smoothed onto _rollCurrent
    // with asymmetric on/off rates (snappy onset, gentle recovery).
    private static void UpdateRollTilt(GameCamera* cam, bool tps, float dt)
    {
        if (noWickyXIV.Config.EnableRollTilt && tps)
        {
            float yaw = cam->currentHRotation;
            if (!_rollInit) { _rollPrevYaw = yaw; _rollInit = true; }
            float yawDelta = AngleDelta(_rollPrevYaw, yaw);
            _rollPrevYaw = yaw;

            float yawVel = yawDelta / dt;
            _rollSmoothedYawVel = ExpDecay(_rollSmoothedYawVel, yawVel, 10f, dt);

            float maxAngle = noWickyXIV.Config.RollTiltMaxAngle;
            float target = Clamp(-_rollSmoothedYawVel * noWickyXIV.Config.RollTiltSensitivity,
                                 -maxAngle, maxAngle);
            float rate = MathF.Abs(target) > MathF.Abs(_rollCurrent)
                ? noWickyXIV.Config.RollTiltOnRate
                : noWickyXIV.Config.RollTiltOffRate;
            _rollCurrent = ExpDecay(_rollCurrent, target, rate, dt);

            cam->tilt = _rollCurrent * (MathF.PI / 180f);  // game expects radians
        }
        else
        {
            // Disabled or out of TPS — decay back to zero so the camera
            // doesn't sit at whatever roll we'd written last.
            if (MathF.Abs(_rollCurrent) > 0.001f)
            {
                _rollCurrent = ExpDecay(_rollCurrent, 0f, 4f, dt);
                if (cam != null) cam->tilt = _rollCurrent * (MathF.PI / 180f);
            }
            else _rollCurrent = 0f;
            _rollSmoothedYawVel = 0f;
            _rollInit = false;
        }
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
