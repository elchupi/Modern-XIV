using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace noWickyXIV;

// Hooks SoundManager.PlaySound to suppress mount-engine sound paths
// while the player is mounted on a mount with a custom audio pack
// loaded. The SoundVolumeCategoryOverride / Category writes on the
// mount character don't reliably silence the engine's mount idle /
// startup loops on this game version, so we filter at the play-call
// site instead.
//
// Strategy:
//   1. Log every distinct sound PATH passing through PlaySound (once
//      per path, gated by Config.LogMountSoundPaths). User mounts the
//      target mount, identifies the engine sound paths from /xllog,
//      pastes them into Config.MountAudioMutePatterns.
//   2. While mounted AND a custom pack is loaded, any PlaySound whose
//      path matches one of those patterns is suppressed (we never
//      call the original).
public static unsafe class MountSoundFilter
{
    // Member function — first param is the SoundManager*. CStringPointer
    // is a raw byte* in interop terms. Bools come across as single-byte
    // values. Return type isn't documented in CS XML; void works for our
    // forwarding purposes since we either suppress (no original) or
    // call original and discard whatever it returns.
    private delegate void PlaySoundDelegate(
        IntPtr mgr,
        byte*  path,
        float  p2,
        uint   p3,
        float  p4,
        float  p5,
        float  p6,
        float  p7,
        int    p8,
        uint   p9,
        byte   p10,
        byte   p11,    // SoundVolumeCategory
        byte   p12,
        int    p13,
        byte   p14,
        byte   p15,
        byte   p16,
        byte   p17);

    private static Hook<PlaySoundDelegate> _hook;
    private static bool _initialized;

    // De-duplication of "first time we saw this path" log lines so we
    // don't spam /xllog with the same engine loop path 60 times per
    // second.
    private static readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _suppressedPathsLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _seenLock = new();

    // Hard kill-switch: when true, Initialize() is a no-op AND any
    // already-installed hook's Detour calls Original immediately
    // without consulting any patterns. Set to true to fully neuter
    // the filter at runtime, regardless of what patterns are in
    // config or whether the hook is somehow still installed from a
    // previous plugin instance.
    public const bool ForceDisabled = true;

    public static void Initialize()
    {
        if (ForceDisabled) return;
        if (_initialized) return;
        try
        {
            var addr = (nint)SoundManager.MemberFunctionPointers.PlaySound;
            if (addr == 0)
            {
                try { DalamudApi.PluginLog.Warning(
                    "[noWickyXIV] MountSoundFilter: SoundManager.PlaySound address not resolved by ClientStructs."); } catch { }
                return;
            }
            _hook = DalamudApi.GameInteropProvider.HookFromAddress<PlaySoundDelegate>(addr, Detour);
            _hook.Enable();
            _initialized = true;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] MountSoundFilter resolved: PlaySound=0x{addr:X}"); } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] MountSoundFilter init threw: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        _initialized = false;
        lock (_seenLock) { _seenPaths.Clear(); _suppressedPathsLogged.Clear(); }
    }

    private static void Detour(
        IntPtr mgr,
        byte*  path,
        float  p2, uint p3, float p4, float p5, float p6, float p7,
        int    p8, uint p9, byte p10, byte p11, byte p12, int p13,
        byte   p14, byte p15, byte p16, byte p17)
    {
        // Hard kill-switch — any leftover hook from a previous
        // plugin instance falls through to Original immediately
        // without consulting any patterns or game state.
        if (ForceDisabled)
        {
            try { _hook.Original(mgr, path, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17); }
            catch { }
            return;
        }

        // Defensive: anything thrown in the detour can break audio for
        // the entire game session. Catch, swallow, then forward.
        bool suppress = false;
        try
        {
            var cfg = noWickyXIV.Config;
            string pathStr = ReadCString(path);

            // Log new paths once per session for diagnosis. Throttled
            // by the OrdinalIgnoreCase set so common loops don't spam.
            if (!string.IsNullOrEmpty(pathStr) && cfg.LogMountSoundPaths)
            {
                bool isNew;
                lock (_seenLock) isNew = _seenPaths.Add(pathStr);
                if (isNew)
                {
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] PlaySound new path: '{pathStr}'"); } catch { }
                }
            }

            // Filter — only when mount audio is enabled, the player is
            // currently mounted, and a custom pack is loaded for this
            // mount. Path matched against user-configured substrings.
            if (cfg.EnableMountAudio
                && !string.IsNullOrEmpty(pathStr)
                && cfg.MountAudioMutePatterns != null
                && cfg.MountAudioMutePatterns.Count > 0
                && IsMountedWithCustomPack())
            {
                for (int i = 0; i < cfg.MountAudioMutePatterns.Count; i++)
                {
                    var pat = cfg.MountAudioMutePatterns[i];
                    if (string.IsNullOrEmpty(pat)) continue;
                    if (pathStr.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suppress = true;
                        // Log every suppression with the matching
                        // pattern so the user can diagnose over-broad
                        // mute patterns. Throttled per (path,pattern)
                        // pair so the log doesn't spam — same path +
                        // pattern pair only logs once per session.
                        if (cfg.LogMountSoundPaths)
                        {
                            string key = $"{pathStr}|{pat}";
                            bool isNew;
                            lock (_seenLock) isNew = _suppressedPathsLogged.Add(key);
                            if (isNew)
                            {
                                try { DalamudApi.PluginLog.Information(
                                    $"[noWickyXIV] PlaySound SUPPRESSED: '{pathStr}' (matched pattern '{pat}')"); } catch { }
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch { /* never let a filter exception break the game's audio */ }

        if (suppress) return;
        try
        {
            _hook.Original(mgr, path, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17);
        }
        catch { /* let the audio thread keep going */ }
    }

    private static string ReadCString(byte* p)
    {
        if (p == null) return "";
        try { return Marshal.PtrToStringAnsi((IntPtr)p) ?? ""; }
        catch { return ""; }
    }

    private static bool IsMountedWithCustomPack()
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return false;
            var ch = (Character*)lp.Address;
            if (ch == null) return false;
            byte mountId = (byte)ch->Mount.MountId;
            if (mountId == 0) return false;
            // Has either a convention-pack directory OR an override
            // configured? We use the same predicate as MountAudio —
            // if MountAudio loaded layers for this mount, the engine
            // sounds are redundant noise we should mute.
            var cfg = noWickyXIV.Config;
            var dir = System.IO.Path.Combine(
                DalamudApi.PluginInterface?.AssemblyLocation?.DirectoryName ?? "",
                "assets", "mount-audio", mountId.ToString());
            bool dirExists = !string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir);
            bool hasOverride = cfg.MountAudioOverrides != null
                && cfg.MountAudioOverrides.Exists(o => o.MountId == mountId
                    && !string.IsNullOrEmpty(o.FilePath));
            return dirExists || hasOverride;
        }
        catch { return false; }
    }
}
