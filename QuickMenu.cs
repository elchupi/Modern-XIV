using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace noWickyXIV;

// Floating bottom-right launcher pill. Mirrors MsqTeleport's visual
// language (dark fill, yellow-gold border, rounded corners, exp-lerp
// slide animation) but anchored to the opposite corner and sliding
// UP from below the viewport on hover. Hidden at rest behind a thin
// invisible hover-strip at the bottom-right edge.
//
// Each row dispatches a single slash command via
// DalamudApi.CommandManager.ProcessCommand — same dispatcher the user
// would type the command into themselves.
public static class QuickMenu
{
    // Layout (unscaled).
    private const float PANEL_WIDTH    = 180f;
    private const float ROW_HEIGHT     = 30f;
    private const float ROW_GAP        = 2f;
    private const float PAD_X          = 12f;
    private const float PAD_Y          = 8f;
    private const float ROUNDING       = 10f;
    private const float BORDER         = 1.5f;
    private const float HIT_STRIP_H    = 22f;   // invisible hit band along the bottom edge when hidden
    private const float MARGIN_X       = 16f;   // distance from right edge
    private const float MARGIN_Y       = 16f;   // distance from bottom edge when fully revealed
    private const float SLIDE_SPEED    = 8f;    // exp-lerp 1/s — matches MsqTeleport

    // Rows are rendered top→bottom in this order.
    private static readonly (string label, string cmd)[] Entries =
    {
        ("/xlplugins",   "/xlplugins"),
        ("/nowickyxiv",  "/nowickyxiv"),
        ("/glamourer",   "/glamourer"),
        ("/penumbra",    "/penumbra"),
        ("/vfxedit",     "/vfxedit"),
    };

    // 0 = panel parked below the viewport, 1 = fully revealed at rest.
    private static float _revealT;
    private static bool  _hovered;

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnableQuickMenu) return;
        if (!DalamudApi.ClientState.IsLoggedIn) return;

        var io = ImGui.GetIO();
        var disp = io.DisplaySize;
        if (disp.X <= 0 || disp.Y <= 0) return;

        float dt    = io.DeltaTime;
        float scale = ImGuiHelpers.GlobalScale;

        float pw       = PANEL_WIDTH * scale;
        float rowH     = ROW_HEIGHT * scale;
        float rowGap   = ROW_GAP * scale;
        float padX     = PAD_X * scale;
        float padY     = PAD_Y * scale;
        float rounding = ROUNDING * scale;
        float border   = BORDER * scale;
        float stripH   = HIT_STRIP_H * scale;
        float marginX  = MARGIN_X * scale;
        float marginY  = MARGIN_Y * scale;

        int   n      = Entries.Length;
        float panelH = padY * 2f + n * rowH + (n - 1) * rowGap;

        float panelRight = disp.X - marginX;
        float panelLeft  = panelRight - pw;

        // Vertical anchor: at reveal=0 the panel sits fully below the
        // viewport (top edge = disp.Y). At reveal=1 it rests with
        // marginY of clearance from the bottom. Lerp between the two.
        float restingTop = disp.Y - marginY - panelH;
        float hiddenTop  = disp.Y;
        float panelTop   = hiddenTop + (restingTop - hiddenTop) * _revealT;
        float panelBot   = panelTop + panelH;

        // Hover hit-area: spans from wherever the panel currently sits
        // down to the bottom edge. When the panel is hidden, this
        // collapses to a thin strip exactly stripH tall in the
        // bottom-right corner — the user's reveal trigger.
        float hitTop = MathF.Min(panelTop, disp.Y - stripH);
        if (hitTop < 0f) hitTop = 0f;
        float hitH = disp.Y - hitTop;

        var hitFlags = ImGuiWindowFlags.NoDecoration
                     | ImGuiWindowFlags.NoNav
                     | ImGuiWindowFlags.NoFocusOnAppearing
                     | ImGuiWindowFlags.NoMove
                     | ImGuiWindowFlags.NoSavedSettings
                     | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.SetNextWindowPos(new Vector2(panelLeft, hitTop));
        ImGui.SetNextWindowSize(new Vector2(pw, hitH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        ImGui.Begin("##nwQuickMenuHit", hitFlags);
        bool windowHovered = ImGui.IsWindowHovered();
        bool mouseClicked  = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        Vector2 mp         = ImGui.GetMousePos();
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        _hovered = windowHovered;

        // Slide animation — same exp-lerp as MsqTeleport so the two
        // pills feel like part of the same family.
        float target = _hovered ? 1f : 0f;
        float k = 1f - MathF.Exp(-SLIDE_SPEED * dt);
        _revealT += (target - _revealT) * k;
        if (_revealT < 0.005f && !_hovered) _revealT = 0f;
        if (_revealT > 0.995f && _hovered)  _revealT = 1f;

        if (_revealT <= 0f) return;

        float alpha = _revealT;
        var dl = ImGui.GetForegroundDrawList();

        // Background pill.
        uint bgCol = PackRgba(0.08f, 0.08f, 0.12f, 0.92f * alpha);
        dl.AddRectFilled(
            new Vector2(panelLeft, panelTop),
            new Vector2(panelRight, panelBot),
            bgCol, rounding);

        // Border.
        uint borderCol = PackRgba(0.95f, 0.75f, 0.20f, 0.7f * alpha);
        dl.AddRect(
            new Vector2(panelLeft, panelTop),
            new Vector2(panelRight, panelBot),
            borderCol, rounding, ImDrawFlags.None, border);

        // Rows. Hit-test uses raw mouse coords against per-row rects
        // so the foreground draw and click handling stay in sync
        // without spinning up extra ImGui windows per row.
        int clickedIndex = -1;
        for (int i = 0; i < n; i++)
        {
            float rowTop   = panelTop + padY + i * (rowH + rowGap);
            float rowBot   = rowTop + rowH;
            float rowLeft  = panelLeft + padX;
            float rowRight = panelRight - padX;

            // Only treat row hovers as real once the panel is mostly
            // open — prevents a click on the hit-strip from registering
            // on whichever row lerps under the cursor at the instant
            // of the click.
            bool rowHover = _revealT > 0.5f && windowHovered
                          && mp.X >= rowLeft && mp.X < rowRight
                          && mp.Y >= rowTop  && mp.Y < rowBot;

            if (rowHover)
            {
                uint hoverBg = PackRgba(0.95f, 0.75f, 0.20f, 0.18f * alpha);
                dl.AddRectFilled(
                    new Vector2(rowLeft, rowTop),
                    new Vector2(rowRight, rowBot),
                    hoverBg, rounding * 0.5f);
                if (mouseClicked) clickedIndex = i;
            }

            string label = Entries[i].label;
            var size = ImGui.CalcTextSize(label);
            float tx = rowLeft + 8f * scale;
            float ty = rowTop + (rowH - size.Y) * 0.5f;
            uint txtCol = PackRgba(1f, 1f, 1f, alpha);
            dl.AddText(new Vector2(tx, ty), txtCol, label);
        }

        if (clickedIndex >= 0)
        {
            try { DalamudApi.CommandManager.ProcessCommand(Entries[clickedIndex].cmd); }
            catch (Exception ex)
            {
                DalamudApi.LogInfo($"[QuickMenu] ProcessCommand failed: {ex.Message}");
            }
        }
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
