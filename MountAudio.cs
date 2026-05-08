using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace noWickyXIV;

// Dynamic mount-audio engine.
//
// Reads the local player's mount id + position-delta-driven speed,
// runs a small state machine (idle / accel / cruise / decel), and
// crossfades user-provided .ogg loops via NAudio so a vehicle-style
// mount (default target: Fenrir / Magitek-class motorcycles) feels
// like an actual engine: idle hum, rev-up on launch, steady cruise
// with pitch tied to speed, and a wind-down on slowing/stopping.
//
// Server-authoritative movement is untouched — this is purely an
// audio-only feature. Position, hitbox, and ability targeting all
// continue to use the engine's normal flow.
//
// Files live in:
//   <plugin-dir>/assets/mount-audio/<mountId>/
//     idle.ogg    — looped while mounted but not moving
//     accel.ogg   — one-shot rev-up on speed rising-edge
//     cruise.ogg  — looped while moving steadily; pitch shifts with speed
//     decel.ogg   — one-shot rev-down on speed falling-edge
//     mount.ogg   — one-shot when mounting (optional)
//     dismount.ogg— one-shot when dismounting (optional)
//
// All optional — missing files are skipped silently. User can config
// per-mount in the Misc → Mount Audio panel.
public static unsafe class MountAudio
{
    private enum State
    {
        Off,    // not mounted, or feature disabled
        Idle,   // mounted, speed below SlowMin
        Slow,   // mounted, speed in [SlowMin, MidMin)
        Mid,    // mounted, speed in [MidMin, TopMin)
        Top,    // mounted, speed >= TopMin
    }

    // -- State tracking --
    private static State    _state = State.Off;
    private static byte     _currentMountId;
    private static bool     _wasMounted;
    private static Vector3  _lastPos;
    private static float    _smoothedSpeed;       // m/s, exp-lerped
    private static float    _lastSmoothedSpeed;
    private static double   _lastStateChangeT;

    // Speed thresholds for state transitions. Tuned for typical
    // FFXIV mount speeds (~14 m/s normal, ~28 m/s fly-mount cap).
    // Below MotionThreshold = idle; above = moving. Acceleration
    // direction (sign of dv/dt) chooses accel vs cruise vs decel.
    //
    // Both speed AND dvdt get exp-smoothed with their own filters.
    // Position-delta is noisy frame-to-frame and 1/dt-differentiation
    // amplifies that into ±20 m/s² spikes at constant cruise speed,
    // which used to thrash the state machine 5+ times/sec — that
    // re-fired decel.wav repeatedly, hammered NAudio, and dipped FPS
    // (which the user perceived as camera jitter while moving).
    private const float MotionThreshold = 0.5f;    // m/s — sub-this counts as stopped
    private const float AccelThreshold  = 3.0f;    // m/s² — above this (smoothed) = real accel
    private const float SpeedSmoothRate = 8f;      // 1/s exp-lerp on raw speed (filters position jitter)
    private const float DvdtSmoothRate  = 4f;      // 1/s exp-lerp on dvdt (filters differentiation noise)
    private const double MinStateDwellSec = 0.30;  // can't leave a state for 300 ms after entering it
    private static float _smoothedDvdt;

    // -- Layer players (9-slot speed-band model) --
    // Four LOOP layers for each speed band:
    //   idle (#5)      — speed below SlowMin
    //   slow (#2)      — [SlowMin, MidMin)
    //   mid  (#9)      — [MidMin, TopMin)
    //   top  (#8)      — >= TopMin
    // Five ONE-SHOT layers fired on band-crossing edges:
    //   mount     (#1) — mount-up edge
    //   idle2slow (#6) — speed crosses SlowMin going up
    //   revup     (#3) — speed crosses MidMin going up (slow→mid)
    //   decel     (#4) — speed crosses SlowMin going down (slow→idle)
    //   dismount  (#7) — dismount edge
    // Loop slots are typed as ILoopLayer so they can hold either a
    // plain MountAudioLayer (LoopStream-rewind) or a
    // CrossfadeLoopLayer (two-instance crossfade) depending on the
    // slot's CrossfadeLoopMs config.
    private static ILoopLayer _idleLayer;               // #5 loop
    private static ILoopLayer _slowLayer;               // #2 loop
    private static MountAudioLayer _midLayer;           // #9 ONE-SHOT (transitional)
    private static ILoopLayer _topLayer;                // #8 loop
    private static MountAudioLayer _mountOneShot;       // #1 one-shot
    private static MountAudioLayer _idle2SlowOneShot;   // #6 one-shot
    private static MountAudioLayer _revupOneShot;       // #3 one-shot
    private static MountAudioLayer _decelOneShot;       // #4 one-shot
    private static MountAudioLayer _dismountOneShot;    // #7 one-shot

    // Pending actions queued by ScheduleSlot — drained per Update
    // tick when each entry's deadline elapses. Lets us defer the
    // start of a slot's playback by its configured DelayMs without
    // blocking the audio thread.
    private struct PendingAction { public double DeadlineSec; public Action Run; public int Generation; }
    private static readonly System.Collections.Generic.List<PendingAction> _pending = new();
    // Generation counter — every time we hit a state transition, we
    // bump this and tag new pending actions with the current value.
    // Stale actions from before the transition are then dropped
    // (e.g. if the user mounts → moves before idle's delay elapses,
    // we don't want a now-irrelevant idle.PlayLoopFadeIn to fire).
    private static int _scheduleGeneration;

