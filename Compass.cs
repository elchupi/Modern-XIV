using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Hypostasis.Game;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace noWickyXIV;

public static unsafe class Compass
{
    private static float _alpha; // 0..1 enable fade
    private static bool  _lastAnchorBottom;
    private static bool  _anchorFadingOut;

    private static readonly Dictionary<uint, ISharedImmediateTexture?> _iconCache = new();

    public static void Update() { }

    public static void Draw()
    {
        float dt;
        try { dt = ImGui.GetIO().DeltaTime; } catch { dt = 0.016f; }
        if (dt <= 0f) dt = 0.016f;

        var cfg = noWickyXIV.Config;

        if (cfg.CompassAnchorBottom != _lastAnchorBottom && !_anchorFadingOut)
            _anchorFadingOut = true;

        bool wantOn = cfg.EnableCompass && !ShouldHide() && !_anchorFadingOut;
        float rate = MathF.Max(0.5f, cfg.CompassFadeSpeed);
        _alpha += ((wantOn ? 1f : 0f) - _alpha) * (1f - MathF.Exp(-rate * dt));

        if (_anchorFadingOut && _alpha < 0.01f)
        {
            _lastAnchorBottom = cfg.CompassAnchorBottom;
            _anchorFadingOut = false;
        }

        if (_alpha < 0.01f) return;

        try
        {
            var cam = Common.CameraManager->worldCamera;
            if (cam == null) return;
            float camYaw = cam->currentHRotation;

            var lp = DalamudApi.ObjectTable?.LocalPlayer;
            if (lp == null) return;
            Vector3 ppos = new(lp.Position.X, lp.Position.Y, lp.Position.Z);

            var vp = ImGui.GetMainViewport();
            float cx = vp.Pos.X + vp.Size.X * 0.5f + cfg.CompassOffsetX;
            float cy;
            if (_lastAnchorBottom)
                cy = vp.Pos.Y + vp.Size.Y - cfg.CompassOffsetY - cfg.CompassHeight * 0.5f;
            else
                cy = vp.Pos.Y + cfg.CompassOffsetY + cfg.CompassHeight * 0.5f;
            Vector2 center = new(cx, cy);

            var dl = ImGui.GetForegroundDrawList();

            DrawBarBackground(dl, center, cfg);
            if (cfg.CompassShowCardinals)    DrawCardinals(dl, center, cfg, camYaw);
            if (cfg.CompassShowWaymarks)     DrawWaymarks(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowParty)        DrawParty(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowFates)        DrawFates(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowAetherytes)   DrawAetherytes(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowMsqMarkers || cfg.CompassShowSideQuestMarkers)
                DrawNpcMarkers(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowFocusTarget)  DrawTarget(dl, center, cfg, camYaw, ppos,
                                                        DalamudApi.TargetManager?.FocusTarget, isFocus: true);
            if (cfg.CompassShowTarget)       DrawTarget(dl, center, cfg, camYaw, ppos,
                                                        DalamudApi.TargetManager?.Target, isFocus: false);
        }
        catch { }
    }

    public static void Dispose()
    {
        _iconCache.Clear();
    }

    // ---------- Drawing ----------

    private static void DrawBarBackground(ImDrawListPtr dl, Vector2 center, Configuration cfg)
    {
        float halfW = cfg.CompassWidth * 0.5f;
        float halfH = cfg.CompassHeight * 0.5f;
        var tl = new Vector2(center.X - halfW, center.Y - halfH);
        var br = new Vector2(center.X + halfW, center.Y + halfH);

        float fadeStart = 1f - MathF.Max(0.001f, cfg.CompassEdgeFadePct * 2f);
        float edgeW = halfW * (1f - fadeStart);
        var cl = new Vector2(tl.X + edgeW, tl.Y);
        var cr = new Vector2(br.X - edgeW, br.Y);
        uint barCol = PackColor(cfg.CompassBarColorR, cfg.CompassBarColorG,
                                cfg.CompassBarColorB, cfg.CompassBarColorA * _alpha);
        uint barColEdge = PackColor(cfg.CompassBarColorR, cfg.CompassBarColorG,
                                    cfg.CompassBarColorB, 0f);
        dl.AddRectFilled(cl, cr, barCol);
        dl.AddRectFilledMultiColor(new Vector2(tl.X, tl.Y), new Vector2(cl.X, br.Y),
            barColEdge, barCol, barCol, barColEdge);
        dl.AddRectFilledMultiColor(new Vector2(cr.X, tl.Y), new Vector2(br.X, br.Y),
            barCol, barColEdge, barColEdge, barCol);
    }

