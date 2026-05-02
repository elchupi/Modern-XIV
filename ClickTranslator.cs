using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace noWickyXIV;

// Two layered input translators for "third-person mode":
//
// 1) LMB → numeric hotbar key (Shift/Ctrl modifiers only)
//      LMB         → 2
//      Shift+LMB   → 1
//      Ctrl +LMB   → 3
//    Modifier reconcile: SendKey temporarily releases user-held Shift/Ctrl
//    so Shift+LMB→1 actually arrives as plain "1" not Shift+1.
//
// 2) Physical "2" keypress + forward/back → Shift+1 / Shift+3
//    Forward/back source = W/S keyboard OR gamepad LeftStick Y axis.
//    Implemented via WH_KEYBOARD_LL low-level hook because we need to
//    SUPPRESS the original "2" keypress, not just send extra keys on top.
//    Synthetic-event recursion is avoided via a dwExtraInfo sentinel.
public static class ClickTranslator
{
    // ---- Win32 imports + structs ----
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint   SendInput(uint cInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

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

    // ---- Per-frame: LMB rising-edge handler ----
    public static void Update()
    {
        if (!noWickyXIV.Config.EnableThirdPersonClickTranslation)
        {
            _lmbPrev = false;
            UninstallHook();
            return;
        }

        // Lazy-install the keyboard hook on first frame the feature is on
        EnsureHookInstalled();

        bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool risingEdge = lmb && !_lmbPrev;
        _lmbPrev = lmb;
        if (!risingEdge) return;

        bool kbShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool kbCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        int  vk;
        bool wantShift, wantCtrl;
        if      (kbShift) { vk = VK_1; wantShift = false; wantCtrl = false; }
        else if (kbCtrl)  { vk = VK_3; wantShift = false; wantCtrl = false; }
        else              { vk = VK_2; wantShift = false; wantCtrl = false; }

        SendKey(vk, wantShift, wantCtrl, kbShift, kbCtrl);
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
        if (kb.vkCode != VK_2) goto pass;

        // Decide forward/back from EITHER keyboard W/S OR gamepad stick.
        bool kbW = (GetAsyncKeyState(VK_W) & 0x8000) != 0;
        bool kbS = (GetAsyncKeyState(VK_S) & 0x8000) != 0;

        float stickY = 0f;
        try { stickY = DalamudApi.GamepadState.LeftStick.Y; } catch { }

        if (!_stickDiagLogged)
        {
            _stickDiagLogged = true;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] First 2-key transform attempt: kbW={kbW} kbS={kbS} stick.Y={stickY:F3} threshold=±{STICK_THRESHOLD}");
            } catch { }
        }

        bool forward = kbW || stickY >  STICK_THRESHOLD;
        bool back    = kbS || stickY < -STICK_THRESHOLD;

        if (!forward && !back) goto pass;

        // Suppress the original 2, then synthesize Shift+1 (back) or Shift+3 (forward)
        bool kbShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool kbCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        int  outVk   = forward ? VK_3 : VK_1;
        SendKey(outVk, wantShift: true, wantCtrl: false, kbShift, kbCtrl);
        return new IntPtr(1); // non-zero = swallow the original event

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