    // Top-loop debounce: when we transition INTO Top from a lower
    // band, mid one-shot is fired (revup having already played for
    // slow→mid). Top loop must wait for mid one-shot to finish
    // before fading in — otherwise the "mid → top" transition
    // sound gets cut off by top's loop. Update tick polls these
    // flags each frame.
    private static bool _pendingTopStart;
    private static bool _topStarted;

    // Tracks whether the topcruise loop has already been kicked off
    // for the current Cruise-state entry. Set when we cross into
    // Cruise state from Accelerating, cleared when we leave Cruise.
    // Used so we only fire-and-forget the topcruise-after-revup
    // scheduling once per cruise session.
    private static bool _topCruiseScheduled;
    // Time at which we entered the Accelerating state — paired with
    // CruiseRevUpDurationSec to know when the rev-up build clip has
    // played out and we should switch to the looped topcruise sound.
    private static double _acceleratingStartT;
    // Tracks "first take-off since this mount-up" so we can use
    // accel.wav for the initial kick-into-gear feel and idle2move.wav
    // for every subsequent idle→move transition.
    private static bool _firstTakeoffSinceMount;

    // -- File lookup cache --
    // Mount id → resolved directory path. Re-resolved when the
    // mount changes (different mounts can have separate audio
    // packs).
    private static byte _loadedForMountId;

    // -- Animation freeze state --
    // On mount-up we want to suppress the bike's idle animation
    // (motorcycle vibration etc.) for a short window so the
    // mount.ogg "turn-on" sound reads as actually starting the
    // engine. State is just a deadline timestamp + bool; per-frame
    // we re-apply the freeze (in case the engine resets it) and
    // release it once the window expires.
    private static double _animFreezeUntilT;
    private static bool   _animFreezeActive;

    // -- Init / dispose --
    public static void Initialize()
    {
        // No setup needed eagerly — audio is created on demand when
        // the user mounts up and the per-mount files exist.
    }

    public static void Dispose()
    {
        try { StopAllLayers(); } catch { }
        DisposeLayers();
        _state = State.Off;
        _wasMounted = false;
        _currentMountId = 0;
        _loadedForMountId = 0;
    }

