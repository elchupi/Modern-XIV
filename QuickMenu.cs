using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;

namespace noWickyXIV;

// Floating bottom-right launcher pill. Horizontal row of five icon
// buttons that slides up from below the viewport on hover. Visuals
// mirror the MsqTeleport pill family (dark fill, yellow-gold border,
// rounded corners, exp-lerp slide).
//
// Each icon dispatches a single slash command via
// DalamudApi.CommandManager.ProcessCommand.
//
// Icon sources — same imagery the user sees in /xlplugins:
//   /xlplugins   → Dalamud's bundled logo.png from dalamudAssets/<ver>/UIRes
//   /nowickyxiv  → this plugin's own icon.png next to the assembly
//   /glamourer   → Glamourer's IconUrl (resolved from its installed manifest)
//   /penumbra    → Penumbra's IconUrl
//   /vfxedit     → VFXEditor's IconUrl
//
// Third-party icons are downloaded once from the manifest's IconUrl,
// then cached at <pluginConfigDir>/quickmenu_icons/<InternalName>.png.
// First frame after install / first run shows a fallback disc while
// the HTTP fetch lands; every subsequent frame reads from the cache.
public static class QuickMenu
{
    // Layout (unscaled).
    private const float ICON_BOX       = 36f;
    private const float ICON_GAP       = 6f;
    private const float ICON_INSET     = 4f;
    private const float PAD_X          = 10f;
    private const float PAD_Y          = 7f;
    private const float ROUNDING       = 10f;
    private const float BORDER         = 1.5f;
    private const float HIT_STRIP_H    = 22f;   // hit band at bottom when hidden
    private const float MARGIN_X       = 16f;
    private const float MARGIN_Y       = 16f;
    private const float SLIDE_SPEED    = 8f;
    // Tolerance around the hit window so cursor jitter / fast travel
    // toward the icons doesn't slip outside the hit area mid-slide.
    private const float HIT_PAD_TOP    = 12f;   // extra above the resting panel top
    private const float HIT_PAD_LEFT   = 16f;   // extra to the left of the panel
    private const float HIT_PAD_BOTTOM = 8f;    // beyond the screen edge (clamped)

    private enum IconKind { Dalamud, SelfPlugin, InstalledPlugin }

    // (command, plugin internal name, icon source). For Dalamud and
    // SelfPlugin entries the internal name is unused.
    private static readonly (string Cmd, string PluginName, IconKind Kind)[] Entries =
    {
        ("/xlplugins",  "",          IconKind.Dalamud),
        ("/nowickyxiv", "",          IconKind.SelfPlugin),
        ("/glamourer",  "Glamourer", IconKind.InstalledPlugin),
        ("/penumbra",   "Penumbra",  IconKind.InstalledPlugin),
        ("/vfxedit",    "VFXEditor", IconKind.InstalledPlugin),
    };

    // Public accessor for the PluginUI tab so it can describe rows
    // without duplicating the source-of-truth array.
    public static IReadOnlyList<string> Commands
    {
        get
        {
            var arr = new string[Entries.Length];
            for (int i = 0; i < Entries.Length; i++) arr[i] = Entries[i].Cmd;
            return arr;
        }
    }

    // Resolved on-disk paths per slot. null = "not resolved yet" or
    // "resolution failed" — the next frame's render falls back to a
    // disc until either a download completes or the resolution retries.
    private static readonly string[] _iconPaths = new string[Entries.Length];
    private static readonly bool[]   _resolveAttempted = new bool[Entries.Length];
    private static readonly bool[]   _fetchInFlight    = new bool[Entries.Length];

    // 0 = parked below the viewport, 1 = fully revealed at rest.
    private static float _revealT;
    private static bool  _hovered;

