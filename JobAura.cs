using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace noWickyXIV;

// Visual / audio aura tied to the active job's primary gauge. SAM-only for
// now (Kenki, 0..100). Three discrete tiers + an "explosive" burst on the
// rising-edge transition into 100% paired with the NRFTW max-energy SFX.
//
// Render path is ImGui foreground draw list — projects the player's world
// position to screen via Dalamud's GameGui and stamps concentric rings
// underneath. Switching this to a real .avfx trigger later is just a matter
// of replacing the per-tier draw with a VfxContainer.Create call.
public static class JobAura
{
    private const uint SAM_CLASSJOB_ROW = 34; // SAM
    private const float TIER1_PCT = 0.33f;
    private const float TIER2_PCT = 0.66f;
    private const float TIER3_PCT = 1.00f;

    // Audio scheduling — relative offsets from the moment the player hits cap.
    private const double SFX_DELAY_PT12 = 1.0; // pt1 + pt2 fire 1s after max-focus

    private static int _tier;             // 0,1,2,3
    private static int _prevTier;
    private static double _burstStartT;   // wall-clock seconds since plugin load
    private static double _bornAt = -1;
    private static float _combatAlpha = 1f; // OOC fade only — applied to player-anchored visuals

    // Cascade-reveal bookkeeping for Sen markers. _overlayRiseT is set
    // when _combatAlpha crosses up through 0.01 from below; Sen markers
    // gate their fade-in by (now - _overlayRiseT >= JobAuraSenCascadeDelay)
    // so the rings / HP indicator land first and the markers cascade
    // in after.
    private static double _overlayRiseT = double.MinValue;
    private static bool   _overlayWasVisible;

    // Hostile-target cascade. Kenki + Sen visuals are gated by these
    // per-slot multipliers so they fade out (and back in) in sequence
    // when the player switches between enemy and friendly targets.
    //   slot 0: Sen markers
    //   slot 1: AllSen double ring
    //   slot 2: Kenki Tier 3 (incl. burst + meditate)
    //   slot 3: Kenki Tier 2
    //   slot 4: Kenki Tier 1
    // On fade-OUT (hostile→friendly) slot 0 starts immediately, slot 4
    // last. On fade-IN (friendly→hostile) the order reverses so the
    // outermost ring lands first and the Sen markers cascade in last
    // — same intuition as the JobAuraSenCascadeDelay reveal.
    private const int HOSTILE_SLOT_COUNT = 5;
    private static readonly float[] _hostileCascadeAlpha = new float[HOSTILE_SLOT_COUNT];
    private static bool   _targetHostile;
    private static bool   _targetHostilePrev;
    private static double _hostileTransitionT = double.MinValue;
    private static float _oocAlpha    = 1f; // smoothed OOC component
    private static float _targetAlpha = 1f; // smoothed target-presence — applied ONLY to target-dependent visuals (HP indicator)
    // When target-anchor is on and the target was just lost, keep rendering
    // at the last known anchor position so the fade-out can complete in
    // place instead of snapping to the (possibly off-screen) player.
    private static Vector3 _lastAnchorWorld;
    private static bool    _haveLastAnchor;
    private static long    _lastBoneDiagKey = -1;
    private static bool _firstUpdateDone;   // suppress rising-edge audio on the very first eval

    // Buff overlay state — additional ring layered on top of the Kenki rings
    // while certain SAM buffs are active. Wired off StatusList by name so we
    // don't have to chase status-id reshuffles across patches.
    private static bool _hasMeditate;
    private static int  _meditateStatusId; // diagnostic: id we matched, -1 if none
    private static bool _hasAllSen;        // Setsu + Getsu + Ka all set ("mangekyu ready")
    private static bool _allSenPrev;
    // Individual Sen flags — drive the three triangle markers.
    private static bool _hasSetsu, _hasGetsu, _hasKa;
    // Per-tier and per-Sen visual alphas — smooth toward target so rings
    // and dots fade out gracefully when the underlying state goes away.
    private static readonly float[] _tierAlpha = new float[3]; // [0]=tier1, [1]=tier2, [2]=tier3
    private static readonly float[] _senAlpha  = new float[3]; // [0]=Setsu cyan, [1]=Ka salmon, [2]=Getsu blue
    private static float _meditateAlpha;
    private static float _allSenAlpha;
    private static bool  _hasHiganbana;     // is the player's target carrying our Higanbana DoT?
    private static float _higanbanaAlpha;
    private static float _higanbanaRemaining;     // seconds left on the DoT
    private const float HIGANBANA_FULL_SECONDS = 60f;

    // Self-buffs — applied to the player by their own combos.
    private static bool  _hasFuka, _hasFugetsu;
    private static float _fukaAlpha, _fugetsuAlpha;
    private static float _fukaRemaining, _fugetsuRemaining;
    private const float FUKA_FULL_SECONDS    = 40f;
    private const float FUGETSU_FULL_SECONDS = 40f;

    // Target HP for the center indicator (replaces the target party-list
    // bar). HP percent drives the circle size; the text stays fixed.
    private static float _targetHpPct;     // 0..1
    private static bool  _hasTargetHp;
    private static float _hpAlpha;         // smoothed visibility for the HP indicator
    private static double _hpPulsePhase;   // 0..1 — drives the HP-circle pulse ring

    // System-font picker for the HP text. Font is rebuilt when path/size
    // change; calling EnsureHpFont each frame from Draw is cheap (it
    // only does work on transition).
    private static Dalamud.Interface.ManagedFontAtlas.IFontHandle _hpFontHandle;
    private static string _loadedFontPath = "";
    private static float  _loadedFontSize = -1f;
    private static System.Collections.Generic.List<string> _systemFontPaths;