    private static void DrawCardinals(ImDrawListPtr dl, Vector2 center, Configuration cfg, float camYaw)
    {
        DrawTick(dl, center, cfg, camYaw, MathF.PI,         "N", primary: true);
        DrawTick(dl, center, cfg, camYaw, 0f,               "S", primary: true);
        DrawTick(dl, center, cfg, camYaw, MathF.PI * 0.5f,  "E", primary: true);
        DrawTick(dl, center, cfg, camYaw, -MathF.PI * 0.5f, "W", primary: true);
        DrawTick(dl, center, cfg, camYaw,  MathF.PI * 0.75f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw,  MathF.PI * 0.25f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw, -MathF.PI * 0.25f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw, -MathF.PI * 0.75f, null, primary: false);
    }

    private static void DrawTick(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                  float camYaw, float worldBearing, string? label, bool primary)
    {
        float rel = AngleDelta(camYaw, worldBearing);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float halfH = cfg.CompassHeight * 0.5f;
        float tickH = primary ? halfH * 0.85f : halfH * 0.45f;
        var a = new Vector2(x, center.Y - tickH);
        var b = new Vector2(x, center.Y + tickH);
        uint tickCol = PackColor(cfg.CompassTickColorR, cfg.CompassTickColorG,
                                  cfg.CompassTickColorB,
                                  cfg.CompassTickColorA * edgeAlpha * _alpha * (primary ? 1f : 0.55f));
        dl.AddLine(a, b, tickCol, primary ? 1.5f : 1f);

        if (label != null)
        {
            var sz = ImGui.CalcTextSize(label);
            var p = new Vector2(x - sz.X * 0.5f, center.Y - halfH - sz.Y - 2f);
            dl.AddText(p, tickCol, label);
        }
    }

    private static void DrawWaymarks(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                      float camYaw, Vector3 ppos)
    {
        var mc = FFXIVClientStructs.FFXIV.Client.Game.UI.MarkingController.Instance();
        if (mc == null) return;

        var markers = mc->FieldMarkers;
        for (int i = 0; i < 8; i++)
        {
            var m = markers[i];
            if (!m.Active) continue;
            Vector3 wpos = new(m.X / 1000f, m.Y / 1000f, m.Z / 1000f);
            uint iconId = (uint)(61241 + i);
            DrawIconAtBearing(dl, center, cfg, camYaw, ppos, wpos, iconId,
                              labelFallback: i < 4 ? ((char)('A' + i)).ToString()
                                                    : (i - 3).ToString());
        }
    }

    private static void DrawTarget(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                    float camYaw, Vector3 ppos,
                                    Dalamud.Game.ClientState.Objects.Types.IGameObject? obj,
                                    bool isFocus)
    {
        if (obj == null) return;
        Vector3 wpos = new(obj.Position.X, obj.Position.Y, obj.Position.Z);
        DrawChevronAtBearing(dl, center, cfg, camYaw, ppos, wpos,
            isFocus ? 0xFFFFA040u : 0xFF40A0FFu);
    }

