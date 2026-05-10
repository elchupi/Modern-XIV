using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace noWickyXIV;

// Floating top-center overlay: slides down on hover, shows the current
// MSQ destination and teleports to the nearest unlocked Aetheryte.
// Hidden by default (faded + off-screen above the viewport edge).
// On mouse-enter the hit-test strip at the very top, the pill slides
// down + fades in. On mouse-leave it slides back up + fades out.
public static unsafe class MsqTeleport
{
    // ── layout constants ──────────────────────────────────────────
    private const float PILL_WIDTH       = 320f;
    private const float PILL_HEIGHT      = 42f;
    private const float HOVER_STRIP_H    = 18f;   // invisible hit-test band at top
    private const float SLIDE_SPEED      = 8f;    // exp-lerp rate (1/s)
    private const float ROUNDING         = 10f;

    // ── cached MSQ data (refreshed every ~2 s) ────────────────────
    private static string _questName       = "";
    private static string _destZoneName    = "";
    private static uint   _bestAetheryteId;
    private static byte   _bestSubIndex;
    private static string _bestAetheryteName = "";
    private static uint   _dutyContentId;          // ContentFinderCondition row id (0 = not a duty)
    private static string _dutyName = "";
    private static double _lastRefreshS;
    private const  double REFRESH_INTERVAL = 2.0;

    // ── animation state ───────────────────────────────────────────
    private static float _revealT;        // 0 = hidden, 1 = fully slid down
    private static bool  _hovered;

