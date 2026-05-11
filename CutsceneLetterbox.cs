using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;

namespace noWickyXIV;

// Removes cinematic letterbox bars during in-game cutscenes by hooking
// the game's UpdateLetterboxing function and clearing the render flag.
//
// Based on the approach from goaaats/Dalamud.FullscreenCutscenes:
// the letterbox bars are NOT UI addon nodes — they're drawn by the
// game's rendering pipeline. A single bit in a rendering config struct
// controls whether the bars are shown. This hook clears that bit
// each frame during cutscenes.
public static unsafe class CutsceneLetterbox
{
    // Signature for the UpdateLetterboxing function call site.
    // Dalamud's ScanText resolves E8 call targets automatically.
    // This may need updating for new FFXIV patches.
    private const string LetterboxSig = "E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ??";

    // Offset of the letterbox control field in the config struct.
    private const int FieldOffset = 0x40;
    // Bit to clear: bit 5 (0x20) disables the letterbox.
    private const int LetterboxBit = 1 << 5;

    private delegate nint UpdateLetterboxingDelegate(nint thisPtr);
    private static Hook<UpdateLetterboxingDelegate> _hook;
    private static bool _initialized;
    private static string _status = "not initialized";

    public static string Status => _status;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            if (!DalamudApi.SigScanner.TryScanText(LetterboxSig, out var addr) || addr == 0)
            {
                _status = "signature not found (game update may have changed it)";
                DalamudApi.LogInfo($"[Letterbox] {_status}");
                return;
            }

            _hook = DalamudApi.GameInteropProvider.HookFromAddress<UpdateLetterboxingDelegate>(
                addr, UpdateLetterboxingDetour);
            _hook.Enable();
            _status = $"hook installed at 0x{addr:X}";
            DalamudApi.LogInfo($"[Letterbox] {_status}");
        }
        catch (Exception ex)
        {
            _status = $"init failed: {ex.Message}";
            DalamudApi.LogInfo($"[Letterbox] {_status}");
        }
    }

    public static void Update()
    {
        // The hook handles letterbox removal automatically.
        // Update() is kept for compatibility with the main loop.
    }

    public static void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        _initialized = false;
    }

    private static nint UpdateLetterboxingDetour(nint thisPtr)
    {
        try
        {
            if (noWickyXIV.Config.HideCutsceneLetterbox && IsInGameCutscene())
            {
                // Clear the letterbox flag bit before the original processes it.
                *(int*)(thisPtr + FieldOffset) &= ~LetterboxBit;
            }
        }
        catch { }

        return _hook.Original(thisPtr);
    }

    private static bool IsInGameCutscene()
    {
        try
        {
            var cond = DalamudApi.Condition;
            return cond[ConditionFlag.OccupiedInCutSceneEvent]
                || cond[ConditionFlag.WatchingCutscene]
                || cond[ConditionFlag.WatchingCutscene78];
        }
        catch { return false; }
    }
}
