using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
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
//
// Notification system: when the quest id or sequence changes, the pill
// auto-reveals for a few seconds, shows a "(New)" badge with pulsing
// border, and fires a Dalamud toast. Hovering or clicking dismisses
// the notification.
public static unsafe class MsqTeleport
{
    // ── layout constants ──────────────────────────────────────────
    private const float PILL_WIDTH       = 320f;
    private const float PILL_HEIGHT      = 42f;
    private const float HOVER_STRIP_H    = 18f;   // invisible hit-test band at top
    private const float SLIDE_SPEED      = 8f;    // exp-lerp rate (1/s)
    private const float ROUNDING         = 10f;
    private const float HOME_BTN_SIZE    = 32f;   // home button square side
    private const float HOME_BTN_GAP     = 8f;    // gap between pill and home btn

    // ── cached MSQ data ──────────────────────────────────────────
    private static string _questName       = "";
    private static string _destZoneName    = "";
    private static uint   _bestAetheryteId;
    private static byte   _bestSubIndex;
    private static string _bestAetheryteName = "";
    private static uint   _dutyContentId;          // ContentFinderCondition row id (0 = not a duty)
    private static string _dutyName = "";
    private static double _lastRefreshS;
    private const  double REFRESH_INTERVAL = 2.0;

    // Skip redundant heavy work: only re-resolve aetherytes when the
    // quest id or sequence actually changes.
    private static ushort _lastMsqId;
    private static byte   _lastSeq;
    private static bool   _needsResolve;
    private static bool   _firstDetect = true;   // suppress toast on initial detection
    private static bool   _diagPrinted;           // one-shot diagnostic dump

    // ── animation state ───────────────────────────────────────────
    private static float _revealT;        // 0 = hidden, 1 = fully slid down
    private static bool  _hovered;

    // ── notification state ────────────────────────────────────────
    private static bool   _isNew;                 // quest changed, not yet dismissed
    private static float  _newFadeT;              // 1→0 fade for the "(New)" badge
    private static double _autoRevealUntilS;      // wall-clock until which pill auto-reveals

