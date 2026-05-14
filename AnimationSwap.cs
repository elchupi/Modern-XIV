using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;

namespace noWickyXIV;

// Race-to-race body animation swap via Penumbra IPC.
//
// Registers game-path-to-game-path swaps through Penumbra's
// AddTemporaryModAll IPC so that all .pap animation files loaded
// for the source race's model code (e.g. c1301 = Au Ra Male) get
// redirected to the target race's model code (e.g. c0701 = Miqo'te
// Male). Penumbra's PapRewriter handles the compiler-inlined
// GetResourceAsync calls that direct hooks at the function entry
// point cannot intercept.
//
// Also hooks ResolvePapPath (vtable[84] on CharacterBase/Human)
// purely for visual-race detection — that hook only sees face
// animations (partial skeleton idx=1), but that's enough to learn
// the Glamourer-altered model code.
public static unsafe class AnimationSwap
{
    // ── ResolvePapPath hook (visual race detection only) ────────
    private delegate nint ResolvePapPathDelegate(
        nint drawObject, nint pathBuffer, nint pathBufferSize,
        uint animIndex, nint animName);

    private static Hook<ResolvePapPathDelegate> _vtableHook;
    private static nint _hookedVtableFnAddr;

    // ── Penumbra IPC ───────────────────────────────────────────
    private const string PenumbraTag = "noWickyXIV_AnimSwap";
    private const int PenumbraPriority = 50;

    private static ICallGateSubscriber<string, Dictionary<string, string>, string, int, int> _penumbraAdd;
    private static ICallGateSubscriber<string, int, int> _penumbraRemove;
    private static ICallGateSubscriber<string, string> _penumbraResolve;
    private static bool _penumbraIpcInit;
    private static bool _penumbraSwapsActive;
    private static string _penumbraStatus = "not initialized";
    private static int _registeredSwapCount;

    // ── Swap state ──────────────────────────────────────────────
    private static bool _anySwapActive;
    private static nint _localPlayerDrawObj;
    private static string _lastSrcCode, _lastTgtCode;

    // Visual race detection — auto-detected from intercepted paths.
    public static byte VisualRaceId;
    public static byte VisualSex;
    public static string VisualModelCode = "";

    // Diagnostic counters and log.
    public static int TotalHookCalls;
    public static int TotalSwaps;
    private static readonly List<string> _diagPapLog = new();
    private static Dictionary<string, string> _lastSwapPaths;

    // Timeline change logger — captures slot transitions so we can see
    // which ActionTimeline keys the game plays during draw/sheathe.
    private static readonly List<string> _timelineChangeLog = new();
    private static readonly ushort[] _lastDiagSlots = new ushort[14];

    // Redraw state.
    private static int _redrawPhase;
    private static int _redrawCooldown;
    private static bool _lastSwapActive;
    private static bool _everActivated; // first-load: schedule delayed re-apply

    // Deferred re-apply — wait for external redraws (Glamourer) to settle.
    private static int _pendingReapplyDelay;

    // ── Job animation swap state ───────────────────────────────
    private const string JobPenumbraTag = "noWickyXIV_JobAnimSwap";
    private const int JobPenumbraPriority = 51;
    private static bool _jobSwapsActive;
    private static string _lastJobSrcFolder;
    private static string _lastJobHoldTgtFolder, _lastJobMoveTgtFolder, _lastJobAttackTgtFolder;
    private static string _lastJobModelCode, _lastJobVisualModel;
    private static int _registeredJobSwapCount;
    private static Dictionary<string, string> _lastJobSwapPaths;
    private static string _jobSwapStatus = "";

    // ── ClassJob → weapon animation folder mapping ─────────────
    // Corrected folder names from SkillSwap / VFXEditor community.
    // ActionTimeline keys like "ws/bt_2sp_emp/wsh001" confirm names.
    // Wrong names safely produce 0 swaps (FileExists validates).
    public static readonly Dictionary<uint, string> JobWeaponFolder = new()
    {
        // Tanks
        {  1, "bt_swd_sld" },  // GLA (sword + shield)
        { 19, "bt_swd_sld" },  // PLD
        {  3, "bt_2ax_emp" },  // MRD (great axe)
        { 21, "bt_2ax_emp" },  // WAR
        { 32, "bt_2sw_emp" },  // DRK (greatsword)
        { 37, "bt_2gb_emp" },  // GNB (gunblade)
        // Melee DPS
        {  2, "bt_clw_clw" },  // PGL (claws/fists)
        { 20, "bt_clw_clw" },  // MNK
        {  4, "bt_2sp_emp" },  // LNC (lance/spear)
        { 22, "bt_2sp_emp" },  // DRG
        { 29, "bt_dgr_dgr" },  // ROG (dual daggers)
        { 30, "bt_dgr_dgr" },  // NIN
        { 34, "bt_2kt_emp" },  // SAM (katana)
        { 39, "bt_2km_emp" },  // RPR (scythe/war scythe)
        { 41, "bt_bld_bld" },  // VPR (dual blades)
        // Ranged Physical
        {  5, "bt_2bw_emp" },  // ARC (bow)
        { 23, "bt_2bw_emp" },  // BRD
        { 31, "bt_2gn_emp" },  // MCH (gun)
        { 38, "bt_chk_chk" },  // DNC (chakrams)
        // Casters
        {  7, "bt_2st_emp" },  // THM (staff)
        { 25, "bt_2st_emp" },  // BLM
        { 26, "bt_2bk_emp" },  // ACN (book)
        { 27, "bt_2bk_emp" },  // SMN
        { 35, "bt_2rp_emp" },  // RDM (rapier)
        // Healers
        {  6, "bt_2st_emp" },  // CNJ (cane/staff — shares with BLM)
        { 24, "bt_2st_emp" },  // WHM
        { 28, "bt_2bk_emp" },  // SCH
        { 33, "bt_gla_emp" },  // AST (star globe)
        { 40, "bt_tac_emp" },  // SGE (nouliths)
    };

    // Animation category patterns for job swaps.
    // Movement patterns → use MoveTargetJob's folder.
    // Auto-attack patterns → use AttackTargetJob's folder.
    // Everything else (hold, stance, emotes) → use HoldTargetJob's folder.
    private static readonly string[] MovePatterns =
        { "resident/move_a", "resident/move_b", "resident/move_c", "resident/move_d" };
    private static readonly string[] AutoAttackPatterns =
        { "auto_attack", "normal_attack" };

    public static int RegisteredJobSwapCount => _registeredJobSwapCount;
    public static string JobSwapStatus => _jobSwapStatus;

    // Marker bytes for scanning paths.
    private static readonly byte[] PathMarker =
        Encoding.ASCII.GetBytes("chara/human/c");

    // ── Territory helpers ───────────────────────────────────────
    public static ushort GetCurrentTerritory()
    {
        try { return (ushort)DalamudApi.ClientState.TerritoryType; }
        catch { return 0; }
    }

