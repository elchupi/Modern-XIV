using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace noWickyXIV;

/// <summary>
/// Glamourer IPC bridge — applies territory-specific glamour designs
/// via Glamourer's IPC and reverts to automation when leaving.
/// </summary>
public static class GlamourerBridge
{
    // ── Glamourer IPC subscribers ──────────────────────────────
    private static ICallGateSubscriber<Dictionary<Guid, string>> _getDesignList;
    private static ICallGateSubscriber<Guid, int, uint, ulong, int> _applyDesign;
    private static ICallGateSubscriber<int, uint, ulong, int> _revertToAutomation;
    private static ICallGateSubscriber<int, uint, ulong, int> _revertState;

    private static bool _ipcInit;
    private static string _status = "not initialized";

    // ── State ──────────────────────────────────────────────────
    private static ushort _lastTerritory;
    private static bool _hadOverride;          // was the previous territory an override?
    private static string _activeDesignName;   // last design we applied (null = none)
    private static bool _wasEnabled;

    // Deferred apply/revert — wait for character to load after teleporting.
    private static string _pendingDesignName;
    private static bool _pendingRevert;        // revert queued for after load
    private static int _pendingDelay;          // frames to wait after load
    private const int PostLoadDelayFrames = 30; // ~0.5s at 60fps

    // ── Design cache (for UI dropdown) ─────────────────────────
    private static Dictionary<Guid, string> _designCache;
    private static DateTime _designCacheTime;
    private static readonly TimeSpan DesignCacheTtl = TimeSpan.FromSeconds(10);

    // ApplyFlag constants from Glamourer.Api
    private const ulong FlagOnceEquipCustomize = 0x07; // Once | Equipment | Customization
    private const ulong FlagEquipCustomize     = 0x06; // Equipment | Customization

    // ── Public API ─────────────────────────────────────────────
    public static string Status => _status;
    public static string ActiveDesignName => _activeDesignName;

    public static void Initialize()
    {
        _lastTerritory = 0;
        _hadOverride = false;
        _activeDesignName = null;
        _wasEnabled = false;
        _pendingDesignName = null;
        _pendingRevert = false;
        _pendingDelay = 0;
    }

    public static void Update()
    {
        bool enabled = noWickyXIV.Config.EnableGlamourerTerritoryAuto;

        // Feature just got disabled — revert and bail.
        if (!enabled)
        {
            if (_wasEnabled && _hadOverride)
                DoRevert();
            _wasEnabled = false;
            _hadOverride = false;
            _activeDesignName = null;
            _lastTerritory = 0;
            _pendingDesignName = null;
            _pendingRevert = false;
            return;
        }

        _wasEnabled = true;
        EnsureIpc();

        // ── Process pending apply or revert (deferred until after load) ──
        if (_pendingDesignName != null || _pendingRevert)
        {
            bool loading = false;
            try { loading = DalamudApi.Condition[ConditionFlag.BetweenAreas]
                         || DalamudApi.Condition[ConditionFlag.BetweenAreas51]; }
            catch { }

            if (!loading && DalamudApi.ObjectTable.LocalPlayer != null)
            {
                if (_pendingDelay > 0)
                {
                    _pendingDelay--;
                    return;
                }

                // Character is loaded — execute the queued action.
                if (_pendingDesignName != null)
                {
                    ApplyDesignByName(_pendingDesignName);
                    _hadOverride = true;
                    _pendingDesignName = null;
                }
                else if (_pendingRevert)
                {
                    DoRevert();
                    _hadOverride = false;
                    _pendingRevert = false;
                }
            }
            return; // still waiting
        }

        // ── Territory change detection ─────────────────────────
        ushort territory = 0;
        try { territory = (ushort)DalamudApi.ClientState.TerritoryType; } catch { return; }
        if (territory == 0) return;

        if (territory == _lastTerritory) return;
        _lastTerritory = territory;

        // Find matching override for this territory.
        var overrides = noWickyXIV.Config.GlamourerTerritoryOverrides;
        var match = overrides.FirstOrDefault(o => o.TerritoryId == territory);

        if (match != null && !string.IsNullOrEmpty(match.DesignName))
        {
            // Don't apply immediately — queue it for after the load screen.
            _pendingDesignName = match.DesignName;
            _pendingDelay = PostLoadDelayFrames;
            _status = $"queued \"{match.DesignName}\" (waiting for load)";
        }
        else
        {
            // Not in any listed territory — queue revert for after load.
            if (_hadOverride)
            {
                _pendingRevert = true;
                _pendingDelay = PostLoadDelayFrames;
                _status = "queued revert (waiting for load)";
            }
        }
    }

    public static void Dispose()
    {
        if (_hadOverride)
        {
            try { DoRevert(); } catch { }
        }
        _ipcInit = false;
        _designCache = null;
    }