    private static void DrawParty(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                   float camYaw, Vector3 ppos)
    {
        var party = DalamudApi.PartyList;
        if (party == null) return;
        ulong selfId = DalamudApi.ObjectTable?.LocalPlayer?.GameObjectId ?? 0UL;
        foreach (var p in party)
        {
            if (p == null) continue;
            if ((ulong)p.ObjectId == selfId) continue;
            var pos = p.Position;
            Vector3 wpos = new(pos.X, pos.Y, pos.Z);
            DrawChevronAtBearing(dl, center, cfg, camYaw, ppos, wpos, 0xFF40FF80u);
        }
    }

    private static void DrawFates(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                   float camYaw, Vector3 ppos)
    {
        var fates = DalamudApi.FateTable;
        if (fates == null) return;
        foreach (var f in fates)
        {
            if (f == null) continue;
            Vector3 wpos = new(f.Position.X, f.Position.Y, f.Position.Z);
            uint icon = (uint)f.IconId;
            if (icon == 0)
                DrawChevronAtBearing(dl, center, cfg, camYaw, ppos, wpos, 0xFFFFFF40u);
            else
                DrawIconAtBearing(dl, center, cfg, camYaw, ppos, wpos, icon, null);
        }
    }

    private static void DrawAetherytes(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                        float camYaw, Vector3 ppos)
    {
        var ot = DalamudApi.ObjectTable;
        if (ot == null) return;
        foreach (var o in ot)
        {
            if (o == null) continue;
            if (o.ObjectKind != ObjectKind.Aetheryte) continue;
            Vector3 wpos = new(o.Position.X, o.Position.Y, o.Position.Z);
            uint icon = 60453u;
            try
            {
                var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                var row = sheet?.GetRowOrDefault(o.DataId);
                if (row != null && !row.Value.IsAetheryte)
                    icon = 60430u;
            }
            catch { }
            DrawIconAtBearing(dl, center, cfg, camYaw, ppos, wpos, icon, null);
        }
    }

    private static void DrawNpcMarkers(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                        float camYaw, Vector3 ppos)
    {
        var ot = DalamudApi.ObjectTable;
        if (ot == null) return;
        foreach (var o in ot)
        {
            if (o == null) continue;
            if (o.ObjectKind != ObjectKind.EventNpc && o.ObjectKind != ObjectKind.BattleNpc) continue;
            var gop = (GameObject*)o.Address;
            if (gop == null) continue;

            uint iconId = gop->NamePlateIconId;
            if (iconId == 0)
                QuestMarkerHider.HiddenIcons.TryGetValue(o.GameObjectId, out iconId);
            if (iconId == 0) continue;

            bool isMsq = iconId >= 71201 && iconId <= 71299;
            bool isSide = iconId >= 71001 && iconId <= 71199;
            if (isMsq && !cfg.CompassShowMsqMarkers) continue;
            if (isSide && !cfg.CompassShowSideQuestMarkers) continue;
            if (!isMsq && !isSide) continue;

            Vector3 wpos = new(o.Position.X, o.Position.Y, o.Position.Z);
            DrawIconAtBearing(dl, center, cfg, camYaw, ppos, wpos, iconId, null);
        }
    }

    // ---------- Bearing / projection helpers ----------

    private static void DrawIconAtBearing(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                           float camYaw, Vector3 ppos, Vector3 wpos,
                                           uint iconId, string? labelFallback)
    {
        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (distSq > maxR * maxR) return;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = AngleDelta(camYaw, worldBearing);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float a = _alpha * edgeAlpha;
        float size = cfg.CompassIconSize;
        var tl = new Vector2(x - size * 0.5f, center.Y - size * 0.5f);
        var br = new Vector2(x + size * 0.5f, center.Y + size * 0.5f);

        var wrap = GetIcon(iconId);
        if (wrap != null)
        {
            try
            {
                var tex = wrap.GetWrapOrEmpty();
                uint tint = PackColor(1f, 1f, 1f, a);
                dl.AddImage(tex.ImGuiHandle, tl, br, Vector2.Zero, Vector2.One, tint);
                return;
            }
            catch { }
        }

        uint discCol = PackColor(cfg.CompassTickColorR, cfg.CompassTickColorG,
                                  cfg.CompassTickColorB, cfg.CompassTickColorA * a);
        dl.AddCircleFilled(new Vector2(x, center.Y), size * 0.4f, discCol);
        if (!string.IsNullOrEmpty(labelFallback))
        {
            var sz = ImGui.CalcTextSize(labelFallback);
            var p = new Vector2(x - sz.X * 0.5f, center.Y - sz.Y * 0.5f);
            dl.AddText(p, 0xFF000000u, labelFallback);
        }
    }