    // -- Per-frame tick --
    private static double _lastHeartbeatT;
    private static State _lastLoggedState = (State)(-1);
    public static void Update()
    {
        var cfg = noWickyXIV.Config;

        // Heartbeat: confirms Update is running every second + logs
        // any state changes immediately. If you don't see ANY of
        // these in /xllog, MountAudio.Update isn't being called or
        // EnableMountAudio is off.
        var nowS = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        bool stateChanged = _state != _lastLoggedState;
        if (stateChanged || nowS - _lastHeartbeatT > 1.0)
        {
            _lastHeartbeatT = nowS;
            _lastLoggedState = _state;
            byte hbMountId = 0;
            try
            {
                var hbLp = DalamudApi.ObjectTable.LocalPlayer;
                if (hbLp != null)
                {
                    var hbCh = (Character*)hbLp.Address;
                    if (hbCh != null) hbMountId = (byte)hbCh->Mount.MountId;
                }
            }
            catch { }
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountAudio HB: enabled={cfg.EnableMountAudio} mountId={hbMountId} "
                + $"state={_state} speed={_smoothedSpeed:F2}m/s "
                + $"layers(idle={_idleLayer != null},slow={_slowLayer != null},mid={_midLayer != null},top={_topLayer != null},"
                + $"mount={_mountOneShot != null},i2s={_idle2SlowOneShot != null},rev={_revupOneShot != null},"
                + $"decel={_decelOneShot != null},dis={_dismountOneShot != null})"); } catch { }
        }

        if (!cfg.EnableMountAudio)
        {
            if (_state != State.Off) ExitToOff();
            return;
        }

        var lp = DalamudApi.ObjectTable.LocalPlayer;
        if (lp == null) { if (_state != State.Off) ExitToOff(); return; }

        // Mount status from the engine's Character struct. mountId 0
        // means not mounted; non-zero is the EXD Mount.exh row id.
        byte mountId = 0;
        try
        {
            var ch = (Character*)lp.Address;
            if (ch != null) mountId = (byte)ch->Mount.MountId;
        }
        catch { }

        bool mounted = mountId != 0;
        // Mount equip / unequip edge events
        if (mounted && !_wasMounted)
        {
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountAudio: mount-up detected — mountId={mountId}"); } catch { }
            _currentMountId = mountId;
            EnsureLayersForMount(mountId);
            // Suppress the engine's native mount sounds IMMEDIATELY
            // on the mount-up edge.
            SuppressNativeMountSounds();
            // Bump generation so any leftover pending actions from
            // a previous mounted session are dropped.
            _scheduleGeneration++;
            TriggerOneShot(mountId, "mount", _mountOneShot);
            _state = State.Idle;
            // Idle loop respects its DelayMs config — set it to e.g.
            // 1500ms if you want the engine-start one-shot to play
            // out before the idle hum begins.
            TriggerLoop(mountId, "idle", _idleLayer);
            // The very next idle→move transition is the "first take-
            // off after mount-up" — uses accel.wav (engine kick).
            // Subsequent idle→moves use idle2move.wav (the gentler
            // "bike already on, easing into motion" sound).
            // 9-slot speed-band model: state machine handles all
            // band transitions on its own; no edge flags to reset
            // here beyond the position snap below.
            // Snap velocity-tracking state so the first frame after
            // mount-up doesn't compute a huge delta from the
            // previous (un-mounted) position.
            _lastPos = lp.Position;
            _smoothedSpeed = 0f;
            _lastSmoothedSpeed = 0f;
            // Schedule the idle-animation freeze so the mount-up
            // sound has space before the bike's vibration kicks in.
            BeginAnimationFreeze();
        }
        else if (!mounted && _wasMounted)
        {
            // Dismount sequence: 4 (decel one-shot) → 7 (dismount
            // one-shot). Decel only fires if we were actually moving
            // (any band above Idle); at idle dismounts, just the
            // engine-off click. Both respect per-slot DelayMs.
            _scheduleGeneration++;
            if (_state == State.Slow || _state == State.Mid || _state == State.Top)
            {
                TriggerOneShot(_currentMountId, "decel", _decelOneShot);
            }
            TriggerOneShot(_currentMountId, "dismount", _dismountOneShot);
            // Always release any active freeze on dismount so we
            // never leave a stale write on memory we no longer
            // own.
            ReleaseAnimationFreeze();
            ExitToOff();
        }
        else if (mounted && mountId != _currentMountId)
        {
            // Mount swapped without dismounting (rare, but shukuchi
            // / bardam-style instant swaps exist). Reload audio
            // pack for the new mount.
            _ = _dismountOneShot?.PlayOneShot();
            _currentMountId = mountId;
            EnsureLayersForMount(mountId);
            _ = _mountOneShot?.PlayOneShot();
        }
        _wasMounted = mounted;
        if (!mounted) return;

        // Velocity from frame-to-frame position delta. Uses
        // game-Y-flat (XZ) magnitude so vertical mount-flying
        // doesn't artificially raise the cruise pitch.
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        if (dt <= 0f) dt = 0.016f;
        var pos = lp.Position;
        float dx = pos.X - _lastPos.X;
        float dz = pos.Z - _lastPos.Z;
        float rawSpeed = MathF.Sqrt(dx * dx + dz * dz) / dt;
        _lastPos = pos;
        // Initial frame after mount-up reads a huge delta because
        // _lastPos was stale. Suppress the first frame by clamping.
        if (_state == State.Idle && _smoothedSpeed < 0.01f && rawSpeed > 5f)
            rawSpeed = 0f;

        // Smooth raw speed to filter idle-animation jitter.
        float k = 1f - MathF.Exp(-SpeedSmoothRate * dt);
        _lastSmoothedSpeed = _smoothedSpeed;
        _smoothedSpeed += (rawSpeed - _smoothedSpeed) * k;
        float rawDvdt = (_smoothedSpeed - _lastSmoothedSpeed) / dt;
        // SECOND-stage filter on dvdt itself — a per-frame derivative
        // of a noisy signal is itself extremely noisy, so we
        // exp-lerp it before letting it drive state decisions.
        float kd = 1f - MathF.Exp(-DvdtSmoothRate * dt);
        _smoothedDvdt += (rawDvdt - _smoothedDvdt) * kd;
        float dvdt = _smoothedDvdt;

        // Speed-band state machine. Map smoothed speed to one of four
        // bands using user-configurable thresholds. Min-dwell cooldown
        // prevents thrashing on residual noise. The band determines
        // which loop is active; band-crossing edges fire one-shots.
        float slowMin = MathF.Max(0.05f, cfg.MountAudioSpeedSlowMin);
        float midMin  = MathF.Max(slowMin + 0.1f, cfg.MountAudioSpeedMidMin);
        float topMin  = MathF.Max(midMin  + 0.1f, cfg.MountAudioSpeedTopMin);
        State next = _state;
        double sinceLastChange = nowS - _lastStateChangeT;
        bool dwellElapsed = sinceLastChange >= MinStateDwellSec;
        if (dwellElapsed)
        {
            if (_smoothedSpeed < slowMin)      next = State.Idle;
            else if (_smoothedSpeed < midMin)  next = State.Slow;
            else if (_smoothedSpeed < topMin)  next = State.Mid;
            else                               next = State.Top;
        }

        if (next != _state)
        {
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountAudio state {_state} -> {next} (speed={_smoothedSpeed:F2}m/s)"); } catch { }
            HandleStateTransition(_state, next);
        }
        _state = next;

        // Drain any deferred actions whose deadlines have arrived.
        // (Stale actions get dropped by Generation check inside.)
        DrainPending();

        // Top-loop debounce: while in Top state, hold off the top
        // loop until the mid one-shot is in its tail (with overlap
        // controlled by mid's FadeOutMs). Top fades in DURING mid's
        // tail so they crossfade naturally instead of mid clipping
        // off when top starts. Cleanly degrades when mid finished
        // already (just starts top immediately) or when no mid was
        // fired (skip-through edge — also starts top immediately).
        if (_state == State.Top && _pendingTopStart && !_topStarted)
        {
            bool midDone = _midLayer == null || !_midLayer.IsPlaying;
            bool midInTail = false;
            if (!midDone)
            {
                var (_, _, midFadeOutMs) = GetTiming(_currentMountId, "mid");
                float overlapSec = midFadeOutMs / 1000f;
                midInTail = _midLayer.RemainingSeconds <= overlapSec;
            }
            if (midDone || midInTail)
            {
                // Tell mid to fade out over its FadeOutMs while top
                // fades in — produces the actual crossfade.
                if (!midDone) TriggerFadeOut(_currentMountId, "mid", _midLayer);
                TriggerLoop(_currentMountId, "top", _topLayer);
                _topStarted = true;
                _pendingTopStart = false;
            }
        }

        // Cruise pitch couples to speed for whichever cruise loop is
        // currently active. Map [0..maxExpectedSpeed] →
        // [pitchMin..pitchMax]. Each band's loop gets the same pitch
        // treatment so the engine RPM feel scales smoothly across
        // band transitions.
        {
            float speedFrac = MathF.Min(1f, _smoothedSpeed / MathF.Max(1f, cfg.MountAudioMaxSpeed));
            float pitch = cfg.MountAudioCruisePitchMin
                        + speedFrac * (cfg.MountAudioCruisePitchMax - cfg.MountAudioCruisePitchMin);
            _slowLayer?.SetPitch(pitch);
            _midLayer?.SetPitch(pitch);
            _topLayer?.SetPitch(pitch);
        }

        // Per-frame fade-in/out updates for any active layer.
        _idleLayer?.Tick(dt);
        _slowLayer?.Tick(dt);
        _midLayer?.Tick(dt);
        _topLayer?.Tick(dt);
        _mountOneShot?.Tick(dt);
        _idle2SlowOneShot?.Tick(dt);
        _revupOneShot?.Tick(dt);
        _decelOneShot?.Tick(dt);
        _dismountOneShot?.Tick(dt);

        // Re-apply the animation freeze each frame while active —
        // the engine may overwrite Timeline.Speed every animation
        // tick so a one-shot write at mount-up wouldn't stick.
        TickAnimationFreeze(lp);

        // Mute the game's native mount audio while we have a custom
        // pack loaded — without this, the engine's default Fenrir
        // engine hum / gallop loop plays alongside our custom layers.
        // Per-frame re-write because the engine may reset the override
        // when the mount actor refreshes its sound state.
        if (_idleLayer != null || _slowLayer != null
            || _midLayer != null || _topLayer != null
            || _idle2SlowOneShot != null || _revupOneShot != null
            || _decelOneShot != null)
        {
            SuppressNativeMountSounds();
        }
    }

    private static void HandleStateTransition(State from, State to)
    {
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        _lastStateChangeT = now;

        // 9-slot speed-band transitions, now using per-slot timing
        // for delay / fade-in / fade-out. Bump generation so any
        // pending actions from the prior state get cancelled.
        _scheduleGeneration++;

        bool goingUp = (int)to > (int)from;
        bool wasIdle = from == State.Idle;
        bool nowIdle = to == State.Idle;
        // Crossing into mid/top territory from below — covers
        // Slow→Mid, Slow→Top (skip-through), Idle→Mid, Idle→Top.
        bool enteringMidOrAbove = (int)to >= (int)State.Mid && (int)from < (int)State.Mid;

        // Fade out the FROM band's loop. All four bands have loops now
        // (idle/slow/mid/top) — mid loops while in mid band and gets
        // faded out when leaving.
        switch (from)
        {
            case State.Idle: TriggerFadeOut(_currentMountId, "idle", _idleLayer); break;
            case State.Slow: TriggerFadeOut(_currentMountId, "slow", _slowLayer); break;
            case State.Mid:
                // Going UP from Mid → Top: KEEP mid playing; the
                // top-debounce poll waits for mid's current iteration
                // to enter its tail before crossfading. Going DOWN
                // (Mid → Slow / Idle): fade mid out normally.
                if (!goingUp) TriggerFadeOut(_currentMountId, "mid", _midLayer);
                break;
            case State.Top:  TriggerFadeOut(_currentMountId, "top",  _topLayer);  break;
        }

        // Fire transition one-shots based on direction + boundaries.
        if (goingUp && wasIdle)
        {
            TriggerOneShot(_currentMountId, "idle2slow", _idle2SlowOneShot);
        }
        if (goingUp && enteringMidOrAbove)
        {
            // Slow → Mid (or skip-through to Top): rev-up build first.
            // Mid loop will start via the to-state switch below; no
            // separate mid one-shot fires now since mid is a loop.
            TriggerOneShot(_currentMountId, "revup", _revupOneShot);
        }
        if (!goingUp && nowIdle)
        {
            TriggerOneShot(_currentMountId, "decel", _decelOneShot);
        }

        // Start the TO band's loop. Top is debounced — its loop
        // doesn't start until mid's current iteration is in its tail.
        // For Slow→Top skip, mid loop also starts here (its iteration
        // will play through before top, matching the "play mid then
        // top" semantics).
        _topStarted = false;
        _pendingTopStart = false;
        switch (to)
        {
            case State.Idle: TriggerLoop(_currentMountId, "idle", _idleLayer); break;
            case State.Slow: TriggerLoop(_currentMountId, "slow", _slowLayer); break;
            case State.Mid:
                // Always (re)start mid loop when entering Mid band —
                // each cycle gets a fresh mid playback.
                TriggerLoop(_currentMountId, "mid", _midLayer);
                break;
            case State.Top:
                // Mid → Top: mid keeps playing (already looping);
                // wait for current iteration tail.
                // Slow/Idle → Top (skip): start mid loop too so the
                // user hears the rev-up tail before top.
                if (from != State.Mid)
                {
                    TriggerLoop(_currentMountId, "mid", _midLayer);
                }
                _pendingTopStart = true;
                break;
        }
    }

    private static void ExitToOff()
    {
        StopAllLayers();
        _state = State.Off;
        _wasMounted = false;
        _smoothedSpeed = 0f;
    }

    private static void StopAllLayers()
    {
        _idleLayer?.Stop();
        _slowLayer?.Stop();
        _midLayer?.Stop();
        _topLayer?.Stop();
        _mountOneShot?.Stop();
        _idle2SlowOneShot?.Stop();
        _revupOneShot?.Stop();
        _decelOneShot?.Stop();
        _dismountOneShot?.Stop();
    }

    private static void DisposeLayers()
    {
        _idleLayer?.Dispose();
        _slowLayer?.Dispose();
        _midLayer?.Dispose();
        _topLayer?.Dispose();
        _mountOneShot?.Dispose();
        _idle2SlowOneShot?.Dispose();
        _revupOneShot?.Dispose();
        _decelOneShot?.Dispose();
        _dismountOneShot?.Dispose();
        _idleLayer = null;
        _slowLayer = null;
        _topLayer = null;
        _midLayer = null;
        _mountOneShot = _idle2SlowOneShot = _revupOneShot
            = _decelOneShot = _dismountOneShot = null;
    }

    private static void EnsureLayersForMount(byte mountId)
    {
        if (_loadedForMountId == mountId && _idleLayer != null) return;
        DisposeLayers();
        _loadedForMountId = mountId;

        var cfg = noWickyXIV.Config;
        var dir = ResolveMountDir(mountId);
        bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        bool hasOverrides = cfg.MountAudioOverrides != null
            && cfg.MountAudioOverrides.Exists(o => o.MountId == mountId
                && !string.IsNullOrEmpty(o.FilePath));
        if (!dirExists && !hasOverrides)
        {
            // No audio pack for this mount AND no per-slot overrides
            // — feature inert, no error.
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountAudio: no audio pack for mount {mountId} (looked in '{dir ?? "<null>"}', no overrides). Skipping."); } catch { }
            return;
        }

        float vol = cfg.MountAudioVolume;
        // cruise.wav is now a ONE-SHOT (the rev-up build sound); the
        // sustained engine cruise comes from topcruise.wav as a loop.
        // idle2move.wav is the gentler one-shot for idle→move
        // transitions while the bike is already running (vs accel.wav
        // for the kick-into-gear feel of the very first take-off).
        // 9-slot speed-band model: four loops (one per band) + five
        // one-shot transitions. Slot names match the UI labels and
        // the user's source filename mapping.
        _idleLayer         = CreateLoopLayer(mountId, "idle", dir, vol);
        _slowLayer         = CreateLoopLayer(mountId, "slow", dir, vol);
        // mid is a LOOP that plays while in mid band. When entering
        // top from mid, we wait for mid's current iteration to enter
        // its tail before fading to top — that's the "debounce"
        // semantics the user wanted. Each new entry into mid band
        // starts mid playing again.
        _midLayer          = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "mid"),       loop: true,  baseVol: vol);
        _topLayer          = CreateLoopLayer(mountId, "top", dir, vol);
        _mountOneShot      = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "mount"),     loop: false, baseVol: vol);
        _idle2SlowOneShot  = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "idle2slow"), loop: false, baseVol: vol);
        _revupOneShot      = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "revup"),     loop: false, baseVol: vol);
        _decelOneShot      = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "decel"),     loop: false, baseVol: vol);
        _dismountOneShot   = MountAudioLayer.TryCreate(ResolveSlotPath(mountId, dir, "dismount"),  loop: false, baseVol: vol);

        try
        {
            int loaded = (_idleLayer != null ? 1 : 0)
                       + (_slowLayer != null ? 1 : 0)
                       + (_midLayer != null ? 1 : 0)
                       + (_topLayer != null ? 1 : 0)
                       + (_mountOneShot != null ? 1 : 0)
                       + (_idle2SlowOneShot != null ? 1 : 0)
                       + (_revupOneShot != null ? 1 : 0)
                       + (_decelOneShot != null ? 1 : 0)
                       + (_dismountOneShot != null ? 1 : 0);
            DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountAudio: mount {mountId} pack loaded — {loaded}/9 slots from '{dir}' "
                + $"(idle={_idleLayer != null}, slow={_slowLayer != null}, mid={_midLayer != null}, top={_topLayer != null}, "
                + $"mount={_mountOneShot != null}, idle2slow={_idle2SlowOneShot != null}, revup={_revupOneShot != null}, "
                + $"decel={_decelOneShot != null}, dismount={_dismountOneShot != null})");
        }
        catch { }
    }

    // Creates a loop layer for a slot. If the slot's CrossfadeLoopMs
    // config is > 0, returns a CrossfadeLoopLayer (two-instance
    // crossfade) so loops with non-matching start/end samples
    // play seamlessly. Otherwise returns a plain MountAudioLayer
    // with LoopStream rewind.
    private static ILoopLayer CreateLoopLayer(byte mountId, string slot, string dir, float vol)
    {
        var path = ResolveSlotPath(mountId, dir, slot);
        if (string.IsNullOrEmpty(path)) return null;

        var timings = noWickyXIV.Config.MountAudioTimings;
        int crossfadeMs = 0;
        if (timings != null)
        {
            for (int i = 0; i < timings.Count; i++)
            {
                var t = timings[i];
                if (t == null) continue;
                if (t.MountId != mountId) continue;
                if (!string.Equals(t.Slot, slot, StringComparison.OrdinalIgnoreCase)) continue;
                crossfadeMs = Math.Max(0, t.CrossfadeLoopMs);
                break;
            }
        }
        if (crossfadeMs > 0)
        {
            var x = CrossfadeLoopLayer.TryCreate(path, vol, crossfadeMs);
            if (x != null) return x;
            // Fall through to plain loop if crossfade-loop creation
            // failed (e.g. file decode issue on one of the two
            // instances).
        }
        return MountAudioLayer.TryCreate(path, loop: true, baseVol: vol);
    }

    // ---- Per-slot timing ----
    // Returns DelayMs / FadeInMs / FadeOutMs for the (mountId, slot)
    // pair, or sensible defaults (0 / 400 / 400) when no entry exists.
    private static (int delayMs, int fadeInMs, int fadeOutMs) GetTiming(byte mountId, string slot)
    {
        var list = noWickyXIV.Config.MountAudioTimings;
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t == null) continue;
                if (t.MountId != mountId) continue;
                if (!string.Equals(t.Slot, slot, StringComparison.OrdinalIgnoreCase)) continue;
                return (Math.Max(0, t.DelayMs),
                        Math.Max(0, t.FadeInMs),
                        Math.Max(0, t.FadeOutMs));
            }
        }
        return (0, 400, 400);
    }

    // Schedule a layer to start (or fade out) at now + delaySec. Tagged
    // with the current generation so it gets skipped if a new state
    // transition has bumped the generation.
    private static void ScheduleAction(double delaySec, Action run)
    {
        if (run == null) return;
        var deadline = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond + Math.Max(0, delaySec);
        _pending.Add(new PendingAction
        {
            DeadlineSec = deadline,
            Run = run,
            Generation = _scheduleGeneration,
        });
    }

    // Drain pending actions whose deadlines have elapsed. Stale
    // actions (lower Generation than current) are dropped without
    // running.
    private static void DrainPending()
    {
        if (_pending.Count == 0) return;
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (now < p.DeadlineSec) continue;
            _pending.RemoveAt(i);
            if (p.Generation != _scheduleGeneration) continue; // stale
            try { p.Run?.Invoke(); } catch { }
        }
    }

    // Helper: trigger a slot's loop layer with delay + fade-in pulled
    // from per-slot timing config. Accepts the ILoopLayer interface
    // so the same call works for plain and crossfade-loop layers.
    private static void TriggerLoop(byte mountId, string slot, ILoopLayer layer)
    {
        if (layer == null) return;
        var (delayMs, fadeInMs, _) = GetTiming(mountId, slot);
        ScheduleAction(delayMs / 1000.0,
            () => layer.PlayLoopFadeIn(fadeInSeconds: fadeInMs / 1000f));
    }

    // Helper: trigger a slot's one-shot layer with delay pulled from
    // per-slot timing config. Fade-in on one-shots is brief (50 ms).
    private static void TriggerOneShot(byte mountId, string slot, MountAudioLayer layer)
    {
        if (layer == null) return;
        var (delayMs, _, _) = GetTiming(mountId, slot);
        ScheduleAction(delayMs / 1000.0, () => { _ = layer.PlayOneShot(); });
    }

    // Helper: fade out a slot's loop layer with fade-out duration
    // pulled from timing config. Accepts ILoopLayer for both layer
    // types. There's also a MountAudioLayer-typed overload for the
    // mid one-shot which isn't a loop.
    private static void TriggerFadeOut(byte mountId, string slot, ILoopLayer layer)
    {
        if (layer == null) return;
        var (_, _, fadeOutMs) = GetTiming(mountId, slot);
        layer.FadeOut(fadeOutSeconds: fadeOutMs / 1000f);
    }
    private static void TriggerFadeOut(byte mountId, string slot, MountAudioLayer layer)
    {
        if (layer == null) return;
        var (_, _, fadeOutMs) = GetTiming(mountId, slot);
        layer.FadeOut(fadeOutSeconds: fadeOutMs / 1000f);
    }

    // Per-slot path resolver. Priority order:
    //   1. User override in Config.MountAudioOverrides matching
    //      (mountId, slot) — wins if FilePath exists on disk.
    //   2. Convention-based lookup in <plugin-dir>/assets/mount-audio/
    //      <mountId>/<slot>.{wav,ogg,mp3}.
    // The override path lets users keep their .wav files anywhere
    // and define them in the UI without copying into the assets dir.
    private static string ResolveSlotPath(byte mountId, string dir, string slot)
    {
        // Only use what the user has EXPLICITLY picked in the UI.
        // No convention-based fallback — if the user hasn't set a
        // path for this (mountId, slot), the slot stays unloaded
        // (no sound). The previous behavior of auto-loading from
        // assets/mount-audio/<mountId>/<slot>.wav was "baking in"
        // sounds the user didn't intend; per "let the files that
        // are being picked determine the sounds that are being set",
        // we never auto-discover.
        var overrides = noWickyXIV.Config.MountAudioOverrides;
        if (overrides != null)
        {
            for (int i = 0; i < overrides.Count; i++)
            {
                var ov = overrides[i];
                if (ov == null) continue;
                if (ov.MountId != mountId) continue;
                if (!string.Equals(ov.Slot, slot, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(ov.FilePath)) continue;
                if (File.Exists(ov.FilePath)) return ov.FilePath;
            }
        }
        return "";
    }

    // Scans the mount audio directory for a file with the given base
    // name and any supported audio extension. Returns the first match
    // or null if none found. .wav is preferred (faster decode, no
    // libvorbis dep at runtime); .ogg/.mp3 fall through if present.
    private static string ResolveAudio(string dir, string baseName)
    {
        foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
        {
            var path = Path.Combine(dir, baseName + ext);
            if (File.Exists(path)) return path;
        }
        // Return non-existent .wav so TryCreate's File.Exists guard
        // returns null cleanly without an extra null-check here.
        return Path.Combine(dir, baseName + ".wav");
    }

    private static string ResolveMountDir(byte mountId)
    {
        try
        {
            var pluginDir = DalamudApi.PluginInterface?.AssemblyLocation?.DirectoryName;
            if (string.IsNullOrEmpty(pluginDir)) return null;
            return Path.Combine(pluginDir, "assets", "mount-audio", mountId.ToString());
        }
        catch { return null; }
    }

    // ---- Native mount audio suppression ----
    // Walk to the mount character via the player's MountedEntityIds
    // and write SoundVolumeCategoryOverride = NoPlay so the game's
    // native engine hum / gallop loop doesn't play alongside our
    // custom audio pack. Per-frame re-write because the engine may
    // reset the override when the mount actor refreshes its sound
    // state. No cleanup needed — when the player dismounts, the
    // mount character object is destroyed and the override goes
    // with it.
    private static double _lastMuteDiagT;
    private static uint _lastMutedEntityId;
    private static byte _lastObservedOverrideValue;
    private static void SuppressNativeMountSounds()
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var ch = (Character*)lp.Address;
            if (ch == null) return;

            // First entry of MountedEntityIds is the mount actor's
            // entity ID. 0 / 0xE0000000 are sentinel "no mount" values.
            var ids = ch->Mount.MountedEntityIds;
            if (ids.Length == 0) return;
            uint mountEntityId = ids[0];
            if (mountEntityId == 0 || mountEntityId == 0xE0000000) return;

            // Resolve the mount actor through Dalamud's ObjectTable.
            // FFXIV uses a fixed indexing convention for "owned"
            // objects: player at index 0, mount at index 1, minion
            // at index 2. Try the mount slot first — it's O(1) and
            // always correct. Fall back to a full scan matching by
            // GameObjectId (Dalamud's wrapper, which handles the
            // struct-offset correctly) if the slot is empty for
            // some reason. We don't trust raw `Character.EntityId`
            // pointer-arithmetic anymore — observation showed the
            // CS struct offset reads the OwnerId field on this game
            // version, which is why earlier iter-by-EntityId scans
            // missed the mount entirely.
            Dalamud.Game.ClientState.Objects.Types.IGameObject mountObj = null;
            try { mountObj = DalamudApi.ObjectTable[1]; } catch { }
            if (mountObj == null
                || (uint)(ulong)mountObj.GameObjectId != mountEntityId)
            {
                // Slot 1 didn't match — fallback scan by GameObjectId.
                mountObj = null;
                foreach (var obj in DalamudApi.ObjectTable)
                {
                    if (obj == null) continue;
                    if ((uint)(ulong)obj.GameObjectId == mountEntityId)
                    {
                        mountObj = obj;
                        break;
                    }
                }
            }
            if (mountObj == null)
            {
                ThrottledMuteDiag($"mount actor not in ObjectTable (entityId=0x{mountEntityId:X}) — slot[1] empty + GoId scan missed");
                return;
            }
            var mountChar = (Character*)mountObj.Address;
            if (mountChar == null) return;
            string mountObjName = mountObj.Name?.ToString() ?? "";

            byte beforeOverride = mountChar->SoundVolumeCategoryOverride;
            byte beforeCategory = mountChar->SoundVolumeCategory;
            // Aggressive mute: write NoPlay to BOTH the override AND
            // the natural category. Some sound paths check Override
            // first, others read Category directly — clobber both
            // so whichever code path the engine takes for mount
            // engine/idle sounds, our mute is on the path.
            mountChar->SoundVolumeCategoryOverride = (byte)SoundVolumeCategory.NoPlay;
            mountChar->SoundVolumeCategory         = (byte)SoundVolumeCategory.NoPlay;
            byte afterOverride = mountChar->SoundVolumeCategoryOverride;
            byte afterCategory = mountChar->SoundVolumeCategory;
            _lastObservedOverrideValue = beforeOverride;

            if (mountEntityId != _lastMutedEntityId
                || beforeOverride != (byte)SoundVolumeCategory.NoPlay
                || beforeCategory != (byte)SoundVolumeCategory.NoPlay)
            {
                ThrottledMuteDiag(
                    $"writing NoPlay to mount actor entityId=0x{mountEntityId:X} "
                    + $"name='{mountObjName}' "
                    + $"override:{beforeOverride}->{afterOverride} "
                    + $"category:{beforeCategory}->{afterCategory}");
                _lastMutedEntityId = mountEntityId;
            }
        }
        catch (Exception ex)
        {
            ThrottledMuteDiag($"threw: {ex.Message}");
        }
    }

    private static void ThrottledMuteDiag(string msg)
    {
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        if (now - _lastMuteDiagT < 1.0) return;
        _lastMuteDiagT = now;
        try { DalamudApi.PluginLog.Information($"[noWickyXIV] MountAudio mute: {msg}"); } catch { }
    }

    // ---- Animation freeze ----
    // Schedules an N-millisecond window during which the mount's
    // idle animation playback is held still. Used to give the
    // mount.ogg "turn-on" sound space before the bike's idle
    // vibration / engine-running animation begins.
    private static void BeginAnimationFreeze()
    {
        var cfg = noWickyXIV.Config;
        if (!cfg.EnableMountAnimationFreeze) return;
        var ms = MathF.Max(0, cfg.MountAnimationFreezeMs);
        if (ms <= 0) return;
        _animFreezeUntilT = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond
                          + ms / 1000.0;
        _animFreezeActive = true;
    }

    private static void ReleaseAnimationFreeze()
    {
        if (!_animFreezeActive) return;
        _animFreezeActive = false;
        _animFreezeUntilT = 0;
        // Best-effort restore of timeline speed in case the
        // memory write below took effect.
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp != null) WriteMountTimelineSpeed(lp, 1f);
        }
        catch { }
    }

    private static void TickAnimationFreeze(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter lp)
    {
        if (!_animFreezeActive) return;
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        if (now >= _animFreezeUntilT)
        {
            ReleaseAnimationFreeze();
            return;
        }
        // Re-write speed=0 each frame in case the engine resets it.
        WriteMountTimelineSpeed(lp, 0f);
    }

    // Memory write to set the mount character's animation timeline
    // speed multiplier. The exact field path on
    // Character.Mount.MountObject and its CharacterBase / Skeleton
    // chain is not yet verified for current FFXIVClientStructs —
    // the call below is a TODO placeholder that walks the most
    // likely path with try/catch protection. If the path is wrong
    // (or the field name has changed across CS versions), the call
    // silently no-ops via the catch block; the audio + momentum
    // continue to work, the freeze just doesn't take effect.
    //
    // To finish this: read Character.MountContainer's MountObject
    // field, walk to its DrawObject (CharacterBase*), find the
    // Skeleton's animation playback speed control, write the
    // multiplier. Anamnesis / Glamourer source has known-good
    // versions of this walk — port the offset path from there
    // when verifying.
    private static void WriteMountTimelineSpeed(
        Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter lp,
        float speedMultiplier)
    {
        try
        {
            var ch = (Character*)lp.Address;
            if (ch == null) return;
            // FFXIVClientStructs path candidates — uncomment the
            // one that matches the SDK version once verified.
            //
            // // Path A: directly on Mount struct (some SDK versions)
            // ch->Mount.RideTimeOnStart = ...; // not relevant
            //
            // // Path B: via mount object's CharacterBase Timeline
            // var mountObj = ch->Mount.MountObject; // BattleChara*
            // if (mountObj == null) return;
            // var drawObj = mountObj->GameObject.DrawObject;
            // if (drawObj == null) return;
            // var charBase = (CharacterBase*)drawObj;
            // charBase->Timeline.PlaybackSpeed = speedMultiplier;
            //
            // // Path C: via player's animation override on the mount
            // // (reuses the same timeline manipulation Anamnesis uses
            // // for character-pose freeze)
            //
            // For now, no-op: scaffold present, actual write
            // pending struct-offset verification.
            _ = speedMultiplier; // silence unused-var warning
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] MountAudio animation freeze write failed: {ex.Message}"); } catch { }
        }
    }
}