    public static System.Collections.Generic.IReadOnlyList<string> EnumerateSystemFonts()
    {
        if (_systemFontPaths != null) return _systemFontPaths;
        var list = new System.Collections.Generic.List<string>();
        try
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var f in System.IO.Directory.GetFiles(fontsDir, "*.ttf"))
                list.Add(f);
            foreach (var f in System.IO.Directory.GetFiles(fontsDir, "*.otf"))
                list.Add(f);
            list.Sort((a, b) => string.Compare(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        _systemFontPaths = list;
        return _systemFontPaths;
    }

    private static void EnsureHpFont()
    {
        string path = noWickyXIV.Config.JobAuraHpFontPath ?? "";
        float size = MathF.Max(6f, noWickyXIV.Config.JobAuraHpFontSize);
        if (path == _loadedFontPath && MathF.Abs(size - _loadedFontSize) < 0.01f) return;

        try { _hpFontHandle?.Dispose(); } catch { }
        _hpFontHandle = null;
        _loadedFontPath = path;
        _loadedFontSize = size;

        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try
            {
                var atlas = DalamudApi.PluginInterface.UiBuilder.FontAtlas;
                _hpFontHandle = atlas.NewDelegateFontHandle(e =>
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(path,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = size }));
                });
            }
            catch (Exception ex)
            {
                try { DalamudApi.PluginLog.Warning($"[noWickyXIV] HP font load failed for [{path}] @ {size}px: {ex.Message}"); } catch { }
                _hpFontHandle = null;
            }
        }
    }

    // One-shot audio gate so we fire once per rising-edge into tier 3.
    private static double _capRisingAt = -1;
    private static bool _pt12Fired;
    private static bool _maxFired;

    // Use mciSendString instead of PlaySound — PlaySound is single-channel
    // (a new call interrupts the previous one), but we need pt1 + pt2 to
    // play simultaneously. mci supports multiple named aliases playing
    // concurrently.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "mciSendStringW")]
    private static extern int mciSendString(string command, System.Text.StringBuilder buffer, int bufferSize, IntPtr hwndCallback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciGetErrorStringW")]
    private static extern bool mciGetErrorString(int mciError, System.Text.StringBuilder buffer, int bufferSize);

    private static string _pathMax, _pathPt1, _pathPt2;
    private static int _aliasSeq;
    // Cache raw wav bytes once so we don't re-hit disk on every play.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _wavCache = new();
    private static int _scaledFileSeq;
    // Per-load unique prefix so alias names from a previous plugin reload —
    // which mci still holds against the host process — don't collide with
    // ours. mci is per-process, so just incrementing _aliasSeq from 0 every
    // load reuses names that the previous instance already opened.
    private static string _aliasPrefix = "nwx";

    // Active VFX handles — held while the gating buff/state is true so we
    // can Remove() on the falling edge. (Legacy meditate/allsen single-
    // path slots are kept; the modular layer list lives in _layerHandles.)
    private static IntPtr _vfxMeditate = IntPtr.Zero;
    private static IntPtr _vfxAllSen   = IntPtr.Zero;

    // Per-layer state, keyed by layer.Id so handles survive list reorders.
    // Session-managed: a layer's vfx is Created ONCE per session; trigger
    // transitions toggle visibility via VfxBridge.SetVisible. We never
    // call any removal function — engine reclaims actor-vfx on its own.
    private static readonly System.Collections.Generic.Dictionary<Guid, IntPtr> _layerHandles = new();
    private static readonly System.Collections.Generic.Dictionary<Guid, ushort> _layerSpawnZone = new();
    private static readonly System.Collections.Generic.Dictionary<Guid, double> _layerSpawnTime = new();
    private static readonly System.Collections.Generic.Dictionary<Guid, bool>   _layerPrev = new();
    // Smoothed scale fade (0..1). target=1 when active, 0 when hiding.
    // current chases target linearly over LAYER_FADE_SECONDS. Drop-after-fade
    // marks layers whose handle should be released once current reaches 0
    // (used by the kill-timer respawn cycle).
    private static readonly System.Collections.Generic.Dictionary<Guid, float> _layerScaleCurrent = new();
    private static readonly System.Collections.Generic.Dictionary<Guid, float> _layerScaleTarget  = new();
    private static readonly System.Collections.Generic.HashSet<Guid>            _layerDropAfterFade = new();
    // Per-layer last-fire timestamp for the MinIntervalSeconds debounce.
    // Prevents rapid combat-event triggers (per-tick damage events) from
    // stacking dozens of vfx per second and whiting out the screen.
    private static readonly System.Collections.Generic.Dictionary<Guid, double> _layerLastFire = new();
    // Per-layer "current shot finishes at" timestamp. Informational
    // only now (modes both fire on rising edge; auto-refire was dropped).
    private static readonly System.Collections.Generic.Dictionary<Guid, double> _layerFiringUntil = new();
    // Per-layer "fire scheduled at" timestamp for the DelaySeconds
    // feature. When a rising edge sets this, the actual fire happens
    // on the first frame where now >= scheduled time. Cleared after
    // the fire executes (or if the layer is otherwise reset).
    private static readonly System.Collections.Generic.Dictionary<Guid, double> _layerScheduledFireAt = new();

    // Chain-mode plumbing.
    //   _layerPathLastFire: keyed on the lowercased Path of any layer that
    //     successfully fired. Updated each time a layer's Create succeeds.
    //   _layerChainConsumedAt: per-chain-layer record of which source-fire
    //     timestamp it has already chained off, so a single source fire only
    //     triggers each chain layer once.
    private static readonly System.Collections.Generic.Dictionary<string, double> _layerPathLastFire =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.Dictionary<Guid, double>   _layerChainConsumedAt = new();

    private const float LAYER_FADE_SECONDS = 0.6f;
    // Last zone we saw — wholesale clears _layerHandles on zone change so
    // we don't try to access engine-reclaimed pointers in the new zone.
    private static ushort _lastZone;
    // Last local-player address we saw. The engine recreates the player's
    // GameObject (and frees attached actor-vfx) on logout/login, character
    // swap, mount/dismount, and gear/glamour changes that trigger draw-
    // object reload. Comparing to last frame lets us drop cached handles
    // BEFORE we ever try to deref them — never call Remove on a freed vfx.
    private static IntPtr _lastPlayerAddr;

    public static void Initialize()
    {
        try { DalamudApi.PluginLog.Information("[noWickyXIV] JobAura.Initialize start (v2-mci-diag)"); } catch { }
        _bornAt = Now();
        _firstUpdateDone = false;
        // Unique per-load prefix to avoid mci alias collisions when the
        // plugin reloads while old aliases are still held by the host.
        _aliasPrefix = $"nwx{Environment.TickCount & 0x7FFFFFFF:X}";
        _aliasSeq = 0;
        TryResolvePaths();
        // Always initialize the VFX bridge — the toggle was gating sig
        // resolution to avoid crashes, but with the zone-guarded Remove
        // and verified Create sig there's no benefit to opt-in. Users
        // with old configs that had the toggle off still get real VFX
        // automatically.
        VfxBridge.Initialize();
        try { DalamudApi.PluginLog.Information($"[noWickyXIV] JobAura.Initialize done (aliasPrefix={_aliasPrefix} vfxAvailable={VfxBridge.Available})"); } catch { }
    }

    public static void Dispose()
    {
        // Static vfx are owner-controlled — Remove on plugin teardown
        // is safe (no engine race like actor vfx had). Iterate every
        // cached handle and free explicitly so we don't leak vfx into
        // the next session.
        try
        {
            foreach (var kv in _layerHandles)
            {
                if (kv.Value != IntPtr.Zero)
                {
                    try { VfxBridge.Remove(kv.Value); } catch { }
                }
            }
        }
        catch { }
        _vfxMeditate = IntPtr.Zero;
        _vfxAllSen   = IntPtr.Zero;
        _layerHandles.Clear();
        _layerSpawnZone.Clear();
        _layerSpawnTime.Clear();
        _layerScaleCurrent.Clear();
        _layerScaleTarget.Clear();
        _layerDropAfterFade.Clear();
        _layerPrev.Clear();
        _lastPlayerAddr = IntPtr.Zero;
        try { _hpFontHandle?.Dispose(); } catch { }
        _hpFontHandle = null;
    }

    // Wholesale-drop all cached vfx handles WITHOUT calling Remove.
    // For use when the engine has already freed the underlying vfx
    // (zone change, player draw-object reload). Calling Remove on
    // already-freed memory is the UAF crash we're avoiding.
    private static void DropAllHandlesNoRemove()
    {
        _layerHandles.Clear();
        _layerSpawnZone.Clear();
        _layerSpawnTime.Clear();
        _layerScaleCurrent.Clear();
        _layerScaleTarget.Clear();
        _layerDropAfterFade.Clear();
    }

    private static void TryResolvePaths()
    {
        try
        {
            // Dalamud loads plugins from in-memory byte arrays; Assembly.Location
            // returns "" for those. Use the plugin interface's AssemblyLocation
            // (which Dalamud sets to the actual disk path) instead.
            string baseDir = "";
            try
            {
                var loc = DalamudApi.PluginInterface?.AssemblyLocation?.FullName;
                if (!string.IsNullOrEmpty(loc))
                    baseDir = Path.GetDirectoryName(loc) ?? "";
            }
            catch { }
            // Fallback: if PluginInterface isn't ready yet, try Assembly.Location
            // anyway — it's empty under Dalamud but works in unit-test scenarios.
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetDirectoryName(typeof(JobAura).Assembly.Location) ?? "";

            string assets = Path.Combine(baseDir, "assets");
            string max = Path.Combine(assets, "reached-max-focus.wav");
            string p1  = Path.Combine(assets, "50health-pt1.wav");
            string p2  = Path.Combine(assets, "50health-pt2.wav");
            _pathMax = File.Exists(max) ? max : null;
            _pathPt1 = File.Exists(p1)  ? p1  : null;
            _pathPt2 = File.Exists(p2)  ? p2  : null;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] JobAura assets: baseDir=[{baseDir}] max={(_pathMax != null)} p1={(_pathPt1 != null)} p2={(_pathPt2 != null)}");
            } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] JobAura asset resolve failed: {ex.Message}"); } catch { }
        }
    }

    private static string MciErrorString(int code)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            mciGetErrorString(code, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return "<unknown>"; }
    }

    // Read a WAV file once and cache its raw bytes.
    private static byte[] LoadWavBytes(string path)
    {
        return _wavCache.GetOrAdd(path, p => File.ReadAllBytes(p));
    }

    // Scale PCM WAV samples by a volume factor and return new bytes.
    // Supports 16-bit and 24-bit signed PCM, including WAVE_FORMAT_EXTENSIBLE
    // (format tag 0xFFFE) wrappers — common for 24-bit files. Returns the
    // original bytes unchanged for any other format (8-bit unsigned PCM,
    // 32-bit float, mp3-in-wav, etc.) so playback at least still happens
    // at full volume rather than glitching.
    private static byte[] ScaleWavPcm(byte[] wav, float volume)
    {
        if (wav == null || wav.Length < 44) return wav;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return wav;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return wav;
        var copy = (byte[])wav.Clone();

        int pos = 12;
        int dataStart = -1, dataLen = 0;
        ushort fmtTag = 0;
        short bitsPerSample = 0;
        bool isPcmExtensible = false;
        while (pos + 8 <= copy.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(copy, pos, 4);
            int size = BitConverter.ToInt32(copy, pos + 4);
            if (size < 0 || pos + 8 + size > copy.Length) break;
            if (id == "fmt " && size >= 16)
            {
                fmtTag        = BitConverter.ToUInt16(copy, pos + 8 + 0);
                bitsPerSample = BitConverter.ToInt16 (copy, pos + 8 + 14);
                if (fmtTag == 0xFFFE && size >= 40)
                {
                    // WAVE_FORMAT_EXTENSIBLE: subformat GUID at offset 24..39.
                    // First 4 bytes of KSDATAFORMAT_SUBTYPE_PCM are 01 00 00 00.
                    isPcmExtensible =
                        copy[pos + 8 + 24] == 0x01 &&
                        copy[pos + 8 + 25] == 0x00 &&
                        copy[pos + 8 + 26] == 0x00 &&
                        copy[pos + 8 + 27] == 0x00;
                }
            }
            else if (id == "data")
            {
                dataStart = pos + 8;
                dataLen = size;
                break;
            }
            pos += 8 + size + (size & 1);
        }
        if (dataStart < 0) return wav;
        bool isPcm = fmtTag == 1 || isPcmExtensible;
        if (!isPcm) return wav;

        int end = Math.Min(copy.Length, dataStart + dataLen);

        if (bitsPerSample == 16)
        {
            for (int i = dataStart; i + 1 < end; i += 2)
            {
                short s = BitConverter.ToInt16(copy, i);
                int scaled = (int)MathF.Round(s * volume);
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                copy[i]     = (byte)(scaled & 0xFF);
                copy[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
            return copy;
        }
        if (bitsPerSample == 24)
        {
            const int MAX24 =  8388607;
            const int MIN24 = -8388608;
            for (int i = dataStart; i + 2 < end; i += 3)
            {
                // Read little-endian signed 24-bit
                int s = copy[i] | (copy[i + 1] << 8) | (copy[i + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000); // sign-extend
                long scaled = (long)MathF.Round(s * volume);
                if (scaled > MAX24) scaled = MAX24;
                else if (scaled < MIN24) scaled = MIN24;
                int v = (int)scaled;
                copy[i]     = (byte)(v & 0xFF);
                copy[i + 1] = (byte)((v >> 8) & 0xFF);
                copy[i + 2] = (byte)((v >> 16) & 0xFF);
            }
            return copy;
        }
        // Unsupported bit depth — return original unchanged.
        return wav;
    }

    // Returns a path to a temporary WAV scaled to the requested volume.
    // Caller is responsible for cleanup (we delete on a delayed task).
    private static string PrepareScaledTempWav(string srcPath, float volume)
    {
        var src = LoadWavBytes(srcPath);
        var scaled = ScaleWavPcm(src, volume);
        if (ReferenceEquals(scaled, src)) return srcPath; // no-op (unsupported format)

        string tmpDir = Path.Combine(Path.GetTempPath(), "noWickyXIV-audio");
        Directory.CreateDirectory(tmpDir);
        int seq = System.Threading.Interlocked.Increment(ref _scaledFileSeq);
        // Include per-load alias prefix so a temp file from a previous
        // plugin instance (still locked by mci) doesn't collide with a
        // new write after reload.
        string tmpPath = Path.Combine(tmpDir, $"{_aliasPrefix}_{seq}_{Path.GetFileName(srcPath)}");
        File.WriteAllBytes(tmpPath, scaled);
        return tmpPath;
    }

    private static void Play(string path, float volume = 1f)
    {
        if (string.IsNullOrEmpty(path))
        {
            try { DalamudApi.PluginLog.Warning("[noWickyXIV] JobAura.Play: null path"); } catch { }
            return;
        }
        try
        {
            // Per-stream volume: mci's setaudio is unreliable on waveaudio,
            // so we pre-scale samples ourselves into a temp wav and play that.
            // At full volume we play the original to avoid the temp-file path.
            string playPath = path;
            string tempToCleanup = null;
            if (volume < 0.999f)
            {
                try
                {
                    string maybeTemp = PrepareScaledTempWav(path, MathF.Max(0f, MathF.Min(1f, volume)));
                    if (!string.Equals(maybeTemp, path, StringComparison.OrdinalIgnoreCase))
                    {
                        playPath = maybeTemp;
                        tempToCleanup = maybeTemp;
                    }
                }
                catch (Exception ex)
                {
                    try { DalamudApi.PluginLog.Warning($"[noWickyXIV] JobAura wav-scale failed for [{path}]: {ex.Message}"); } catch { }
                }
            }

            // Each call gets a unique alias so multiple sounds layer.
            string alias = $"{_aliasPrefix}_{System.Threading.Interlocked.Increment(ref _aliasSeq)}";
            string openCmd = $"open \"{playPath}\" type waveaudio alias {alias}";
            int rcOpen = mciSendString(openCmd, null, 0, IntPtr.Zero);
            if (rcOpen != 0)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] JobAura mci open FAILED rc={rcOpen} ({MciErrorString(rcOpen)}) cmd=[{openCmd}]");
                } catch { }
                return;
            }
            // Volume already baked into the temp wav (or playing original
            // at 1.0×); no setaudio needed.
            int rcPlay = mciSendString($"play {alias}", null, 0, IntPtr.Zero);
            if (rcPlay != 0)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] JobAura mci play FAILED rc={rcPlay} ({MciErrorString(rcPlay)}) alias={alias}");
                } catch { }
            }
            else
            {
                try { DalamudApi.PluginLog.Information($"[noWickyXIV] JobAura played: {Path.GetFileName(path)} vol={volume:F2} (alias={alias})"); } catch { }
            }
            // Schedule cleanup — best-effort, runs after clip finishes.
            string capturedTemp = tempToCleanup;
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(8000);
                try { mciSendString($"close {alias}", null, 0, IntPtr.Zero); } catch { }
                if (capturedTemp != null)
                    try { File.Delete(capturedTemp); } catch { }
            });
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] JobAura.Play threw: {ex.Message}"); } catch { }
        }
    }

    // Called from Update (Framework thread) — drives tier state + audio scheduling.
    public static unsafe void Update()
    {
        if (!noWickyXIV.Config.EnableJobAura)
        {
            _tier = 0; _prevTier = 0;
            _prevKenkiForTiers = -1;
            _kenkiTier1FromBelow = false;
            _kenkiTier2FromBelow = false;
            _kenkiTier3FromBelow = false;
            _capRisingAt = -1; _pt12Fired = false; _maxFired = false;
            return;
        }

        // Only relevant when logged in and on SAM.
        var lp = DalamudApi.ObjectTable.LocalPlayer;
        if (lp == null || lp.ClassJob.RowId != SAM_CLASSJOB_ROW)
        {
            _tier = 0; _prevTier = 0;
            _prevKenkiForTiers = -1;
            _kenkiTier1FromBelow = false;
            _kenkiTier2FromBelow = false;
            _kenkiTier3FromBelow = false;
            _capRisingAt = -1; _pt12Fired = false; _maxFired = false;
            _isMoving = false; _motionTracked = false;
            return;
        }

        // Sample player motion each frame so the Moving/Stopped triggers
        // have current state when EvaluateTrigger is called below.
        UpdateMotionState(lp);

        // Optional: only while weapon drawn. Out-of-stance the rings vanish
        // (and any pending audio sequence is cleared).
        if (noWickyXIV.Config.JobAuraOnlyWeaponDrawn)
        {
            bool drawn = false;
            try { drawn = (lp.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.WeaponOut) != 0; } catch { }
            if (!drawn)
            {
                _tier = 0; _prevTier = 0;
            _prevKenkiForTiers = -1;
            _kenkiTier1FromBelow = false;
            _kenkiTier2FromBelow = false;
            _kenkiTier3FromBelow = false;
                _capRisingAt = -1; _pt12Fired = false; _maxFired = false;
                return;
            }
        }

        // Hide in cutscenes / between zones — same gates as the crosshair.
        // Between-zones path also INSTANTLY zeroes the smoothed alphas and
        // drops the cached target anchor so the loading screen doesn't
        // show a fading ring on top of it, and so we don't reattach to a
        // stale target when the new zone loads.
        try
        {
            var cond = DalamudApi.Condition;
            bool loading = cond[ConditionFlag.BetweenAreas] || cond[ConditionFlag.BetweenAreas51];
            if (loading)
            {
                ClearVisualState();
                return;
            }
            if (cond[ConditionFlag.OccupiedInCutSceneEvent] ||
                cond[ConditionFlag.WatchingCutscene]        ||
                cond[ConditionFlag.WatchingCutscene78])
                return;
        }
        catch { }

        // Detect SAM "Meditate" buff. Match by name so we survive status-id
        // reshuffles. Logs the matched id once so the user can confirm what
        // we're seeing.
        bool hadMeditate = _hasMeditate;
        _hasMeditate = false;
        try
        {
            var statuses = lp.StatusList;
            for (int i = 0; i < statuses.Length; i++)
            {
                var s = statuses[i];
                if (s == null || s.StatusId == 0) continue;
                string nm = s.GameData.ValueNullable?.Name.ExtractText() ?? "";
                if (nm.Equals("Meditate", StringComparison.OrdinalIgnoreCase))
                {
                    _hasMeditate = true;
                    if (_meditateStatusId != (int)s.StatusId)
                    {
                        _meditateStatusId = (int)s.StatusId;
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] JobAura matched Meditate status id={s.StatusId}"); } catch { }
                    }
                    break;
                }
            }
        }
        catch { }
        if (_hasMeditate != hadMeditate)
        {
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] JobAura Meditate {(_hasMeditate ? "ON" : "OFF")}"); } catch { }
        }

        // Higanbana — DoT applied by player to a hostile target. Detect by
        // scanning the player's current target's StatusList for the named
        // status, matching SourceId to local player so other SAMs' DoTs
        // don't trigger our indicator. Also reads target HP for the center
        // health indicator while we already have the IBattleChara handle.
        bool hadHigan = _hasHiganbana;
        _hasHiganbana = false;
        _hasTargetHp = false;
        try
        {
            var tgt = DalamudApi.TargetManager?.Target;
            if (tgt is Dalamud.Game.ClientState.Objects.Types.IBattleChara bc)
            {
                if (bc.MaxHp > 0)
                {
                    _hasTargetHp = true;
                    _targetHpPct = MathF.Min(1f, MathF.Max(0f, bc.CurrentHp / (float)bc.MaxHp));
                }

                var statuses = bc.StatusList;
                ulong myId = lp.GameObjectId;
                for (int i = 0; i < statuses.Length; i++)
                {
                    var s = statuses[i];
                    if (s == null || s.StatusId == 0) continue;
                    if (s.SourceId != myId) continue;
                    string nm = s.GameData.ValueNullable?.Name.ExtractText() ?? "";
                    if (nm.Equals("Higanbana", StringComparison.OrdinalIgnoreCase))
                    {
                        _hasHiganbana = true;
                        _higanbanaRemaining = MathF.Max(0f, s.RemainingTime);
                        break;
                    }
                }
            }
        }
        catch { }
        if (_hasHiganbana != hadHigan)
        {
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] JobAura Higanbana {(_hasHiganbana ? "ON (target DoT)" : "OFF")}"); } catch { }
        }

        // Fuka / Fugetsu — SAM self-buffs from the speed/damage combos.
        // Same scan pattern as Meditate but capturing remaining time too.
        bool hadFuka    = _hasFuka;
        bool hadFugetsu = _hasFugetsu;
        _hasFuka = false; _hasFugetsu = false;
        try
        {
            var pStatuses = lp.StatusList;
            for (int i = 0; i < pStatuses.Length; i++)
            {
                var s = pStatuses[i];
                if (s == null || s.StatusId == 0) continue;
                string nm = s.GameData.ValueNullable?.Name.ExtractText() ?? "";
                if (!_hasFuka && nm.Equals("Fuka", StringComparison.OrdinalIgnoreCase))
                {
                    _hasFuka = true;
                    _fukaRemaining = MathF.Max(0f, s.RemainingTime);
                }
                else if (!_hasFugetsu && nm.Equals("Fugetsu", StringComparison.OrdinalIgnoreCase))
                {
                    _hasFugetsu = true;
                    _fugetsuRemaining = MathF.Max(0f, s.RemainingTime);
                }
                if (_hasFuka && _hasFugetsu) break;
            }
        }
        catch { }
        if (_hasFuka != hadFuka)
            try { DalamudApi.PluginLog.Information($"[noWickyXIV] JobAura Fuka {(_hasFuka ? "ON" : "OFF")}"); } catch { }
        if (_hasFugetsu != hadFugetsu)
            try { DalamudApi.PluginLog.Information($"[noWickyXIV] JobAura Fugetsu {(_hasFugetsu ? "ON" : "OFF")}"); } catch { }

        // Read Kenki (0..100) + Sen flags (Setsu/Getsu/Ka).
        // JobGauges API on the wrong job throws — guard.
        int kenki = 0;
        bool hasSetsu = false, hasGetsu = false, hasKa = false;
        try
        {
            var g = DalamudApi.JobGauges.Get<SAMGauge>();
            kenki = g.Kenki;
            hasSetsu = g.HasSetsu;
            hasGetsu = g.HasGetsu;
            hasKa    = g.HasKa;
        }
        catch { return; }
        _hasSetsu = hasSetsu; _hasGetsu = hasGetsu; _hasKa = hasKa;
        // Kenki tier "entered from below" tracking — see notes on the
        // _kenkiTierFromBelow fields. Evaluated once per Update before
        // EvaluateTrigger reads the flags. Without this, descending
        // through a tier (e.g. 70 → 50 enters Tier1 range) would fire
        // a rising-edge of the layer trigger, which is visually
        // confusing for the user.
        UpdateKenkiTierFromBelow(kenki);
        bool allSen = hasSetsu && hasGetsu && hasKa;
        _allSenPrev = _hasAllSen;
        _hasAllSen = allSen;
        if (_hasAllSen != _allSenPrev)
        {
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] JobAura AllSen {(_hasAllSen ? "ON (mangekyu ready)" : "OFF")}"); } catch { }
        }

        // (Modular VFX layer engine moved to AFTER tier evaluation, below.)

        float pct = MathF.Min(1f, MathF.Max(0f, kenki / 100f));
        int t = pct >= TIER3_PCT ? 3 : pct >= TIER2_PCT ? 2 : pct >= TIER1_PCT ? 1 : 0;

        // First eval after Initialize: just sync state without firing any
        // rising-edge audio. Prevents a pop on plugin load if Kenki is
        // already capped (e.g. between zones, post-relog at 100 Kenki).
        if (!_firstUpdateDone)
        {
            _prevTier = t;
            _tier = t;
            _capRisingAt = -1; _pt12Fired = false; _maxFired = false;
            _firstUpdateDone = true;
        }
        else
        {
            // Rising edge into tier 3 → burst + audio schedule.
            if (t == 3 && _prevTier < 3)
            {
                _burstStartT = Now();
                _capRisingAt = Now();
                _pt12Fired = false;
                _maxFired = false;
                // One-shot real-vfx burst at the player. The vfx self-cleans
                // after its animation, so we don't track the handle.
                if (!string.IsNullOrEmpty(noWickyXIV.Config.JobAuraVfxBurstPath))
                {
                    try
                    {
                        var h = VfxBridge.Create(noWickyXIV.Config.JobAuraVfxBurstPath);
                        // Position the burst at the player once at spawn —
                        // it's a one-shot, so no per-frame tracking needed.
                        if (h != IntPtr.Zero)
                        {
                            VfxBridge.SetWorldTransform(h,
                                new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z),
                                lp.Rotation, 0f, 0f, 0f);
                        }
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] JobAura vfx Burst fire handle=0x{h.ToInt64():X}"); } catch { }
                    }
                    catch { }
                }
            }
            // Falling out of tier 3 → cancel pending pt1/pt2 schedule (don't
            // fire the delayed sounds if Kenki dropped before 1s elapsed).
            if (t < 3 && _prevTier == 3)
            {
                _capRisingAt = -1; _pt12Fired = false; _maxFired = false;
            }

            // Audio: max-focus immediately on rising-edge, pt1+pt2 1s later.
            if (_capRisingAt > 0 && !noWickyXIV.Config.MuteJobAuraSfx)
            {
                double since = Now() - _capRisingAt;
                if (!_maxFired)
                {
                    Play(_pathMax, noWickyXIV.Config.JobAuraVolMax);
                    _maxFired = true;
                }
                if (!_pt12Fired && since >= SFX_DELAY_PT12)
                {
                    Play(_pathPt1, noWickyXIV.Config.JobAuraVolPt1);
                    Play(_pathPt2, noWickyXIV.Config.JobAuraVolPt2);
                    _pt12Fired = true;
                }
            }

            _prevTier = _tier;
            _tier = t;
        }

        // ---- Session-managed VFX layer engine ----
        // For each user-configured layer:
        //   - Burst: Create on rising-edge, never tracked, never removed.
        //   - Sustained: Create ONCE per session at first activation, cache
        //     handle. On rising-edge after first: SetVisible(true).
        //     On falling-edge: SetVisible(false). NEVER call Remove.
        //   - Engine-invalidation detection: pre-loop, drop any cached
        //     handle whose vfx no longer parents to the player's draw
        //     object. Don't access vfx fields after that point.
        //   - Zone change: clear _layerHandles wholesale before any access.
        try
        {
            // Zone-change wipe: previous zone's handles are stale (engine
            // freed them). Drop without touching them.
            ushort curZone = VfxBridge.SafeCurrentZone();
            if (curZone != _lastZone)
            {
                if (_layerHandles.Count > 0)
                {
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] JobAura zone change {_lastZone}→{curZone}: dropping {_layerHandles.Count} cached handles."); } catch { }
                    DropAllHandlesNoRemove();
                    _layerLastFire.Clear();
                }
                _lastZone = curZone;
            }

            // Player-address-change wipe: the engine recreated the player's
            // GameObject (logout/login, character swap, mount/dismount,
            // gear/glamour reload). Any actor-anchored vfx attached to the
            // OLD player are already freed by the engine — calling Remove
            // on them would UAF. Drop without touching.
            var lpForAddr = DalamudApi.ObjectTable.LocalPlayer;
            IntPtr curPlayerAddr = lpForAddr != null ? lpForAddr.Address : IntPtr.Zero;
            if (curPlayerAddr != _lastPlayerAddr)
            {
                if (_layerHandles.Count > 0)
                {
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] JobAura player-address change 0x{_lastPlayerAddr.ToInt64():X}→0x{curPlayerAddr.ToInt64():X}: dropping {_layerHandles.Count} cached handles."); } catch { }
                    DropAllHandlesNoRemove();
                    _layerLastFire.Clear();
                }
                _lastPlayerAddr = curPlayerAddr;
            }

            // NOTE: per-frame engine-invalidation sweep removed. The
            // ParentObject==DrawObject check was rejecting healthy handles
            // every ~10ms, causing constant invalidate→drop→respawn loops
            // (the user saw the screen strobing the avfx forever). The only
            // events that actually invalidate an actor-vfx are zone change
            // (handled wholesale above) and full plugin teardown (handled
            // by Reset). Trust the cached handle for the rest of the
            // session — SetScale is null-safe and IsValidForOwner is too
            // strict to use as a liveness check.

            var layers = noWickyXIV.Config.JobAuraVfxLayers;
            if (layers != null)
            {
                bool capRising = (_tier == 3 && _prevTier < 3);
                double now = Now();

                foreach (var layer in layers)
                {
                    if (layer == null || string.IsNullOrEmpty(layer.Path)) continue;
                    // Path validation. A partial or malformed path
                    // crashes the engine's resource loader through
                    // Penumbra/VFXEditor's hook chain (verified dump
                    // 190359). Skip until the path looks sane:
                    //   - lowercase forward-slashes (FFXIV convention)
                    //   - ends with .avfx
                    //   - no whitespace runs
                    if (!IsLikelyValidVfxPath(layer.Path))
                    {
                        // Log once per layer-id+path so the user can
                        // see WHY a configured layer is silently
                        // skipped (most common cause: trailing space,
                        // backslashes, or wrong extension).
                        WarnPathRejectedOnce(layer);
                        continue;
                    }

                    // Default-mode layers fire from their own Trigger.
                    // Chain/Chained-mode layers ignore Trigger for firing
                    // decisions and instead pick up "source fired" events
                    // below. Chain and Chained share firing semantics —
                    // they only differ in how the source path is edited
                    // in the UI (dropdown vs free text + quick-pick).
                    bool isChain = layer.SourceMode == JobAuraLayerSourceMode.Chain
                                || layer.SourceMode == JobAuraLayerSourceMode.Chained;

                    bool active;
                    if (isChain)
                    {
                        // Rising edge for chain layers = source path fired
                        // since we last consumed an event from it. Gating
                        // by layer.Enabled here is critical — without it,
                        // unchecking Enabled doesn't produce a falling
                        // edge, so the in-flight vfx never gets the
                        // end-trigger and keeps emitting forever.
                        active = false;
                        if (layer.Enabled
                            && !string.IsNullOrEmpty(layer.ChainSourcePath)
                            && _layerPathLastFire.TryGetValue(layer.ChainSourcePath, out var srcAt))
                        {
                            double consumed = _layerChainConsumedAt.TryGetValue(layer.Id, out var cAt) ? cAt : double.MinValue;
                            if (srcAt > consumed) active = true;
                        }
                    }
                    else
                    {
                        active = layer.Enabled && EvaluateTrigger(
                            layer.Trigger, kenki, capRising,
                            _hasSetsu, _hasGetsu, _hasKa, _hasAllSen);
                    }

                    bool prev = _layerPrev.TryGetValue(layer.Id, out var p) && p;
                    bool rising = active && !prev;
                    bool falling = !active && prev;
                    _layerPrev[layer.Id] = active;

                    // NOTE: do NOT `continue` here on !layer.Enabled.
                    // The falling-edge block below needs to run when the
                    // user unchecks Enabled mid-fire so the live handle
                    // gets its end-trigger and any pending scheduled fire
                    // is cancelled. Skipping to the next layer here was
                    // the cause of "toggle off doesn't actually stop the
                    // effect".

                    // Chain layers consume the source-fire timestamp on
                    // their rising edge so they don't keep re-firing while
                    // the same source event is still the "latest".
                    if (isChain && rising
                        && !string.IsNullOrEmpty(layer.ChainSourcePath)
                        && _layerPathLastFire.TryGetValue(layer.ChainSourcePath, out var srcAt2))
                    {
                        _layerChainConsumedAt[layer.Id] = srcAt2;
                    }

                    double last = _layerLastFire.TryGetValue(layer.Id, out var lt) ? lt : double.MinValue;
                    bool minIntervalMet = layer.MinIntervalSeconds <= 0f
                                          || (now - last) >= layer.MinIntervalSeconds;

                    // ---- Falling edge → graceful end via CallTrigger ----
                    // Dispatch the layer's EndTriggerId into the running
                    // vfx. The avfx's OWN timeline picks up the trigger
                    // and runs its end-animation (the natural fade), then
                    // the engine self-cleans the vfx. We never call
                    // Remove ourselves so there's no UAF and no hook
                    // collision with VFXEditor.
                    // EndTriggerId < 0 means "don't dispatch anything"
                    // (vfx plays its natural duration to completion).
                    if (falling)
                    {
                        // Cancel pending scheduled fire ONLY when the
                        // user explicitly disabled the layer mid-fire.
                        // Natural edge-trigger falls (rising edge frame
                        // → trigger goes false the next frame, e.g.
                        // NormalHit/CritHit which clear after one
                        // frame) MUST NOT kill a fire that's still
                        // waiting on its DelaySeconds window. That
                        // regression dropped every delayed combat-
                        // event fire on the floor.
                        if (!layer.Enabled)
                            _layerScheduledFireAt.Remove(layer.Id);

                        if (_layerHandles.TryGetValue(layer.Id, out var hExisting)
                            && hExisting != IntPtr.Zero && layer.EndTriggerId >= 0)
                        {
                            try { DalamudApi.PluginLog.Information(
                                $"[noWickyXIV] JobAura layer '{layer.Name}' falling — Trigger({layer.EndTriggerId}) on handle=0x{hExisting.ToInt64():X}"); } catch { }
                            VfxBridge.Trigger(hExisting, (uint)layer.EndTriggerId);
                        }
                        // Drop our handle reference unconditionally on
                        // falling — even when no end-trigger is configured.
                        _layerHandles.Remove(layer.Id);
                    }

                    // ---- Schedule on rising edge ----
                    // Both modes fire only on rising edge. Schedule the
                    // actual fire DelaySeconds in the future (0 = next
                    // line). Stacked layers can stagger themselves by
                    // setting different delays.
                    //
                    // SuppressWhileOthersFiring: when on, skip the
                    // schedule if ANY other enabled layer is still
                    // inside its RunTimeSeconds window. The Stopped
                    // layer is the obvious case — gap-closer actions
                    // settle the player's motion for a frame while the
                    // gap closer's own effect is still playing, and
                    // without this gate the Stopped layer fires on top
                    // of it and reads as jitter.
                    bool suppressedByOthers =
                        layer.SuppressWhileOthersFiring
                        && AnyOtherEnabledLayerFiring(layers, layer.Id, now);

                    if (rising && minIntervalMet && !suppressedByOthers
                        && !_layerScheduledFireAt.ContainsKey(layer.Id))
                    {
                        _layerScheduledFireAt[layer.Id] = now + Math.Max(0.0, layer.DelaySeconds);
                    }

                    // ---- Execute scheduled fires whose time has come ----
                    // Belt-and-braces: even though falling-edge clears any
                    // pending schedule, gate execution by layer.Enabled so
                    // a same-frame disable can't slip a fire through.
                    if (layer.Enabled
                        && _layerScheduledFireAt.TryGetValue(layer.Id, out var fireAt) && now >= fireAt)
                    {
                        _layerScheduledFireAt.Remove(layer.Id);

                        var h = VfxBridge.Create(layer.Path);
                        if (h == IntPtr.Zero)
                        {
                            try { DalamudApi.PluginLog.Warning(
                                $"[noWickyXIV] JobAura layer '{layer.Name}' Create returned null for path={layer.Path}"); } catch { }
                        }
                        else
                        {
                            _layerHandles[layer.Id] = h;
                            _layerLastFire[layer.Id] = now;
                            _layerFiringUntil[layer.Id] = now;

                            // Stamp the path-fire timestamp so any Chain-
                            // mode layer watching this Path will pick it up
                            // on its next iteration and schedule its own
                            // fire DelaySeconds later.
                            if (!string.IsNullOrEmpty(layer.Path))
                                _layerPathLastFire[layer.Path] = now;

                            try { DalamudApi.PluginLog.Information(
                                $"[noWickyXIV] JobAura layer '{layer.Name}' fire mode={layer.Mode} src={layer.SourceMode} delay={layer.DelaySeconds:F2}s trigger={layer.Trigger} handle=0x{h.ToInt64():X}"); } catch { }
                        }

                        // Sound plays alongside the vfx, with the same
                        // delay applied. Empty SoundPath = silent layer.
                        if (!string.IsNullOrEmpty(layer.SoundPath))
                        {
                            try { Play(layer.SoundPath, layer.SoundVolume); } catch { }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] JobAura layer engine threw: {ex.Message}"); } catch { }
        }

        // Combat-event edge flags consumed for this frame — clear so the
        // next frame starts cleanly. Hooks accumulate again between now
        // and the next Update call.
        CombatEvents.ResetEdgeFlags();

        // Smooth ring/marker alphas toward their target (1 if active, 0 if not)
        // so spending Kenki / shedding a Sen fades the visual out instead of
        // cutting it. Reuses the existing OOC fade rate slider for cadence.
        try
        {
            float dtr = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            if (dtr <= 0f) dtr = 0.016f;
            float fr = MathF.Max(0.5f, noWickyXIV.Config.JobAuraFadeRate);
            float k = 1f - MathF.Exp(-fr * dtr);

            for (int i = 0; i < 3; i++)
            {
                float target = (_tier >= i + 1) ? 1f : 0f;
                _tierAlpha[i] += (target - _tierAlpha[i]) * k;
            }
            float sM = _hasMeditate ? 1f : 0f;
            _meditateAlpha += (sM - _meditateAlpha) * k;
            float sA = _hasAllSen ? 1f : 0f;
            _allSenAlpha   += (sA - _allSenAlpha) * k;
            float sH = _hasHiganbana ? 1f : 0f;
            _higanbanaAlpha += (sH - _higanbanaAlpha) * k;
            _fukaAlpha    += ((_hasFuka    ? 1f : 0f) - _fukaAlpha)    * k;
            _fugetsuAlpha += ((_hasFugetsu ? 1f : 0f) - _fugetsuAlpha) * k;
            _hpAlpha      += ((_hasTargetHp? 1f : 0f) - _hpAlpha)      * k;

            // HP pulse-ring phase: full HP → slow (1.0s period), 0 HP →
            // fast (0.18s period). Phase wraps in [0,1).
            float hpPctSafe = MathF.Max(0.05f, _targetHpPct);
            float pulsePeriod = 0.18f + (1.0f - 0.18f) * hpPctSafe;
            _hpPulsePhase += dtr / pulsePeriod;
            if (_hpPulsePhase >= 1.0) _hpPulsePhase -= Math.Floor(_hpPulsePhase);
            _senAlpha[0] += ((_hasSetsu ? 1f : 0f) - _senAlpha[0]) * k;
            _senAlpha[1] += ((_hasKa    ? 1f : 0f) - _senAlpha[1]) * k;
            _senAlpha[2] += ((_hasGetsu ? 1f : 0f) - _senAlpha[2]) * k;
        }
        catch { }

        // Visibility fades — independently smooth OOC and target-presence,
        // then multiply into the combined _combatAlpha that Draw uses.
        try
        {
            float dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            if (dt <= 0f) dt = 0.016f;
            float rate = MathF.Max(0.5f, noWickyXIV.Config.JobAuraFadeRate);
            float kk = 1f - MathF.Exp(-rate * dt);

            // OOC fade
            bool inCombat = false;
            try { inCombat = DalamudApi.Condition[ConditionFlag.InCombat]; } catch { }
            float oocTarget = (!noWickyXIV.Config.JobAuraFadeOutOfCombat || inCombat)
                ? 1f
                : MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.JobAuraOutOfCombatAlpha));
            _oocAlpha += (oocTarget - _oocAlpha) * kk;

            // Target-presence fade
            bool hasTarget = false;
            try { hasTarget = DalamudApi.TargetManager?.Target != null; } catch { }
            float tgtTarget = (!noWickyXIV.Config.JobAuraFadeWhenNoTarget || hasTarget)
                ? 1f
                : MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.JobAuraNoTargetAlpha));
            _targetAlpha += (tgtTarget - _targetAlpha) * kk;

            // Combined: _combatAlpha = OOC fade × target-presence fade.
            // Every element that multiplies its draw alpha by
            // _combatAlpha (rings, Sen markers, Meditate, AllSen,
            // Kenki tiers, HP) now fades TOGETHER when the target is
            // dropped, matching user expectation. Previously rings
            // hard-cut while only HP faded — that was a deliberate
            // decoupling that turned out to feel inconsistent.
            _combatAlpha = _oocAlpha * _targetAlpha;

            // Stamp the overlay rise time on the rising edge of "any
            // visibility at all" so the Sen marker cascade delay is
            // measured from the moment the overlay becomes visible.
            bool nowVisible = _combatAlpha > 0.01f;
            if (nowVisible && !_overlayWasVisible)
                _overlayRiseT = Now();
            _overlayWasVisible = nowVisible;

            // ---- Hostile-target cascade ----
            // Kenki + Sen visuals are gated by per-slot multipliers
            // that fade in cascade order when the target is hostile,
            // and reverse-cascade fade out when targeting a friendly
            // (player, ally NPC, self, etc.). Falls through to all
            // slots = 0 (everything hidden) when there's no target at
            // all, but in that case _combatAlpha is already 0 anyway.
            bool hostile = IsTargetHostile();
            if (hostile != _targetHostilePrev)
            {
                _hostileTransitionT = Now();
                _targetHostilePrev = hostile;
            }
            _targetHostile = hostile;

            double cascadeDelay = MathF.Max(0f, noWickyXIV.Config.JobAuraHostileCascadeDelay);
            for (int i = 0; i < HOSTILE_SLOT_COUNT; i++)
            {
                // Cascade order: on fade-OUT (friendly), slot 0 (Sen)
                // starts immediately and slot 4 (Tier1 ring) starts last.
                // On fade-IN (hostile), reverse — Tier1 first, Sen last.
                int order = hostile ? (HOSTILE_SLOT_COUNT - 1 - i) : i;
                double kickoff = _hostileTransitionT + order * cascadeDelay;
                if (Now() < kickoff) continue; // hold this slot until its turn

                float target = hostile ? 1f : 0f;
                _hostileCascadeAlpha[i] += (target - _hostileCascadeAlpha[i]) * kk;
                if (MathF.Abs(target - _hostileCascadeAlpha[i]) < 0.002f)
                    _hostileCascadeAlpha[i] = target;
            }
        }
        catch { _combatAlpha = 1f; }
    }

    // One-shot test invocation for the UI button — fires the full audio
    // sequence without requiring Kenki to actually hit cap. Useful for
    // verifying mci on the user's setup.
    public static void TestSpawnVfx(string path)
    {
        try
        {
            var h = VfxBridge.Create(path);
            // Place at player so the test is visible. Static vfx default
            // to world origin without an explicit transform write.
            if (h != IntPtr.Zero)
            {
                var lp = DalamudApi.ObjectTable.LocalPlayer;
                if (lp != null)
                {
                    VfxBridge.SetWorldTransform(h,
                        new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z),
                        lp.Rotation, 0f, 0f, 0f);
                }
            }
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] JobAura.TestSpawnVfx({path}) → handle=0x{h.ToInt64():X}"); } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] JobAura.TestSpawnVfx({path}) threw: {ex.Message}"); } catch { }
        }
    }

    public static void TestPlaySequence()
    {
        try { DalamudApi.PluginLog.Information("[noWickyXIV] JobAura.TestPlaySequence invoked"); } catch { }
        Play(_pathMax, noWickyXIV.Config.JobAuraVolMax);
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1000);
            Play(_pathPt1, noWickyXIV.Config.JobAuraVolPt1);
            Play(_pathPt2, noWickyXIV.Config.JobAuraVolPt2);
        });
    }

    // Called from Draw (also Framework thread, ImGui valid) — render rings.
    public static unsafe void Draw()
    {
        if (!noWickyXIV.Config.EnableJobAura) return;

        // Hard gate: loading screen → drop everything instantly so the
        // target ring (and any other smoothed visual) doesn't render on
        // top of the loading image and doesn't keep a cached anchor that
        // would re-bind to whatever was previously targeted on zone-in.
        try
        {
            var cond = DalamudApi.Condition;
            if (cond[ConditionFlag.BetweenAreas] || cond[ConditionFlag.BetweenAreas51])
            {
                ClearVisualState();
                return;
            }
        }
        catch { }

        // Bail only when nothing is visible: every smoothed alpha at zero,
        // no fading burst, and no active flags.
        float maxA = MathF.Max(MathF.Max(_tierAlpha[0], _tierAlpha[1]), _tierAlpha[2]);
        maxA = MathF.Max(maxA, MathF.Max(_meditateAlpha, _allSenAlpha));
        maxA = MathF.Max(maxA, MathF.Max(_senAlpha[0], MathF.Max(_senAlpha[1], _senAlpha[2])));
        maxA = MathF.Max(maxA, MathF.Max(_higanbanaAlpha, MathF.Max(_fukaAlpha, _fugetsuAlpha)));
        maxA = MathF.Max(maxA, _hpAlpha);
        if (maxA < 0.01f && Now() - _burstStartT > 0.6) return;

        try
        {
            // Anchor object: target if the user wants to watch the enemy's
            // animation, otherwise the local player.
            Dalamud.Game.ClientState.Objects.Types.IGameObject anchor = null;
            if (noWickyXIV.Config.JobAuraAnchorToTarget)
            {
                try { anchor = DalamudApi.TargetManager?.Target; } catch { }
            }
            if (anchor == null && !noWickyXIV.Config.JobAuraAnchorToTarget)
                anchor = DalamudApi.ObjectTable.LocalPlayer;

            Vector3 world;
            bool usedCached = false;
            if (anchor != null)
            {
                // Compute fresh world position from anchor. Pick the bone
                // index appropriate for player vs target. Allied player
                // targets and enemy targets need different anchor heights —
                // the same bone index hits visibly lower on a player
                // skeleton vs an enemy NPC skeleton, so we keep two
                // separate target slots and switch based on ObjectKind.
                bool anchorIsTarget = noWickyXIV.Config.JobAuraAnchorToTarget;
                bool targetIsAlliedPlayer = false;
                if (anchorIsTarget)
                {
                    try
                    {
                        // Dalamud's ObjectKind enum is forwarded to
                        // FFXIVClientStructs's, where the player-character
                        // value is named `Pc` (not `Player`).
                        targetIsAlliedPlayer =
                            anchor.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
                    }
                    catch { }
                }
                int boneIdx = anchorIsTarget
                    ? (targetIsAlliedPlayer
                        ? noWickyXIV.Config.JobAuraTargetBoneIndexPlayer
                        : noWickyXIV.Config.JobAuraTargetBoneIndex)
                    : noWickyXIV.Config.JobAuraBoneIndex;
                Vector3 root = new Vector3(anchor.Position.X, anchor.Position.Y, anchor.Position.Z);
                world = root;
                if (noWickyXIV.Config.JobAuraAnchorToBone)
                {
                    try
                    {
                        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)anchor.Address;
                        bool drawOk = go != null && go->DrawObject != null;
                        bool sigOk  = Hypostasis.Game.Common.getWorldBonePosition.IsValid;
                        Vector3 bw = Vector3.Zero;
                        if (drawOk && sigOk)
                        {
                            bw = Hypostasis.Game.Common.GetBoneWorldPosition(
                                go, (uint)Math.Max(0, boneIdx));
                            if (bw != Vector3.Zero) world = bw;
                        }
                        // One-shot diag whenever the (anchor, boneIdx) combo
                        // changes so we can see if the bone slider is taking
                        // effect on the target.
                        long key = ((long)anchor.GameObjectId << 16) | (uint)boneIdx;
                        if (key != _lastBoneDiagKey)
                        {
                            _lastBoneDiagKey = key;
                            try { DalamudApi.PluginLog.Information(
                                $"[noWickyXIV] JobAura bone resolve: anchorIsTarget={anchorIsTarget} boneIdx={boneIdx} drawOk={drawOk} sigOk={sigOk} bonePos=({bw.X:F2},{bw.Y:F2},{bw.Z:F2}) root=({root.X:F2},{root.Y:F2},{root.Z:F2})");
                            } catch { }
                        }
                    }
                    catch { /* fall back to root */ }
                }
                _lastAnchorWorld = world;
                _haveLastAnchor = true;
            }
            else if (_haveLastAnchor && _targetAlpha > 0.01f)
            {
                // Target gone, fade still in flight: keep drawing at the
                // last known position so rings ease out in place instead of
                // snapping to the player (which may be off-screen).
                world = _lastAnchorWorld;
                usedCached = true;
            }
            else
            {
                return;
            }
            _ = usedCached; // (reserved for future per-cache styling)
            world.X += noWickyXIV.Config.JobAuraOffsetX;
            world.Y += noWickyXIV.Config.JobAuraOffsetY;
            world.Z += noWickyXIV.Config.JobAuraOffsetZ;

            // Project anchored world position to screen
            if (!DalamudApi.GameGui.WorldToScreen(world, out var screen)) return;
            var dl = ImGui.GetForegroundDrawList();
            float uiScale = ImGuiHelpers.GlobalScale;
            float groupScale = MathF.Max(0.1f, noWickyXIV.Config.JobAuraScale);

            // Base ring radius in pixels — perspective-flat. Multiplied by
            // user-controlled groupScale.
            float baseR = 60f * uiScale * groupScale;

            // Per-tier styling. Colors are user-configurable via the
            // Effects tab — defaults preserve the original amber → red
            // ramp.
            var cfgT = noWickyXIV.Config;
            (float r, float thick, Vector4 col) ringFor(int tier) => tier switch
            {
                1 => (baseR,        2.0f * uiScale * groupScale,
                      new Vector4(cfgT.JobAuraTier1ColorR, cfgT.JobAuraTier1ColorG, cfgT.JobAuraTier1ColorB, cfgT.JobAuraTier1Alpha)),
                2 => (baseR * 1.4f, 3.0f * uiScale * groupScale,
                      new Vector4(cfgT.JobAuraTier2ColorR, cfgT.JobAuraTier2ColorG, cfgT.JobAuraTier2ColorB, cfgT.JobAuraTier2Alpha)),
                3 => (baseR * 1.8f, 4.0f * uiScale * groupScale,
                      new Vector4(cfgT.JobAuraTier3ColorR, cfgT.JobAuraTier3ColorG, cfgT.JobAuraTier3ColorB, cfgT.JobAuraTier3Alpha)),
                _ => (0f, 0f, Vector4.Zero),
            };

            // Stack rings — render each independently of _tier so smoothed
            // alphas can fade in/out without snap-cuts when Kenki dips.
            for (int i = 1; i <= 3; i++)
            {
                float a = _tierAlpha[i - 1];
                if (a < 0.01f) continue;
                var (r, thick, col) = ringFor(i);
                if (i == 3)
                {
                    double ph = (Now() - _burstStartT) % 1.2;
                    float pulse = 0.5f + 0.5f * MathF.Sin((float)(ph * Math.PI * 2.0 / 1.2));
                    col.W *= 0.65f + 0.35f * pulse;
                    r *= 1.0f + 0.07f * pulse;
                }
                // Hostile cascade gate: Tier i (1..3) maps to slot
                // (5 - i) so Tier 1 (innermost) is slot 4 and fades
                // OUT last on friendly-target / fades IN first on
                // hostile-target.
                col.W *= a * _combatAlpha * _hostileCascadeAlpha[5 - i];
                if (col.W < 0.01f) continue;
                dl.AddCircle(screen, r, ImGui.GetColorU32(col), 64, thick);
            }

            // Explosive burst on tier-3 entry — expanding alpha-fading ring,
            // ~0.6s lifetime. Computed independent of pulse above.
            double bAge = Now() - _burstStartT;
            if (_tier >= 3 && bAge >= 0 && bAge < 0.6)
            {
                float k = (float)(bAge / 0.6);
                float bR = baseR * (0.8f + 4.5f * k);
                // Burst rides the Tier 3 cascade slot (2) so it
                // disappears with the rest of the Kenki feedback when
                // we're on a friendly target.
                float bA = (1f - k) * 0.85f * _combatAlpha * _hostileCascadeAlpha[2];
                var bCol = new Vector4(1f, 0.55f, 0.15f, bA);
                if (bA >= 0.01f)
                    dl.AddCircle(screen, bR, ImGui.GetColorU32(bCol), 96, 5f * uiScale * groupScale);
            }

            // Meditate overlay — wider yellow Inner-Release-feel ring layered
            // on top of the Kenki tier rings while the buff is up. Pulses
            // independent of the tier-3 pulse so you can see both at once.
            if (_meditateAlpha >= 0.01f)
            {
                double mph = (Now() - _bornAt) % 1.6;
                float mpulse = 0.5f + 0.5f * MathF.Sin((float)(mph * Math.PI * 2.0 / 1.6));
                float mR = baseR * (2.1f + 0.06f * mpulse);
                // Meditate rides the Tier 3 cascade slot (2) — same
                // visual layer as Kenki Tier 3.
                float mA = (0.55f + 0.35f * mpulse) * _combatAlpha * _meditateAlpha * _hostileCascadeAlpha[2];
                if (mA >= 0.01f)
                {
                    var mInner = new Vector4(1.0f, 0.92f, 0.45f, mA);
                    var mOuter = new Vector4(1.0f, 0.78f, 0.20f, mA * 0.55f);
                    dl.AddCircle(screen, mR,         ImGui.GetColorU32(mInner), 96, 4.5f * uiScale * groupScale);
                    dl.AddCircle(screen, mR * 1.08f, ImGui.GetColorU32(mOuter), 96, 2.5f * uiScale * groupScale);
                }
            }

            // AllSen ("full zen / mangekyu ready") double ring.
            // Two-phase reveal driven by _allSenAlpha (0..1):
            //   alpha 0.0 → 0.5 : inner ring fades in alpha, outer hidden
            //   alpha 0.5 → 1.0 : inner is full, outer ring traces around
            //                     the circle like a snake (PathArcTo)
            // On fade-out the same progress curve runs backwards so the
            // snake retracts before the inner alpha drops.
            float largestRingR = baseR * 2.45f;
            if (_allSenAlpha >= 0.01f)
            {
                var cfg = noWickyXIV.Config;
                double sph = (Now() - _bornAt) % 0.9;
                float spulse = 0.5f + 0.5f * MathF.Sin((float)(sph * Math.PI * 2.0 / 0.9));
                float sR = baseR * (2.45f + 0.09f * spulse);
                // AllSen rings ride cascade slot 1 — fades just before
                // the Sen markers (slot 0) on friendly target.
                float pulseA = (0.6f + 0.35f * spulse) * _combatAlpha * _hostileCascadeAlpha[1];
                largestRingR = sR;

                // Phase split based on the smoothed _allSenAlpha. When
                // it's between 0 and 0.5, inner ramps; between 0.5 and
                // 1, outer snake-traces.
                float innerProg = MathF.Min(1f, _allSenAlpha / 0.5f);
                float outerProg = MathF.Max(0f, MathF.Min(1f, (_allSenAlpha - 0.5f) / 0.5f));

                // Inner ring (full circle, alpha-ramped).
                float innerA = pulseA * cfg.JobAuraAllSenInnerAlpha * innerProg;
                if (innerA >= 0.01f)
                {
                    var sInner = new Vector4(
                        cfg.JobAuraAllSenInnerColorR, cfg.JobAuraAllSenInnerColorG, cfg.JobAuraAllSenInnerColorB,
                        innerA);
                    dl.AddCircle(screen, sR, ImGui.GetColorU32(sInner), 96,
                        cfg.JobAuraAllSenInnerThickness * uiScale * groupScale);
                }

                // Outer ring — partial arc that grows from 12 o'clock
                // clockwise as outerProg goes 0 → 1. At 1.0 it closes
                // into a full circle.
                if (outerProg > 0.001f)
                {
                    float outerA = pulseA * cfg.JobAuraAllSenOuterAlpha;
                    if (outerA >= 0.01f)
                    {
                        float outerR = sR * cfg.JobAuraAllSenOuterRadiusFactor;
                        // ImGui angle convention: 0 = right, π/2 = down,
                        // π = left, 3π/2 = up. Start at top (-π/2) and
                        // sweep clockwise.
                        float aMin = -MathF.PI / 2f;
                        float aMax = aMin + (2f * MathF.PI * outerProg);
                        // Segments scale with sweep so the partial arc
                        // stays smooth at small fractions.
                        int segs = Math.Max(8, (int)MathF.Ceiling(96f * outerProg));
                        var sOuter = new Vector4(
                            cfg.JobAuraAllSenOuterColorR, cfg.JobAuraAllSenOuterColorG, cfg.JobAuraAllSenOuterColorB,
                            outerA);
                        dl.PathArcTo(screen, outerR, aMin, aMax, segs);
                        dl.PathStroke(ImGui.GetColorU32(sOuter), ImDrawFlags.None,
                            cfg.JobAuraAllSenOuterThickness * uiScale * groupScale);
                    }
                }
            }

            // Center HP indicator — persistent dark-red backdrop circle
            // sized at full radius; an inner brighter-red core that shrinks
            // AND fades as HP drops, so the backdrop remains as a stable
            // foreground for the text. Text "XX%" stays at fixed font size.
            if (_hpAlpha >= 0.01f)
            {
                float hpA = _hpAlpha * _combatAlpha;
                if (hpA >= 0.01f)
                    DrawHpRingsAt(dl, screen, baseR, _targetHpPct, hpA, uiScale, drawText: true);
            }

            // Party HP rings — mirror the player/target HP indicator on
            // every party member (excluding self). Anchored to the same
            // bone slot the user has configured for ally targets so the
            // ring lands where the head/upper-spine sits, regardless of
            // race skeleton differences.
            if (noWickyXIV.Config.JobAuraPartyHpRings && _combatAlpha >= 0.01f)
            {
                try
                {
                    var party = DalamudApi.PartyList;
                    var lpId  = DalamudApi.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
                    if (party != null && party.Length > 0)
                    {
                        int boneIdx = noWickyXIV.Config.JobAuraTargetBoneIndexPlayer;
                        foreach (var pm in party)
                        {
                            if (pm == null) continue;
                            var go = pm.GameObject;
                            if (go == null || !go.IsValid()) continue;
                            if (go.GameObjectId == lpId) continue;

                            // Resolve world position the same way the
                            // primary anchor does — bone if configured,
                            // root otherwise. Add the user's offsets so
                            // the ring sits where the player wants.
                            Vector3 mw = new Vector3(go.Position.X, go.Position.Y, go.Position.Z);
                            if (noWickyXIV.Config.JobAuraAnchorToBone)
                            {
                                try
                                {
                                    var cgo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)go.Address;
                                    if (cgo != null && cgo->DrawObject != null
                                        && Hypostasis.Game.Common.getWorldBonePosition.IsValid)
                                    {
                                        var bw = Hypostasis.Game.Common.GetBoneWorldPosition(
                                            cgo, (uint)Math.Max(0, boneIdx));
                                        if (bw != Vector3.Zero) mw = bw;
                                    }
                                }
                                catch { /* fall back to root */ }
                            }
                            mw.X += noWickyXIV.Config.JobAuraOffsetX;
                            mw.Y += noWickyXIV.Config.JobAuraOffsetY;
                            mw.Z += noWickyXIV.Config.JobAuraOffsetZ;

                            if (!DalamudApi.GameGui.WorldToScreen(mw, out var memberScreen)) continue;

                            float memberPct = 1f;
                            if (go is Dalamud.Game.ClientState.Objects.Types.IBattleChara ibc && ibc.MaxHp > 0)
                                memberPct = MathF.Max(0f, MathF.Min(1f, ibc.CurrentHp / (float)ibc.MaxHp));

                            DrawHpRingsAt(dl, memberScreen, baseR, memberPct, _combatAlpha, uiScale, drawText: false);
                        }
                    }
                }
                catch { /* defensive — party iteration shouldn't break the rest of Draw */ }
            }

            // Top-cluster buff indicators — small triangle FLOATING ABOVE
            // the rings (centered horizontally), so the ring center stays
            // free for the HP indicator. Same layout as before:
            //   bottom    = Higanbana (target DoT, red-pink)
            //   top-left  = Fuka      (yellow)
            //   top-right = Fugetsu   (light indigo blue)
            // Each filled circle still scales by remaining/maxDuration.
            {
                float clusterCx = screen.X;
                float clusterCy = screen.Y - largestRingR * 1.18f;   // sit just above the largest ring
                float innerRadius = baseR * 0.30f;                    // tighter triangle than before
                Vector2[] innerDirs =
                {
                    new Vector2( 0.0f,   1.0f),    // bottom    → Higanbana
                    new Vector2(-0.866f, -0.5f),   // top-left  → Fuka
                    new Vector2( 0.866f, -0.5f),   // top-right → Fugetsu
                };
                float[]   innerAlphas = { _higanbanaAlpha, _fukaAlpha, _fugetsuAlpha };
                float[]   innerPct    = {
                    MathF.Min(1f, _higanbanaRemaining / HIGANBANA_FULL_SECONDS),
                    MathF.Min(1f, _fukaRemaining      / FUKA_FULL_SECONDS),
                    MathF.Min(1f, _fugetsuRemaining   / FUGETSU_FULL_SECONDS),
                };
                Vector4[] innerCore =
                {
                    new Vector4(1.00f, 0.20f, 0.40f, 1f), // red-pink Higanbana
                    new Vector4(1.00f, 0.95f, 0.30f, 1f), // yellow Fuka
                    new Vector4(0.55f, 0.65f, 1.00f, 1f), // light indigo blue Fugetsu
                };
                Vector4[] innerHalo =
                {
                    new Vector4(0.95f, 0.10f, 0.30f, 1f),
                    new Vector4(0.95f, 0.85f, 0.15f, 1f),
                    new Vector4(0.40f, 0.50f, 0.95f, 1f),
                };
                float baseDot = baseR * 0.14f;
                for (int i = 0; i < 3; i++)
                {
                    float a = innerAlphas[i] * _combatAlpha;
                    if (a < 0.01f) continue;
                    float scaleByTime = MathF.Max(0.05f, innerPct[i]);
                    float r = baseDot * scaleByTime;
                    var pos = new Vector2(
                        clusterCx + innerDirs[i].X * innerRadius,
                        clusterCy + innerDirs[i].Y * innerRadius);
                    var halo = innerHalo[i]; halo.W = a * 0.35f;
                    var core = innerCore[i]; core.W = a * 0.85f;
                    dl.AddCircleFilled(pos, r * 1.45f, ImGui.GetColorU32(halo), 48);
                    dl.AddCircleFilled(pos, r,         ImGui.GetColorU32(core), 48);
                }
            }

            // Sen markers — three filled dots placed on a triangle around
            // the largest ring. Layout (apex-up triangle):
            //   top-center    = Setsu (cyan)
            //   bottom-left   = Ka    (salmon)
            //   bottom-right  = Getsu (blue)
            // Each fades in/out with its own smoothed alpha so picking up
            // or spending a Sen doesn't snap.
            {
                float markerR = largestRingR * MathF.Max(0.1f, noWickyXIV.Config.JobAuraSenPadding);
                float dotSize = 8f * uiScale * groupScale * MathF.Max(0.1f, noWickyXIV.Config.JobAuraSenScale);
                // Y is screen-down; sin(60°)≈0.866, cos(60°)=0.5
                Vector2[] dirs =
                {
                    new Vector2( 0.0f,   -1.0f), // top-center   → Setsu (cyan)
                    new Vector2(-0.866f,  0.5f), // bottom-left  → Ka    (salmon)
                    new Vector2( 0.866f,  0.5f), // bottom-right → Getsu (blue)
                };
                Vector4[] cols =
                {
                    new Vector4(1.00f, 0.55f, 0.55f, 1f), // salmon-red → Setsu (top)
                    new Vector4(0.45f, 1.00f, 0.75f, 1f), // light cyan-green → Ka (bottom-left)
                    new Vector4(0.35f, 0.55f, 1.00f, 1f), // blue → Getsu (bottom-right)
                };
                // Sen cascade gate: markers wait JobAuraSenCascadeDelay
                // after the overlay first becomes visible before they
                // begin showing, so they read as appearing AFTER the
                // rings / HP indicator. Linear ramp over 0.25s once the
                // delay has elapsed.
                float cascadeDelay = MathF.Max(0f, noWickyXIV.Config.JobAuraSenCascadeDelay);
                float sinceRise = (float)(Now() - _overlayRiseT);
                float cascadeGate;
                if (sinceRise <= cascadeDelay)        cascadeGate = 0f;
                else if (sinceRise >= cascadeDelay + 0.25f) cascadeGate = 1f;
                else                                  cascadeGate = (sinceRise - cascadeDelay) / 0.25f;

                for (int i = 0; i < 3; i++)
                {
                    // Sen markers ride hostile cascade slot 0 (fades
                    // OUT first on friendly target, IN last on hostile)
                    // in addition to the existing initial-overlay
                    // cascadeGate.
                    float sa = _senAlpha[i] * _combatAlpha * cascadeGate * _hostileCascadeAlpha[0];
                    if (sa < 0.01f) continue;
                    var pos = new Vector2(
                        screen.X + dirs[i].X * markerR,
                        screen.Y + dirs[i].Y * markerR);
                    var c  = cols[i]; c.W = sa;
                    var co = cols[i]; co.W = sa * 0.55f;
                    // Outer halo for legibility
                    dl.AddCircleFilled(pos, dotSize * 1.6f, ImGui.GetColorU32(co), 32);
                    // Inner solid dot
                    dl.AddCircleFilled(pos, dotSize, ImGui.GetColorU32(c), 32);
                    // Thin ring outline (helps it pop against bright backgrounds)
                    dl.AddCircle(pos, dotSize, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, sa * 0.6f)), 32, 1.5f * uiScale);
                }
            }
        }
        catch { /* defensive */ }
    }

    private static double Now() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    // True if the current target is a hostile combat entity. Friendly
    // NPCs, player characters (incl. self via target-self macro),
    // pets / chocobos, event NPCs, etc. all count as "not hostile" so
    // the Kenki + Sen cascade fades out on them.
    private static bool IsTargetHostile()
    {
        try
        {
            var t = DalamudApi.TargetManager?.Target;
            if (t == null) return false;
            // Players are always non-hostile in PvE; PvP doesn't
            // currently distinguish via ObjectKind alone, so accept
            // the false-negative there for simplicity.
            if (t.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                return false;
            if (t is Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn)
                // Dalamud's BattleNpcSubKind enum values for friendlies
                // are 1..3 (Pet / Chocobo / Buddy). Enemy/Combatant
                // sits at 5; use the numeric value to dodge enum
                // member-name churn between Dalamud versions.
                return (int)bn.BattleNpcKind == 5;
        }
        catch { }
        return false;
    }

    // Draws the 3-layer HP indicator (backdrop + core + pulse) at the
    // given screen position. Extracted so the same draw runs for the
    // primary anchor (target/player) AND for every party member when
    // JobAuraPartyHpRings is enabled. drawText controls the "XX%"
    // overlay — primary anchor uses it, party rings skip it because
    // the on-screen clutter would be too much.
    private static unsafe void DrawHpRingsAt(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 screen, float baseR, float hpPct, float hpA, float uiScale,
        bool drawText)
    {
        var cfg = noWickyXIV.Config;
        // Outer (backdrop) ring — persistent.
        float backdropR = baseR * cfg.JobAuraHpBackdropRadiusFactor;
        var backdrop = new Vector4(
            cfg.JobAuraHpBackdropColorR, cfg.JobAuraHpBackdropColorG, cfg.JobAuraHpBackdropColorB,
            hpA * cfg.JobAuraHpBackdropAlpha);
        dl.AddCircleFilled(screen, backdropR, Dalamud.Bindings.ImGui.ImGui.GetColorU32(backdrop), 64);

        // Inner core — radius + alpha scale with HP.
        float coreR = baseR * cfg.JobAuraHpInnerRadiusFactor * hpPct;
        float coreA = hpA * cfg.JobAuraHpInnerAlpha * MathF.Max(0.05f, hpPct);
        if (coreR > 0.5f && coreA >= 0.01f)
        {
            var core = new Vector4(
                cfg.JobAuraHpInnerColorR, cfg.JobAuraHpInnerColorG, cfg.JobAuraHpInnerColorB,
                coreA);
            dl.AddCircleFilled(screen, coreR, Dalamud.Bindings.ImGui.ImGui.GetColorU32(core), 64);
        }

        // Text — primary anchor only. Skipped for party rings.
        if (drawText && cfg.JobAuraShowHpText)
        {
            string label = $"{(int)MathF.Round(hpPct * 100f)}%";
            EnsureHpFont();
            bool pushed = false;
            try
            {
                if (_hpFontHandle != null && _hpFontHandle.Available)
                {
                    _hpFontHandle.Push();
                    pushed = true;
                }
                var ts = Dalamud.Bindings.ImGui.ImGui.CalcTextSize(label);
                var textPos = new Vector2(screen.X - ts.X * 0.5f, screen.Y - ts.Y * 0.5f);
                var shadow = new Vector4(0f, 0f, 0f, hpA * 0.9f);
                var fg     = new Vector4(1f, 1f, 1f, hpA);
                dl.AddText(new Vector2(textPos.X + 1, textPos.Y + 1),
                    Dalamud.Bindings.ImGui.ImGui.GetColorU32(shadow), label);
                dl.AddText(textPos, Dalamud.Bindings.ImGui.ImGui.GetColorU32(fg), label);
            }
            finally
            {
                if (pushed) _hpFontHandle?.Pop();
            }
        }

        // Pulse ring emanating from the HP backdrop.
        float pulseT = (float)_hpPulsePhase;
        float pulseRStart = backdropR;
        float pulseREnd   = backdropR * cfg.JobAuraHpPulseExpandFactor;
        float pulseR = pulseRStart + (pulseREnd - pulseRStart) * pulseT;
        float pulseA = (1f - pulseT) * cfg.JobAuraHpPulseAlpha * hpA;
        if (pulseA >= 0.01f)
        {
            var pCol = new Vector4(
                cfg.JobAuraHpPulseColorR, cfg.JobAuraHpPulseColorG, cfg.JobAuraHpPulseColorB,
                pulseA);
            dl.AddCircle(screen, pulseR, Dalamud.Bindings.ImGui.ImGui.GetColorU32(pCol),
                96, cfg.JobAuraHpPulseThickness * uiScale);
        }
    }

    // True if any OTHER enabled layer in the list fired within its
    // RunTimeSeconds window — i.e. its visual is presumed still
    // playing. Used by layers with SuppressWhileOthersFiring set so
    // they don't pile their effect on top of an in-flight one.
    private static bool AnyOtherEnabledLayerFiring(
        System.Collections.Generic.List<JobAuraVfxLayer> layers,
        Guid selfId, double now)
    {
        if (layers == null) return false;
        foreach (var l in layers)
        {
            if (l == null || !l.Enabled || l.Id == selfId) continue;
            if (!_layerLastFire.TryGetValue(l.Id, out var lastFire)) continue;
            // RunTimeSeconds is the user-configured nominal duration of
            // a single shot — not a hard guarantee but a good proxy for
            // "still playing". Floor 50ms so a misconfigured 0 doesn't
            // disable the gate entirely.
            float window = MathF.Max(0.05f, l.RunTimeSeconds);
            if (now - lastFire < window) return true;
        }
        return false;
    }

    // De-duplication for path-rejection warnings so we don't spam the
    // log every frame. Re-warns when the path string actually changes.
    private static readonly System.Collections.Generic.Dictionary<Guid, string> _layerPathRejectWarned = new();
    private static void WarnPathRejectedOnce(JobAuraVfxLayer layer)
    {
        try
        {
            if (_layerPathRejectWarned.TryGetValue(layer.Id, out var seen)
                && seen == layer.Path) return;
            _layerPathRejectWarned[layer.Id] = layer.Path;
            DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] JobAura layer '{layer.Name}' path rejected by validator: '{layer.Path}' (must be forward-slashed, end in .avfx, no spaces, ≥8 chars).");
        }
        catch { }
    }
    private static float MathHelpers_Lerp(float a, float b, float t) => a + (b - a) * MathF.Max(0f, MathF.Min(1f, t));

    // Instantly zero every smoothed visual state and drop the cached
    // anchor — used when a loading screen comes up so the target ring
    // doesn't render above it and we don't re-bind to whatever was
    // targeted before the zone change.
    private static void ClearVisualState()
    {
        for (int i = 0; i < _tierAlpha.Length; i++) _tierAlpha[i] = 0f;
        for (int i = 0; i < _senAlpha.Length;  i++) _senAlpha[i]  = 0f;
        _allSenAlpha    = 0f;
        _meditateAlpha  = 0f;
        _higanbanaAlpha = 0f;
        _fukaAlpha      = 0f;
        _fugetsuAlpha   = 0f;
        _hpAlpha        = 0f;
        _targetAlpha    = 0f;
        _combatAlpha    = 0f;
        _oocAlpha       = 0f;
        _haveLastAnchor = false;
        _lastAnchorWorld = default;
        // Reset cascade timing too so re-entering a zone re-cascades
        // the Sen markers from scratch instead of skipping the delay.
        _overlayRiseT = double.MinValue;
        _overlayWasVisible = false;
        // Wipe hostile cascade state so the next acquisition gets a
        // fresh fade-in instead of inheriting whatever the previous
        // zone left behind.
        for (int i = 0; i < _hostileCascadeAlpha.Length; i++) _hostileCascadeAlpha[i] = 0f;
        _targetHostile = false;
        _targetHostilePrev = false;
        _hostileTransitionT = double.MinValue;
    }

    // Cheap path validator. The game's resource loader (especially
    // through Penumbra/VFXEditor's hooks) chokes on malformed input —
    // partial paths typed mid-edit have crashed us native-side. Reject
    // anything that isn't a complete-looking avfx path before we hand
    // it to ActorVfxCreate.
    private static bool IsLikelyValidVfxPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length < 8) return false;          // shortest plausible path
        if (path.IndexOf('\\') >= 0) return false;  // FFXIV uses forward slashes
        if (path.IndexOf(' ') >= 0) return false;   // no whitespace in valid paths
        if (!path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Per-tier "entered from below" tracking. A Kenki tier trigger
    // is only `active` while we're in the tier range AND we entered
    // it by crossing the lower bound from below. Going DOWN through
    // a tier (e.g. 70 → 50 lands in Tier1's 33–65 range) does NOT
    // activate the trigger, because the user just spent kenki and
    // wants visual feedback only on growth, not decay.
    private static int  _prevKenkiForTiers = -1;
    private static bool _kenkiTier1FromBelow;
    private static bool _kenkiTier2FromBelow;
    private static bool _kenkiTier3FromBelow;

    private static void UpdateKenkiTierFromBelow(int kenki)
    {
        int prev = _prevKenkiForTiers < 0 ? kenki : _prevKenkiForTiers;
        // Tier 1 range = [33, 66).
        bool prevInT1 = prev   >= 33 && prev   < 66;
        bool nowInT1  = kenki  >= 33 && kenki  < 66;
        if (!prevInT1 && nowInT1)  _kenkiTier1FromBelow = (prev <  33);  // entered by crossing the floor going up
        if ( prevInT1 && !nowInT1) _kenkiTier1FromBelow = false;          // left in either direction → reset
        // Tier 2 range = [66, 100).
        bool prevInT2 = prev   >= 66 && prev   < 100;
        bool nowInT2  = kenki  >= 66 && kenki  < 100;
        if (!prevInT2 && nowInT2)  _kenkiTier2FromBelow = (prev <  66);
        if ( prevInT2 && !nowInT2) _kenkiTier2FromBelow = false;
        // Tier 3 range = [100, ∞). Only enterable from below.
        bool prevInT3 = prev   >= 100;
        bool nowInT3  = kenki  >= 100;
        if (!prevInT3 && nowInT3)  _kenkiTier3FromBelow = true;
        if ( prevInT3 && !nowInT3) _kenkiTier3FromBelow = false;
        _prevKenkiForTiers = kenki;
    }

    private static bool EvaluateTrigger(JobAuraTrigger trig, int kenki, bool capRising,
                                        bool setsu, bool getsu, bool ka, bool allSen)
    {
        switch (trig)
        {
            // Exclusive Kenki tier ranges — each tier owns its band so that
            // a layer at Tier1 deactivates the moment Tier2 kicks in.
            // Direction-gated: only active when entered from below (going
            // up) — descending into a tier does not trigger the rising
            // edge, so spending Kenki doesn't fire visual feedback for
            // tiers it passes through.
            case JobAuraTrigger.KenkiTier1:   return kenki >= 33 && kenki <  66 && _kenkiTier1FromBelow;
            case JobAuraTrigger.KenkiTier2:   return kenki >= 66 && kenki < 100 && _kenkiTier2FromBelow;
            case JobAuraTrigger.KenkiTier3:   return kenki >= 100 && _kenkiTier3FromBelow;
            case JobAuraTrigger.KenkiCapEdge: return capRising;
            case JobAuraTrigger.Setsu:        return setsu;
            case JobAuraTrigger.Getsu:        return getsu;
            case JobAuraTrigger.Ka:           return ka;
            case JobAuraTrigger.AllSen:       return allSen;
            case JobAuraTrigger.NormalHit:      return CombatEvents.NormalHit;
            case JobAuraTrigger.CritHit:        return CombatEvents.CritHit;
            case JobAuraTrigger.IncomingDamage: return CombatEvents.IncomingDamage;
            case JobAuraTrigger.Moving:         return _isMoving;
            case JobAuraTrigger.Stopped:        return _isMoving == false && _motionTracked;
            default:                          return false;
        }
    }

    // Player motion state — sampled each Update from local-player
    // position delta. Threshold is in metres-per-second; a quick
    // glance confirms in-game walk speed is ~3 m/s and run is ~6 m/s,
    // so 0.5 m/s is comfortably above "standing still" jitter.
    private const float MOTION_SPEED_THRESHOLD = 0.5f;
    private static bool _isMoving;
    private static bool _motionTracked;
    private static System.Numerics.Vector3 _lastMotionPos;
    private static double _lastMotionAt;

    // Called once per Update with the current player. Computes the
    // instantaneous speed and updates _isMoving.
    private static void UpdateMotionState(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter lp)
    {
        if (lp == null) { _isMoving = false; _motionTracked = false; return; }
        var nowPos = new System.Numerics.Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);
        double now = Now();
        if (!_motionTracked)
        {
            _lastMotionPos = nowPos;
            _lastMotionAt = now;
            _motionTracked = true;
            _isMoving = false;
            return;
        }
        double dt = now - _lastMotionAt;
        if (dt <= 0.001) return; // wait for a real frame delta
        var delta = nowPos - _lastMotionPos;
        // Y axis ignored — vertical jitter from terrain shouldn't
        // count as moving. Horizontal speed only.
        float horizDist = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        float speed = horizDist / (float)dt;
        _isMoving = speed > MOTION_SPEED_THRESHOLD;
        _lastMotionPos = nowPos;
        _lastMotionAt = now;
    }

    // Public read-only snapshot for the UI's "current state" debug column.
    public static System.Collections.Generic.IReadOnlyDictionary<JobAuraTrigger, bool> SnapshotTriggers()
    {
        var d = new System.Collections.Generic.Dictionary<JobAuraTrigger, bool>
        {
            // Exclusive ranges to mirror EvaluateTrigger.
            [JobAuraTrigger.KenkiTier1]   = _tier == 1,
            [JobAuraTrigger.KenkiTier2]   = _tier == 2,
            [JobAuraTrigger.KenkiTier3]   = _tier == 3,
            [JobAuraTrigger.KenkiCapEdge] = false, // edge-only — never "currently on"
            [JobAuraTrigger.Setsu]        = _hasSetsu,
            [JobAuraTrigger.Getsu]        = _hasGetsu,
            [JobAuraTrigger.Ka]           = _hasKa,
            [JobAuraTrigger.AllSen]       = _hasAllSen,
            [JobAuraTrigger.NormalHit]      = CombatEvents.NormalHit,
            [JobAuraTrigger.CritHit]        = CombatEvents.CritHit,
            [JobAuraTrigger.IncomingDamage] = CombatEvents.IncomingDamage,
            [JobAuraTrigger.Moving]         = _isMoving,
            [JobAuraTrigger.Stopped]        = _motionTracked && !_isMoving,
        };
        return d;
    }
}