    public static string LookupTerritoryName(ushort territoryId)
    {
        if (territoryId == 0) return "Any";
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            var row = sheet?.GetRowOrDefault(territoryId);
            if (row.HasValue && row.Value.PlaceName.IsValid)
            {
                string name = row.Value.PlaceName.Value.Name.ExtractText();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch { }
        return $"Territory #{territoryId}";
    }

    // ── Public API ──────────────────────────────────────────────

    // Compatibility properties used by PluginUI diagnostic section.
    public static string ResourceHookStatus => _penumbraStatus;
    public static int TotalResourceCalls => _registeredSwapCount;
    public static int TotalPapCalls => 0;

    /// <summary>
    /// Schedule AnimationSwap to re-register all Penumbra swaps after a
    /// delay. Call this after anything that redraws the character externally
    /// (e.g. Glamourer design application) — the delay lets the external
    /// redraw fully settle before we re-apply + redraw.
    /// </summary>
    public static void ForceReapply(int delayFrames = 60)
    {
        _pendingReapplyDelay = delayFrames;
    }

    // Login readiness retry. On a cold-start login the player can take
    // many frames after Login fires to actually finish drawing —
    // LocalPlayer arrives first, then DrawObject, then the engine has
    // to redraw once for the vtable hook (installed by Update()) to
    // observe a PAP path and resolve VisualRaceId. Without a fully
    // resolved VisualRaceId the apply targets the customize race
    // instead of the Glamourer-overridden visual race, and the cache
    // locks that wrong value in for the rest of the session.
    //
    // Counter ticks down each ExecuteReapply call; while it's > 0 and
    // the player isn't fully ready, ExecuteReapply re-arms itself and
    // forces a redraw so the hook gets a chance to fire. Once ready
    // (or out of retries), the normal flow takes over.
    private static int _loginReapplyRetries;
    private const int  LOGIN_REAPPLY_MAX_RETRIES = 30;
    private const int  LOGIN_REAPPLY_RETRY_FRAMES = 60; // ~1s between retries

    private static unsafe bool IsPlayerFullyReady()
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return false;
            var go = (GameObject*)lp.Address;
            if (go == null || go->DrawObject == null) return false;
            // VisualRaceId is only populated by the vtable hook
            // observing a PAP-path resolve — i.e. the character has
            // actually drawn at least once since the hook installed.
            // No race detection = no valid model code to swap against.
            if (VisualRaceId == 0) return false;
            return true;
        }
        catch { return false; }
    }

    private static void ExecuteReapply()
    {
        _lastSrcCode = null;
        _lastTgtCode = null;
        _lastJobSrcFolder = null;
        _lastJobHoldTgtFolder = null;
        _lastJobMoveTgtFolder = null;
        _lastJobAttackTgtFolder = null;
        _lastJobModelCode = null;
        _lastJobVisualModel = null;

        // Login-path retry: if the player isn't fully drawn yet, force
        // a redraw and try again in LOGIN_REAPPLY_RETRY_FRAMES. Caches
        // are already cleared above so the eventual apply runs against
        // whatever state the engine has populated by retry time.
        if (_loginReapplyRetries > 0 && !IsPlayerFullyReady())
        {
            _loginReapplyRetries--;
            DalamudApi.LogInfo(
                $"[AnimSwap] Reapply not ready (lp={(DalamudApi.ObjectTable.LocalPlayer != null)} race={VisualRaceId}); " +
                $"requesting redraw + retry, {_loginReapplyRetries} attempts left");
            RequestRedraw();
            _pendingReapplyDelay = LOGIN_REAPPLY_RETRY_FRAMES;
            return;
        }

        _loginReapplyRetries = 0;
        DalamudApi.LogInfo("[AnimSwap] Deferred ForceReapply executing — resetting cached state");
    }

    public static void Update()
    {
        bool raceEnabled = noWickyXIV.Config.EnableAnimationSwaps;
        bool jobEnabled  = noWickyXIV.Config.EnableJobAnimationSwaps;

        // Clean up race swaps if disabled.
        if (!raceEnabled && _lastSwapActive)
        {
            _lastSwapActive = false;
            _anySwapActive = false;
            RemovePenumbraSwaps();
            RequestRedraw();
        }

        // Clean up job swaps if disabled.
        if (!jobEnabled && _jobSwapsActive)
        {
            RemoveJobSwaps();
            RequestRedraw();
        }

        // If neither feature is enabled, disable hooks and bail.
        if (!raceEnabled && !jobEnabled)
        {
            DisableVtableHook();
            _pendingReapplyDelay = 0;
            return;
        }

        // Process deferred re-apply (after Glamourer redraw settles).
        if (_pendingReapplyDelay > 0)
        {
            _pendingReapplyDelay--;
            if (_pendingReapplyDelay == 0)
                ExecuteReapply();
        }

        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;

            var go = (GameObject*)lp.Address;
            if (go == null || go->DrawObject == null) return;

            _localPlayerDrawObj = (nint)go->DrawObject;

            // Install vtable hook for visual race detection.
            EnsureVtableHooked(go->DrawObject);

            // Initialize Penumbra IPC (one-time).
            EnsurePenumbraIpc();

            if (raceEnabled) UpdateSwapState(lp);
            if (jobEnabled)  UpdateJobSwapState(lp);

            // Log timeline slot changes for diagnostic — captures what
            // ActionTimeline keys the game plays during draw/sheathe/etc.
            try
            {
                var diagCh = (Character*)lp.Address;
                if (diagCh != null && _timelineChangeLog.Count < 200)
                {
                    for (uint s = 0; s < 14; s++)
                    {
                        ushort tid = diagCh->Timeline.TimelineSequencer.GetSlotTimeline(s);
                        if (tid != _lastDiagSlots[s])
                        {
                            _lastDiagSlots[s] = tid;
                            if (tid != 0)
                                _timelineChangeLog.Add(
                                    $"{DateTime.Now:HH:mm:ss.fff} slot{s}: {tid} \"{LookupTimelineKey(tid)}\"");
                        }
                    }
                }
            }
            catch { }

            ProcessRedraw(go);
        }
        catch { }
    }

    public static void ForceRedraw() => RequestRedraw();

    /// <summary>
    /// Called from the noWickyXIV login workflow. The very first Update()
    /// after login can hit any of:
    ///   * Penumbra IPC not yet ready -> ApplyPenumbraSwaps no-ops silently
    ///     and the cached _lastSrcCode never gets populated.
    ///   * VisualRaceId still 0 (vtable hook fires during character draw,
    ///     which only happens once a redraw triggers a re-resolve).
    ///   * Race-specific code path under chara/human/c{visual}/animation
    ///     isn't yet known to the system.
    /// Force a redraw immediately so the vtable hook gets a fresh draw to
    /// observe, then schedule a deferred re-apply far enough out that the
    /// visual race is detected and Penumbra is reachable.
    /// </summary>
    public static void OnLogin()
    {
        // Reset every cached "we've already registered this code" guard
        // so the next UpdateSwapState always re-registers with whatever
        // race/visual the engine reports post-redraw.
        _lastSrcCode = null;
        _lastTgtCode = null;
        _lastJobSrcFolder = null;
        _lastJobHoldTgtFolder = null;
        _lastJobMoveTgtFolder = null;
        _lastJobAttackTgtFolder = null;
        _lastJobModelCode = null;
        _lastJobVisualModel = null;
        _everActivated = false;
        VisualRaceId = 0;
        VisualSex = 0;
        VisualModelCode = "";

        RequestRedraw();
        // First reapply tick at 30 frames (~0.5 s) — catches most warm
        // logins. If at that point the player still isn't fully drawn
        // (DrawObject null or VisualRaceId still 0 — common on cold
        // start), ExecuteReapply auto-rearms itself and forces another
        // redraw, up to LOGIN_REAPPLY_MAX_RETRIES times (~30 s total
        // window). Each retry gives the vtable hook another chance to
        // observe a PAP-path resolve and populate VisualRaceId before
        // the apply runs.
        _loginReapplyRetries = LOGIN_REAPPLY_MAX_RETRIES;
        ForceReapply(30);
    }

    public static void Dispose()
    {
        RemoveJobSwaps();
        RemovePenumbraSwaps();
        DisableVtableHook();
        try { _vtableHook?.Dispose(); } catch { }
        _vtableHook = null;
        _hookedVtableFnAddr = nint.Zero;
        _anySwapActive = false;
        _penumbraIpcInit = false;
    }

    // ── Penumbra IPC lifecycle ──────────────────────────────────

    private static void EnsurePenumbraIpc()
    {
        if (_penumbraIpcInit) return;
        _penumbraIpcInit = true;

        try
        {
            _penumbraAdd = DalamudApi.PluginInterface
                .GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>(
                    "Penumbra.AddTemporaryModAll.V5");
            _penumbraRemove = DalamudApi.PluginInterface
                .GetIpcSubscriber<string, int, int>(
                    "Penumbra.RemoveTemporaryModAll.V5");
            _penumbraResolve = DalamudApi.PluginInterface
                .GetIpcSubscriber<string, string>(
                    "Penumbra.ResolvePlayerPath");
            _penumbraStatus = "Penumbra IPC ready";
            DalamudApi.LogInfo("[AnimSwap] Penumbra IPC subscribers created");
        }
        catch (Exception ex)
        {
            _penumbraStatus = $"IPC init failed: {ex.Message}";
            DalamudApi.LogInfo($"[AnimSwap] {_penumbraStatus}");
        }
    }

    private static void ApplyPenumbraSwaps(string srcCode, string tgtCode)
    {
        if (_penumbraAdd == null)
        {
            _penumbraStatus = "Penumbra IPC not initialized";
            return;
        }

        try
        {
            var swaps = BuildSwapPaths(srcCode, tgtCode);
            if (swaps.Count == 0)
            {
                _penumbraStatus = "no valid swap paths found in game data";
                DalamudApi.LogInfo("[AnimSwap] No valid swap paths found");
                return;
            }

            // Penumbra does an atomic replace when the tag already exists.
            int result = _penumbraAdd.InvokeFunc(PenumbraTag, swaps, "", PenumbraPriority);

            if (result == 0) // PenumbraApiEc.Success
            {
                _penumbraSwapsActive = true;
                _registeredSwapCount = swaps.Count;
                _lastSwapPaths = swaps;
                TotalSwaps = swaps.Count;
                _penumbraStatus = $"active ({swaps.Count} path swaps)";
                DalamudApi.LogInfo($"[AnimSwap] Registered {swaps.Count} Penumbra swaps: {srcCode} -> {tgtCode}");
            }
            else
            {
                _penumbraStatus = $"Penumbra error code {result}";
                DalamudApi.LogInfo($"[AnimSwap] Penumbra AddTemporaryModAll returned {result}");
            }
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("IpcNotReady"))
        {
            _penumbraStatus = "Penumbra not loaded (install Penumbra for animation swaps)";
            DalamudApi.LogInfo("[AnimSwap] Penumbra IPC not ready — plugin not loaded");
        }
        catch (Exception ex)
        {
            _penumbraStatus = $"error: {ex.Message}";
            DalamudApi.LogInfo($"[AnimSwap] ApplyPenumbraSwaps error: {ex.Message}");
        }
    }

    private static void RemovePenumbraSwaps()
    {
        if (!_penumbraSwapsActive) return;

        try { _penumbraRemove?.InvokeFunc(PenumbraTag, PenumbraPriority); }
        catch { }

        _penumbraSwapsActive = false;
        _registeredSwapCount = 0;
        _lastSwapPaths = null;
        TotalSwaps = 0;
        _penumbraStatus = _penumbraIpcInit ? "Penumbra IPC ready" : "not initialized";
        DalamudApi.LogInfo("[AnimSwap] Penumbra swaps removed");
    }

    // ── Build swap path dictionary ─────────────────────────────
    // Discovers valid animation .pap files for the source model by
    // iterating ActionTimeline keys and probing FileExists. Each
    // matching source path gets a corresponding target path with
    // the model code replaced.

    private static Dictionary<string, string> BuildSwapPaths(string srcCode, string tgtCode)
    {
        var swaps = new Dictionary<string, string>();
        var tried = new HashSet<string>();

        // 1. Iterate ActionTimeline sheet keys — each key often maps
        //    directly to an animation sub-path under bt_common.
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<ActionTimeline>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    try
                    {
                        string key = row.Key.ExtractText();
                        if (string.IsNullOrEmpty(key) || key.Length < 2) continue;
                        if (key.StartsWith("mon_") || key.StartsWith("demi_human/")
                            || key.StartsWith("weapon/") || key.StartsWith("bg/")) continue;

                        // Skip idle animations — only swap walk/run/movement.
                        if (key.Contains("idle", StringComparison.OrdinalIgnoreCase)) continue;

                        TryAddSwap(swaps, tried, srcCode, tgtCode, $"a0001/bt_common/{key}");
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. Also try known resident/nonresident sub-paths that may
        //    not appear as ActionTimeline keys.
        // Known movement/action paths — idle excluded (only walk/run swaps).
        string[] knownPaths =
        {
            "resident/move", "resident/move_a", "resident/move_b",
            "resident/move_c", "resident/move_d", "resident/run", "resident/walk",
            "resident/sprint", "resident/sprint_a", "resident/sprint_b",
            "resident/jump", "resident/fall", "resident/land", "resident/landing",
            "resident/turn_l", "resident/turn_r",
            "resident/sit", "resident/sit_loop", "resident/stand",
            "nonresident/move", "nonresident/run", "nonresident/walk",
        };

        foreach (var p in knownPaths)
        {
            TryAddSwap(swaps, tried, srcCode, tgtCode, $"a0001/bt_common/{p}");
            TryAddSwap(swaps, tried, srcCode, tgtCode, $"a0001/{p}");
        }

        DalamudApi.LogInfo($"[AnimSwap] BuildSwapPaths: probed {tried.Count} paths, found {swaps.Count} valid swaps");
        return swaps;
    }

    private static void TryAddSwap(Dictionary<string, string> swaps, HashSet<string> tried,
        string srcCode, string tgtCode, string relPath)
    {
        string srcPath = $"chara/human/{srcCode}/animation/{relPath}.pap";
        if (!tried.Add(srcPath)) return;

        try
        {
            if (DalamudApi.DataManager.FileExists(srcPath))
                swaps[srcPath] = $"chara/human/{tgtCode}/animation/{relPath}.pap";
        }
        catch { }
    }

    // ── Job animation swap logic ───────────────────────────────

    private static void UpdateJobSwapState(Dalamud.Game.ClientState.Objects.Types.IGameObject lp)
    {
        var ch = (Character*)lp.Address;
        if (ch == null) return;

        uint currentJob = DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJob == 0) return;

        // Battle/weapon animations are stored canonically under c0101
        // (Hyur Midlander Male). Most weapon anims only exist there.
        // However, some (notably event_bt_active/deactive = draw/sheathe)
        // ALSO exist under race-specific models (c0701 for Miqo'te, etc.).
        // The game loads the race-specific version when it exists, so we
        // must register swaps for BOTH model codes.
        const string modelCode = "c0101";

        // Visual model code — race-specific (e.g. c0701 for Miqo'te).
        string visualModel = VisualModelCode;
        if (string.IsNullOrEmpty(visualModel))
        {
            byte vRace = ch->DrawData.CustomizeData.Race;
            byte vSex = ch->DrawData.CustomizeData.Sex;
            visualModel = GetModelCode(vRace, vSex, GetDefaultTribe(vRace));
        }

        string srcFolder = null;
        string holdTgtFolder = null, moveTgtFolder = null, attackTgtFolder = null;
        bool active = false;

        ushort jobTerritory = 0;
        try { jobTerritory = (ushort)DalamudApi.ClientState.TerritoryType; } catch { }

        foreach (var rule in noWickyXIV.Config.JobAnimSwapRules)
        {
            if (!rule.Enabled) continue;
            if (rule.TerritoryId != 0 && rule.TerritoryId != jobTerritory) continue;
            if (rule.SourceJob != 0 && rule.SourceJob != currentJob) continue;
            if (rule.HoldTargetJob == 0 && rule.MoveTargetJob == 0 && rule.AttackTargetJob == 0) continue;
            if (!JobWeaponFolder.TryGetValue(currentJob, out srcFolder)) continue;

            // Resolve hold target folder.
            if (rule.HoldTargetJob != 0
                && JobWeaponFolder.TryGetValue(rule.HoldTargetJob, out var hf)
                && hf != srcFolder)
                holdTgtFolder = hf;

            // Resolve movement target folder.
            if (rule.MoveTargetJob != 0
                && JobWeaponFolder.TryGetValue(rule.MoveTargetJob, out var mf)
                && mf != srcFolder)
                moveTgtFolder = mf;

            // Resolve attack target folder.
            if (rule.AttackTargetJob != 0
                && JobWeaponFolder.TryGetValue(rule.AttackTargetJob, out var af)
                && af != srcFolder)
                attackTgtFolder = af;

            active = holdTgtFolder != null || moveTgtFolder != null || attackTgtFolder != null;
            break;
        }

        bool needsUpdate = active != _jobSwapsActive
            || (active && (srcFolder != _lastJobSrcFolder
                || holdTgtFolder != _lastJobHoldTgtFolder
                || moveTgtFolder != _lastJobMoveTgtFolder
                || attackTgtFolder != _lastJobAttackTgtFolder
                || modelCode != _lastJobModelCode || visualModel != _lastJobVisualModel));

        if (needsUpdate)
        {
            if (active && srcFolder != null)
            {
                ApplyJobSwaps(modelCode, srcFolder, holdTgtFolder, moveTgtFolder, attackTgtFolder, visualModel);
                _lastJobSrcFolder = srcFolder;
                _lastJobHoldTgtFolder = holdTgtFolder;
                _lastJobMoveTgtFolder = moveTgtFolder;
                _lastJobAttackTgtFolder = attackTgtFolder;
                _lastJobModelCode = modelCode;
                _lastJobVisualModel = visualModel;
            }
            else
            {
                RemoveJobSwaps();
                _lastJobSrcFolder = null;
                _lastJobHoldTgtFolder = null;
                _lastJobMoveTgtFolder = null;
                _lastJobAttackTgtFolder = null;
                _lastJobModelCode = null;
                _lastJobVisualModel = null;
            }

            _jobSwapsActive = active;
            RequestRedraw();
        }
    }

    private static void ApplyJobSwaps(string modelCode, string srcFolder,
        string holdTgtFolder, string moveTgtFolder, string attackTgtFolder,
        string visualModelCode)
    {
        if (_penumbraAdd == null) { _jobSwapStatus = "Penumbra IPC not initialized"; return; }

        try
        {
            var swaps = BuildJobSwapPaths(modelCode, srcFolder, holdTgtFolder, moveTgtFolder, attackTgtFolder, visualModelCode);
            if (swaps.Count == 0)
            {
                _jobSwapStatus = $"no swap paths found ({srcFolder})";
                DalamudApi.LogInfo($"[AnimSwap] Job: no valid paths for {srcFolder}");
                return;
            }

            int result = _penumbraAdd.InvokeFunc(JobPenumbraTag, swaps, "", JobPenumbraPriority);

            string holdLabel = holdTgtFolder ?? "—";
            string moveLabel = moveTgtFolder ?? "—";
            string atkLabel  = attackTgtFolder ?? "—";
            if (result == 0)
            {
                _jobSwapsActive = true;
                _registeredJobSwapCount = swaps.Count;
                _lastJobSwapPaths = swaps;
                _jobSwapStatus = $"active ({swaps.Count} swaps, hold:{holdLabel} move:{moveLabel} atk:{atkLabel})";
                DalamudApi.LogInfo($"[AnimSwap] Job: registered {swaps.Count} swaps: hold={holdLabel} move={moveLabel} atk={atkLabel}");
            }
            else
            {
                _jobSwapStatus = $"Penumbra error code {result}";
            }
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("IpcNotReady"))
        {
            _jobSwapStatus = "Penumbra not loaded";
        }
        catch (Exception ex)
        {
            _jobSwapStatus = $"error: {ex.Message}";
        }
    }

    private static void RemoveJobSwaps()
    {
        if (!_jobSwapsActive && _registeredJobSwapCount == 0) return;

        try { _penumbraRemove?.InvokeFunc(JobPenumbraTag, JobPenumbraPriority); }
        catch { }

        _jobSwapsActive = false;
        _registeredJobSwapCount = 0;
        _lastJobSwapPaths = null;
        _jobSwapStatus = "";
    }

    private static Dictionary<string, string> BuildJobSwapPaths(string modelCode,
        string srcFolder, string holdTgtFolder, string moveTgtFolder,
        string attackTgtFolder, string visualModelCode)
    {
        var swaps = new Dictionary<string, string>();

        string basePath = $"chara/human/{modelCode}/animation/a0001";
        int holdHits = 0, moveHits = 0, attackHits = 0, directHits = 0;

        // ── Strategy A: Iterate ActionTimeline keys ──────────────
        // Skip ws/ keys. Route movement keys to moveTgtFolder,
        // auto-attack keys to attackTgtFolder, everything else to
        // holdTgtFolder.
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<ActionTimeline>();
            if (sheet == null) return swaps;

            foreach (var row in sheet)
            {
                try
                {
                    string key = row.Key.ExtractText();
                    if (string.IsNullOrEmpty(key) || key.Length < 2) continue;
                    if (key.StartsWith("mon_") || key.StartsWith("demi_human/")
                        || key.StartsWith("weapon/") || key.StartsWith("bg/")) continue;
                    if (key.StartsWith("ws/", StringComparison.OrdinalIgnoreCase)) continue;

                    // Decide which target folder to use (move > attack > hold).
                    bool isMove   = MatchesAny(key, MovePatterns);
                    bool isAttack = !isMove && MatchesAny(key, AutoAttackPatterns);
                    string tgtFolder = isMove   ? moveTgtFolder
                                     : isAttack ? attackTgtFolder
                                     :            holdTgtFolder;
                    if (tgtFolder == null) continue;

                    string srcPath = $"{basePath}/{srcFolder}/{key}.pap";
                    if (swaps.ContainsKey(srcPath)) continue;

                    string tgtPath = $"{basePath}/{tgtFolder}/{key}.pap";
                    if (DalamudApi.DataManager.FileExists(srcPath)
                        && DalamudApi.DataManager.FileExists(tgtPath))
                    {
                        swaps[srcPath] = ResolvePenumbraPath(tgtPath);
                        if (isMove) moveHits++;
                        else if (isAttack) attackHits++;
                        else holdHits++;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { DalamudApi.LogInfo($"[AnimSwap] BuildJobSwapPaths A error: {ex.Message}"); }

        // ── Strategy B: Direct probing of known weapon sub-paths ──
        try
        {
            // Hold-category paths — stances, events, emotes.
            string[] holdProbes = {
                // Stance / idle
                "resident/idle", "resident/move", "resident/b_idle",
                "resident/battle_idle", "battle/idle", "battle/ready",
                // Events
                "event/event_bt_active", "event/event_bt_deactive",
                "event/event_bt_active2", "event/event_bt_deactive2",
                "event/event_bt_idle",
                // Battle emotes / poses
                "emote/battle01", "emote/battle02", "emote/battle03",
                "emote/battle04", "emote/battle05", "emote/battle06",
                "emote/battle07", "emote/battle08", "emote/battle09",
                "emote/b_pose01", "emote/b_pose01_start", "emote/b_pose01_loop",
                "emote/b_pose02", "emote/b_pose02_start", "emote/b_pose02_loop",
                "emote/b_pose03", "emote/b_pose03_start", "emote/b_pose03_loop",
            };
            // Movement-category paths — weapon-held locomotion.
            string[] moveProbes = {
                "resident/move_a", "resident/move_b", "resident/move_c", "resident/move_d",
                "resident/run", "resident/walk", "resident/sprint",
                "resident/b_run", "resident/b_walk", "resident/b_move", "resident/b_sprint",
                "battle/run", "battle/walk", "battle/move", "battle/sprint",
            };
            // Attack-category paths.
            string[] attackProbes = {
                "battle/auto_attack1", "battle/auto_attack2", "battle/auto_attack3",
                "battle/auto_attack_1", "battle/auto_attack_2", "battle/auto_attack_3",
            };

            if (holdTgtFolder != null)
            {
                foreach (string p in holdProbes)
                {
                    int before = swaps.Count;
                    TryAddJobSwap(swaps, basePath, $"{srcFolder}/{p}", $"{holdTgtFolder}/{p}");
                    if (swaps.Count > before) directHits++;
                }
            }
            if (moveTgtFolder != null)
            {
                foreach (string p in moveProbes)
                {
                    int before = swaps.Count;
                    TryAddJobSwap(swaps, basePath, $"{srcFolder}/{p}", $"{moveTgtFolder}/{p}");
                    if (swaps.Count > before) directHits++;
                }
            }
            if (attackTgtFolder != null)
            {
                foreach (string p in attackProbes)
                {
                    int before = swaps.Count;
                    TryAddJobSwap(swaps, basePath, $"{srcFolder}/{p}", $"{attackTgtFolder}/{p}");
                    if (swaps.Count > before) directHits++;
                }
            }
        }
        catch (Exception ex) { DalamudApi.LogInfo($"[AnimSwap] BuildJobSwapPaths B error: {ex.Message}"); }

        DalamudApi.LogInfo($"[AnimSwap] BuildJobSwapPaths: " +
            $"{holdHits} hold + {moveHits} move + {attackHits} attack + {directHits} direct = {swaps.Count}");

        // ── Race-specific model swap registration ───────────────────
        if (!string.IsNullOrEmpty(visualModelCode) && visualModelCode != modelCode)
        {
            string raceBasePath = $"chara/human/{visualModelCode}/animation/a0001";
            int raceHits = 0;
            var c0101Swaps = new List<KeyValuePair<string, string>>(swaps);
            foreach (var (src, tgt) in c0101Swaps)
            {
                string raceSrc = src.Replace(basePath, raceBasePath);
                string raceTgt = tgt.Replace(basePath, raceBasePath);
                if (raceSrc == src || swaps.ContainsKey(raceSrc)) continue;
                try
                {
                    if (DalamudApi.DataManager.FileExists(raceSrc))
                    {
                        if (DalamudApi.DataManager.FileExists(raceTgt))
                        {
                            swaps[raceSrc] = ResolvePenumbraPath(raceTgt);
                            raceHits++;
                        }
                        else
                        {
                            // Cross-model fallback: race source → c0101 target.
                            // tgt is already resolved from the c0101 pass.
                            swaps[raceSrc] = tgt;
                            raceHits++;
                        }
                    }
                }
                catch { }
            }
            DalamudApi.LogInfo($"[AnimSwap] Race-specific ({visualModelCode}): {raceHits} additional swaps");
        }

        return swaps;
    }

    /// <summary>
    /// Resolve a game path through the player's Penumbra collection.
    /// If a mod replaces the path, returns the modded file path;
    /// otherwise returns the original game path unchanged.
    /// </summary>
    private static string ResolvePenumbraPath(string gamePath)
    {
        if (_penumbraResolve == null) return gamePath;
        try
        {
            string resolved = _penumbraResolve.InvokeFunc(gamePath);
            if (!string.IsNullOrEmpty(resolved) && resolved != gamePath)
                return resolved;
        }
        catch { }
        return gamePath;
    }

    /// <summary>
    /// Try adding a single job swap path. Both source and target must exist in game data.
    /// If Penumbra already mods the target path, uses the modded file instead.
    /// </summary>
    private static void TryAddJobSwap(Dictionary<string, string> swaps, string basePath,
        string srcRel, string tgtRel)
    {
        string srcPath = $"{basePath}/{srcRel}.pap";
        if (swaps.ContainsKey(srcPath)) return;

        string tgtPath = $"{basePath}/{tgtRel}.pap";
        if (srcPath == tgtPath) return;

        try
        {
            if (DalamudApi.DataManager.FileExists(srcPath)
                && DalamudApi.DataManager.FileExists(tgtPath))
            {
                swaps[srcPath] = ResolvePenumbraPath(tgtPath);
            }
        }
        catch { }
    }

    private static bool MatchesAny(string key, string[] patterns)
    {
        foreach (var p in patterns)
            if (key.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ── Vtable hook lifecycle (visual race detection) ───────────

    private static void EnsureVtableHooked(DrawObject* drawObj)
    {
        var vtable = *(nint**)drawObj;
        var fnPtr = ((nint*)vtable)[84];

        if (fnPtr == _hookedVtableFnAddr && _vtableHook != null)
        {
            if (!_vtableHook.IsEnabled) _vtableHook.Enable();
            return;
        }

        try { _vtableHook?.Disable(); } catch { }
        try { _vtableHook?.Dispose(); } catch { }
        _vtableHook = DalamudApi.GameInteropProvider.HookFromAddress<ResolvePapPathDelegate>(
            fnPtr, ResolvePapPathDetour);
        _vtableHook.Enable();
        _hookedVtableFnAddr = fnPtr;
        DalamudApi.LogInfo($"[AnimSwap] Vtable hook installed at 0x{fnPtr:X}");
    }

    private static void DisableVtableHook()
    {
        try { if (_vtableHook is { IsEnabled: true }) _vtableHook.Disable(); } catch { }
    }

    // ── Swap state update ──────────────────────────────────────

    private static void UpdateSwapState(Dalamud.Game.ClientState.Objects.Types.IGameObject lp)
    {
        var ch = (Character*)lp.Address;
        if (ch == null) return;

        byte sex = ch->DrawData.CustomizeData.Sex;

        byte matchRace = VisualRaceId != 0 ? VisualRaceId : ch->DrawData.CustomizeData.Race;
        byte matchSex = VisualRaceId != 0 ? VisualSex : sex;

        bool prevActive = _anySwapActive;

        _anySwapActive = false;

        string srcCode = null;
        string tgtCode = null;

        ushort currentTerritory = 0;
        try { currentTerritory = (ushort)DalamudApi.ClientState.TerritoryType; } catch { }

        foreach (var rule in noWickyXIV.Config.AnimationSwapRules)
        {
            if (!rule.Enabled) continue;
            if (rule.TerritoryId != 0 && rule.TerritoryId != currentTerritory) continue;
            if (rule.SourceRace != 0 && rule.SourceRace != matchRace) continue;
            if (rule.TargetRace == 0) continue;
            // Same race is only valid when opposite gender is on — the
            // sex difference produces a different model code.
            if (rule.TargetRace == matchRace && !rule.UseFemaleAnims) continue;

            byte srcTribe = GetDefaultTribe(matchRace);
            srcCode = GetModelCode(matchRace, matchSex, srcTribe);

            byte tgtTribe = GetDefaultTribe(rule.TargetRace);
            byte tgtSex = rule.UseFemaleAnims ? (byte)(matchSex == 0 ? 1 : 0) : matchSex;
            tgtCode = GetModelCode(rule.TargetRace, tgtSex, tgtTribe);

            _anySwapActive = true;
            break;
        }

        bool needsUpdate = _anySwapActive != prevActive
            || (_anySwapActive && (srcCode != _lastSrcCode || tgtCode != _lastTgtCode));

        if (needsUpdate)
        {
            if (_anySwapActive && srcCode != null && tgtCode != null)
            {
                ApplyPenumbraSwaps(srcCode, tgtCode);
                // Only cache state if Penumbra registration succeeded.
                // If it failed (e.g. Penumbra not ready), we retry next frame.
                if (_penumbraSwapsActive)
                {
                    _lastSrcCode = srcCode;
                    _lastTgtCode = tgtCode;
                }
            }
            else
            {
                RemovePenumbraSwaps();
                _lastSrcCode = null;
                _lastTgtCode = null;
            }

            _lastSwapActive = _anySwapActive;
            RequestRedraw();

            // On first successful activation, schedule a delayed re-apply.
            // The game often redraws the character during post-login loading,
            // which can override our initial swap. The delayed re-apply catches this.
            if (_anySwapActive && _penumbraSwapsActive && !_everActivated)
            {
                _everActivated = true;
                ForceReapply(120);
            }
        }
    }

    // ── Character redraw ────────────────────────────────────────

    private static void RequestRedraw()
    {
        if (_redrawPhase == 0)
        {
            _redrawPhase = 1;
            _redrawCooldown = 0;
        }
    }

    private static void ProcessRedraw(GameObject* go)
    {
        switch (_redrawPhase)
        {
            case 1:
                go->DisableDraw();
                _redrawPhase = 2;
                _redrawCooldown = 3;
                break;

            case 2:
                if (_redrawCooldown > 0) { _redrawCooldown--; break; }
                go->EnableDraw();
                _redrawPhase = 0;
                break;
        }
    }

    // ── Vtable detour (visual race detection) ───────────────────

    private static nint ResolvePapPathDetour(
        nint drawObject, nint pathBuffer, nint pathBufferSize,
        uint animIndex, nint animName)
    {
        nint result = _vtableHook.Original(drawObject, pathBuffer, pathBufferSize,
            animIndex, animName);

        try
        {
            Interlocked.Increment(ref TotalHookCalls);

            // _localPlayerDrawObj is set at the top of Update() from
            // go->DrawObject, but during ProcessRedraw's EnableDraw the
            // game tears down the old DrawObject and constructs a new
            // one — pap resolves for the new pointer fire synchronously
            // before Update() runs again to refresh the cache. Re-read
            // the current DrawObject here so a fresh redraw doesn't lose
            // the very PAP events that populate VisualRaceId on plugin
            // reload (when the character was already drawn before our
            // hook came up). Accept either pointer to stay robust.
            bool isLocalPlayer = drawObject == _localPlayerDrawObj;
            if (!isLocalPlayer)
            {
                try
                {
                    var lpRef = DalamudApi.ObjectTable.LocalPlayer;
                    if (lpRef != null)
                    {
                        var goRef = (GameObject*)lpRef.Address;
                        if (goRef != null && goRef->DrawObject != null
                            && drawObject == (nint)goRef->DrawObject)
                        {
                            isLocalPlayer = true;
                            _localPlayerDrawObj = drawObject;
                        }
                    }
                }
                catch { }
            }

            // Log for diagnostics.
            if (_diagPapLog.Count < 200)
            {
                string path = Marshal.PtrToStringAnsi(pathBuffer) ?? "(null)";
                string anim = animName != nint.Zero
                    ? (Marshal.PtrToStringAnsi(animName) ?? "?") : "(null)";
                _diagPapLog.Add($"vtbl idx={animIndex} anim=\"{anim}\" path=\"{path}\"" +
                                $" isLP={isLocalPlayer}");
            }

            if (!isLocalPlayer) return result;

            // Extract model code from the resolved path for visual race detection.
            DetectVisualRace((byte*)pathBuffer);
        }
        catch { }

        return result;
    }

    // ── Visual race detection from paths ────────────────────────

    private static void DetectVisualRace(byte* pathBuf)
    {
        int pathLen = 0;
        while (pathLen < 260 && pathBuf[pathLen] != 0) pathLen++;

        int markerPos = FindBytes(pathBuf, pathLen, PathMarker);
        if (markerPos < 0) return;

        int codeStart = markerPos + PathMarker.Length; // First digit after 'c'
        if (codeStart + 4 > pathLen) return;

        int bodyId = 0;
        bool validDigits = true;
        for (int d = 0; d < 4; d++)
        {
            byte ch = pathBuf[codeStart + d];
            if (ch < (byte)'0' || ch > (byte)'9') { validDigits = false; break; }
            bodyId = bodyId * 10 + (ch - '0');
        }

        if (!validDigits || bodyId <= 0) return;

        // Reverse-map bodyId -> race + sex.
        // bodyId = (baseBody + sex) * 100 + 1
        int combo = (bodyId - 1) / 100;
        int baseBody = combo % 2 == 0 ? combo - 1 : combo;
        byte detectedSex = (byte)(combo % 2 == 0 ? 1 : 0);
        byte detectedRace = ReverseMapRace(baseBody);

        if (detectedRace != 0)
        {
            VisualRaceId = detectedRace;
            VisualSex = detectedSex;
            VisualModelCode = $"c{bodyId:D4}";
        }
    }

    // ── Helper methods ──────────────────────────────────────────

    private static int FindBytes(byte* haystack, int haystackLen, byte[] needle)
    {
        int needleLen = needle.Length;
        for (int i = 0; i <= haystackLen - needleLen; i++)
        {
            bool match = true;
            for (int j = 0; j < needleLen; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    // ── Race -> model code mapping ──────────────────────────────

    private static string GetModelCode(byte race, byte sex, byte tribe)
    {
        int baseBody = race switch
        {
            1 => tribe == 2 ? 3 : 1,
            2 => 5, 3 => 11, 4 => 7, 5 => 9,
            6 => 13, 7 => 15, 8 => 17, _ => 1,
        };
        int bodyId = (baseBody + sex) * 100 + 1;
        return $"c{bodyId:D4}";
    }

    private static byte ReverseMapRace(int baseBody)
    {
        return baseBody switch
        {
            1 or 3 => 1,   // Hyur (Mid / High)
            5  => 2,        // Elezen
            11 => 3,        // Lalafell
            7  => 4,        // Miqo'te
            9  => 5,        // Roegadyn
            13 => 6,        // Au Ra
            15 => 7,        // Hrothgar
            17 => 8,        // Viera
            _ => 0,
        };
    }

    private static byte GetDefaultTribe(byte race)
    {
        return race switch
        {
            1 => 1, 2 => 3, 3 => 5, 4 => 7,
            5 => 9, 6 => 11, 7 => 13, 8 => 15,
            _ => 1,
        };
    }

    // ── Diagnostic file output ──────────────────────────────────

    public static void FlushDiag()
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var ch = (Character*)lp.Address;
            if (ch == null) return;

            byte race  = ch->DrawData.CustomizeData.Race;
            byte sex   = ch->DrawData.CustomizeData.Sex;
            byte tribe = ch->DrawData.CustomizeData.Tribe;

            var sb = new StringBuilder();
            sb.AppendLine($"=== Animation Swap Diagnostic — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Server race: {race} ({LookupRaceName(race)})  Tribe: {tribe}  Gender: {(sex == 0 ? "Male" : "Female")}");
            sb.AppendLine($"Visual race: {VisualRaceId} ({LookupRaceName(VisualRaceId)})  " +
                          $"Visual sex: {VisualSex}  Visual model: {VisualModelCode}");

            sb.AppendLine();
            sb.AppendLine("── Penumbra IPC ──");
            sb.AppendLine($"  Status: {_penumbraStatus}");
            sb.AppendLine($"  Swaps active: {_penumbraSwapsActive}  Registered paths: {_registeredSwapCount}");

            sb.AppendLine();
            sb.AppendLine($"Vtable hook: {(_vtableHook != null ? $"installed at 0x{_hookedVtableFnAddr:X}" : "not installed")}");
            sb.AppendLine($"Swap active: {_anySwapActive}");
            sb.AppendLine($"Total vtable calls: {TotalHookCalls}");
            sb.AppendLine($"LP DrawObj: 0x{_localPlayerDrawObj:X}");

            // Dump configured rules.
            sb.AppendLine();
            byte matchRace = VisualRaceId != 0 ? VisualRaceId : race;
            sb.AppendLine($"── Configured rules ({noWickyXIV.Config.AnimationSwapRules.Count}) ──");
            sb.AppendLine($"   (matching against visual race {matchRace} = {LookupRaceName(matchRace)})");
            for (int i = 0; i < noWickyXIV.Config.AnimationSwapRules.Count; i++)
            {
                var r = noWickyXIV.Config.AnimationSwapRules[i];
                bool srcMatch = r.SourceRace == 0 || r.SourceRace == matchRace;
                bool tgtValid = r.TargetRace != 0
                    && (r.TargetRace != matchRace || r.UseFemaleAnims);
                sb.AppendLine($"  [{i}] Enabled={r.Enabled} Src={r.SourceRace}({LookupRaceName(r.SourceRace)}) " +
                              $"Tgt={r.TargetRace}({LookupRaceName(r.TargetRace)}) " +
                              $"OppGender={r.UseFemaleAnims}");
                sb.AppendLine($"       srcMatch={srcMatch} tgtValid={tgtValid} " +
                              $"wouldActivate={r.Enabled && srcMatch && tgtValid}");
            }

            // Timeline slots.
            sb.AppendLine();
            sb.AppendLine("── Timeline slots ──");
            ushort slot0 = ch->Timeline.TimelineSequencer.GetSlotTimeline(0);
            sb.AppendLine($"  Slot 0: {slot0} \"{LookupTimelineKey(slot0)}\"");
            for (uint s = 1; s < 14; s++)
            {
                ushort id = ch->Timeline.TimelineSequencer.GetSlotTimeline(s);
                if (id == 0) continue;
                sb.AppendLine($"  Slot {s}: {id} \"{LookupTimelineKey(id)}\"");
            }

            // Registered Penumbra swap paths.
            sb.AppendLine();
            sb.AppendLine($"── Penumbra swap paths ({_lastSwapPaths?.Count ?? 0}) ──");
            if (_lastSwapPaths != null)
            {
                int shown = 0;
                foreach (var (src, tgt) in _lastSwapPaths)
                {
                    sb.AppendLine($"  {src}");
                    sb.AppendLine($"    -> {tgt}");
                    if (++shown >= 100)
                    {
                        sb.AppendLine($"  ... and {_lastSwapPaths.Count - shown} more");
                        break;
                    }
                }
            }
            else
            {
                sb.AppendLine("  (no swaps registered)");
            }

            // Job swap state.
            sb.AppendLine();
            sb.AppendLine("── Job animation swaps ──");
            sb.AppendLine($"  Status: {_jobSwapStatus}");
            sb.AppendLine($"  Active: {_jobSwapsActive}  Registered: {_registeredJobSwapCount}");
            sb.AppendLine($"  Src folder: {_lastJobSrcFolder ?? "(none)"}  Hold: {_lastJobHoldTgtFolder ?? "(none)"}  Move: {_lastJobMoveTgtFolder ?? "(none)"}  Atk: {_lastJobAttackTgtFolder ?? "(none)"}");

            // Job swap rules.
            uint currentJob = 0;
            try { currentJob = DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0; } catch { }
            sb.AppendLine($"  Current job ID: {currentJob}");
            sb.AppendLine($"  Job swap rules ({noWickyXIV.Config.JobAnimSwapRules.Count}):");
            for (int i = 0; i < noWickyXIV.Config.JobAnimSwapRules.Count; i++)
            {
                var jr = noWickyXIV.Config.JobAnimSwapRules[i];
                string srcName = JobWeaponFolder.TryGetValue(jr.SourceJob, out var sf) ? sf : "—";
                string holdName = JobWeaponFolder.TryGetValue(jr.HoldTargetJob, out var hf) ? hf : "—";
                string moveName = JobWeaponFolder.TryGetValue(jr.MoveTargetJob, out var mf) ? mf : "—";
                string atkName = JobWeaponFolder.TryGetValue(jr.AttackTargetJob, out var af) ? af : "—";
                sb.AppendLine($"    [{i}] Enabled={jr.Enabled} Src={jr.SourceJob}({srcName}) " +
                              $"Hold={jr.HoldTargetJob}({holdName}) Move={jr.MoveTargetJob}({moveName}) Atk={jr.AttackTargetJob}({atkName})");
            }

            // Job swap paths.
            sb.AppendLine();
            sb.AppendLine($"── Job swap paths ({_lastJobSwapPaths?.Count ?? 0}) ──");
            if (_lastJobSwapPaths != null)
            {
                int shown = 0;
                foreach (var (src, tgt) in _lastJobSwapPaths)
                {
                    sb.AppendLine($"  {src}");
                    sb.AppendLine($"    -> {tgt}");
                    if (++shown >= 100)
                    {
                        sb.AppendLine($"  ... and {_lastJobSwapPaths.Count - shown} more");
                        break;
                    }
                }
            }
            else
            {
                sb.AppendLine("  (no job swaps registered)");
            }

            // Comprehensive weapon folder and animation structure analysis.
            sb.AppendLine();
            sb.AppendLine("── Weapon animation analysis ──");
            try
            {
                byte diagRace = VisualRaceId != 0 ? VisualRaceId : ch->DrawData.CustomizeData.Race;
                byte diagSex = VisualRaceId != 0 ? VisualSex : sex;
                string diagModel = GetModelCode(diagRace, diagSex, GetDefaultTribe(diagRace));
                sb.AppendLine($"  Model: {diagModel}");

                var tlSheet = DalamudApi.DataManager.GetExcelSheet<ActionTimeline>();
                if (tlSheet != null)
                {
                    // 1. Discover weapon folders from "ws/{folder}/..." ActionTimeline keys.
                    //    These keys directly reveal which weapon folders exist in the game data.
                    var discoveredFolders = new Dictionary<string, int>();
                    var allKeys = new List<string>();

                    foreach (var tlRow in tlSheet)
                    {
                        try
                        {
                            string k = tlRow.Key.ExtractText();
                            if (string.IsNullOrEmpty(k) || k.Length < 2) continue;
                            if (k.StartsWith("mon_") || k.StartsWith("demi_human/")
                                || k.StartsWith("weapon/") || k.StartsWith("bg/")) continue;
                            allKeys.Add(k);

                            // Extract folder from "ws/{folder}/..." keys.
                            if (k.StartsWith("ws/bt_", StringComparison.OrdinalIgnoreCase))
                            {
                                int secondSlash = k.IndexOf('/', 3);
                                if (secondSlash > 3)
                                {
                                    string folder = k.Substring(3, secondSlash - 3);
                                    if (!discoveredFolders.ContainsKey(folder))
                                        discoveredFolders[folder] = 0;
                                    discoveredFolders[folder]++;
                                }
                            }
                        }
                        catch { }
                    }

                    sb.AppendLine($"  ActionTimeline keys: {allKeys.Count}");
                    sb.AppendLine();
                    sb.AppendLine("  Weapon folders discovered from ws/ keys:");
                    if (discoveredFolders.Count > 0)
                    {
                        foreach (var kv in discoveredFolders)
                            sb.AppendLine($"    {kv.Key}: {kv.Value} weapon skill keys");
                    }
                    else
                    {
                        sb.AppendLine("    (none found — ws/ prefix keys may use different naming)");
                    }

                    // 2. Validate configured weapon folders against game data.
                    //    For each folder in JobWeaponFolder, probe a few keys to see if files exist.
                    sb.AppendLine();
                    sb.AppendLine("  Configured folder validation:");
                    var checkedFolders = new HashSet<string>();
                    foreach (var kv in JobWeaponFolder)
                    {
                        if (!checkedFolders.Add(kv.Value)) continue;
                        int hits = 0;
                        string firstHit = null;
                        foreach (var k in allKeys)
                        {
                            string testPath = $"chara/human/{diagModel}/animation/a0001/{kv.Value}/{k}.pap";
                            try
                            {
                                if (DalamudApi.DataManager.FileExists(testPath))
                                {
                                    hits++;
                                    firstHit ??= k;
                                    if (hits >= 5) break;
                                }
                            }
                            catch { }
                        }
                        string status = hits > 0 ? $"{hits}+ hits (e.g. {firstHit})" : "NO FILES FOUND";
                        sb.AppendLine($"    {kv.Value}: {status}");
                    }

                    // 3. Show bt_common subdirectory structure.
                    sb.AppendLine();
                    sb.AppendLine("  bt_common subdirectory structure:");
                    var subdirCounts = new Dictionary<string, int>();
                    int totalBtCommon = 0;
                    foreach (var k in allKeys)
                    {
                        string testPath = $"chara/human/{diagModel}/animation/a0001/bt_common/{k}.pap";
                        try
                        {
                            if (DalamudApi.DataManager.FileExists(testPath))
                            {
                                totalBtCommon++;
                                int si = k.IndexOf('/');
                                string subdir = si >= 0 ? k.Substring(0, si) : "(root)";
                                if (!subdirCounts.ContainsKey(subdir))
                                    subdirCounts[subdir] = 0;
                                subdirCounts[subdir]++;
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine($"    Total files: {totalBtCommon}");
                    var sortedDirs = new List<KeyValuePair<string, int>>(subdirCounts);
                    sortedDirs.Sort((a, b) => b.Value.CompareTo(a.Value));
                    foreach (var kv in sortedDirs)
                        sb.AppendLine($"    {kv.Key}/: {kv.Value} files");

                    // 4. Show ws/ keys for the current job's weapon folder.
                    sb.AppendLine();
                    string curJobFolder = null;
                    if (currentJob > 0)
                        JobWeaponFolder.TryGetValue(currentJob, out curJobFolder);
                    sb.AppendLine($"  ws/ keys for current job ({currentJob}, folder={curJobFolder ?? "?"}):");
                    if (curJobFolder != null)
                    {
                        string prefix = $"ws/{curJobFolder}/";
                        int wsCount = 0;
                        foreach (var k in allKeys)
                        {
                            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                wsCount++;
                                if (wsCount <= 20)
                                    sb.AppendLine($"    {k}");
                            }
                        }
                        if (wsCount > 20)
                            sb.AppendLine($"    ... and {wsCount - 20} more");
                        if (wsCount == 0)
                            sb.AppendLine($"    (none — folder name '{curJobFolder}' may be wrong)");
                    }
                    else
                    {
                        sb.AppendLine("    (no weapon folder mapped for current job)");
                    }

                    // 5. Weapon animation PAP path probing — test multiple
                    //    model codes and path patterns to find where weapon
                    //    skill/stance/draw PAP files actually live.
                    sb.AppendLine();
                    sb.AppendLine("  Weapon PAP path probing (current job folder):");
                    if (curJobFolder != null)
                    {
                        // Test against several model codes — weapon anims may
                        // only exist under c0101 (Hyur base) or c0201 (Highlander).
                        string[] probeModels = { diagModel, "c0101", "c0201", "c0301", "c0401" };
                        foreach (string pm in probeModels)
                        {
                            string dBase = $"chara/human/{pm}/animation/a0001";
                            int hits = 0;

                            // ws/ weapon skills — 4 path patterns x first 3 numbers.
                            for (int n = 1; n <= 3; n++)
                            {
                                string num = n.ToString("D2");
                                string[] paths = {
                                    $"{dBase}/ws/{curJobFolder}/ws_s{num}.pap",
                                    $"{dBase}/{curJobFolder}/ws/{curJobFolder}/ws_s{num}.pap",
                                    $"{dBase}/{curJobFolder}/ws_s{num}.pap",
                                };
                                foreach (var pp in paths)
                                    try { if (DalamudApi.DataManager.FileExists(pp)) hits++; } catch { }
                            }

                            // Known weapon sub-paths.
                            string[] subPaths = {
                                $"{dBase}/{curJobFolder}/resident/idle.pap",
                                $"{dBase}/{curJobFolder}/resident/draw.pap",
                                $"{dBase}/{curJobFolder}/resident/sheathe.pap",
                                $"{dBase}/{curJobFolder}/battle/idle.pap",
                                $"{dBase}/{curJobFolder}/battle/auto_attack_1.pap",
                                $"{dBase}/{curJobFolder}/battle/auto_attack1.pap",
                                $"{dBase}/{curJobFolder}/event/event_bt_active.pap",
                                $"{dBase}/{curJobFolder}/event/event_bt_deactive.pap",
                                $"{dBase}/{curJobFolder}/emote/battle01.pap",
                            };
                            int subHits = 0;
                            var subFound = new List<string>();
                            foreach (var sp in subPaths)
                                try { if (DalamudApi.DataManager.FileExists(sp)) { subHits++; subFound.Add(sp.Substring(dBase.Length + 1)); } } catch { }

                            // Exhaustive ActionTimeline key scan under weapon folder.
                            int exHits = 0;
                            var exFound = new List<string>();
                            foreach (var k in allKeys)
                            {
                                string ep = $"{dBase}/{curJobFolder}/{k}.pap";
                                try { if (DalamudApi.DataManager.FileExists(ep)) { exHits++; if (exFound.Count < 10) exFound.Add(k); } } catch { }
                            }

                            // ws/ keys at a0001 root.
                            int wsRootHits = 0;
                            var wsRootFound = new List<string>();
                            string wsP = $"ws/{curJobFolder}/";
                            foreach (var k in allKeys)
                            {
                                if (k.StartsWith(wsP, StringComparison.OrdinalIgnoreCase))
                                {
                                    string rp = $"{dBase}/{k}.pap";
                                    try { if (DalamudApi.DataManager.FileExists(rp)) { wsRootHits++; if (wsRootFound.Count < 10) wsRootFound.Add(k); } } catch { }
                                }
                            }

                            sb.AppendLine($"    [{pm}] ws_probe={hits} sub={subHits} exhaust={exHits} wsRoot={wsRootHits}");
                            foreach (var f in subFound) sb.AppendLine($"      sub: {f}");
                            foreach (var f in exFound) sb.AppendLine($"      ex: {f}");
                            foreach (var f in wsRootFound) sb.AppendLine($"      wsR: {f}");
                        }

                        // 5b: Probe weapon model paths.
                        // Weapon animations may live under chara/weapon/ not chara/human/.
                        sb.AppendLine("    Weapon model path probing:");
                        // Try common weapon model IDs for SAM katana (w2001-w2020 range).
                        int[] weaponIds = { 1, 101, 201, 301, 401, 501, 601, 701, 801, 901,
                                            1001, 1501, 2001, 2501, 3001, 3501, 4001, 4501,
                                            5001, 5501, 6001, 6501, 7001, 7501, 8001, 8501,
                                            9001, 9501, 10001, 10101, 10301 };
                        int wepHits = 0;
                        foreach (int wid in weaponIds)
                        {
                            if (wepHits >= 5) break; // limit output
                            string wBase = $"chara/weapon/w{wid:D4}/obj/body/b0001/animation/a0001";
                            string probe1 = $"{wBase}/ws_s01.pap";
                            string probe2 = $"{wBase}/resident/idle.pap";
                            string probe3 = $"{wBase}/bt_common/ws_s01.pap";
                            bool f1 = false, f2 = false, f3 = false;
                            try { f1 = DalamudApi.DataManager.FileExists(probe1); } catch { }
                            try { f2 = DalamudApi.DataManager.FileExists(probe2); } catch { }
                            try { f3 = DalamudApi.DataManager.FileExists(probe3); } catch { }
                            if (f1 || f2 || f3)
                            {
                                wepHits++;
                                sb.AppendLine($"      w{wid:D4}: ws_s01={f1} idle={f2} bt_ws={f3}");
                            }
                        }
                        if (wepHits == 0)
                            sb.AppendLine("      (no weapon model anims found at probed IDs)");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine($"  Analysis failed: {ex.Message}"); }

            // Movement / locomotion animation probe — check if weapon folders
            // contain per-job run/walk/sprint files under various naming conventions.
            sb.AppendLine();
            sb.AppendLine("── Movement animation probe (weapon folder) ──");
            try
            {
                string diagJobFolder2 = null;
                string diagModel2 = "c0101";
                try
                {
                    uint cj2 = DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                    JobWeaponFolder.TryGetValue(cj2, out diagJobFolder2);
                    var ch2 = (Character*)DalamudApi.ObjectTable.LocalPlayer.Address;
                    if (ch2 != null)
                    {
                        var cd2 = ch2->DrawData;
                        byte r2 = cd2.CustomizeData.Race, s2 = cd2.CustomizeData.Sex;
                        diagModel2 = $"c{(r2 * 100 + 1 + s2):D4}";
                    }
                }
                catch { }

                string[] moveNames = {
                    // resident/ variants
                    "resident/move", "resident/move_a", "resident/move_b",
                    "resident/move_c", "resident/move_d", "resident/run",
                    "resident/walk", "resident/sprint", "resident/b_run",
                    "resident/b_walk", "resident/b_move", "resident/b_sprint",
                    "resident/move_run", "resident/move_walk",
                    // battle/ variants
                    "battle/run", "battle/walk", "battle/move", "battle/sprint",
                    "battle/b_run", "battle/b_walk", "battle/b_move",
                    "battle/run_a", "battle/run_b", "battle/walk_a",
                    "battle/move_a", "battle/move_b",
                    // locomotion
                    "locomo/run", "locomo/walk", "locomo/sprint",
                    "locomo/battle_run", "locomo/battle_walk",
                    // move root
                    "move/run", "move/walk", "move/sprint",
                    "move/battle_run", "move/battle_walk",
                    // normal variants
                    "normal/run", "normal/walk", "normal/move", "normal/sprint",
                    // misc
                    "run", "walk", "move", "sprint",
                    "b_run", "b_walk", "b_move", "b_sprint",
                };

                string[] probeModels2 = { "c0101", diagModel2 };
                string[] probeFolders = diagJobFolder2 != null
                    ? new[] { diagJobFolder2, "bt_common" }
                    : new[] { "bt_common" };

                int totalMoveProbes = 0, totalMoveHits = 0;
                foreach (string pm in probeModels2)
                {
                    string dBase2 = $"chara/human/{pm}/animation/a0001";
                    foreach (string folder in probeFolders)
                    {
                        var foundMoves = new List<string>();
                        foreach (string mn in moveNames)
                        {
                            totalMoveProbes++;
                            string mp = $"{dBase2}/{folder}/{mn}.pap";
                            try
                            {
                                if (DalamudApi.DataManager.FileExists(mp))
                                {
                                    totalMoveHits++;
                                    foundMoves.Add(mn);
                                }
                            }
                            catch { }
                        }
                        if (foundMoves.Count > 0)
                        {
                            sb.AppendLine($"  [{pm}] {folder}: {foundMoves.Count} found");
                            foreach (var fm in foundMoves)
                                sb.AppendLine($"    ✓ {fm}.pap");
                        }
                        else
                        {
                            sb.AppendLine($"  [{pm}] {folder}: none of {moveNames.Length} move probes exist");
                        }
                    }
                }
                sb.AppendLine($"  Total: {totalMoveProbes} probes, {totalMoveHits} found");
            }
            catch (Exception ex) { sb.AppendLine($"  Move probe failed: {ex.Message}"); }

            // Focused draw/sheathe path probe — check where battle_start/end
            // actually live across all folders and model codes.
            sb.AppendLine();
            sb.AppendLine("── Draw/sheathe path probe ──");
            try
            {
                string[] drawKeys = {
                    // Previous round: none found
                    "battle/battle_start", "battle/battle_end",
                    "battle_start", "battle_end",
                    // Modding community names for draw/sheathe
                    "resident/start", "resident/end",
                    "resident/start_loop", "resident/end_loop",
                    "start", "end",
                    // Other possible names
                    "resident/battle_start", "resident/battle_end",
                    "event/battle_start", "event/battle_end",
                    "draw", "sheathe", "unsheathe",
                    "battle/draw", "battle/sheathe",
                    // Weapon-folder root variants
                    "bt_draw", "bt_sheathe", "bt_start", "bt_end",
                    "active", "deactive",
                    // Numbered variants
                    "resident/start01", "resident/end01",
                    "resident/start_01", "resident/end_01",
                };
                byte dRace = VisualRaceId != 0 ? VisualRaceId : ch->DrawData.CustomizeData.Race;
                byte dSex = VisualRaceId != 0 ? VisualSex : sex;
                string drawModel = GetModelCode(dRace, dSex, GetDefaultTribe(dRace));
                string[] drawModels = { "c0101", drawModel };
                string diagJobFolder = null;
                try { uint cj = DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                      if (cj > 0) JobWeaponFolder.TryGetValue(cj, out diagJobFolder); } catch { }
                string[] drawFolders = { "bt_common", diagJobFolder ?? "bt_2kt_emp" };
                if (diagJobFolder != null)
                {
                    // Also check target folders from the first job rule.
                    foreach (var jr in noWickyXIV.Config.JobAnimSwapRules)
                    {
                        if (!jr.Enabled) continue;
                        var tgtSet = new HashSet<string> { "bt_common", diagJobFolder };
                        if (JobWeaponFolder.TryGetValue(jr.HoldTargetJob, out var htf)) tgtSet.Add(htf);
                        if (JobWeaponFolder.TryGetValue(jr.AttackTargetJob, out var atf)) tgtSet.Add(atf);
                        drawFolders = new string[tgtSet.Count];
                        tgtSet.CopyTo(drawFolders);
                        break;
                    }
                }
                foreach (string dm in drawModels)
                {
                    foreach (string df in drawFolders)
                    {
                        int found = 0;
                        foreach (string dk in drawKeys)
                        {
                            string dp = $"chara/human/{dm}/animation/a0001/{df}/{dk}.pap";
                            try
                            {
                                if (DalamudApi.DataManager.FileExists(dp))
                                {
                                    sb.AppendLine($"  FOUND: {dm}/{df}/{dk}.pap");
                                    found++;
                                }
                            }
                            catch { }
                        }
                        if (found == 0)
                            sb.AppendLine($"  {dm}/{df}: none of {drawKeys.Length} probed keys exist");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine($"  probe failed: {ex.Message}"); }

            // Scan for draw/sheathe as .pap AND .tmb across sets and folders.
            sb.AppendLine();
            sb.AppendLine("── Draw/sheathe file scan (.pap + .tmb) ──");
            try
            {
                string[] scanKeys = { "battle/battle_start", "battle/battle_end",
                                      "resident/start", "resident/end",
                                      "battle_start", "battle_end",
                                      "start", "end" };
                string[] exts = { ".pap", ".tmb" };
                string scanModel = "c0101";
                string scanJobFolder = null;
                try { uint sj = DalamudApi.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                      if (sj > 0) JobWeaponFolder.TryGetValue(sj, out scanJobFolder); } catch { }
                string scanFolder = scanJobFolder ?? "bt_2kt_emp";
                int totalProbes = 0, totalFound = 0;
                // Scan a0001 only (no need for wider range) but try all extensions.
                string[] folders = { scanFolder, "bt_common", "" };
                foreach (string ext in exts)
                {
                    foreach (string folder in folders)
                    {
                        foreach (string sk in scanKeys)
                        {
                            string p = string.IsNullOrEmpty(folder)
                                ? $"chara/human/{scanModel}/animation/a0001/{sk}{ext}"
                                : $"chara/human/{scanModel}/animation/a0001/{folder}/{sk}{ext}";
                            totalProbes++;
                            try { if (DalamudApi.DataManager.FileExists(p))
                                { sb.AppendLine($"  FOUND: {p}"); totalFound++; } } catch { }
                        }
                    }
                }

                // Also: brute-force scan ALL ActionTimeline keys as .tmb under weapon folder
                var tlSheet2 = DalamudApi.DataManager.GetExcelSheet<ActionTimeline>();
                int tmbHits = 0;
                if (tlSheet2 != null)
                {
                    foreach (var tlRow in tlSheet2)
                    {
                        try
                        {
                            string k = tlRow.Key.ExtractText();
                            if (string.IsNullOrEmpty(k) || k.Length < 2) continue;
                            if (k.StartsWith("mon_") || k.StartsWith("demi_human/")
                                || k.StartsWith("weapon/") || k.StartsWith("bg/")) continue;
                            string tp = $"chara/human/{scanModel}/animation/a0001/{scanFolder}/{k}.tmb";
                            totalProbes++;
                            try
                            {
                                if (DalamudApi.DataManager.FileExists(tp))
                                {
                                    tmbHits++;
                                    if (tmbHits <= 20) sb.AppendLine($"  TMB: {scanFolder}/{k}.tmb");
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
                sb.AppendLine($"  Total: {totalProbes} probes, {totalFound} .pap/.tmb found, {tmbHits} TMBs under {scanFolder}");
            }
            catch (Exception ex) { sb.AppendLine($"  scan failed: {ex.Message}"); }

            // Timeline change log — shows what ActionTimeline keys played.
            sb.AppendLine();
            sb.AppendLine($"── Timeline change log ({_timelineChangeLog.Count} entries) ──");
            sb.AppendLine("  (draw/sheathe your weapon before dumping to capture the keys)");
            foreach (var entry in _timelineChangeLog)
                sb.AppendLine($"  {entry}");
            if (_timelineChangeLog.Count == 0)
                sb.AppendLine("  (no timeline changes captured)");

            // Intercepted vtable path log.
            sb.AppendLine();
            sb.AppendLine($"── Intercepted vtable path log ({_diagPapLog.Count} entries) ──");
            foreach (var entry in _diagPapLog)
                sb.AppendLine($"  {entry}");
            if (_diagPapLog.Count == 0)
                sb.AppendLine("  (no calls intercepted)");

            var dir = PluginConfiguration.ConfigFolder.FullName;
            var path = Path.Combine(dir, "animswap_diag.txt");
            File.WriteAllText(path, sb.ToString());
            DalamudApi.LogInfo($"[AnimSwap] Diagnostic written to: {path}");
        }
        catch { }
    }

    // ── Lookup caches (used by UI) ──────────────────────────────

    private static Dictionary<ushort, string> _keyCache;

    public static string LookupTimelineKey(ushort rowId)
    {
        if (rowId == 0) return "(none)";
        try
        {
            _keyCache ??= new Dictionary<ushort, string>();
            if (_keyCache.TryGetValue(rowId, out var cached))
                return cached;

            var sheet = DalamudApi.DataManager.GetExcelSheet<ActionTimeline>();
            if (sheet == null) return $"#{rowId}";

            var row = sheet.GetRowOrDefault(rowId);
            if (row == null) return $"#{rowId}";

            string key = row.Value.Key.ExtractText();
            if (string.IsNullOrEmpty(key)) key = $"#{rowId}";
            _keyCache[rowId] = key;
            return key;
        }
        catch { return $"#{rowId}"; }
    }

    private static Dictionary<byte, string> _raceNameCache;

    public static string LookupRaceName(byte raceId)
    {
        if (raceId == 0) return "Any";
        try
        {
            _raceNameCache ??= new Dictionary<byte, string>();
            if (_raceNameCache.TryGetValue(raceId, out var cached))
                return cached;

            var sheet = DalamudApi.DataManager.GetExcelSheet<Race>();
            if (sheet == null) return $"Race #{raceId}";

            var row = sheet.GetRowOrDefault(raceId);
            if (row == null) return $"Race #{raceId}";

            string name = row.Value.Masculine.ExtractText();
            if (string.IsNullOrEmpty(name)) name = $"Race #{raceId}";
            _raceNameCache[raceId] = name;
            return name;
        }
        catch { return $"Race #{raceId}"; }
    }
}