    private static void DrawChevronAtBearing(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                              float camYaw, Vector3 ppos, Vector3 wpos, uint rgba)
    {
        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (distSq > maxR * maxR) return;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = AngleDelta(camYaw, worldBearing);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float a = _alpha * edgeAlpha;
        uint col = MultiplyAlpha(rgba, a);
        float halfH = cfg.CompassHeight * 0.5f;
        float s = cfg.CompassIconSize * 0.5f;
        var p1 = new Vector2(x,         center.Y - halfH + 2f);
        var p2 = new Vector2(x - s,     center.Y - halfH - s);
        var p3 = new Vector2(x + s,     center.Y - halfH - s);
        dl.AddTriangleFilled(p1, p2, p3, col);
    }

    private static bool ProjectToBar(float rel, Configuration cfg, out float x, out float edgeAlpha)
    {
        x = 0f; edgeAlpha = 0f;
        float fovRad = MathF.Max(10f, cfg.CompassFovDegrees) * MathF.PI / 180f;
        if (MathF.Abs(rel) > fovRad * 0.5f) return false;

        float t = rel / (fovRad * 0.5f); // [-1, 1]
        var vp = ImGui.GetMainViewport();
        float center = vp.Pos.X + vp.Size.X * 0.5f + cfg.CompassOffsetX;
        x = center + t * cfg.CompassWidth * 0.5f;

        float u = MathF.Abs(t);
        float fadeStart = 1f - MathF.Max(0.001f, cfg.CompassEdgeFadePct * 2f);
        edgeAlpha = u < fadeStart
            ? 1f
            : MathF.Max(0f, 1f - (u - fadeStart) / (1f - fadeStart));
        return edgeAlpha > 0f;
    }

    private static float AngleDelta(float from, float to)
    {
        float d = to - from;
        while (d >  MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    private static uint PackColor(float r, float g, float b, float a)
        => ImGui.GetColorU32(new Vector4(r, g, b, MathF.Max(0f, MathF.Min(1f, a))));

    private static uint MultiplyAlpha(uint rgba, float mul)
    {
        uint a = (rgba >> 24) & 0xFFu;
        float na = MathF.Max(0f, MathF.Min(1f, (a / 255f) * mul));
        return (rgba & 0x00FFFFFFu) | ((uint)(na * 255f) << 24);
    }

    private static ISharedImmediateTexture? GetIcon(uint iconId)
    {
        if (_iconCache.TryGetValue(iconId, out var cached)) return cached;
        try
        {
            var tex = DalamudApi.TextureProvider?.GetFromGameIcon(new GameIconLookup(iconId));
            _iconCache[iconId] = tex;
            return tex;
        }
        catch
        {
            _iconCache[iconId] = null;
            return null;
        }
    }

    private static bool ShouldHide()
    {
        try
        {
            var cond = DalamudApi.Condition;
            if (cond[ConditionFlag.OccupiedInCutSceneEvent]) return true;
            if (cond[ConditionFlag.WatchingCutscene]) return true;
            if (cond[ConditionFlag.WatchingCutscene78]) return true;
            if (cond[ConditionFlag.OccupiedInQuestEvent]) return true;
        }
        catch { }
        return false;
    }
}
