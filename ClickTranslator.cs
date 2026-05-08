using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
            UninstallHook();
            return;
        }

        // Lazy-install the keyboard hook on first frame the feature is on
        EnsureHookInstalled();

        // Drive the MouseBothClick override off the same gates that would
        // allow translation to fire — only force-off when the user is
        // actually in a state where LMB+RMB would otherwise auto-walk them
        // out from under their click. IsLoggedIn keeps us dormant on the
        // title / character-select / login screens.
        bool loggedIn = false;
        try { loggedIn = DalamudApi.ClientState.IsLoggedIn; } catch { }
        bool active = loggedIn && IsGameForeground() && IsWeaponDrawn();
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

        // All LMB outputs are Shift+<digit>:
        //   plain LMB    → Shift+2
        //   Shift+LMB    → Shift+3 (user already holds Shift; just send 3)
        //   Ctrl/RMB+LMB → Shift+1
        int digitVk;
        if      (kbShift)  digitVk = VK_3;
        else if (ctrlLike) digitVk = VK_1;
        else               digitVk = VK_2;

        SendShiftDigit(digitVk, kbShift);
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
        // Gate on FFXIV-foreground + weapon-drawn so the chord doesn't leak
        // into other apps and doesn't trigger out of combat stance (where
        // plain hotbar slots are usually what the player wants).
        if (rmb && IsGameForeground() && IsWeaponDrawn())
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