    // ── status flash ──────────────────────────────────────────────
    private static string _statusMsg = "";
    private static double _statusUntilS;

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableMsqTeleport) return;

        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastRefreshS >= REFRESH_INTERVAL)
        {
            _lastRefreshS = now;
            RefreshMsqData();
        }
    }

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnableMsqTeleport) return;
        if (!DalamudApi.ClientState.IsLoggedIn) return;

        var io = ImGui.GetIO();
        var disp = io.DisplaySize;
        if (disp.X <= 0 || disp.Y <= 0) return;

        float dt = io.DeltaTime;
        float scale = ImGuiHelpers.GlobalScale;
        float pw = PILL_WIDTH * scale;
        float ph = PILL_HEIGHT * scale;
        float stripH = HOVER_STRIP_H * scale;

        float posX = (disp.X - pw) * 0.5f;
        float pillY = -ph + _revealT * (ph + 4f);

        // ── Invisible ImGui window for hover + click detection ──
        // Raw foreground-draw-list doesn't participate in ImGui's
        // input system, so we lay a transparent window over the
        // hover strip + pill area.
        float hitH = MathF.Max(stripH, pillY + ph);
        if (hitH < stripH) hitH = stripH;

        ImGui.SetNextWindowPos(new Vector2(posX, 0f));
        ImGui.SetNextWindowSize(new Vector2(pw, hitH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        var flags = ImGuiWindowFlags.NoDecoration
                  | ImGuiWindowFlags.NoNav
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.Begin("##MsqTpHit", flags);
        bool wndHovered = ImGui.IsWindowHovered();
        bool clicked = wndHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        _hovered = wndHovered;

        // Animate reveal.
        float target = _hovered ? 1f : 0f;
        float k = 1f - MathF.Exp(-SLIDE_SPEED * dt);
        _revealT += (target - _revealT) * k;
        if (_revealT < 0.005f && !_hovered) _revealT = 0f;

        // Don't draw if fully hidden.
        if (_revealT <= 0f) return;

        float alpha = _revealT;
        float posY = pillY;

        var dl = ImGui.GetForegroundDrawList();

        // Background pill.
        uint bgCol = PackRgba(0.08f, 0.08f, 0.12f, 0.92f * alpha);
        dl.AddRectFilled(
            new Vector2(posX, posY),
            new Vector2(posX + pw, posY + ph),
            bgCol, ROUNDING * scale);

        // Border.
        uint borderCol = PackRgba(0.95f, 0.75f, 0.20f, 0.7f * alpha);
        dl.AddRect(
            new Vector2(posX, posY),
            new Vector2(posX + pw, posY + ph),
            borderCol, ROUNDING * scale, ImDrawFlags.None, 1.5f * scale);

        // Text content.
        double now = Environment.TickCount64 / 1000.0;
        bool hasStatus = _statusMsg.Length > 0 && now < _statusUntilS;

        string label;
        if (hasStatus)
            label = _statusMsg;
        else if (_bestAetheryteId != 0)
            label = $"{_destZoneName}  -  {_bestAetheryteName}";
        else if (_dutyContentId != 0)
            label = $"{_dutyName}  (Duty Finder)";
        else
            label = _questName.Length > 0 ? $"MSQ: {_questName}" : "No active MSQ";

        uint textCol = PackRgba(1f, 1f, 1f, alpha);
        var textSize = ImGui.CalcTextSize(label);
        float tx = posX + (pw - textSize.X) * 0.5f;
        float ty = posY + (ph - textSize.Y) * 0.5f;
        dl.AddText(new Vector2(tx, ty), textCol, label);

        // Click action: teleport or open Duty Finder.
        if (clicked)
        {
            if (_bestAetheryteId != 0)
                ExecuteTeleport();
            else if (_dutyContentId != 0)
                OpenDutyFinder();
        }
    }

    // ── MSQ data resolution ───────────────────────────────────────

    private static void RefreshMsqData()
    {
        _questName = "";
        _destZoneName = "";
        _bestAetheryteId = 0;
        _bestSubIndex = 0;
        _bestAetheryteName = "";
        _dutyContentId = 0;
        _dutyName = "";

        try
        {
            // 1. Get current MSQ quest id from AgentScenarioTree.
            var agent = AgentScenarioTree.Instance();
            if (agent == null || agent->Data == null) return;

            ushort msqId = 0;
            // Try each path index — [0] is the main MSQ path.
            for (int i = 0; i < 3; i++)
            {
                ushort id = agent->Data->MainScenarioQuestIds[i];
                if (id != 0)
                {
                    msqId = id;
                    break;
                }
            }
            if (msqId == 0) return;

            // 2. Look up the quest in the Lumina Quest sheet.
            //    Quest sheet uses row id = questId + 0x10000 in some versions,
            //    but Dalamud's sheet accessor works with the raw id. We try
            //    the id as-is first, then with the offset.
            var questSheet = DalamudApi.DataManager.GetExcelSheet<Quest>();
            if (questSheet == null) return;

            // The MSQ id from AgentScenarioTree is the lower 16 bits;
            // the Quest sheet row id is typically questId + 0x10000.
            var questRow = questSheet.GetRowOrDefault(msqId);
            if (questRow == null)
                questRow = questSheet.GetRowOrDefault((uint)msqId + 0x10000u);
            if (questRow == null) return;

            var quest = questRow.Value;
            _questName = quest.Name.ExtractText();

            // 3. Build a priority-ordered list of candidate locations.
            //    First: TodoParams objectives for the current sequence
            //    step (where the game is telling you to go right now).
            //    Then:  IssuerLocation as a last-resort fallback.
            var candidates = new List<(float x, float z, uint terr)>();

            byte seq = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.GetQuestSequence(msqId);

            // TodoParams objective locations (step-based).
            // If we have a current sequence, try that step's group first,
            // then add all other groups as fallbacks.
            int todoCount = quest.TodoParams.Count;

            // Current-step group first (seq is 1-based, TodoParams 0-based).
            if (seq > 0 && seq - 1 < todoCount)
            {
                try
                {
                    var locCol = quest.TodoParams[seq - 1].ToDoLocation;
                    for (int j = 0; j < locCol.Count; j++)
                    {
                        try
                        {
                            var levelRef = locCol[j];
                            if (!levelRef.IsValid) continue;
                            var level = levelRef.Value;
                            if (!level.Territory.IsValid) continue;
                            candidates.Add((level.X, level.Z, level.Territory.RowId));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Remaining groups as fallbacks.
            for (int i = 0; i < todoCount; i++)
            {
                if (seq > 0 && i == seq - 1) continue; // already added above
                try
                {
                    var locCol = quest.TodoParams[i].ToDoLocation;
                    for (int j = 0; j < locCol.Count; j++)
                    {
                        try
                        {
                            var levelRef = locCol[j];
                            if (!levelRef.IsValid) continue;
                            var level = levelRef.Value;
                            if (!level.Territory.IsValid) continue;
                            candidates.Add((level.X, level.Z, level.Territory.RowId));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // IssuerLocation = last-resort fallback (quest giver, not
            // the objective — user wants to TP to where they're GOING).
            try
            {
                if (quest.IssuerLocation.IsValid)
                {
                    var issuer = quest.IssuerLocation.Value;
                    if (issuer.Territory.IsValid)
                        candidates.Add((issuer.X, issuer.Z, issuer.Territory.RowId));
                }
            }
            catch { }

            if (candidates.Count == 0) return;

            // 4. Find the nearest unlocked Aetheryte to ANY candidate.
            //    Walk each candidate in priority order. For each, find the
            //    closest same-territory aetheryte. Stop at the first
            //    candidate that produces a match — current-step objectives
            //    win over the quest giver's location.
            var aetheryteSheet = DalamudApi.DataManager.GetExcelSheet<Aetheryte>();
            if (aetheryteSheet == null) return;

            foreach (var cand in candidates)
            {
                float bestDist = float.MaxValue;
                uint  bestId   = 0;
                byte  bestSub  = 0;
                string bestName = "";

                foreach (var ae in DalamudApi.AetheryteList)
                {
                    try
                    {
                        var aethData = aetheryteSheet.GetRowOrDefault(ae.AetheryteId);
                        if (aethData == null) continue;
                        var aeth = aethData.Value;
                        if (!aeth.IsAetheryte) continue;
                        if (!aeth.Territory.IsValid) continue;
                        if (aeth.Territory.RowId != cand.terr) continue;

                        // Same territory — compute distance.
                        float ax = 0f, az = 0f;
                        try
                        {
                            var lvlArr = aeth.Level;
                            if (lvlArr.Count > 0 && lvlArr[0].IsValid)
                            {
                                var lvl = lvlArr[0].Value;
                                ax = lvl.X;
                                az = lvl.Z;
                            }
                        }
                        catch { }

                        float dx = ax - cand.x;
                        float dz = az - cand.z;
                        float dist = dx * dx + dz * dz;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId   = ae.AetheryteId;
                            bestSub  = ae.SubIndex;
                            bestName = "";
                            try
                            {
                                if (aeth.PlaceName.IsValid)
                                    bestName = aeth.PlaceName.Value.Name.ExtractText();
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (bestId != 0)
                {
                    _bestAetheryteId   = bestId;
                    _bestSubIndex      = bestSub;
                    _bestAetheryteName = bestName;
                    // Use this candidate's territory for the zone name.
                    try
                    {
                        var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                        var terr = terrSheet?.GetRowOrDefault(cand.terr);
                        if (terr.HasValue && terr.Value.PlaceName.IsValid)
                            _destZoneName = terr.Value.PlaceName.Value.Name.ExtractText();
                    }
                    catch { }
                    break;   // first match wins (IssuerLocation priority)
                }
            }

            // 5. If no aetheryte found, check if any candidate territory
            //    is a duty (dungeon/trial/raid). If so, store the
            //    ContentFinderCondition id so we can open the DF instead.
            if (_bestAetheryteId == 0)
            {
                var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                if (terrSheet != null)
                {
                    foreach (var cand in candidates)
                    {
                        try
                        {
                            var terr = terrSheet.GetRowOrDefault(cand.terr);
                            if (terr == null) continue;
                            var tt = terr.Value;
                            if (!tt.ContentFinderCondition.IsValid) continue;
                            var cfc = tt.ContentFinderCondition.Value;
                            uint cfcId = tt.ContentFinderCondition.RowId;
                            if (cfcId == 0) continue;

                            _dutyContentId = cfcId;
                            _dutyName = cfc.Name.ExtractText();
                            if (string.IsNullOrEmpty(_dutyName))
                                _dutyName = $"Duty #{cfcId}";

                            // Resolve zone name from the duty territory.
                            if (tt.PlaceName.IsValid)
                                _destZoneName = tt.PlaceName.Value.Name.ExtractText();
                            break;
                        }
                        catch { }
                    }
                }
            }

            if (string.IsNullOrEmpty(_destZoneName))
                _destZoneName = _bestAetheryteId != 0 ? _bestAetheryteName : "";
            if (string.IsNullOrEmpty(_bestAetheryteName) && _bestAetheryteId != 0)
                _bestAetheryteName = $"Aetheryte #{_bestAetheryteId}";
        }
        catch (Exception ex)
        {
            DalamudApi.LogDebug($"[MsqTeleport] Refresh failed: {ex.Message}");
        }
    }

    private static void OpenDutyFinder()
    {
        try
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
            {
                ShowStatus("Duty Finder unavailable", 2.0);
                return;
            }
            agent->OpenRegularDuty(_dutyContentId);
            ShowStatus($"Opening {_dutyName}...", 2.0);
        }
        catch (Exception ex)
        {
            DalamudApi.LogDebug($"[MsqTeleport] Duty Finder failed: {ex.Message}");
            ShowStatus("Duty Finder failed", 2.0);
        }
    }

    private static void ExecuteTeleport()
    {
        try
        {
            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null)
            {
                ShowStatus("Teleport unavailable", 2.0);
                return;
            }

            bool ok = telepo->Teleport(_bestAetheryteId, _bestSubIndex);
            if (ok)
                ShowStatus($"Teleporting to {_bestAetheryteName}...", 3.0);
            else
                ShowStatus("Cannot teleport right now", 2.0);
        }
        catch (Exception ex)
        {
            DalamudApi.LogDebug($"[MsqTeleport] Teleport failed: {ex.Message}");
            ShowStatus("Teleport failed", 2.0);
        }
    }

    private static void ShowStatus(string msg, double durationS)
    {
        _statusMsg = msg;
        _statusUntilS = Environment.TickCount64 / 1000.0 + durationS;
    }

    private static uint PackRgba(float r, float g, float b, float a)
    {
        byte br = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, r)) * 255f);
        byte bg = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, g)) * 255f);
        byte bb = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, b)) * 255f);
        byte ba = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, a)) * 255f);
        return ((uint)ba << 24) | ((uint)bb << 16) | ((uint)bg << 8) | br;
    }
}