    /// <summary>
    /// Get cached design list for UI dropdowns. Auto-refreshes every 10s.
    /// </summary>
    public static Dictionary<Guid, string> GetDesignList()
    {
        EnsureIpc();
        if (_designCache != null && DateTime.UtcNow - _designCacheTime < DesignCacheTtl)
            return _designCache;

        try
        {
            _designCache = _getDesignList?.InvokeFunc();
            _designCacheTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _designCache = null;
            DalamudApi.LogInfo($"[GlamBridge] GetDesignList error: {ex.Message}");
        }

        return _designCache;
    }

    /// <summary>Force-refresh the design cache.</summary>
    public static void RefreshDesignCache()
    {
        _designCacheTime = DateTime.MinValue;
        GetDesignList();
    }

    /// <summary>Force re-evaluate territory (e.g. after rule changes).</summary>
    public static void ForceReevaluate()
    {
        _lastTerritory = 0;
    }

    /// <summary>Manual revert.</summary>
    public static void ManualRevert()
    {
        DoRevert();
        _hadOverride = false;
        _activeDesignName = null;
        _pendingDesignName = null;
        _pendingRevert = false;
    }

    /// <summary>Test apply — immediate, skips territory/load checks.</summary>
    public static void TestApply(string designName)
    {
        EnsureIpc();

        if (_getDesignList == null) { _status = "TEST: _getDesignList is null"; return; }
        if (_applyDesign == null)   { _status = "TEST: _applyDesign is null"; return; }

        Dictionary<Guid, string> designs = null;
        try { designs = _getDesignList.InvokeFunc(); }
        catch (Exception ex) { _status = $"TEST: GetDesignList threw: {ex.GetType().Name}: {ex.Message}"; return; }

        if (designs == null) { _status = "TEST: GetDesignList returned null"; return; }
        if (designs.Count == 0) { _status = "TEST: design list empty"; return; }

        var found = designs.FirstOrDefault(kv =>
            kv.Value.Equals(designName, StringComparison.OrdinalIgnoreCase));

        if (found.Value == null)
        {
            var partial = designs.FirstOrDefault(kv =>
                kv.Value.Contains(designName, StringComparison.OrdinalIgnoreCase));
            _status = partial.Value != null
                ? $"TEST: exact \"{designName}\" not found, partial: \"{partial.Value}\""
                : $"TEST: \"{designName}\" not found in {designs.Count} designs";
            return;
        }

        try
        {
            int result = _applyDesign.InvokeFunc(found.Key, 0, 0u, FlagOnceEquipCustomize);
            _status = $"TEST: ApplyDesign returned {result} (0=success) for \"{found.Value}\"";
            _activeDesignName = designName;
            _hadOverride = true;
        }
        catch (Exception ex)
        {
            _status = $"TEST: ApplyDesign threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── Internals ──────────────────────────────────────────────

    private static void EnsureIpc()
    {
        if (_ipcInit) return;
        _ipcInit = true;

        try
        {
            _getDesignList = DalamudApi.PluginInterface
                .GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
            _applyDesign = DalamudApi.PluginInterface
                .GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
            _revertToAutomation = DalamudApi.PluginInterface
                .GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertToAutomation.V2");
            _revertState = DalamudApi.PluginInterface
                .GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
            _status = "Glamourer IPC ready";
        }
        catch (Exception ex)
        {
            _status = $"IPC init failed: {ex.Message}";
        }
    }

    private static void ApplyDesignByName(string designName)
    {
        if (_applyDesign == null || _getDesignList == null)
        {
            _status = "Glamourer IPC not available";
            return;
        }

        try
        {
            var designs = GetDesignList();
            if (designs == null || designs.Count == 0)
            {
                _status = "no designs found in Glamourer";
                return;
            }

            var found = designs.FirstOrDefault(kv =>
                kv.Value.Equals(designName, StringComparison.OrdinalIgnoreCase));

            if (found.Value == null)
            {
                _status = $"design \"{designName}\" not found";
                return;
            }

            _activeDesignName = designName;
            int result = _applyDesign.InvokeFunc(found.Key, 0, 0u, FlagOnceEquipCustomize);

            if (result == 0)
            {
                _status = $"applied \"{designName}\"";
                DalamudApi.LogInfo($"[GlamBridge] Applied \"{designName}\"");
            }
            else
            {
                _status = $"apply returned {result} for \"{designName}\"";
                DalamudApi.LogInfo($"[GlamBridge] ApplyDesign returned {result}");
            }
        }
        catch (Exception ex)
        {
            _status = $"apply error: {ex.Message}";
        }
    }

    private static void DoRevert()
    {
        _activeDesignName = null;

        // Try RevertToAutomation first (puts user back to their Glamourer profile).
        try
        {
            int result = _revertToAutomation?.InvokeFunc(0, 0u, FlagEquipCustomize) ?? -1;
            if (result == 0)
            {
                _status = "reverted to automation";
                DalamudApi.LogInfo("[GlamBridge] Reverted to automation");
                return;
            }
        }
        catch { }

        // Fallback: RevertState.
        try
        {
            int result = _revertState?.InvokeFunc(0, 0u, FlagEquipCustomize) ?? -1;
            _status = result == 0 ? "reverted to game state" : $"revert code {result}";
        }
        catch (Exception ex)
        {
            _status = $"revert error: {ex.Message}";
        }
    }
}
