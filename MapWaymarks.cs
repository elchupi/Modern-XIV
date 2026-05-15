using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace noWickyXIV;

public static unsafe class MapWaymarks
{
    private static bool _menuRegistered;
    private static bool _showWmWindow;
    private static Vector2 _wmWindowPos;
    private static Vector2 _clickScreenPos;

    private const int VK_MBUTTON = 0x04;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private static bool _mmbWasDown;

    public static void Initialize()
    {
        if (_menuRegistered) return;
        try
        {
            DalamudApi.ContextMenu.OnMenuOpened += OnMenuOpened;
            _menuRegistered = true;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[MapWaymarks] Context menu hook failed: {ex.Message}");
        }
    }

    public static void Dispose()
    {
        if (!_menuRegistered) return;
        try { DalamudApi.ContextMenu.OnMenuOpened -= OnMenuOpened; } catch { }
        _menuRegistered = false;
    }

    public static void Update() { }

    public static void Draw()
    {
        try { DetectMapClick(); } catch { }
        DrawWaymarkWindow();
    }

    private static void DrawWaymarkWindow()
    {
        if (!_showWmWindow) return;

        ImGui.SetNextWindowPos(_wmWindowPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(Vector2.Zero);
        bool open = _showWmWindow;
        if (ImGui.Begin("##nwx-map-waymark-window", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextDisabled("Place waymark at map position");
            ImGui.Separator();
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) ImGui.SameLine();
                if (ImGui.Button($" {WaymarkLabel(i)} ##wm-w-{i}", new Vector2(28, 0)))
                {
                    PlaceAtMapClick(i);
                    open = false;
                }
            }
            for (int i = 4; i < 8; i++)
            {
                if (i > 4) ImGui.SameLine();
                if (ImGui.Button($" {WaymarkLabel(i)} ##wm-w-{i}", new Vector2(28, 0)))
                {
                    PlaceAtMapClick(i);
                    open = false;
                }
            }
            ImGui.Separator();
            if (ImGui.Button("Clear all##wm-w-clear"))
            {
                ClearAll();
                open = false;
            }
        }
        ImGui.End();
        _showWmWindow = open;
    }

    private static void DetectMapClick()
    {
        // DISABLED: middle-click map waymark quick-place picker. Code kept for reference.
        return;
        bool mmbDown = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

        if (mmbDown && !_mmbWasDown)
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("AreaMap", 1);
            var addr = wrapper.Address;
            if (addr != IntPtr.Zero)
            {
                var atk = (AtkUnitBase*)addr;
                if (atk->IsVisible && atk->RootNode != null)
                {
                    var mouse = ImGui.GetMousePos();
                    float x0 = atk->X;
                    float y0 = atk->Y;
                    float s  = MathF.Max(0.01f, atk->Scale);
                    float w  = atk->RootNode->Width  * s;
                    float h  = atk->RootNode->Height * s;
                    if (mouse.X >= x0 && mouse.X <= x0 + w &&
                        mouse.Y >= y0 && mouse.Y <= y0 + h)
                    {
                        _showWmWindow = true;
                        _wmWindowPos = mouse;
                        _clickScreenPos = mouse;
                    }
                }
            }
        }