    // Per-icon hover lerp (smooth transitions instead of hard on/off).
    private static readonly float[] _iconHoverT = new float[16];

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnableQuickMenu) return;
        if (!DalamudApi.ClientState.IsLoggedIn) return;

        var io = ImGui.GetIO();
        var disp = io.DisplaySize;
        if (disp.X <= 0 || disp.Y <= 0) return;

        float dt    = io.DeltaTime;
        float scale = ImGuiHelpers.GlobalScale;

        float iconBox    = noWickyXIV.Config.QuickMenuIconSize * scale;
        float iconGap    = noWickyXIV.Config.QuickMenuIconGap  * scale;
        float iconIns    = ICON_INSET   * scale;
        float padX       = noWickyXIV.Config.QuickMenuPadX     * scale;
        float padY       = noWickyXIV.Config.QuickMenuPadY     * scale;
        float rounding   = ROUNDING     * scale;
        float border     = BORDER       * scale;
        float stripH     = HIT_STRIP_H  * scale;
        float marginX    = MARGIN_X     * scale;
        float marginY    = MARGIN_Y     * scale;
        float hitPadTop  = HIT_PAD_TOP  * scale;
        float hitPadLeft = HIT_PAD_LEFT * scale;

        int   n      = Entries.Length;
        float panelW = padX * 2f + n * iconBox + (n - 1) * iconGap;
        float panelH = padY * 2f + iconBox;

        // Corner anchor: offsets pulled from config so the user can
        // place the launcher in any screen corner. Slide direction is
        // implicit from the chosen vertical edge — top corners drop
        // in, bottom corners rise.
        var corner = noWickyXIV.Config.QuickMenuCorner;
        marginX = noWickyXIV.Config.QuickMenuOffsetX * scale;
        marginY = noWickyXIV.Config.QuickMenuOffsetY * scale;
        bool atRight  = corner == ScreenCorner.TopRight    || corner == ScreenCorner.BottomRight;
        bool atBottom = corner == ScreenCorner.BottomLeft  || corner == ScreenCorner.BottomRight
                     || corner == ScreenCorner.BottomCenter;
        bool atCenter = corner == ScreenCorner.TopCenter   || corner == ScreenCorner.BottomCenter;
        bool atTop    = corner == ScreenCorner.TopLeft     || corner == ScreenCorner.TopRight
                     || corner == ScreenCorner.TopCenter;

        float panelLeft;
        if (atCenter)
            panelLeft = (disp.X - panelW) * 0.5f + marginX;
        else if (atRight)
            panelLeft = disp.X - marginX - panelW;
        else
            panelLeft = marginX;
        float panelRight = panelLeft + panelW;

        float restingTop = atBottom
            ? (disp.Y - marginY - panelH)
            : marginY;
        float hiddenTop  = atBottom ? disp.Y : -panelH;
        float panelTop   = hiddenTop + (restingTop - hiddenTop) * _revealT;
        float panelBot   = panelTop + panelH;

        float hitTop, hitBot;
        if (atBottom)
        {
            hitTop = _revealT <= 0f ? disp.Y - stripH : restingTop - hitPadTop;
            hitBot = disp.Y;
        }
        else
        {
            hitTop = 0f;
            hitBot = _revealT <= 0f ? stripH : (restingTop + panelH + hitPadTop);
        }
        if (hitTop < 0f) hitTop = 0f;
        if (hitBot > disp.Y) hitBot = disp.Y;
        float hitH = MathF.Max(1f, hitBot - hitTop);

        float hitLeft, hitRight;
        if (atCenter)
        {
            hitLeft  = _revealT > 0f ? panelLeft - hitPadLeft : panelLeft;
            hitRight = _revealT > 0f ? panelRight + hitPadLeft : panelRight;
        }
        else if (atRight)
        {
            hitLeft  = _revealT > 0f ? panelLeft - hitPadLeft : panelLeft;
            hitRight = disp.X;
        }
        else
        {
            hitLeft  = 0f;
            hitRight = _revealT > 0f ? panelRight + hitPadLeft : panelRight;
        }
        if (hitLeft < 0f) hitLeft = 0f;
        if (hitRight > disp.X) hitRight = disp.X;
        float hitW = hitRight - hitLeft;
        if (hitW < panelW) hitW = panelW;

        var hitFlags = ImGuiWindowFlags.NoDecoration
                     | ImGuiWindowFlags.NoNav
                     | ImGuiWindowFlags.NoFocusOnAppearing
                     | ImGuiWindowFlags.NoMove
                     | ImGuiWindowFlags.NoSavedSettings
                     | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.SetNextWindowPos(new Vector2(hitLeft, hitTop));
        ImGui.SetNextWindowSize(new Vector2(hitW, hitH));
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

        float target = _hovered ? 1f : 0f;
        float k = 1f - MathF.Exp(-SLIDE_SPEED * dt);
        _revealT += (target - _revealT) * k;
        if (_revealT < 0.005f && !_hovered) _revealT = 0f;
        if (_revealT > 0.995f && _hovered)  _revealT = 1f;

        if (_revealT <= 0f) return;

        float alpha = _revealT;
        var dl = ImGui.GetForegroundDrawList();

        var uiBg = noWickyXIV.Config.UiColorBackground;
        uint bgCol = PackRgba(uiBg.X, uiBg.Y, uiBg.Z, uiBg.W * alpha);
        dl.AddRectFilled(
            new Vector2(panelLeft, panelTop),
            new Vector2(panelRight, panelBot),
            bgCol, rounding);

        var uiBord = noWickyXIV.Config.UiColorBorder;
        uint borderCol = PackRgba(uiBord.X, uiBord.Y, uiBord.Z, uiBord.W * alpha);
        dl.AddRect(
            new Vector2(panelLeft, panelTop),
            new Vector2(panelRight, panelBot),
            borderCol, rounding, ImDrawFlags.None, border);

        int clickedIndex = -1;
        for (int i = 0; i < n; i++)
        {
            float boxLeft  = panelLeft + padX + i * (iconBox + iconGap);
            float boxTop   = panelTop  + padY;
            float boxRight = boxLeft + iconBox;
            float boxBot   = boxTop  + iconBox;

            bool iconHover = _revealT > 0.5f && windowHovered
                           && mp.X >= boxLeft && mp.X < boxRight
                           && mp.Y >= boxTop  && mp.Y < boxBot;

            // Smooth hover transition (exp-lerp, same rate as teleport rows).
            float hTarget = iconHover ? 1f : 0f;
            float hK = 1f - MathF.Exp(-12f * dt);
            _iconHoverT[i] += (hTarget - _iconHoverT[i]) * hK;
            if (_iconHoverT[i] < 0.005f) _iconHoverT[i] = 0f;
            if (_iconHoverT[i] > 0.995f) _iconHoverT[i] = 1f;

            if (_iconHoverT[i] > 0f)
            {
                var uiHov = noWickyXIV.Config.UiColorHover;
                uint hoverBg = PackRgba(uiHov.X, uiHov.Y, uiHov.Z, uiHov.W * 0.35f * alpha * _iconHoverT[i]);
                dl.AddRectFilled(
                    new Vector2(boxLeft, boxTop),
                    new Vector2(boxRight, boxBot),
                    hoverBg, rounding * 0.5f);
            }
            if (iconHover && mouseClicked) clickedIndex = i;

            var iconTL = new Vector2(boxLeft + iconIns, boxTop + iconIns);
            var iconBR = new Vector2(boxRight - iconIns, boxBot - iconIns);
            DrawIcon(dl, i, iconTL, iconBR, alpha);
        }

        if (clickedIndex >= 0)
        {
            try { DalamudApi.CommandManager.ProcessCommand(Entries[clickedIndex].Cmd); }
            catch (Exception ex)
            {
                DalamudApi.LogInfo($"[QuickMenu] ProcessCommand failed: {ex.Message}");
            }
        }
    }

    // Renders the icon image for the given slot. If the path isn't
    // resolved yet, kicks off resolution (which may include an async
    // HTTP download) and shows a labelled fallback disc in the meantime.
    private static void DrawIcon(ImDrawListPtr dl, int slot,
                                  Vector2 tl, Vector2 br, float alpha)
    {
        if (!_resolveAttempted[slot])
            TryResolveIconPath(slot);

        string path = _iconPaths[slot];
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var wrap = DalamudApi.TextureProvider?.GetFromFile(path);
                if (wrap != null)
                {
                    var tex = wrap.GetWrapOrEmpty();
                    var uiTxt = noWickyXIV.Config.UiColorText;
                    uint tint = PackRgba(uiTxt.X, uiTxt.Y, uiTxt.Z, uiTxt.W * alpha);
                    dl.AddImage(tex.Handle, tl, br,
                        Vector2.Zero, Vector2.One, tint);
                    return;
                }
            }
            catch { /* fall through */ }
        }

        // Fallback — small disc + numeric label so an unresolved slot
        // is still distinguishable.
        Vector2 c = (tl + br) * 0.5f;
        float r = MathF.Min(br.X - tl.X, br.Y - tl.Y) * 0.4f;
        var uiBg2 = noWickyXIV.Config.UiColorBackground;
        uint discCol = PackRgba(uiBg2.X + 0.1f, uiBg2.Y + 0.1f, uiBg2.Z + 0.15f, 0.85f * alpha);
        dl.AddCircleFilled(c, r, discCol);
        string lbl = (slot + 1).ToString();
        var sz = ImGui.CalcTextSize(lbl);
        var uiTxt2 = noWickyXIV.Config.UiColorText;
        dl.AddText(new Vector2(c.X - sz.X * 0.5f, c.Y - sz.Y * 0.5f),
            PackRgba(uiTxt2.X, uiTxt2.Y, uiTxt2.Z, uiTxt2.W * alpha), lbl);
    }

    private static void TryResolveIconPath(int slot)
    {
        _resolveAttempted[slot] = true;
        var entry = Entries[slot];

        try
        {
            // Config icon URL override — if the user pasted a URL for
            // this slot, download from it instead of the default path.
            var urls = noWickyXIV.Config.QuickMenuIconUrls;
            if (urls != null && slot < urls.Length && !string.IsNullOrWhiteSpace(urls[slot]))
            {
                var cacheDir = Path.Combine(
                    DalamudApi.PluginInterface.GetPluginConfigDirectory(),
                    "quickmenu_icons");
                var cacheFile = Path.Combine(cacheDir, $"custom_{slot}.png");
                if (File.Exists(cacheFile)) { _iconPaths[slot] = cacheFile; return; }
                if (!_fetchInFlight[slot])
                {
                    _fetchInFlight[slot] = true;
                    _ = FetchIconAsync(urls[slot], cacheDir, cacheFile, slot);
                }
                return;
            }

            switch (entry.Kind)
            {
                case IconKind.SelfPlugin:
                {
                    var asm = DalamudApi.PluginInterface.AssemblyLocation;
                    var dir = asm?.DirectoryName;
                    if (dir == null) return;
                    var p = Path.Combine(dir, "icon.png");
                    if (File.Exists(p)) _iconPaths[slot] = p;
                    return;
                }

                case IconKind.Dalamud:
                {
                    // dalamudAssets/<ver>/UIRes/logo.png — pick the
                    // newest version folder so we ride future updates.
                    var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var assetsRoot = Path.Combine(roaming, "XIVLauncher", "dalamudAssets");
                    if (!Directory.Exists(assetsRoot)) return;

                    string newest = null;
                    int newestNum = -1;
                    foreach (var d in Directory.GetDirectories(assetsRoot))
                    {
                        var name = Path.GetFileName(d);
                        if (int.TryParse(name, out int num) && num > newestNum)
                        {
                            newestNum = num;
                            newest = d;
                        }
                    }
                    if (newest == null) return;

                    var logo = Path.Combine(newest, "UIRes", "logo.png");
                    if (File.Exists(logo)) _iconPaths[slot] = logo;
                    return;
                }

                case IconKind.InstalledPlugin:
                {
                    string pluginName = entry.PluginName;
                    var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var pluginRoot = Path.Combine(roaming, "XIVLauncher", "installedPlugins", pluginName);
                    if (!Directory.Exists(pluginRoot)) return;

                    // Latest version folder = lexicographically largest
                    // (works fine for "1.6.10.13" vs "1.6.10.12" within
                    // a given major series; if the user has multiple
                    // installs, we'd ideally compare as System.Version
                    // but a single-version install is the norm).
                    var versionDir = Directory.GetDirectories(pluginRoot)
                        .OrderByDescending(d => d, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (versionDir == null) return;

                    // (a) Loose-shipped icon — some plugins put one
                    //     directly in their install root.
                    foreach (var rel in new[] { "icon.png", "images/icon.png", "Media/icon.png", "Media/Images/icon.png" })
                    {
                        var probe = Path.Combine(versionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(probe)) { _iconPaths[slot] = probe; return; }
                    }

                    // (b) Cached download — if we've fetched IconUrl
                    //     on a previous run, the file is already here.
                    var cacheDir = Path.Combine(
                        DalamudApi.PluginInterface.GetPluginConfigDirectory(),
                        "quickmenu_icons");
                    var cacheFile = Path.Combine(cacheDir, pluginName + ".png");
                    if (File.Exists(cacheFile)) { _iconPaths[slot] = cacheFile; return; }

                    // (c) Read the IconUrl from the plugin's manifest
                    //     and queue an async download into the cache.
                    var manifest = Path.Combine(versionDir, pluginName + ".json");
                    if (!File.Exists(manifest)) return;

                    string iconUrl = null;
                    try
                    {
                        using var fs = File.OpenRead(manifest);
                        using var doc = JsonDocument.Parse(fs);
                        if (doc.RootElement.TryGetProperty("IconUrl", out var iurl)
                            && iurl.ValueKind == JsonValueKind.String)
                            iconUrl = iurl.GetString();
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(iconUrl)) return;

                    if (!_fetchInFlight[slot])
                    {
                        _fetchInFlight[slot] = true;
                        _ = FetchIconAsync(iconUrl, cacheDir, cacheFile, slot);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[QuickMenu] icon resolve failed for slot {slot}: {ex.Message}");
        }
    }

    private static async Task FetchIconAsync(string url, string cacheDir, string dest, int slot)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
            _iconPaths[slot] = dest;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[QuickMenu] icon download failed: {ex.Message}");
            // Allow a future re-resolve attempt — clear the
            // "already attempted" flag so the user retoggling the
            // feature or relaunching can try again.
            _resolveAttempted[slot] = false;
        }
        finally
        {
            _fetchInFlight[slot] = false;
        }
    }

    /// <summary>Manually re-resolve all icon paths — useful after the
    /// user installs a missing plugin and wants the icon to populate
    /// without restarting.</summary>
    public static void ReresolveAllIcons()
    {
        for (int i = 0; i < _resolveAttempted.Length; i++)
        {
            _resolveAttempted[i] = false;
            _iconPaths[i] = null;
        }
        // Clear custom icon cache so re-resolve picks up new URLs.
        try
        {
            var cacheDir = Path.Combine(
                DalamudApi.PluginInterface.GetPluginConfigDirectory(),
                "quickmenu_icons");
            for (int i = 0; i < Entries.Length; i++)
            {
                var f = Path.Combine(cacheDir, $"custom_{i}.png");
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch { }
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
