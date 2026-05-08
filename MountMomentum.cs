using System;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace noWickyXIV;

// Speed-carry-over for vehicle mounts via virtual XInput gamepad.
//
// FFXIV's movement input is dual-channel: keyboard WASD is digital
// (binary on/off → server treats as full speed when held), gamepad
// left stick is analog (magnitude 0..1 → server-side speed scales
// linearly with magnitude). By emitting a virtual XInput gamepad
// alongside the user's keyboard, we can make the server interpret a
// tapering magnitude as the player coasting to a stop — the same
// way an analog stick release would coast a real gamepad.
//
// Because the server is the one computing the movement speed from
// the analog input, this is real momentum visible to other clients,
// respected by hitboxes, walls, fall damage, etc. — not client-side
// visual fakery.
//
// Per-frame logic:
//   1. Read W/A/S/D state via GetAsyncKeyState (already used elsewhere).
//   2. Compute target stick vector from keys (W = +Y, S = -Y, etc.).
//   3. Lerp currentVec toward targetVec at one of three rates:
//        accel   — when target magnitude > current (hold-down)
//        coast   — when target = 0 and last input was W release
//        brake   — when target points opposite of current motion
//   4. Emit gamepad axis values (LeftThumbX/Y in [-32767..32767]).
//   5. Skip emission entirely if not on a momentum mount → keyboard
//      remains the sole input, no behavioral change for normal
//      gameplay.
//
// Requires ViGEmBus driver installed on Windows (one-time setup —
// free, signed, kernel driver bundled with most controller-emu
// software). If the driver is missing, Initialize fails gracefully
// with a chat message and the feature stays inert.
public static unsafe class MountMomentum
{
    private static ViGEmClient _client;
    private static IXbox360Controller _pad;
    private static bool _initialized;
    private static bool _driverMissingNotified;

    // Magnitude envelope state. _current is the smoothed [-1..+1]
    // stick vector being emitted; _target is what the keyboard
    // says we should be aiming at. The envelope chooses which lerp
    // rate to apply per axis.
    private static Vector2 _current;
    private static Vector2 _target;
    private static bool   _wasMounted;
    private static byte   _currentMountId;
    // Tracks whether the last "release" was W → the coast envelope
    // applies. If the user simply isn't mounted on a momentum mount,
    // we never emit and the envelope stays at zero.
    private static bool   _wasMomentumActive;