    // ── status flash ──────────────────────────────────────────────
    private static string _statusMsg = "";
    private static double _statusUntilS;

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableMsqTeleport) return;

        // Always run: cheap quest-change detection (two pointer reads
        // + one static call). This is what powers the notification
        // system — we need to know about quest changes even when the
        // pill is hidden.
        DetectQuestChange();

        // Full aetheryte/territory resolve only runs when the pill is
        // visible (hovered or auto-revealing after a quest change).
        double now = Environment.TickCount64 / 1000.0;
        bool visible = _hovered || now < _autoRevealUntilS;
        if (!visible) return;

        if (_needsResolve || now - _lastRefreshS >= REFRESH_INTERVAL)
        {
            _lastRefreshS = now;
            _needsResolve = false;
            RefreshMsqData();
        }
    }

    // Lightweight check that runs every frame. Reads just the quest
    // id + sequence and compares to the last known values. On change,
    // sets notification state, fires a toast, and triggers auto-reveal.
    private static void DetectQuestChange()
    {
        try
        {
            var agent = AgentScenarioTree.Instance();
            if (agent == null || agent->Data == null) return;

            ushort msqId = 0;
            for (int i = 0; i < 3; i++)
            {
                ushort id = agent->Data->MainScenarioQuestIds[i];
                if (id != 0) { msqId = id; break; }
            }
            if (msqId == 0) return;

            byte seq = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.GetQuestSequence(msqId);

            if (msqId == _lastMsqId && seq == _lastSeq) return;

            bool wasFirst = _firstDetect;
            _firstDetect = false;
            _lastMsqId = msqId;
            _lastSeq   = seq;
            _needsResolve = true;

            // Don't toast on initial plugin load — only on actual progression.
            if (wasFirst) return;

            // Quick quest-name lookup for the toast (just one sheet read,
            // no aetheryte iteration). The full resolve fills in the rest
            // when the pill becomes visible.
            string toastName = "";
            try
            {
                var questSheet = DalamudApi.DataManager.GetExcelSheet<Quest>();
                if (questSheet != null)
                {
                    var row = questSheet.GetRowOrDefault(msqId);
                    if (row == null) row = questSheet.GetRowOrDefault((uint)msqId + 0x10000u);
                    if (row != null) toastName = row.Value.Name.ExtractText();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(toastName)) toastName = $"Quest #{msqId}";

            _isNew = true;
            _newFadeT = 1f;
            _autoRevealUntilS = Environment.TickCount64 / 1000.0 + 5.0;

            try { DalamudApi.ShowQuestToast($"MSQ Updated: {toastName}"); } catch { }
        }
        catch { }
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

        float homeS = HOME_BTN_SIZE * scale;
        float homeGap = HOME_BTN_GAP * scale;

        // Center the pill (home button sits outside to the right).
        float posX = (disp.X - pw) * 0.5f;
        float pillY = -ph + _revealT * (ph + 4f);

        // Home button Y: vertically centered with the pill.
        float homeBtnX = posX + pw + homeGap;
        float homeBtnY = pillY + (ph - homeS) * 0.5f;

        // ── Invisible ImGui windows for hover + click detection ──
        // The pill and home button each get their own window so hover
        // detection is independent and clicking one doesn't require
        // the other's window to report hovered.
        float hitH = MathF.Max(stripH, pillY + ph);
        if (hitH < stripH) hitH = stripH;

        var hitFlags = ImGuiWindowFlags.NoDecoration
                     | ImGuiWindowFlags.NoNav
                     | ImGuiWindowFlags.NoFocusOnAppearing
                     | ImGuiWindowFlags.NoMove
                     | ImGuiWindowFlags.NoSavedSettings
                     | ImGuiWindowFlags.NoBringToFrontOnFocus;

        // — Pill hit-test (pill only) —
        ImGui.SetNextWindowPos(new Vector2(posX, 0f));
        ImGui.SetNextWindowSize(new Vector2(pw, hitH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        ImGui.Begin("##MsqTpHit", hitFlags);
        bool pillHovered = ImGui.IsWindowHovered();
        bool clickedPill = pillHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        // — Home button hit-test (independent, same height as pill strip) —
        ImGui.SetNextWindowPos(new Vector2(homeBtnX, 0f));
        ImGui.SetNextWindowSize(new Vector2(homeS, hitH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        ImGui.Begin("##MsqHomeHit", hitFlags);
        bool homeHovered = ImGui.IsWindowHovered();
        bool clickedHome = homeHovered && _revealT > 0.1f && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        // Either zone triggers the reveal.
        _hovered = pillHovered || homeHovered;

        // ── Dismiss notification on hover or click ──
        if (_isNew && (_hovered || clickedPill))
            _isNew = false;

        // Decay "(New)" badge opacity after dismissal.
        if (!_isNew && _newFadeT > 0f)
        {
            _newFadeT -= dt * 3f;   // ~0.33s fade-out
            if (_newFadeT < 0f) _newFadeT = 0f;
        }

        // ── Animate reveal ──
        double now = Environment.TickCount64 / 1000.0;
        bool autoRevealing = now < _autoRevealUntilS;
        float target = (_hovered || autoRevealing) ? 1f : 0f;
        float k = 1f - MathF.Exp(-SLIDE_SPEED * dt);
        _revealT += (target - _revealT) * k;
        if (_revealT < 0.005f && !_hovered && !autoRevealing) _revealT = 0f;

        // Don't draw if fully hidden.
        if (_revealT <= 0f) return;

        float alpha = _revealT;
        float posY = pillY;

        var dl = ImGui.GetForegroundDrawList();

        // ── Background pill ──
        var uiBg = noWickyXIV.Config.UiColorBackground;
        uint bgCol = PackRgba(uiBg.X, uiBg.Y, uiBg.Z, uiBg.W * alpha);
        dl.AddRectFilled(
            new Vector2(posX, posY),
            new Vector2(posX + pw, posY + ph),
            bgCol, ROUNDING * scale);

        // ── Border — pulses while notification is active ──
        float borderAlpha;
        if (_isNew || _newFadeT > 0.5f)
        {
            // Sine pulse: oscillates border brightness 0.35..1.0 at ~3 Hz.
            float pulse = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin((float)(now * 6.0)));
            borderAlpha = pulse * alpha;
        }
        else
        {
            borderAlpha = 0.7f * alpha;
        }
        var uiAccent = noWickyXIV.Config.UiColorAccent;
        uint borderCol = PackRgba(uiAccent.X, uiAccent.Y, uiAccent.Z, borderAlpha);
        dl.AddRect(
            new Vector2(posX, posY),
            new Vector2(posX + pw, posY + ph),
            borderCol, ROUNDING * scale, ImDrawFlags.None, 1.5f * scale);

        // ── Text content ──
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

        var uiTxt = noWickyXIV.Config.UiColorText;
        uint textCol = PackRgba(uiTxt.X, uiTxt.Y, uiTxt.Z, uiTxt.W * alpha);
        var textSize = ImGui.CalcTextSize(label);
        float tx = posX + (pw - textSize.X) * 0.5f;
        float ty = posY + (ph - textSize.Y) * 0.5f;
        dl.AddText(new Vector2(tx, ty), textCol, label);

        // ── "(New)" badge — floats at bottom-center of the pill ──
        if (_newFadeT > 0.01f)
        {
            string newLabel = "(New)";
            var newSize = ImGui.CalcTextSize(newLabel);
            float padX = 8f * scale;
            float padY = 3f * scale;
            float badgeW = newSize.X + padX * 2f;
            float badgeH = newSize.Y + padY * 2f;
            float badgeX = posX + (pw - badgeW) * 0.5f;
            float badgeY = posY + ph - badgeH * 0.5f;  // straddles the bottom edge

            float badgeAlpha = _newFadeT * alpha;
            uint badgeBg   = PackRgba(uiAccent.X * 0.9f, uiAccent.Y * 0.33f, uiAccent.Z * 0.2f, 0.95f * badgeAlpha);
            uint badgeBord = PackRgba(uiAccent.X, uiAccent.Y * 0.67f, uiAccent.Z * 0.43f, 0.8f * badgeAlpha);
            uint badgeText = PackRgba(uiTxt.X, uiTxt.Y, uiTxt.Z, uiTxt.W * badgeAlpha);

            dl.AddRectFilled(
                new Vector2(badgeX, badgeY),
                new Vector2(badgeX + badgeW, badgeY + badgeH),
                badgeBg, 6f * scale);
            dl.AddRect(
                new Vector2(badgeX, badgeY),
                new Vector2(badgeX + badgeW, badgeY + badgeH),
                badgeBord, 6f * scale, ImDrawFlags.None, 1f * scale);
            dl.AddText(
                new Vector2(badgeX + padX, badgeY + padY),
                badgeText, newLabel);
        }

        // ── Home button — floating circle to the right of the pill ──
        {
            float cx = homeBtnX + homeS * 0.5f;
            float cy = homeBtnY + homeS * 0.5f;
            float r  = homeS * 0.5f;

            // Background circle.
            bool homeHover = homeHovered;
            float homeBgA = homeHover ? 0.95f : 0.85f;
            uint homeBg = PackRgba(uiBg.X, uiBg.Y, uiBg.Z, homeBgA * alpha);
            dl.AddCircleFilled(new Vector2(cx, cy), r, homeBg, 24);

            // Border circle.
            uint homeBorder = PackRgba(uiAccent.X, uiAccent.Y, uiAccent.Z, 0.7f * alpha);
            dl.AddCircle(new Vector2(cx, cy), r, homeBorder, 24, 1.5f * scale);

            // House icon — geometric: triangle roof + rectangle body.
            float iconScale = homeS * 0.35f;
            float iconX = cx;
            float iconTopY = cy - iconScale * 0.9f;
            float roofHalfW = iconScale * 0.85f;
            float roofBaseY = cy - iconScale * 0.15f;
            float bodyBot   = cy + iconScale * 0.75f;
            float bodyHalfW = iconScale * 0.6f;

            uint iconCol = PackRgba(uiTxt.X, uiTxt.Y, uiTxt.Z, uiTxt.W * alpha);

            // Roof triangle.
            dl.AddTriangleFilled(
                new Vector2(iconX, iconTopY),
                new Vector2(iconX - roofHalfW, roofBaseY),
                new Vector2(iconX + roofHalfW, roofBaseY),
                iconCol);

            // Body rectangle.
            dl.AddRectFilled(
                new Vector2(iconX - bodyHalfW, roofBaseY),
                new Vector2(iconX + bodyHalfW, bodyBot),
                iconCol);

            // Door cutout (dark rect in lower center of body).
            float doorHalfW = bodyHalfW * 0.35f;
            float doorTop   = roofBaseY + (bodyBot - roofBaseY) * 0.35f;
            uint doorCol = PackRgba(uiBg.X, uiBg.Y, uiBg.Z, homeBgA * alpha);
            dl.AddRectFilled(
                new Vector2(iconX - doorHalfW, doorTop),
                new Vector2(iconX + doorHalfW, bodyBot),
                doorCol);
        }

        // ── Click actions ──
        // Home button sits inside the pill's hit-test area, so both
        // clickedPill and clickedHome are true when clicking the icon.
        // Prioritise the home button — only fall through to MSQ if it
        // wasn't a home click.
        if (clickedHome)
            TeleportMenu.TeleportToFcHouse();
        else if (clickedPill)
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
        try
        {
            // 1. Get current MSQ quest id from AgentScenarioTree.
            //    This is cheap — two pointer reads.
            var agent = AgentScenarioTree.Instance();
            if (agent == null || agent->Data == null)
            {
                ClearCachedData();
                return;
            }

            ushort msqId = 0;
            for (int i = 0; i < 3; i++)
            {
                ushort id = agent->Data->MainScenarioQuestIds[i];
                if (id != 0)
                {
                    msqId = id;
                    break;
                }
            }
            if (msqId == 0) { ClearCachedData(); return; }

            // Full resolve — quest or step changed.
            ClearCachedData();

            // 2. Look up the quest in the Lumina Quest sheet.
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
            byte seq = _lastSeq;

            // 3. Build candidate location lists.
            //    Current-step locations are the primary signal. When
            //    a single step spans multiple territories (common in
            //    MSQ — cutscene markers, transition zones, instanced
            //    content), picking the FIRST territory with an aetheryte
            //    was wrong (it grabbed Sinus Lacrimarum for a quest
            //    whose playable objective is Old Sharlayan). Fix:
            //    group current-step candidates by territory, pick the
            //    territory with the MOST entries (majority vote), then
            //    find the nearest aetheryte there. Fallback steps and
            //    IssuerLocation only fire if the current step yields
            //    nothing.
            var currentStepCands = new List<(float x, float z, uint terr)>();
            var fallbackCands    = new List<(float x, float z, uint terr)>();

            int todoCount = quest.TodoParams.Count;

            // seq=255 means "ready to turn in" — the player needs to
            // go back to the quest NPC. Use IssuerLocation as the
            // primary candidate so we teleport to the turn-in NPC
            // instead of picking a random territory from old steps.
            bool isTurnIn = (seq == 255 || seq - 1 >= todoCount);

            // Current-step objectives (seq is 1-based, TodoParams 0-based).
            if (!isTurnIn && seq > 0 && seq - 1 < todoCount)
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
                            currentStepCands.Add((level.X, level.Z, level.Territory.RowId));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Turn-in: IssuerLocation is the primary destination.
            if (isTurnIn)
            {
                try
                {
                    if (quest.IssuerLocation.IsValid)
                    {
                        var issuer = quest.IssuerLocation.Value;
                        if (issuer.Territory.IsValid)
                            currentStepCands.Add((issuer.X, issuer.Z, issuer.Territory.RowId));
                    }
                }
                catch { }
            }

            // Other steps as fallbacks (+ IssuerLocation when not turn-in).
            for (int i = 0; i < todoCount; i++)
            {
                if (!isTurnIn && seq > 0 && i == seq - 1) continue;
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
                            fallbackCands.Add((level.X, level.Z, level.Territory.RowId));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            if (!isTurnIn)
            {
                try
                {
                    if (quest.IssuerLocation.IsValid)
                    {
                        var issuer = quest.IssuerLocation.Value;
                        if (issuer.Territory.IsValid)
                            fallbackCands.Add((issuer.X, issuer.Z, issuer.Territory.RowId));
                    }
                }
                catch { }
            }

            if (currentStepCands.Count == 0 && fallbackCands.Count == 0) return;

            // Capture issuer territory — used as a tiebreaker when
            // the current step has equal-count candidates across
            // territories. MSQ quests often return the player to
            // the hub they got the quest from after a cutscene in
            // another zone, so the issuer territory is a strong
            // signal for "where should I actually teleport."
            uint issuerTerr = 0;
            try
            {
                if (quest.IssuerLocation.IsValid)
                {
                    var il = quest.IssuerLocation.Value;
                    if (il.Territory.IsValid)
                        issuerTerr = il.Territory.RowId;
                }
            }
            catch { }

            // Diagnostic: write candidate territory breakdown to file (once).
            // Remove once routing is confirmed correct.
            if (!_diagPrinted)
            {
                _diagPrinted = true;
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"=== MSQ Diagnostic — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    sb.AppendLine($"Quest: {_questName}  id={msqId}  seq={seq}  isTurnIn={isTurnIn}");
                    sb.AppendLine($"IssuerTerr: {issuerTerr}");
                    sb.AppendLine($"CurrentStepCands: {currentStepCands.Count}  FallbackCands: {fallbackCands.Count}");
                    sb.AppendLine();

                    var terrNames = new Dictionary<uint, string>();
                    var terrSheet2 = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                    Action<string, List<(float x, float z, uint terr)>> dumpCands = (label, cands) =>
                    {
                        var terrCounts = new Dictionary<uint, int>();
                        foreach (var c in cands)
                        {
                            if (!terrCounts.ContainsKey(c.terr)) terrCounts[c.terr] = 0;
                            terrCounts[c.terr]++;
                            if (!terrNames.ContainsKey(c.terr) && terrSheet2 != null)
                            {
                                var tr = terrSheet2.GetRowOrDefault(c.terr);
                                if (tr.HasValue && tr.Value.PlaceName.IsValid)
                                    terrNames[c.terr] = tr.Value.PlaceName.Value.Name.ExtractText();
                            }
                        }
                        sb.AppendLine($"  {label}:");
                        foreach (var kv in terrCounts)
                        {
                            string name = terrNames.ContainsKey(kv.Key) ? terrNames[kv.Key] : $"terr#{kv.Key}";
                            string marker = kv.Key == issuerTerr ? " [issuer]" : "";
                            sb.AppendLine($"    {name} x{kv.Value}{marker}");
                        }
                        if (terrCounts.Count == 0) sb.AppendLine("    (none)");
                    };
                    dumpCands("Current step", currentStepCands);
                    dumpCands("Fallback", fallbackCands);

                    var dir = PluginConfiguration.ConfigFolder.FullName;
                    var path = System.IO.Path.Combine(dir, "msq_diag.txt");
                    File.WriteAllText(path, sb.ToString());
                    DalamudApi.LogInfo($"[MSQ] Diagnostic written to: {path}");
                }
                catch { }
            }

            // 4. Find the best aetheryte.
            var aetheryteSheet = DalamudApi.DataManager.GetExcelSheet<Aetheryte>();
            if (aetheryteSheet == null) return;

            // Try current-step candidates first, territory-majority-ordered,
            // with issuer territory as tiebreaker.
            if (currentStepCands.Count > 0
                && TryResolveAetheryte(currentStepCands, aetheryteSheet,
                       majorityVote: true, preferTerr: issuerTerr))
                goto resolved;

            // Fallback: other steps / issuer, simple first-match order.
            if (fallbackCands.Count > 0
                && TryResolveAetheryte(fallbackCands, aetheryteSheet,
                       majorityVote: false, preferTerr: 0))
                goto resolved;

            resolved:

            // 5. If no aetheryte found, check if any candidate territory
            //    is a duty (dungeon/trial/raid).
            if (_bestAetheryteId == 0)
            {
                var allCands = currentStepCands.Count > 0 ? currentStepCands : fallbackCands;
                var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                if (terrSheet != null)
                {
                    foreach (var cand in allCands)
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
            DalamudApi.LogInfo($"[MsqTeleport] Refresh failed: {ex.Message}");
        }
    }

    // Resolve the best aetheryte from a list of candidates.
    // When majorityVote is true, candidates are grouped by territory
    // and the territory with the most entries is tried first. This
    // prevents a single cutscene/transition marker in another zone
    // from overriding the primary objective territory (e.g. one
    // Sinus Lacrimarum entry vs. three Old Sharlayan entries).
    // When majorityVote is false, candidates are tried in list order.
    // Returns true if an aetheryte was found and cached.
    private static bool TryResolveAetheryte(
        List<(float x, float z, uint terr)> candidates,
        Lumina.Excel.ExcelSheet<Aetheryte> aetheryteSheet,
        bool majorityVote, uint preferTerr = 0)
    {
        // Build ordered territory list.
        IEnumerable<(float x, float z, uint terr)> ordered;
        if (majorityVote && candidates.Count > 1)
        {
            // Group by territory, sort by count descending (most
            // entries = most likely the real destination). If counts
            // are tied, prefer the issuer's territory (MSQ quests
            // frequently start and end in the same hub — cutscene
            // markers in other zones are noise). Earliest list index
            // breaks remaining ties.
            var groups = new Dictionary<uint, (int count, int firstIdx)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                uint t = candidates[i].terr;
                if (!groups.ContainsKey(t))
                    groups[t] = (1, i);
                else
                {
                    var g = groups[t];
                    groups[t] = (g.count + 1, g.firstIdx);
                }
            }
            // Sort territories: most entries first, issuer territory
            // wins ties, then earliest index.
            var sortedTerrs = groups.OrderByDescending(kv => kv.Value.count)
                                    .ThenByDescending(kv => preferTerr != 0 && kv.Key == preferTerr ? 1 : 0)
                                    .ThenBy(kv => kv.Value.firstIdx)
                                    .Select(kv => kv.Key)
                                    .ToList();

            // Rebuild candidate list in territory-priority order.
            var reordered = new List<(float x, float z, uint terr)>();
            foreach (uint terr in sortedTerrs)
                foreach (var c in candidates)
                    if (c.terr == terr)
                        reordered.Add(c);
            ordered = reordered;
        }
        else
        {
            ordered = candidates;
        }

        foreach (var cand in ordered)
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
                try
                {
                    var terrSheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
                    var terr = terrSheet?.GetRowOrDefault(cand.terr);
                    if (terr.HasValue && terr.Value.PlaceName.IsValid)
                        _destZoneName = terr.Value.PlaceName.Value.Name.ExtractText();
                }
                catch { }
                return true;
            }
        }
        return false;
    }

    private static void ClearCachedData()
    {
        _questName = "";
        _destZoneName = "";
        _bestAetheryteId = 0;
        _bestSubIndex = 0;
        _bestAetheryteName = "";
        _dutyContentId = 0;
        _dutyName = "";
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
            DalamudApi.LogInfo($"[MsqTeleport] Duty Finder failed: {ex.Message}");
            ShowStatus("Duty Finder failed", 2.0);
        }
    }

    private static void TeleportToFcEstate()
    {
        // Use the same saved config values as TeleportMenu.TeleportToFcHouse()
        // so the home icon and the FC entry in the teleport popup share one
        // global destination.
        var cfg = noWickyXIV.Config;
        if (cfg.FcHouseAetheryteId == 0)
        {
            ShowStatus("FC house not set — use Set in teleport menu", 3.0);
            return;
        }

        try
        {
            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null) { ShowStatus("Teleport unavailable", 2.0); return; }
            try { telepo->UpdateAetheryteList(); } catch { }

            bool ok = telepo->Teleport(cfg.FcHouseAetheryteId, cfg.FcHouseSubIndex);
            ShowStatus(ok ? "Teleporting to FC Estate..." : "Cannot teleport right now", ok ? 3.0 : 2.0);
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[MsqTeleport] FC teleport failed: {ex.Message}");
            ShowStatus("FC teleport failed", 2.0);
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
            DalamudApi.LogInfo($"[MsqTeleport] Teleport failed: {ex.Message}");
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
