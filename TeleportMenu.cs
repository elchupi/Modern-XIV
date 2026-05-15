using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace noWickyXIV;

/// <summary>
/// Custom teleport menu — replaces the game's native Teleport window
/// with a searchable, region-grouped list that includes FC house
/// shortcut and recently-visited tracking.
/// </summary>
public static unsafe class TeleportMenu
{
    // ── Data types ─────────────────────────────────────────────
    public class TeleportEntry
    {
        public uint AetheryteId;
        public byte SubIndex;
        public string AetheryteName;   // e.g. "Summerford Farms"
        public string AreaName;        // e.g. "Middle La Noscea"
        public string RegionName;      // e.g. "La Noscea"
        public ushort TerritoryId;
        public uint GilCost;
        public bool IsFavorite;
        public bool IsFcHouse;
        public bool IsPersonalHouse;
        public bool IsApartment;
    }

    // ── State ──────────────────────────────────────────────────
    public static bool IsWindowOpen;
    public static string SearchFilter = "";

    private static List<TeleportEntry> _cachedList;
    private static TeleportEntry _fcHouseEntry;
    private static DateTime _lastCacheTime;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    // Grouped cache for UI.
    private static List<(string region, List<TeleportEntry> entries)> _groupedCache;

    // ── FC house capture ──────────────────────────────────────
    public static bool CapturingFcHouse;
    private static uint _captureStartTerritory;

    // ── Safety ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true only when the game's teleport data structures are
    /// likely initialised — player is logged in, not in a loading screen,
    /// and the Telepo singleton itself is non-null.
    /// Calling <c>Telepo->UpdateAetheryteList()</c> outside these
    /// conditions can crash (access-violation inside the native func).
    /// </summary>
    private static bool IsTelepoSafe()
    {
        try
        {
            // Not logged in — aetheryte data doesn't exist yet.
            if (DalamudApi.ObjectTable.LocalPlayer == null) return false;

            // Territory 0 = between zones / loading.
            if (DalamudApi.ClientState.TerritoryType == 0) return false;

            // Condition flags: BetweenAreas / BetweenAreas51 indicate
            // the client is mid-zone-transition and game structs may be
            // partially torn down.
            if (DalamudApi.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
                || DalamudApi.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                return false;

            var telepo = Telepo.Instance();
            if (telepo == null) return false;

            return true;
        }
        catch { return false; }
    }

    // ── Addon intercept ────────────────────────────────────────
    private static bool _hooksRegistered;

    public static void Initialize()
    {
        _cachedList = null;
        _fcHouseEntry = null;
        _groupedCache = null;
        IsWindowOpen = false;
        SearchFilter = "";
        CapturingFcHouse = false;

        RegisterAddonHooks();
    }

    public static void Dispose()
    {
        UnregisterAddonHooks();
        _cachedList = null;
        _groupedCache = null;
    }

    // ── Addon hooks ────────────────────────────────────────────
    // When the game's Teleport window opens, hide it and show ours.

    private static void RegisterAddonHooks()
    {
        if (_hooksRegistered) return;
        try
        {
            DalamudApi.AddonLifecycle.RegisterListener(
                AddonEvent.PostSetup, "Teleport", OnTeleportAddonSetup);
            DalamudApi.AddonLifecycle.RegisterListener(
                AddonEvent.PreDraw, "Teleport", OnTeleportAddonPreDraw);
            _hooksRegistered = true;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[TeleportMenu] Failed to register addon hooks: {ex.Message}");
        }
    }

    private static void UnregisterAddonHooks()
    {
        if (!_hooksRegistered) return;
        try
        {
            DalamudApi.AddonLifecycle.UnregisterListener(OnTeleportAddonSetup);
            DalamudApi.AddonLifecycle.UnregisterListener(OnTeleportAddonPreDraw);
        }
        catch { }
        _hooksRegistered = false;
    }

    private static void OnTeleportAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (!noWickyXIV.Config.EnableCustomTeleportMenu) return;

        // During FC capture, let the native window through so the user
        // can teleport to their FC house via the game's UI.
        if (CapturingFcHouse) return;

        // Game's teleport window just opened — show ours instead.
        IsWindowOpen = true;
        SearchFilter = "";
        RefreshCache();
    }

