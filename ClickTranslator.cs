using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace noWickyXIV;

// Two layered input translators for "third-person mode":
//
// 1) LMB → Shift+<n>  (RMB acts as virtual Ctrl)
//      LMB              → Shift+2
//      Shift+LMB        → Shift+3
//      Ctrl +LMB        → Shift+1
//      RMB  +LMB        → Shift+1   (RMB-as-Ctrl rule)
//
// 2) Physical 1/2/3 keypress + RMB via WH_KEYBOARD_LL hook:
//      RMB + 1          → Hotbar 3 Slot 1 (≡ Ctrl+1)
//      RMB + 2          → Hotbar 3 Slot 2 (≡ Ctrl+2)
//      RMB + 3          → Hotbar 3 Slot 3 (≡ Ctrl+3)
//    Plain 1/2/3 keypresses (no modifier) pass through unchanged.
//    Synthetic-event recursion avoided via a dwExtraInfo sentinel.
public static class ClickTranslator
{
    // ---- Win32 imports + structs ----
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint   SendInput(uint cInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Cache our own PID once — the plugin lives in-process with FFXIV, so a
    // foreground window owned by the same PID means FFXIV has focus.
    private static readonly uint _selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    private static bool IsGameForeground()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == _selfPid;
        }
        catch { return false; }
    }

    // Translation only fires when the player has their weapon drawn — out of
    // combat stance, LMB should behave normally so the player can interact
    // with NPCs / nodes / UI. StatusFlags.WeaponOut is the engine-side bit
    // that tracks weapon-drawn vs sheathed.
    private static bool IsWeaponDrawn()
    {
        try
        {
            var p = DalamudApi.ObjectTable.LocalPlayer;
            if (p == null) return false;
            return (p.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.WeaponOut) != 0;
        }
        catch { return false; }
    }

    // True when the local player has a HOSTILE BattleNpc currently
    // targeted. With Crosshair auto-target picking enemies for the
    // user, we want LMB to translate into the hotbar action even
    // before the weapon is drawn — the engine auto-draws on the
    // first action use, so the gate just has to acknowledge that
    // a hostile target exists.
    private static bool HasHostileTarget()
    {
        try
        {
            var t = DalamudApi.TargetManager?.Target;
            if (t is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn) return false;
            if (!bn.IsTargetable) return false;
            if (bn.CurrentHp <= 0) return false;
            // BattleNpcKind 2 = friendly NPC. Anything else is fair
            // game (enemy / striking dummy / event mob).
            if ((byte)bn.BattleNpcKind == 2) return false;
            return true;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const uint INPUT_KEYBOARD   = 1;
    private const uint KEYEVENTF_KEYUP  = 2;
    private const int  WH_KEYBOARD_LL   = 13;
    private const int  WM_KEYDOWN       = 0x0100;
    private const int  WM_SYSKEYDOWN    = 0x0104;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_W       = 0x57;
    private const int VK_S       = 0x53;
    private const int VK_1       = 0x31;
    private const int VK_2       = 0x32;
    private const int VK_3       = 0x33;

    // Sentinel placed in dwExtraInfo on every synthetic event we generate,
    // so the LL hook can recognize them and not recurse into transformation.
    // Any non-trivial constant works; "noWickyXIV".GetHashCode() at compile.
    private static readonly nint SYNTHETIC_TAG = unchecked((nint)0x6E6F574B49495631); // "noWKIIV1"

    // Stick deflection threshold. Adjusted so light drift doesn't trigger.
    private const float STICK_THRESHOLD = 0.5f;

    // Hotbar mapping — assumes FFXIV-default keybinds:
    //   plain numbers   → Hotbar 1 (idx 0)
    //   Shift+numbers   → Hotbar 2 (idx 1)
    //   Ctrl +numbers   → Hotbar 3 (idx 2)
    // Slots are 0-indexed (slot 1 = idx 0, slot 2 = idx 1, slot 3 = idx 2).
    private const int HOTBAR_PLAIN = 0;
    private const int HOTBAR_SHIFT = 1;
    private const int HOTBAR_CTRL  = 2;

    // Send a Shift+<vk> chord via SendInput. Routes through the OS input stack
    // so FFXIV's keybind handler sees it like a physical keypress.
    //
    // FFXIV polls GetAsyncKeyState for modifier state on its own frame cycle,
    // not via WM message order. If we send Shift-down + 2-down + 2-up + Shift-up
    // in one batch, the entire chord completes in microseconds and by the time
    // FFXIV processes the "2" message its async-state for Shift already reads
    // FALSE. Result: game sees plain "2" instead of Shift+2 (other apps render
    // "@" because they use message-order, which is why this looked OK there).
    //
    // Fix: hold each transition across enough wall time for FFXIV to poll at
    // least once between them. Done on a worker thread so we don't block
    // Framework.Update; ordering is preserved per-task.
    //
    // physicalShift = whether the user is currently holding Shift physically.
    // If they are, we just tap the digit (game already sees Shift held).
    private const int CHORD_HOLD_MS = 25; // ~1.5 game frames at 60fps

    private static void SendShiftDigit(int digitVk, bool physicalShift)
        => SendModifierDigit(VK_SHIFT, digitVk, physicalShift);

    private static void SendCtrlDigit(int digitVk, bool physicalCtrl)
        => SendModifierDigit(VK_CONTROL, digitVk, physicalCtrl);

    private static void SendModifierDigit(int modifierVk, int digitVk, bool physicalModifier)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var single = new INPUT[1];
                if (!physicalModifier)
                {
                    single[0] = MakeKey(modifierVk, false);
                    SendInput(1, single, Marshal.SizeOf<INPUT>());
                    System.Threading.Thread.Sleep(CHORD_HOLD_MS);
                }
                single[0] = MakeKey(digitVk, false);
                SendInput(1, single, Marshal.SizeOf<INPUT>());
                System.Threading.Thread.Sleep(CHORD_HOLD_MS);
                single[0] = MakeKey(digitVk, true);
                SendInput(1, single, Marshal.SizeOf<INPUT>());
                if (!physicalModifier)
                {
                    System.Threading.Thread.Sleep(CHORD_HOLD_MS);
                    single[0] = MakeKey(modifierVk, true);
                    SendInput(1, single, Marshal.SizeOf<INPUT>());
                }
            }
            catch { /* worker thread; swallow */ }
        });
    }

    private static bool _lmbPrev;
    private static IntPtr _hookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc? _hookProc; // keep alive — Win32 holds raw fnptr

    // Per-session diagnostic — log stick value on first translated event so
    // we can see if the gamepad service is actually returning values.
    private static bool _stickDiagLogged;

    // ---- Lifecycle ----
    public static void EnsureHookInstalled()
    {
        if (_hookHandle != IntPtr.Zero) return;
        try
        {
            _hookProc = HookCallback;
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            if (_hookHandle == IntPtr.Zero)
                DalamudApi.PluginLog.Warning("[noWickyXIV] WH_KEYBOARD_LL install failed (LastError=" + Marshal.GetLastWin32Error() + ")");
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] LL hook install threw: {ex.Message}"); } catch { }
        }
    }

    public static void UninstallHook()
    {
        if (_hookHandle == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hookHandle); } catch { }
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
    }

    // While the translator is active we force FFXIV's "MouseBothClick" config
    // off, otherwise LMB+RMB (used for the Ctrl/RMB-modifier path) would also
    // make the character run forward. Track the original value so we restore
    // it whenever the active state ends (weapon sheathed, focus lost, plugin
    // disabled, or unload).
    private static bool _bothClickForcedOff;
    private static bool _bothClickOriginal;

    private static void SetBothClickOverride(bool forceOff)
    {
        try
        {
            if (forceOff && !_bothClickForcedOff)
            {
                if (DalamudApi.GameConfig.System.TryGet("MouseBothClick", out bool original))
                    _bothClickOriginal = original;
                DalamudApi.GameConfig.System.Set("MouseBothClick", false);
                _bothClickForcedOff = true;
            }
            else if (!forceOff && _bothClickForcedOff)
            {
                DalamudApi.GameConfig.System.Set("MouseBothClick", _bothClickOriginal);
                _bothClickForcedOff = false;
            }
        }
        catch { /* GameConfig not ready (e.g. not logged in); retry next tick */ }
    }

    // ---- Per-frame: LMB rising-edge handler ----
    public static void Update()
    {
        if (!noWickyXIV.Config.EnableThirdPersonClickTranslation)
        {
            _lmbPrev = false;
            SetBothClickOverride(false);
            CancelAutoCycle();
            UninstallHook();
            return;
        }

        // Lazy-install the keyboard hook on first frame the feature is on
        EnsureHookInstalled();

        // Drive any in-flight auto-cycle. Runs on the main game thread
        // so the GCD probe can safely read game memory.
        TickAutoCycle();

        // Drive the MouseBothClick override off the same gates that would
        // allow translation to fire — only force-off when the user is
        // actually in a state where LMB+RMB would otherwise auto-walk them
        // out from under their click. IsLoggedIn keeps us dormant on the
        // title / character-select / login screens.
        bool loggedIn = false;
        try { loggedIn = DalamudApi.ClientState.IsLoggedIn; } catch { }
        // Translation fires when EITHER weapon is drawn OR a hostile
        // target is selected. The hostile-target branch lets the
        // crosshair auto-target → LMB → action chain work even while
        // sheathed — the engine auto-draws on the first ability use.
        bool active = loggedIn && IsGameForeground()
                   && (IsWeaponDrawn() || HasHostileTarget());
        SetBothClickOverride(active);

        bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool risingEdge = lmb && !_lmbPrev;
        _lmbPrev = lmb;
        if (!risingEdge) return;

        if (!active) return;

        bool kbShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool kbCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool rmb     = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        // RMB is a virtual Ctrl modifier (per user spec).
        bool ctrlLike = kbCtrl || rmb;

        // Auto-cycle: a single LMB click runs the FULL combo cycle for the
        // current positional, GCD-paced. Same suppression rules as the
        // per-click injector — any manual modifier or True North / Meikyo
        // falls back through to the single-click path below.
        if (noWickyXIV.Config.EnablePositionalAutoCycle
            && !kbShift && !ctrlLike
            && !HasPositionalBypassBuff()
            && HasHostileTarget())
        {
            StartAutoCycle();
            return;
        }

        // Positional auto-injection: when no manual modifier is held and the
        // current target is a hostile BattleNpc, swap the default rear path
        // for a Shift/Ctrl-like virtual modifier based on which arc the
        // player is standing in. Manual modifiers always win. True North
        // (1250) OR Meikyo Shisui (1233) suppresses the swap entirely —
        // both buffs make positional/combo reqs irrelevant, so let the
        // user drive the slot manually.
        bool autoShift = false;
        bool autoCtrlLike = false;
        if (noWickyXIV.Config.EnablePositionalAutoLmb && !kbShift && !ctrlLike && !HasPositionalBypassBuff())
        {
            switch (GetTargetPositional())
            {
                case PositionalZone.Front: autoCtrlLike = true; break;
                case PositionalZone.Flank: autoShift    = true; break;
            }
        }

        // All LMB outputs are Shift+<digit>:
        //   plain LMB    → Shift+2
        //   Shift+LMB    → Shift+3 (user already holds Shift; just send 3)
        //   Ctrl/RMB+LMB → Shift+1
        int digitVk;
        bool effectiveShift = kbShift || autoShift;
        bool effectiveCtrlLike = ctrlLike || autoCtrlLike;
        if      (effectiveShift)    digitVk = VK_3;
        else if (effectiveCtrlLike) digitVk = VK_1;
        else                        digitVk = VK_2;

        // physicalShift only true when user is actually holding Shift —
        // for the auto-flank path we still need to synthesize the Shift
        // modifier ourselves.
        SendShiftDigit(digitVk, kbShift);
    }

    private const uint STATUS_TRUE_NORTH    = 1250;
    private const uint STATUS_MEIKYO_SHISUI = 1233;

    // Auto-cycle state machine, driven from Update() which runs on
    // the Framework thread (main game thread). Game-memory reads such
    // as ActionManager.GetRecastTime are only safe on this thread —
    // running them from a Task.Run worker returned bogus data and
    // tripped the "registered" check, which is why mid-sequence chords
    // were dropping.
    private enum CyclePhase { Idle, WaitingToRegister, WaitingForTail }
    private static CyclePhase _cyclePhase = CyclePhase.Idle;
    private static int[]?     _cycleSeq;
    private static int        _cycleIdx;
    private static double     _cyclePhaseStartS;
    // Previous-frame GCD remaining, used to detect the rising edge
    // (old GCD ending → new GCD starting) that means our chord
    // actually landed. Sentinel -1 = "first frame of phase".
    private static float      _cyclePrevRemaining;

    // SAM weaponskill IDs — all share the global GCD recast group, so
    // probing any of them reads the GCD timer. We probe multiple
    // because individual replaced actions (e.g. Hakaze → Gyofu at
    // lv92) can report 0 from GetRecastTime even when the GCD is
    // running. Taking the max across the set is robust to any one of
    // them being inert.
    private static readonly uint[] SAM_GCD_PROBE_IDS = new uint[]
    {
        7477,   // Hakaze   (lv1, upgraded to Gyofu at 92)
        36963,  // Gyofu    (lv92)
        7478,   // Jinpu
        7479,   // Shifu
        7480,   // Yukikaze
        7481,   // Gekko
        7482,   // Kasha
    };

    private static unsafe float GetGcdRemainingSeconds()
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return 0f;
            float maxRemaining = 0f;
            foreach (var id in SAM_GCD_PROBE_IDS)
            {
                float total   = am->GetRecastTime(ActionType.Action, id);
                float elapsed = am->GetRecastTimeElapsed(ActionType.Action, id);
                float remaining = total - elapsed;
                if (remaining > maxRemaining) maxRemaining = remaining;
            }
            return maxRemaining;
        }
        catch { return 0f; }
    }

    private const float CYCLE_REGISTER_THRESHOLD_S = 0.3f;
    private const double CYCLE_REGISTER_TIMEOUT_S  = 1.5;
    private const double CYCLE_MAX_WAIT_S          = 5.0;

    private static double NowSeconds() => Environment.TickCount64 / 1000.0;

    private static void StartAutoCycle()
    {
        // Chord 0 is always the opener (Hakaze). It doesn't matter
        // which slot we hit — every "Hakaze" slot fires action-id
        // Hakaze, which advances the shared combo state. The tail of
        // the sequence (chord 1 onwards) is filled in once chord 0
        // lands so the player's positional is sampled FRESH after the
        // opener resolves — letting them dance into a new arc during
        // chord 0's GCD and have the cycle adapt.
        _cycleSeq = new[] { VK_2 };
        _cycleIdx = 0;
        SendShiftDigit(_cycleSeq[0], false);
        _cyclePhase = CyclePhase.WaitingToRegister;
        _cyclePhaseStartS = NowSeconds();
        _cyclePrevRemaining = -1f;
    }

    // Append the positional-dependent tail to the cycle sequence,
    // sampled at call time. Called once chord 0 has registered. Combo
    // state in FFXIV locks after chord 1 (Shifu commits to Kasha,
    // Jinpu commits to Gekko, Yukikaze terminates) so we don't need
    // to re-sample again later.
    private static void ResolveCycleTail()
    {
        if (_cycleSeq == null) return;
        var zone = GetTargetPositional();
        int[] tail = zone switch
        {
            PositionalZone.Front => new[] { VK_1 },          // → yukikaze
            PositionalZone.Flank => new[] { VK_3, VK_3 },    // → shifu → kasha
            _                    => new[] { VK_2, VK_2 },    // → jinpu → gekko
        };
        var combined = new int[_cycleSeq.Length + tail.Length];
        _cycleSeq.CopyTo(combined, 0);
        tail.CopyTo(combined, _cycleSeq.Length);
        _cycleSeq = combined;
    }

    private static void CancelAutoCycle()
    {
        _cyclePhase = CyclePhase.Idle;
        _cycleSeq = null;
    }

    // Drive the cycle one frame. Called from Update() so all game-
    // memory reads (ActionManager.GetRecastTime) are on the main
    // thread. Returns true if the cycle is active (caller should not
    // re-enter single-click logic).
    private static void TickAutoCycle()
    {
        if (_cyclePhase == CyclePhase.Idle || _cycleSeq == null) return;

        // FFXIV's action queue accepts only in the last ~0.5s of the
        // GCD; firing earlier silently drops the input. 0.4 sits just
        // inside that window with headroom for input latency.
        const float windowSec = 0.4f;

        double phaseElapsed = NowSeconds() - _cyclePhaseStartS;
        if (phaseElapsed > CYCLE_MAX_WAIT_S) { CancelAutoCycle(); return; }

        float remaining = GetGcdRemainingSeconds();

        if (_cyclePhase == CyclePhase.WaitingToRegister)
        {
            // Detect a rising edge in GCD remaining — old GCD wound
            // down past 0 and a fresh GCD just kicked off. A simple
            // "remaining > threshold" check would falsely register on
            // the OLD GCD's tail (e.g. 0.39s remaining after firing
            // at 0.4) and immediately fire the next chord, which
            // would collide with the queue.
            if (_cyclePrevRemaining >= 0f &&
                remaining > _cyclePrevRemaining + 0.5f)
            {
                _cyclePhase = CyclePhase.WaitingForTail;
                _cyclePhaseStartS = NowSeconds();
                _cyclePrevRemaining = remaining;
                return;
            }
            _cyclePrevRemaining = remaining;
            // Chord didn't register within the timeout — bail out
            // rather than spam the rest of the sequence into the void.
            if (phaseElapsed > CYCLE_REGISTER_TIMEOUT_S) CancelAutoCycle();
            return;
        }

        if (_cyclePhase == CyclePhase.WaitingForTail)
        {
            if (remaining > windowSec) return;

            // First time we're about to fire a follow-up (sequence
            // still only holds the opener) — sample positional NOW so
            // the chord-1 slot reflects where the player actually is
            // at the moment of firing, not where they were when chord
            // 0 landed seconds ago. After chord 1, FFXIV's combo state
            // locks the branch so chord 2 is determined.
            if (_cycleSeq.Length == 1)
                ResolveCycleTail();

            if (_cycleSeq == null) { CancelAutoCycle(); return; }

            _cycleIdx++;
            if (_cycleIdx >= _cycleSeq.Length) { CancelAutoCycle(); return; }
            SendShiftDigit(_cycleSeq[_cycleIdx], false);
            if (_cycleIdx >= _cycleSeq.Length - 1) { CancelAutoCycle(); return; }
            _cyclePhase = CyclePhase.WaitingToRegister;
            _cyclePhaseStartS = NowSeconds();
            _cyclePrevRemaining = -1f;
        }
    }

    private static bool HasPositionalBypassBuff()
    {
        try
        {
            var p = DalamudApi.ObjectTable.LocalPlayer;
            if (p == null) return false;
            var statuses = p.StatusList;
            if (statuses == null) return false;
            for (int i = 0; i < statuses.Length; i++)
            {
                var s = statuses[i];
                if (s == null) continue;
                if (s.StatusId == STATUS_TRUE_NORTH || s.StatusId == STATUS_MEIKYO_SHISUI)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    private enum PositionalZone { Rear, Front, Flank }

    private static PositionalZone GetTargetPositional()
    {
        try
        {
            var self = DalamudApi.ObjectTable.LocalPlayer;
            if (self == null) return PositionalZone.Rear;
            var t = DalamudApi.TargetManager?.Target;
            if (t is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn) return PositionalZone.Rear;
            if ((byte)bn.BattleNpcKind == 2) return PositionalZone.Rear;

            // FFXIV rotation 0 = facing -Z. Forward = (-sin θ, 0, -cos θ).
            float ex = -MathF.Sin(bn.Rotation);
            float ez = -MathF.Cos(bn.Rotation);
            float dx = self.Position.X - bn.Position.X;
            float dz = self.Position.Z - bn.Position.Z;

            float fwd   = dx * ex + dz * ez;          // +front, -rear
            float right = dx * ez - dz * ex;          // ± flank

            // Symmetric 90° quadrants by |right| vs fwd.
            if (fwd >  MathF.Abs(right)) return PositionalZone.Front;
            if (fwd < -MathF.Abs(right)) return PositionalZone.Rear;
            return PositionalZone.Flank;
        }
        catch { return PositionalZone.Rear; }
    }

    // ---- Low-level keyboard hook: physical "2" + forward/back → transform ----
    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) goto pass;
        if (!noWickyXIV.Config.EnableThirdPersonClickTranslation) goto pass;

        int msg = wParam.ToInt32();
        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN) goto pass;

        KBDLLHOOKSTRUCT kb;
        try { kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam); }
        catch { goto pass; }

        // Don't transform our own synthetic events
        if (kb.dwExtraInfo == SYNTHETIC_TAG) goto pass;

        // Only intercept number row 1/2/3
        bool isOne   = kb.vkCode == VK_1;
        bool isTwo   = kb.vkCode == VK_2;
        bool isThree = kb.vkCode == VK_3;
        if (!isOne && !isTwo && !isThree) goto pass;

        bool kbShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool kbCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool rmb     = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        // -- RMB-as-Ctrl rule (highest priority): RMB held + 1/2/3 → Ctrl+<n>.
        // Gate matches the LMB translator above: weapon drawn OR hostile
        // target selected. Lets the auto-target → ability chain work
        // while sheathed.
        if (rmb && IsGameForeground() && (IsWeaponDrawn() || HasHostileTarget()))
        {
            int digitVk = isOne ? VK_1 : (isTwo ? VK_2 : VK_3);
            SendCtrlDigit(digitVk, kbCtrl);
            return new IntPtr(1); // suppress original digit
        }

        // (forward/back + 2 rule removed — redundant with RMB-as-Ctrl above.)

    pass:
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // ---- Modifier-aware SendInput wrapper ----
    private static void SendKey(int vk, bool wantShift, bool wantCtrl, bool userShift, bool userCtrl)
    {
        var inputs = new List<INPUT>(8);

        // PRE: reconcile modifiers
        if (userShift && !wantShift) inputs.Add(MakeKey(VK_SHIFT,   true));
        if (!userShift && wantShift) inputs.Add(MakeKey(VK_SHIFT,   false));
        if (userCtrl  && !wantCtrl)  inputs.Add(MakeKey(VK_CONTROL, true));
        if (!userCtrl && wantCtrl)   inputs.Add(MakeKey(VK_CONTROL, false));

        // The key itself
        inputs.Add(MakeKey(vk, false));
        inputs.Add(MakeKey(vk, true));

        // POST: restore user's original modifier state
        if (userShift && !wantShift) inputs.Add(MakeKey(VK_SHIFT,   false));
        if (!userShift && wantShift) inputs.Add(MakeKey(VK_SHIFT,   true));
        if (userCtrl  && !wantCtrl)  inputs.Add(MakeKey(VK_CONTROL, false));
        if (!userCtrl && wantCtrl)   inputs.Add(MakeKey(VK_CONTROL, true));

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKey(int vk, bool keyUp) => new INPUT
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT
        {
            wVk = (ushort)vk,
            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u,
            dwExtraInfo = SYNTHETIC_TAG
        }}
    };
}