        _mmbWasDown = mmbDown;
    }

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        // DISABLED: waymark context menu items on AreaMap/_MiniMap. Code kept for reference.
        return;
        if (args.AddonName != "AreaMap" && args.AddonName != "_MiniMap") return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Place waymark…",
            PrefixChar = 'n',
            OnClicked = _ => { },
        });
        for (int i = 0; i < 8; i++)
        {
            int captured = i;
            args.AddMenuItem(new MenuItem
            {
                Name = $"   {WaymarkLabel(captured)}",
                PrefixChar = 'n',
                OnClicked = _ => PlaceAtPlayer(captured),
            });
        }
        args.AddMenuItem(new MenuItem
        {
            Name = "   Clear all",
            PrefixChar = 'n',
            OnClicked = _ => ClearAll(),
        });
    }

    public static string WaymarkLabel(int idx)
        => idx switch
        {
            0 => "A", 1 => "B", 2 => "C", 3 => "D",
            4 => "1", 5 => "2", 6 => "3", 7 => "4",
            _ => idx.ToString(),
        };

    private static void PlaceAtMapClick(int idx)
    {
        if (idx < 0 || idx > 7) return;
        try
        {
            var worldPos = ScreenToWorldOnMap(_clickScreenPos);
            if (worldPos.HasValue)
            {
                PlaceAt(idx, worldPos.Value);
                PrintClickableLocation(idx, worldPos.Value);
            }
            else
            {
                PlaceAtPlayer(idx);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[MapWaymarks] PlaceAtMapClick error: {ex.Message}");
            PlaceAtPlayer(idx);
        }
    }

    private static Vector3? ScreenToWorldOnMap(Vector2 screenPos)
    {
        var wrapper = DalamudApi.GameGui.GetAddonByName("AreaMap", 1);
        if (wrapper.Address == IntPtr.Zero) return null;
        var atk = (AtkUnitBase*)wrapper.Address;
        if (!atk->IsVisible || atk->RootNode == null) return null;

        var agentMap = AgentMap.Instance();
        if (agentMap == null) return null;

        uint mapId = agentMap->CurrentMapId;
        var mapSheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
        var mapRow = mapSheet?.GetRowOrDefault(mapId);
        if (mapRow == null) return null;

        float sizeFactor = mapRow.Value.SizeFactor / 100f;
        float offsetX = mapRow.Value.OffsetX;
        float offsetY = mapRow.Value.OffsetY;

        // Find the AtkComponentMap node in the AreaMap addon.
        // Walk the child list looking for a ComponentNode whose component
        // is an AtkComponentMap (has MapScale/MapOffsetX/Y fields).
        AtkComponentMap* mapComp = null;
        AtkResNode* mapNode = null;
        var node = atk->RootNode->ChildNode;
        while (node != null)
        {
            if ((int)node->Type >= 1000)
            {
                var compNode = (AtkComponentNode*)node;
                if (compNode->Component != null)
                {
                    // AtkComponentMap is identified by GetComponentType == 19 (Map)
                    // We read the vtable-dispatched type check.
                    try
                    {
                        var comp = compNode->Component;
                        // Try casting — if MapScale field is at offset 0x374,
                        // we can read it and verify it's a sane value.
                        var candidate = (AtkComponentMap*)comp;
                        float testScale = *(float*)((byte*)candidate + 0x374);
                        if (testScale > 0.1f && testScale < 100f)
                        {
                            mapComp = candidate;
                            mapNode = node;
                            break;
                        }
                    }
                    catch { }
                }
            }
            node = node->PrevSiblingNode;
        }

        if (mapComp == null || mapNode == null) return null;

        float mapScale = *(float*)((byte*)mapComp + 0x374);
        float mapOffX  = *(float*)((byte*)mapComp + 0x384);
        float mapOffY  = *(float*)((byte*)mapComp + 0x388);

        // Compute the map component's screen-space center.
        // The component node has position/size within the addon.
        float addonScale = MathF.Max(0.01f, atk->Scale);
        float nodeScreenX = atk->X + mapNode->X * addonScale;
        float nodeScreenY = atk->Y + mapNode->Y * addonScale;
        float nodeW = mapNode->Width * addonScale * mapNode->ScaleX;
        float nodeH = mapNode->Height * addonScale * mapNode->ScaleY;
        float nodeCenterX = nodeScreenX + nodeW * 0.5f;
        float nodeCenterY = nodeScreenY + nodeH * 0.5f;

        // Screen offset from component center → map texture offset.
        // MapOffsetX/Y are in texture-pixel space (0,0 = texture center = pixel 1024,1024).
        // MapScale: at 1.0 the full 2048px texture fits the component.
        // At 2.0, only half the texture is visible (2x zoom).
        float texPerScreenX = 2048f / (nodeW * mapScale);
        float texPerScreenY = 2048f / (nodeH * mapScale);

        float dx = screenPos.X - nodeCenterX;
        float dy = screenPos.Y - nodeCenterY;

        float texX = 1024f + mapOffX + dx * texPerScreenX;
        float texY = 1024f + mapOffY + dy * texPerScreenY;

        // Texture pixel → world coordinate.
        // Standard formula: pixel = (worldCoord + offset) / 100 * sizeFactor + 1024
        // Inverse: worldCoord = (pixel - 1024) / sizeFactor * 100 - offset
        float worldX = (texX - 1024f) / sizeFactor * 100f - offsetX;
        float worldZ = (texY - 1024f) / sizeFactor * 100f - offsetY;

        // Use player's Y for vertical.
        var lp = DalamudApi.ObjectTable?.LocalPlayer;
        float worldY = lp?.Position.Y ?? 0f;

        return new Vector3(worldX, worldY, worldZ);
    }

    public static void PlaceAtPlayer(int idx)
    {
        if (idx < 0 || idx > 7) return;
        try
        {
            var lp = DalamudApi.ObjectTable?.LocalPlayer;
            if (lp == null) return;
            var pos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);
            PlaceAt(idx, pos);
            PrintClickableLocation(idx, pos);
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[MapWaymarks] PlaceAtPlayer error: {ex.Message}");
        }
    }

    private static void PrintClickableLocation(int idx, Vector3 worldPos)
    {
        try
        {
            uint terrId = DalamudApi.ClientState.TerritoryType;
            if (terrId == 0) return;

            uint mapId = 0;
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            var row = sheet?.GetRowOrDefault(terrId);
            if (row != null) mapId = row.Value.Map.RowId;
            if (mapId == 0) return;

            float mx = ConvertWorldToMap(worldPos.X, mapId);
            float mz = ConvertWorldToMap(worldPos.Z, mapId);

            var mapLink = SeString.CreateMapLink(terrId, mapId, mx, mz);
            var msg = new SeStringBuilder()
                .AddText($"Waymark {WaymarkLabel(idx)} placed: ")
                .Append(mapLink)
                .Build();
            DalamudApi.ChatGui.Print(msg);
        }
        catch { }
    }

    private static float ConvertWorldToMap(float world, uint mapId)
    {
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>();
            var row = sheet?.GetRowOrDefault(mapId);
            if (row != null)
            {
                float scale = row.Value.SizeFactor / 100f;
                float offset = row.Value.OffsetX;
                return (world + offset) * scale / 2048f * 41f / scale + 1f;
            }
        }
        catch { }
        return 0f;
    }

    public static void PlaceAt(int idx, Vector3 worldPos)
    {
        if (idx < 0 || idx > 7) return;
        try
        {
            var mc = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
            if (mc == null) return;
            ref var m = ref mc->FieldMarkers[idx];
            m.X = (int)MathF.Round(worldPos.X * 1000f);
            m.Y = (int)MathF.Round(worldPos.Y * 1000f);
            m.Z = (int)MathF.Round(worldPos.Z * 1000f);
            m.Active = true;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[MapWaymarks] PlaceAt error: {ex.Message}");
        }
    }

    public static void Clear(int idx)
    {
        if (idx < 0 || idx > 7) return;
        try
        {
            var mc = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
            if (mc == null) return;
            mc->FieldMarkers[idx].Active = false;
        }
        catch { }
    }

    public static void ClearAll()
    {
        try
        {
            var mc = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
            if (mc == null) return;
            for (int i = 0; i < 8; i++) mc->FieldMarkers[i].Active = false;
        }
        catch { }
    }
}