    // Public read for MountAudio's cruise-pitch coupling. Returns
    // |currentVec| in 0..1 — the same magnitude the server sees.
    public static float CurrentMagnitude => _current.Length();

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // No-op on plugin start. Eager pad allocation (even an
    // allocate-and-dispose scrub) momentarily registers a virtual
    // Xbox controller with the OS, which FFXIV's input layer
    // can latch onto and read default state from before we
    // disconnect — that was producing the "stuck running forward"
    // symptom. The real pad is allocated lazily by
    // EnsureInitialized() ONLY when EnableMountMomentum is true
    // AND we're on a momentum mount. Off-mount or feature off,
    // ViGEm is never touched.
    //
    // Note: if a previous plugin instance crashed without calling
    // Dispose (game crash, hot-reload race), a phantom virtual
    // pad owned by the dead process may persist in the bus
    // driver. Our process can't reach those — the only fix is to
    // fully restart FFXIV (or use Device Manager to remove the
    // orphan "Xbox 360 Controller for Windows" entries).
    public static void Initialize() { }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        try
        {
            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.Connect();
            // Submit one zero-state report immediately so the OS sees
            // neutral stick values from the very first frame after
            // Connect. ViGEm's "between Connect and first SubmitReport"
            // window has been observed to register undefined values.
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
            _pad.SubmitReport();
            _initialized = true;
        }
        catch (Exception ex)
        {
            // Most common failure: ViGEmBus driver not installed.
            // Don't crash the plugin — just log + show a chat
            // message once so the user knows.
            if (!_driverMissingNotified)
            {
                _driverMissingNotified = true;
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] MountMomentum init failed: {ex.Message}"); } catch { }
                try { DalamudApi.ChatGui.Print(
                    "[MountMomentum] Virtual gamepad init failed. Install ViGEmBus driver from https://github.com/nefarius/ViGEmBus/releases (one-time, signed) to enable mount momentum. Feature stays inert until then."); } catch { }
            }
            _initialized = false;
        }
    }

    // Tear down the virtual gamepad. Used when the feature flag flips
    // off so we don't leave a phantom Xbox controller hooked into the
    // OS reading our neutral state into the game. The pad will be
    // re-created on demand the next time the feature is enabled.
    private static void TeardownPad()
    {
        try
        {
            if (_pad != null)
            {
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                _pad.SubmitReport();
                _pad.Disconnect();
            }
        }
        catch { }
        try { _client?.Dispose(); } catch { }
        _pad = null;
        _client = null;
        _initialized = false;
        _current = Vector2.Zero;
        _target = Vector2.Zero;
        _wasMomentumActive = false;
    }

    public static void Dispose()
    {
        try
        {
            // Drop magnitude to 0 + submit one final report so we
            // don't leave the game thinking we're still holding the
            // stick after Disconnect.
            if (_pad != null)
            {
                _pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                _pad.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                _pad.SubmitReport();
                _pad.Disconnect();
            }
        }
        catch { }
        try { _client?.Dispose(); } catch { }
        _pad = null;
        _client = null;
        _initialized = false;
        _current = Vector2.Zero;
        _target = Vector2.Zero;
    }

    public static void Update()
    {
        var cfg = noWickyXIV.Config;
        // Feature gate: when disabled, ensure the virtual pad is NOT
        // connected (tear down if previously initialized).
        if (!cfg.EnableMountMomentum)
        {
            if (_initialized) TeardownPad();
            return;
        }

        var lp = DalamudApi.ObjectTable.LocalPlayer;
        if (lp == null)
        {
            if (_initialized) TeardownPad();
            return;
        }

        // Read mount id; gate by user's MountMomentumIds list.
        byte mountId = 0;
        try
        {
            var ch = (Character*)lp.Address;
            if (ch != null) mountId = (byte)ch->Mount.MountId;
        }
        catch { }

        bool mounted = mountId != 0;
        bool isMomentumMount = mounted
            && cfg.MountMomentumIds != null
            && cfg.MountMomentumIds.Contains(mountId);

        // The CRITICAL gate: only allocate / keep the virtual gamepad
        // connected while we are ACTUALLY on a momentum mount. Off
        // the bike → tear it down so FFXIV doesn't read its analog
        // state as input. This was the cause of the stuck-running-
        // forward bug: a connected pad with even neutral state can
        // be misinterpreted by the game's input layer.
        if (!isMomentumMount)
        {
            if (_initialized) TeardownPad();
            // Reset bookkeeping so when we DO mount, we start clean.
            _wasMounted = mounted;
            _currentMountId = mountId;
            _current = Vector2.Zero;
            _target = Vector2.Zero;
            _wasMomentumActive = false;
            return;
        }

        // We're on a momentum mount — bring the pad up if needed.
        EnsureInitialized();
        if (!_initialized) return;

        // Mount changes — reset envelope cleanly so we don't carry
        // stale magnitude across mount swaps / dismounts.
        if (mounted != _wasMounted || mountId != _currentMountId)
        {
            ResetEnvelope();
            _wasMounted = mounted;
            _currentMountId = mountId;
        }

        _wasMomentumActive = true;

        // Keyboard input → target vector.
        // FFXIV default bindings: W = forward (+Y stick), S = back
        // (-Y), A = strafe left (-X), D = strafe right (+X). The
        // server treats analog left-stick the same way.
        bool w = KeyDown(0x57); // W
        bool a = KeyDown(0x41); // A
        bool s = KeyDown(0x53); // S
        bool d = KeyDown(0x44); // D

        Vector2 keyVec = Vector2.Zero;
        if (w) keyVec.Y += 1f;
        if (s) keyVec.Y -= 1f;
        if (d) keyVec.X += 1f;
        if (a) keyVec.X -= 1f;
        if (keyVec.LengthSquared() > 1f) keyVec = Vector2.Normalize(keyVec);
        _target = keyVec;

        // Pick the appropriate lerp rate per-frame:
        //   accel — when current magnitude is rising (rev up)
        //   brake — when target is opposite-direction of current
        //           (S held while still moving forward, etc.)
        //   coast — when target = 0 (release coasting)
        float seconds;
        if (_target.LengthSquared() < 0.001f)
        {
            // No input. Coast phase.
            seconds = MathF.Max(0.05f, cfg.MountMomentumCoastSec);
        }
        else if (Vector2.Dot(_target, _current) < 0f && _current.LengthSquared() > 0.04f)
        {
            // Target is opposing current motion ⇒ brake.
            seconds = MathF.Max(0.05f, cfg.MountMomentumBrakeSec);
        }
        else
        {
            // Accel / continuing in same direction.
            seconds = MathF.Max(0.05f, cfg.MountMomentumAccelSec);
        }

        LerpCurrent(seconds);
        Emit();
    }

    // Time-constant lerp: at the configured "seconds to full," the
    // current value covers ~63% of the gap per second-equivalent.
    // Using exp-lerp so the asymptotic approach feels natural
    // (matches a real damped-spring response).
    private static void LerpCurrent(float seconds)
    {
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        if (dt <= 0f) dt = 0.016f;
        float rate = 1f / seconds;
        float k = 1f - MathF.Exp(-rate * dt);
        _current += (_target - _current) * k;

        // Snap to zero on a low-magnitude tail so we don't dribble
        // tiny non-zero stick values forever after a coast ends.
        if (_target.LengthSquared() < 0.001f && _current.LengthSquared() < 0.0008f)
            _current = Vector2.Zero;
    }

    private static void Emit()
    {
        try
        {
            short x = (short)Math.Clamp((int)(_current.X * 32767f), -32767, 32767);
            short y = (short)Math.Clamp((int)(_current.Y * 32767f), -32767, 32767);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, x);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, y);
            _pad.SubmitReport();
        }
        catch { /* defensive */ }
    }

    private static void ResetEnvelope()
    {
        _current = Vector2.Zero;
        _target = Vector2.Zero;
        Emit();
        _wasMomentumActive = false;
    }
}