    private static void OnTeleportAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!noWickyXIV.Config.EnableCustomTeleportMenu) return;

        // Let the native window stay visible during FC house capture.
        if (CapturingFcHouse) return;

        // Hide the native teleport addon every frame.
        try
        {
            var addr = args.Addon.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode != null)
            {
                addon->RootNode->ToggleVisibility(false);
                // Move it offscreen as a fallback.
                addon->SetPosition(-9999, -9999);
            }
        }
        catch { }
    }

    // ── Public API ─────────────────────────────────────────────

    /// <summary>Refresh the aetheryte cache from game data.</summary>
    public static void RefreshCache()
    {
        _lastCacheTime = DateTime.UtcNow;

        try
        {
            // Refresh Telepo's internal list (picks up housing changes).
            // Guard: UpdateAetheryteList dereferences internal pointers
            // that are null during loading screens / before login.
            if (IsTelepoSafe())
                Telepo.Instance()->UpdateAetheryteList();
        }
        catch { }

        var list = new List<TeleportEntry>();
        _fcHouseEntry = null;

        try
        {
            var aethSheet = DalamudApi.DataManager.GetExcelSheet<Aetheryte>();
            var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();

            foreach (var ae in DalamudApi.AetheryteList)
            {
                var aethData = aethSheet?.GetRowOrDefault(ae.AetheryteId);
                if (aethData == null) continue;

                // Detect housing flags.
                bool isFcHouse = ae.IsSharedHouse && !ae.IsApartment;
                bool isPersonal = !ae.IsSharedHouse && !ae.IsApartment && ae.Ward > 0;
                bool isApartment = ae.IsApartment;
                bool isHousing = isFcHouse || isPersonal || isApartment;

                // Get aetheryte display name.
                string aeName = "";
                try { aeName = aethData.Value.PlaceName.Value.Name.ExtractText(); } catch { }
                if (string.IsNullOrEmpty(aeName)) aeName = $"Aetheryte #{ae.AetheryteId}";

                // Get territory info.
                ushort terrId = (ushort)aethData.Value.Territory.RowId;
                string areaName = "";
                string regionName = "";

                var terrData = terrSheet?.GetRowOrDefault(terrId);
                if (terrData != null)
                {
                    try { areaName = terrData.Value.PlaceName.Value.Name.ExtractText(); } catch { }
                    try { regionName = terrData.Value.PlaceNameRegion.Value.Name.ExtractText(); } catch { }
                }

                if (string.IsNullOrEmpty(areaName)) areaName = aeName;
                if (string.IsNullOrEmpty(regionName)) regionName = "Other";

                // Housing entries get special region/name handling.
                // (isFcHouse, isPersonal, isApartment set above.)
                if (isHousing)
                    regionName = "Residential Areas";

                if (isFcHouse)
                    aeName = $"Estate Hall (Free Company)";
                else if (isApartment)
                    aeName = $"Apartment ({aeName})";
                else if (isPersonal)
                    aeName = $"Private Estate ({aeName})";

                var entry = new TeleportEntry
                {
                    AetheryteId = ae.AetheryteId,
                    SubIndex = ae.SubIndex,
                    AetheryteName = aeName,
                    AreaName = areaName,
                    RegionName = regionName,
                    TerritoryId = terrId,
                    GilCost = ae.GilCost,
                    IsFavorite = ae.IsFavourite,
                    IsFcHouse = isFcHouse,
                    IsPersonalHouse = isPersonal,
                    IsApartment = isApartment,
                };

                list.Add(entry);

                if (isFcHouse && _fcHouseEntry == null)
                {
                    _fcHouseEntry = entry;

                    // Auto-save FC house destination from the aetheryte list
                    // so the FC button works without manual capture.
                    if (noWickyXIV.Config.FcHouseAetheryteId != entry.AetheryteId
                        || noWickyXIV.Config.FcHouseSubIndex != entry.SubIndex)
                    {
                        noWickyXIV.Config.FcHouseAetheryteId = entry.AetheryteId;
                        noWickyXIV.Config.FcHouseSubIndex = entry.SubIndex;
                        noWickyXIV.Config.Save();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[TeleportMenu] Cache refresh error: {ex.Message}");
        }

        _cachedList = list;
        BuildGroupedCache();
    }

    private static void BuildGroupedCache()
    {
        if (_cachedList == null) { _groupedCache = null; return; }

        // Group by region, with Residential Areas first, then alphabetical.
        _groupedCache = _cachedList
            .Where(e => !e.IsFcHouse && !e.IsPersonalHouse && !e.IsApartment) // housing shown separately
            .GroupBy(e => e.RegionName)
            .OrderBy(g => g.Key == "Residential Areas" ? "AAA" : g.Key)
            .Select(g => (g.Key, g.OrderBy(e => e.AreaName).ThenBy(e => e.AetheryteName).ToList()))
            .ToList();
    }

    /// <summary>Get the cached aetheryte list, auto-refreshing if stale.</summary>
    public static List<TeleportEntry> GetList()
    {
        if (_cachedList == null || DateTime.UtcNow - _lastCacheTime > CacheTtl)
            RefreshCache();
        return _cachedList;
    }

    /// <summary>Get the grouped list for UI display.</summary>
    public static List<(string region, List<TeleportEntry> entries)> GetGroupedList()
    {
        if (_groupedCache == null || DateTime.UtcNow - _lastCacheTime > CacheTtl)
            RefreshCache();
        return _groupedCache;
    }

    /// <summary>Get the FC house entry (null if none).</summary>
    public static TeleportEntry GetFcHouse()
    {
        if (_cachedList == null) RefreshCache();
        return _fcHouseEntry;
    }

    /// <summary>Get housing entries (FC, personal, apartment).</summary>
    public static List<TeleportEntry> GetHousingEntries()
    {
        if (_cachedList == null) RefreshCache();
        return _cachedList?.Where(e => e.IsFcHouse || e.IsPersonalHouse || e.IsApartment).ToList()
               ?? new List<TeleportEntry>();
    }

    /// <summary>Execute a teleport and track it in recently visited.</summary>
    public static bool DoTeleport(TeleportEntry entry)
    {
        if (entry == null) return false;

        // Click confirm SE — same menu-confirm tick the native UI plays
        // when you click a menu item. Fires on the click side; the
        // teleport-cast SE the game plays afterward is unaffected.
        PlayMenuConfirmSound();

        try
        {
            if (!IsTelepoSafe()) return false;
            var telepo = Telepo.Instance();

            // Refresh the internal list before teleporting so IDs are
            // current (housing entries can shift between sessions).
            try { telepo->UpdateAetheryteList(); } catch { }

            bool ok = telepo->Teleport(entry.AetheryteId, entry.SubIndex);
            if (ok)
            {
                TrackRecentTeleport(entry);
                IsWindowOpen = false;

                // Close the native addon if it's open.
                CloseNativeAddon();
            }
            return ok;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[TeleportMenu] Teleport failed: {ex.Message}");
            return false;
        }
    }

    private static unsafe void PlayMenuConfirmSound()
    {
        try
        {
            var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
            if (stage == null) return;
            var mgr = stage->RaptureAtkUnitManager;
            if (mgr == null) return;
            var entries = mgr->AllLoadedUnitsList.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                var unit = entries[i].Value;
                if (unit == null) continue;
                unit->PlaySoundEffect(8);
                return;
            }
        }
        catch { }
    }

    /// <summary>Teleport to FC house using saved config values.</summary>
    public static bool TeleportToFcHouse()
    {
        var cfg = noWickyXIV.Config;
        if (cfg.FcHouseAetheryteId == 0)
        {
            DalamudApi.PrintError("FC house not set. Go to your FC house, then click Set.");
            return false;
        }

        try
        {
            if (!IsTelepoSafe()) { DalamudApi.PrintError("Teleport unavailable."); return false; }
            var telepo = Telepo.Instance();
            try { telepo->UpdateAetheryteList(); } catch { }

            bool ok = telepo->Teleport(cfg.FcHouseAetheryteId, cfg.FcHouseSubIndex);
            if (ok)
            {
                IsWindowOpen = false;
                CloseNativeAddon();
            }
            else
            {
                DalamudApi.PrintError("Cannot teleport right now.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[TeleportMenu] FC teleport failed: {ex.Message}");
            return false;
        }
    }

    // Known FC estate aetheryte IDs (one per residential district).
    private static readonly HashSet<uint> HousingAetheryteIds = new() { 56, 57, 58, 96, 164 };

    /// <summary>Auto-detect and save the FC house aetheryte, with diagnostic dump.</summary>
    public static void StartFcCapture()
    {
        try
        {
            var aethSheet = DalamudApi.DataManager.GetExcelSheet<Aetheryte>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Territory: {DalamudApi.ClientState.TerritoryType}");
            sb.AppendLine($"Current Config: AetheryteId={noWickyXIV.Config.FcHouseAetheryteId} SubIndex={noWickyXIV.Config.FcHouseSubIndex}");
            sb.AppendLine();

            uint foundId = 0;
            sb.AppendLine("── DalamudApi.AetheryteList ──");
            foreach (var ae in DalamudApi.AetheryteList)
            {
                string name = "";
                try
                {
                    var row = aethSheet?.GetRowOrDefault(ae.AetheryteId);
                    if (row != null) name = row.Value.PlaceName.Value.Name.ExtractText();
                }
                catch { }

                bool isHousing = HousingAetheryteIds.Contains(ae.AetheryteId);
                sb.AppendLine(
                    $"  ID={ae.AetheryteId} Sub={ae.SubIndex} Gil={ae.GilCost}" +
                    $" \"{name}\"{(isHousing ? " ★HOUSING" : "")}");

                if (isHousing && foundId == 0)
                    foundId = ae.AetheryteId;
            }

            var path = System.IO.Path.Combine(
                DalamudApi.PluginInterface.GetPluginConfigDirectory(), "tp_diag.txt");
            System.IO.File.WriteAllText(path, sb.ToString());

            if (foundId != 0)
            {
                noWickyXIV.Config.FcHouseAetheryteId = foundId;
                noWickyXIV.Config.FcHouseSubIndex = 0;
                noWickyXIV.Config.Save();
                DalamudApi.PrintEcho($"FC house set to aetheryte {foundId}. Dump written to: {path}");
            }
            else
            {
                DalamudApi.PrintError($"No housing aetheryte found in list. Dump written to: {path}");
            }
        }
        catch (Exception ex)
        {
            DalamudApi.PrintError($"Failed: {ex.Message}");
        }
    }

    /// <summary>Set a flag at the player's position and send the clickable map link to chat.</summary>
    public static unsafe void ShareCurrentLocation()
    {
        try
        {
            var player = DalamudApi.ObjectTable?.LocalPlayer;
            if (player == null) return;

            uint terrId = DalamudApi.ClientState.TerritoryType;
            if (terrId == 0) return;

            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            if (agent == null) return;

            uint mapId = agent->CurrentMapId;
            if (mapId == 0)
            {
                var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                var terrRow = terrSheet?.GetRowOrDefault(terrId);
                if (terrRow == null) return;
                mapId = terrRow.Value.Map.RowId;
            }

            var mapSheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var mapRow = mapSheet?.GetRowOrDefault(mapId);
            if (mapRow == null) return;

            float sizeFactor = mapRow.Value.SizeFactor;
            int offsetX = mapRow.Value.OffsetX;
            int offsetY = mapRow.Value.OffsetY;

            float sc = sizeFactor / 100.0f;
            float mapX = 41.0f / sc * ((player.Position.X + offsetX) * sc + 1024.0f) / 2048.0f + 1.0f;
            float mapZ = 41.0f / sc * ((player.Position.Z + offsetY) * sc + 1024.0f) / 2048.0f + 1.0f;

            agent->SetFlagMapMarker(terrId, mapId, mapX, mapZ);

            ChatSend.Send("<flag>");
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[TeleportMenu] ShareLocation failed: {ex.Message}");
        }
    }

    /// <summary>Called from noWickyXIV.Update() every frame.</summary>
    public static void Update()
    {
        // Nothing to poll — FC house is set instantly via StartFcCapture().
    }

    // ── Recently visited tracking ─────────────────────────────

    private static void TrackRecentTeleport(TeleportEntry entry)
    {
        // Don't track housing in recents.
        if (entry.IsFcHouse || entry.IsPersonalHouse || entry.IsApartment) return;

        var recents = noWickyXIV.Config.RecentTeleports;

        // Remove existing entry for this aetheryte (dedup).
        recents.RemoveAll(r => r.AetheryteId == entry.AetheryteId && r.SubIndex == entry.SubIndex);

        // Add to front.
        recents.Insert(0, new RecentTeleportEntry
        {
            AetheryteId = entry.AetheryteId,
            SubIndex = entry.SubIndex,
            Name = entry.AetheryteName,
            AreaName = entry.AreaName,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        // Keep only 3.
        while (recents.Count > 3)
            recents.RemoveAt(recents.Count - 1);

        noWickyXIV.Config.Save();
    }

    /// <summary>Get recently visited entries matched against current aetheryte data (for gil cost).</summary>
    public static List<TeleportEntry> GetRecentTeleports()
    {
        var recents = noWickyXIV.Config.RecentTeleports;
        if (recents == null || recents.Count == 0) return new List<TeleportEntry>();

        var list = GetList();
        if (list == null) return new List<TeleportEntry>();

        var result = new List<TeleportEntry>();
        foreach (var r in recents)
        {
            var match = list.FirstOrDefault(e =>
                e.AetheryteId == r.AetheryteId && e.SubIndex == r.SubIndex);
            if (match != null)
                result.Add(match);
        }
        return result;
    }

    // ── Native addon management ───────────────────────────────

    private static void CloseNativeAddon()
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("Teleport", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            addon->Close(true);
        }
        catch { }
    }

    /// <summary>Called when our custom window is closed by the user.</summary>
    public static void OnWindowClosed()
    {
        IsWindowOpen = false;
        CloseNativeAddon();
    }
}
