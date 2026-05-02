using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace noWickyXIV;

// Third-person click translator: routes LMB to numeric hotbar keys with
// modifier-driven slot selection. Movement modifiers (forward/back) come
// from EITHER WASD keyboard OR the controller left stick — whichever the
// user has active.
//
// Mapping (priority order):
//   forward + LMB  → Shift+3   (W held OR LeftStick.Y > +threshold)
//   back    + LMB  → Shift+1   (S held OR LeftStick.Y < -threshold)
//   Shift   + LMB  → 1
//   Ctrl    + LMB  → 3
//   LMB            → 2
//
// Movement modifier (forward/back) WINS over Shift/Ctrl when both held.
public static class ClickTranslator
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint   SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

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

    private const uint INPUT_KEYBOARD   = 1;
    private const uint KEYEVENTF_KEYUP  = 2;

    private const int VK_LBUTTON = 0x01;
    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_W       = 0x57;
    private const int VK_S       = 0x53;
    private const int VK_1       = 0x31;
    private const int VK_2       = 0x32;
    private const int VK_3       = 0x33;

    // Stick deflection above this counts as "held" — small dead-zone above
    // the typical FFXIV-side dead-zone so it triggers reliably without false
    // positives from light stick drift.
    private const float STICK_THRESHOLD = 0.5f;

    private static bool _lmbPrev;

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableThirdPersonClickTranslation) { _lmbPrev = false; return; }

        bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool risingEdge = lmb && !_lmbPrev;
        _lmbPrev = lmb;
        if (!risingEdge) return;

        // Modifier reads — keyboard (always) + gamepad stick (when present).
        bool kbShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
        bool kbCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool kbW     = (GetAsyncKeyState(VK_W)       & 0x8000) != 0;
        bool kbS     = (GetAsyncKeyState(VK_S)       & 0x8000) != 0;

        float stickY = 0f;
        try { stickY = DalamudApi.GamepadState.LeftStick.Y; } catch { }

        bool forward = kbW || stickY >  STICK_THRESHOLD;
        bool back    = kbS || stickY < -STICK_THRESHOLD;

        // Decide target. Forward/back win over Shift/Ctrl. If both forward
        // AND back somehow asserted (impossible on a stick, possible if user
        // holds W and S on keyboard), forward wins.
        int  vk;
        bool wantShift;
        bool wantCtrl;
        if      (forward) { vk = VK_3; wantShift = true;  wantCtrl = false; }
        else if (back)    { vk = VK_1; wantShift = true;  wantCtrl = false; }
        else if (kbShift) { vk = VK_1; wantShift = false; wantCtrl = false; }  // explicit: NO shift around the 1
        else if (kbCtrl)  { vk = VK_3; wantShift = false; wantCtrl = false; }  // explicit: NO ctrl around the 3
        else              { vk = VK_2; wantShift = false; wantCtrl = false; }

        SendKey(vk, wantShift, wantCtrl, kbShift, kbCtrl);
    }

    // Reconcile each modifier between what the user is currently holding and
    // what the synthetic keypress NEEDS to be wrapped in:
    //   user=Y, want=Y → no-op (already held)
    //   user=N, want=Y → press before, release after
    //   user=Y, want=N → release before, press after  ← the case the user hit
    //                                                   when Shift+LMB still
    //                                                   produced Shift+1
    //   user=N, want=N → no-op
    private static void SendKey(int vk, bool wantShift, bool wantCtrl, bool userShift, bool userCtrl)
    {
        var inputs = new List<INPUT>(8);

        // PRE: get modifier state to match wantX
        if (userShift && !wantShift) inputs.Add(MakeKey(VK_SHIFT,   true));   // release
        if (!userShift && wantShift) inputs.Add(MakeKey(VK_SHIFT,   false));  // press
        if (userCtrl  && !wantCtrl)  inputs.Add(MakeKey(VK_CONTROL, true));
        if (!userCtrl && wantCtrl)   inputs.Add(MakeKey(VK_CONTROL, false));

        // The key itself
        inputs.Add(MakeKey(vk, false));
        inputs.Add(MakeKey(vk, true));

        // POST: restore the user's original modifier state
        if (userShift && !wantShift) inputs.Add(MakeKey(VK_SHIFT,   false));  // re-press
        if (!userShift && wantShift) inputs.Add(MakeKey(VK_SHIFT,   true));   // re-release
        if (userCtrl  && !wantCtrl)  inputs.Add(MakeKey(VK_CONTROL, false));
        if (!userCtrl && wantCtrl)   inputs.Add(MakeKey(VK_CONTROL, true));

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKey(int vk, bool keyUp) => new INPUT
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u } }
    };
}
