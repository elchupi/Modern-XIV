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
using AgentMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap;

namespace noWickyXIV;

public static unsafe class Compass
{
    private static float _alpha;
    private static bool  _lastAnchorBottom;
    private static bool  _anchorFadingOut;

    private static TargetState _tgt;
    private static TargetState _ftgt;

    private struct TargetState
    {
        public ulong Id;
        public float Scale;
        public float Alpha;
        public Vector3 LastPos;
        public uint LastIcon;
        public bool Active;
    }

    private static readonly Dictionary<uint, ISharedImmediateTexture?> _iconCache = new();

    // Per-marker animated state — covers EVERY marker we draw on the
    // compass (waymarks, party, fates, aetherytes, NPC quest markers).
    // Keyed by a string namespace + id so the same logical entity keeps
    // its alpha across frames; fade-in on first sighting, fade-out from
    // last known position when the marker stops appearing. Eliminates
    // pop-in/pop-out for all marker classes.
    private sealed class MarkerState
    {
        public float    Alpha;          // 0..1, eased toward 1 while seen, 0 while not
        public Vector3  LastPos;
        public uint     IconId;
        public uint     OverrideColor;
        public string   LabelFallback;
    }
    private static readonly Dictionary<string, MarkerState> _markerStates = new();
    private static readonly HashSet<string> _markerSeenThisFrame = new();
    private static float _markerFadeK;   // exp-decay factor cached per frame

    private static void BeginMarkerPass(float dt, Configuration cfg)
    {
        float fadeRate = MathF.Max(0.5f, cfg.CompassFadeSpeed);
        _markerFadeK = 1f - MathF.Exp(-fadeRate * dt);
        _markerSeenThisFrame.Clear();
    }

    // Track + fade a marker by key, then defer to DrawIconAtBearing.
    // Pass-through fields match DrawIconAtBearing exactly; the only
    // added work is alpha lookup + last-pos record.
    private static void DrawTrackedMarker(
        string key,
        ImDrawListPtr dl, Vector2 center, Configuration cfg,
        float camYaw, Vector3 ppos, Vector3 wpos,
        uint iconId, string labelFallback, uint overrideColor = 0)
    {
        _markerSeenThisFrame.Add(key);
        if (!_markerStates.TryGetValue(key, out var ms))
        {
            ms = new MarkerState { Alpha = 0f };
            _markerStates[key] = ms;
        }
        ms.LastPos        = wpos;
        ms.IconId         = iconId;
        ms.OverrideColor  = overrideColor;
        ms.LabelFallback  = labelFallback;
        ms.Alpha         += (1f - ms.Alpha) * _markerFadeK;

        DrawIconAtBearing(dl, center, cfg, camYaw, ppos, wpos, iconId, labelFallback,
                          overrideColor: overrideColor, alphaMul: ms.Alpha);
    }

    // After all marker passes have run, draw + fade out any tracked
    // markers that weren't seen this frame (NPC despawned, fate ended,
    // party member dropped, etc.). Drop them once they're invisible.
    private static void EndMarkerPass(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                       float camYaw, Vector3 ppos)
    {
        if (_markerStates.Count == 0) return;
        List<string> toRemove = null;
        foreach (var kv in _markerStates)
        {
            if (_markerSeenThisFrame.Contains(kv.Key)) continue;
            var ms = kv.Value;
            ms.Alpha += (0f - ms.Alpha) * _markerFadeK;
            if (ms.Alpha < 0.01f)
            {
                (toRemove ??= new List<string>()).Add(kv.Key);
                continue;
            }
            DrawIconAtBearing(dl, center, cfg, camYaw, ppos, ms.LastPos,
                              ms.IconId, ms.LabelFallback,
                              overrideColor: ms.OverrideColor, alphaMul: ms.Alpha);
        }
        if (toRemove != null)
            foreach (var k in toRemove) _markerStates.Remove(k);
    }

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

        // Capture targets and animate scale/alpha
        var targetObj = cfg.CompassShowTarget ? DalamudApi.TargetManager?.Target : null;
        var focusObj  = cfg.CompassShowFocusTarget ? DalamudApi.TargetManager?.FocusTarget : null;
        UpdateTargetState(ref _tgt, targetObj, 1.4f, dt);
        UpdateTargetState(ref _ftgt, focusObj, 1.3f, dt);

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
            bool bottom = _lastAnchorBottom;

            var dl = ImGui.GetForegroundDrawList();

            // Layer 0: bar background + center chevron
            DrawBarBackground(dl, center, cfg);
            DrawCenterChevron(dl, center, cfg, bottom);

            // Layer 1: cardinals
            if (cfg.CompassShowCardinals) DrawCardinals(dl, center, cfg, camYaw, bottom);

            // Layer 2: world icons (non-selected only). All marker
            // classes go through DrawTrackedMarker so they fade in on
            // first appearance and fade out from last known position
            // when they stop being seen — no pop-in / pop-out anywhere.
            BeginMarkerPass(dt, cfg);
            if (cfg.CompassShowWaymarks)   DrawWaymarks(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowParty)      DrawParty(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowFates)      DrawFates(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowAetherytes) DrawAetherytes(dl, center, cfg, camYaw, ppos);
            if (cfg.CompassShowMsqMarkers || cfg.CompassShowSideQuestMarkers || cfg.CompassShowUnlockQuestMarkers)
            {
                // Two passes: nearby NPCs (ObjectTable) AND the map agent's
                // marker list (covers distant quest targets whose NPC isn't
                // loaded into ObjectTable). DrawTrackedMarker dedupes by key
                // so a marker present in both sources renders once.
                DrawNpcMarkers(dl, center, cfg, camYaw, ppos);
                DrawMapMarkers(dl, center, cfg, camYaw, ppos);
            }
            EndMarkerPass(dl, center, cfg, camYaw, ppos);

            // Layer 3: target indicators on top of everything (keep drawing during fade-out)
            if (_ftgt.Alpha > 0.01f)
                DrawTargetOverlay(dl, center, cfg, camYaw, ppos, ref _ftgt, isFocus: true);
            if (_tgt.Alpha > 0.01f)
                DrawTargetOverlay(dl, center, cfg, camYaw, ppos, ref _tgt, isFocus: false);
        }
        catch { }
    }

    public static void Dispose()
    {
        _iconCache.Clear();
    }

    // ---------- Structural elements ----------

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

    private static void DrawCenterChevron(ImDrawListPtr dl, Vector2 center, Configuration cfg, bool bottom)
    {
        float halfH = cfg.CompassHeight * 0.5f;
        float s = 6f;
        float thick = 1.5f;
        float yOff = cfg.CompassChevronOffsetY;
        uint col = PackColor(cfg.CompassTickColorR, cfg.CompassTickColorG,
                             cfg.CompassTickColorB, cfg.CompassTickColorA * _alpha);

        if (bottom)
        {
            float baseY = center.Y + halfH + yOff;
            var tip   = new Vector2(center.X, baseY - 2f);
            var left  = new Vector2(center.X - s, baseY + s);
            var right = new Vector2(center.X + s, baseY + s);
            dl.AddLine(left, tip, col, thick);
            dl.AddLine(tip, right, col, thick);
        }
        else
        {
            float baseY = center.Y - halfH - yOff;
            var tip   = new Vector2(center.X, baseY + 2f);
            var left  = new Vector2(center.X - s, baseY - s);
            var right = new Vector2(center.X + s, baseY - s);
            dl.AddLine(left, tip, col, thick);
            dl.AddLine(tip, right, col, thick);
        }
    }

    // ---------- Data sources ----------

    private static void DrawCardinals(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                       float camYaw, bool bottom)
    {
        DrawTick(dl, center, cfg, camYaw, bottom, MathF.PI,         "N", primary: true);
        DrawTick(dl, center, cfg, camYaw, bottom, 0f,               "S", primary: true);
        DrawTick(dl, center, cfg, camYaw, bottom, MathF.PI * 0.5f,  "E", primary: true);
        DrawTick(dl, center, cfg, camYaw, bottom, -MathF.PI * 0.5f, "W", primary: true);
        DrawTick(dl, center, cfg, camYaw, bottom,  MathF.PI * 0.75f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw, bottom,  MathF.PI * 0.25f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw, bottom, -MathF.PI * 0.25f, null, primary: false);
        DrawTick(dl, center, cfg, camYaw, bottom, -MathF.PI * 0.75f, null, primary: false);
    }

    private static void DrawTick(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                  float camYaw, bool bottom, float worldBearing,
                                  string? label, bool primary)
    {
        float rel = BearingToRel(worldBearing, camYaw);
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
            float labelY;
            if (bottom)
                labelY = center.Y - halfH - sz.Y - 2f;
            else
                labelY = center.Y + halfH + 2f;
            var p = new Vector2(MathF.Round(x - sz.X * 0.5f), labelY);
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
            DrawTrackedMarker("waymark:" + i,
                              dl, center, cfg, camYaw, ppos, wpos, iconId,
                              labelFallback: i < 4 ? ((char)('A' + i)).ToString()
                                                    : (i - 3).ToString());
        }
    }

    private static void UpdateTargetState(ref TargetState st,
                                            Dalamud.Game.ClientState.Objects.Types.IGameObject? obj,
                                            float maxScale, float dt)
    {
        float lerpRate = 1f - MathF.Exp(-10f * dt);
        bool hasTarget = obj != null;

        if (hasTarget)
        {
            ulong id = obj!.GameObjectId;
            st.Active = true;
            st.Id = id;
            st.LastPos = new Vector3(obj.Position.X, obj.Position.Y, obj.Position.Z);
            st.Alpha += (1f - st.Alpha) * lerpRate;
            st.Scale += (maxScale - st.Scale) * lerpRate;

            // Resolve icon
            try
            {
                if (obj.ObjectKind == ObjectKind.Aetheryte)
                {
                    st.LastIcon = 60453u;
                    var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                    var row = sheet?.GetRowOrDefault(obj.BaseId);
                    if (row != null && !row.Value.IsAetheryte)
                        st.LastIcon = 60430u;
                }
                else
                {
                    var gop = (GameObject*)obj.Address;
                    if (gop != null && gop->NamePlateIconId != 0)
                        st.LastIcon = gop->NamePlateIconId;
                    else if (st.LastIcon == 0)
                        QuestMarkerHider.HiddenIcons.TryGetValue(id, out st.LastIcon);
                }
            }
            catch { }
        }
        else
        {
            st.Alpha += (0f - st.Alpha) * lerpRate;
            st.Scale += (1f - st.Scale) * lerpRate;
            if (st.Alpha < 0.01f)
            {
                st.Active = false;
                st.Id = 0;
                st.LastIcon = 0;
            }
        }
    }

    private static void DrawTargetOverlay(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                           float camYaw, Vector3 ppos,
                                           ref TargetState st, bool isFocus)
    {
        Vector3 wpos = st.LastPos;

        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (distSq > maxR * maxR) return;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = BearingToRel(worldBearing, camYaw);
        rel = DampenProximity(rel, distSq);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float a = _alpha * edgeAlpha * st.Alpha;
        uint baseColor = isFocus ? 0xFFFFA040u : 0xFF40A0FFu;
        float size = cfg.CompassIconSize * st.Scale;

        var tl = new Vector2(x - size * 0.5f, center.Y - size * 0.5f);
        var br = new Vector2(x + size * 0.5f, center.Y + size * 0.5f);

        // No icon = don't show on compass
        if (st.LastIcon == 0) return;

        var wrap = GetIcon(st.LastIcon);
        if (wrap == null) return;

        try
        {
            var tex = wrap.GetWrapOrEmpty();

            // Image glow: draw the icon at offsets tinted light yellow
            float gs = 3f * st.Scale;
            for (int pass = 0; pass < 2; pass++)
            {
                float off = pass == 0 ? gs : gs * 0.5f;
                uint pt = PackColor(1f, 1f, 0.7f, a * (pass == 0 ? 0.3f : 0.5f));
                dl.AddImage(tex.Handle, tl + new Vector2(-off, 0), br + new Vector2(-off, 0), Vector2.Zero, Vector2.One, pt);
                dl.AddImage(tex.Handle, tl + new Vector2(off, 0),  br + new Vector2(off, 0),  Vector2.Zero, Vector2.One, pt);
                dl.AddImage(tex.Handle, tl + new Vector2(0, -off), br + new Vector2(0, -off), Vector2.Zero, Vector2.One, pt);
                dl.AddImage(tex.Handle, tl + new Vector2(0, off),  br + new Vector2(0, off),  Vector2.Zero, Vector2.One, pt);
            }

            // Actual icon on top
            uint tint = PackColor(1f, 1f, 1f, a);
            dl.AddImage(tex.Handle, tl, br, Vector2.Zero, Vector2.One, tint);
        }
        catch { }

        // Altitude indicator at top-right — was previously a hardcoded
        // UP chevron used purely as target decoration, which read as a
        // bogus altitude hint. Now driven by real dy so it disappears
        // when target is at roughly the same elevation.
        DrawAltitudeArrow(dl, x, center.Y, size, wpos.Y - ppos.Y, a);
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
            if ((ulong)p.EntityId == selfId) continue;
            var pos = p.Position;
            Vector3 wpos = new(pos.X, pos.Y, pos.Z);
            DrawTrackedMarker("party:" + p.EntityId,
                              dl, center, cfg, camYaw, ppos, wpos, 0, "P",
                              overrideColor: 0xFF40FF80u);
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
            uint icon = (uint)f.IconId;
            // Skip FATEs without an icon — the previous "draw a yellow
            // circle as fallback" path made every iconless FATE in the
            // zone render a circle on the compass once distance gates
            // were lifted. Better to omit them than spam circles.
            if (icon == 0) continue;
            Vector3 wpos = new(f.Position.X, f.Position.Y, f.Position.Z);
            string key = "fate:" + f.FateId;
            DrawTrackedMarker(key, dl, center, cfg, camYaw, ppos, wpos, icon, null);
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
                var row = sheet?.GetRowOrDefault(o.BaseId);
                if (row != null && !row.Value.IsAetheryte)
                    icon = 60430u;
            }
            catch { }
            DrawTrackedMarker("aether:" + o.GameObjectId,
                              dl, center, cfg, camYaw, ppos, wpos, icon, null);
        }
    }

    // Read quest markers from AgentMap.EventMarkers. This is the source
    // the map UI uses for "event" markers (quest givers, turn-ins,
    // objectives, leves), independent of whether the NPC is currently
    // loaded into ObjectTable. ObjectTable-only iteration (DrawNpcMarkers)
    // misses far-off quest targets because FFXIV streams NPCs in/out
    // by distance; EventMarkers stays populated as long as the agent
    // is tracking the quest.
    private static double _lastMarkerDumpT;
    private static void DrawMapMarkers(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                        float camYaw, Vector3 ppos)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        try
        {
            double now = System.DateTime.UtcNow.Ticks / (double)System.TimeSpan.TicksPerSecond;
            if (now - _lastMarkerDumpT > 2.0)
            {
                _lastMarkerDumpT = now;
                var sb = new System.Text.StringBuilder();
                sb.Append(System.DateTime.Now.ToString("HH:mm:ss.fff")).Append('\n');
                sb.Append("PlayerPos = (").Append(ppos.X.ToString("F1")).Append(", ")
                  .Append(ppos.Y.ToString("F2")).Append(", ")
                  .Append(ppos.Z.ToString("F1")).Append(")\n");
                uint curMap = agent->CurrentMapId;
                sb.Append("CurrentMapId=").Append(curMap).Append('\n');
                var evDiag = agent->EventMarkers;
                sb.Append("EventMarkers count=").Append(evDiag.Count).Append('\n');
                for (int i = 0; i < evDiag.Count; i++)
                {
                    var d = evDiag[i];
                    uint ic = d.IconId;
                    bool dIsMsq    = ic == 71005 || ic == 71201 || ic == 71001 || ic == 71003;
                    bool dIsUnlock = ic >= 71241 && ic <= 71259;
                    bool dIsSide   = ic >= 71001 && ic <= 71999 && !dIsMsq && !dIsUnlock;
                    string clazz = dIsMsq ? "MSQ"
                                 : dIsUnlock ? "UNLOCK"
                                 : dIsSide   ? "SIDE"
                                 : "UNCLASSIFIED";
                    bool wrongMap = d.MapId != 0 && d.MapId != curMap;
                    bool fatePin  = d.MarkerType == 1;
                    string flags  = (wrongMap ? " [wrong-map]" : "")
                                  + (fatePin  ? " [fate-pin]"  : "");
                    float dyDiag = d.Position.Y - ppos.Y;
                    sb.Append("  [").Append(i).Append("] icon=").Append(ic)
                      .Append(" pos=(").Append(d.Position.X.ToString("F1")).Append(",")
                      .Append(d.Position.Y.ToString("F2")).Append(",")
                      .Append(d.Position.Z.ToString("F1")).Append(")")
                      .Append(" dy=").Append(dyDiag.ToString("+0.00;-0.00;0"))
                      .Append(" mtype=").Append(d.MarkerType)
                      .Append(" estate=").Append(d.EventState)
                      .Append(" mapId=").Append(d.MapId)
                      .Append(" -> ").Append(clazz).Append(flags)
                      .Append('\n');
                }
                var ot = DalamudApi.ObjectTable;
                if (ot != null)
                {
                    sb.Append("=== Nearby NPC nameplate icons ===\n");
                    foreach (var o in ot)
                    {
                        if (o == null) continue;
                        if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                         && o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc) continue;
                        var gop = (GameObject*)o.Address;
                        if (gop == null) continue;
                        uint nicon = gop->NamePlateIconId;
                        if (nicon == 0) continue;
                        float dx = (float)o.Position.X - ppos.X;
                        float dz = (float)o.Position.Z - ppos.Z;
                        float dist = MathF.Sqrt(dx * dx + dz * dz);
                        float dyNpc = (float)o.Position.Y - ppos.Y;
                        sb.Append("  npc#").Append(o.GameObjectId)
                          .Append(" name=").Append(o.Name?.TextValue ?? "?")
                          .Append(" icon=").Append(nicon)
                          .Append(" pos=(").Append(o.Position.X.ToString("F1")).Append(",")
                          .Append(o.Position.Y.ToString("F2")).Append(",")
                          .Append(o.Position.Z.ToString("F1")).Append(")")
                          .Append(" dy=").Append(dyNpc.ToString("+0.00;-0.00;0"))
                          .Append(" dist=").Append(dist.ToString("F1"))
                          .Append('\n');
                    }
                }
                string path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                    "compass_markers.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
            }
        }
        catch { }

        try
        {
            // Filter to the current map only — EventMarkers can hold
            // entries for distant maps you've been to recently, and
            // without this gate the compass would surface (and never
            // fade) MSQ markers for the wrong zone.
            uint currentMapId = agent->CurrentMapId;

            var ev = agent->EventMarkers;
            int count = ev.Count;
            for (int i = 0; i < count; i++)
            {
                var m = ev[i];
                uint iconId = m.IconId;
                if (iconId == 0) continue;
                if (m.MapId != 0 && m.MapId != currentMapId) continue;

                // Skip MarkerType=1 entries — diag confirmed these are
                // FATE objective pins (icon 60458 at the FATE position),
                // NOT quest objectives. Letting them through made FATEs
                // leak onto the compass even when CompassShowFates was
                // off. DrawFates handles FATEs separately via FateTable
                // when the toggle is enabled.
                if (m.MarkerType == 1) continue;

                // Icon ranges in the 71xxx nameplate block:
                //   71201-71219  MSQ (meteor icon)
                //   71241-71259  Feature / unlock quest (blue +)
                //   everything else in 71xxx = side quest
                // MSQ icons under test:
                //   71005  "ready to turn in" — confirmed.
                //   71201  comet icon on NPC nameplate (close range).
                //   71001  EventMarker variant for far-range MSQ —
                //          testing this run.
                // 71003 — MSQ "next step" pointer for an in-progress
                // quest (directional marker to the next objective; not
                // the green turn-in icon).
                bool isMsq    = iconId == 71005
                             || iconId == 71201
                             || iconId == 71001
                             || iconId == 71003;
                bool isUnlock = iconId >= 71241 && iconId <= 71259;
                bool isSide   = iconId >= 71001 && iconId <= 71999 && !isMsq && !isUnlock;
                if (isMsq    && !cfg.CompassShowMsqMarkers) continue;
                if (isUnlock && !cfg.CompassShowUnlockQuestMarkers) continue;
                if (isSide   && !cfg.CompassShowSideQuestMarkers) continue;
                if (!isMsq && !isUnlock && !isSide) continue;

                // MapMarkerData.Position is world-space (X, Y, Z).
                Vector3 wpos = m.Position;
                // Stable identity — key by ObjectiveId+iconId so the
                // marker keeps its fade-in alpha across frames.
                // Position-based key so a marker that's in BOTH sources
                // (NPC nameplate via ObjectTable + EventMarker for the
                // same quest, common at the boundary of NPC streaming
                // distance) collapses onto a single tracked state — no
                // visible duplicate or fade-in/fade-out doubling.
                // Round to ~5-yalm cell so tiny position diffs between
                // the NPC and its EventMarker (e.g. 0.1 yalm) match.
                string key = "q:" + ((int)(wpos.X / 5f)) + "_" + ((int)(wpos.Z / 5f));
                DrawTrackedMarker(key, dl, center, cfg, camYaw, ppos, wpos, iconId, null);
            }
        }
        catch { /* defensive — AgentMap layout can shift between patches */ }
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

            // Quest icon sub-ranges within the 71xxx nameplate block.
            bool isMsq    = iconId >= 71201 && iconId <= 71219;
            bool isSideNarrow = iconId >= 71221 && iconId <= 71239;
            bool isUnlock = iconId >= 71001 && iconId <= 71999 && !isMsq && !isSideNarrow;
            if (isMsq        && !cfg.CompassShowMsqMarkers) continue;
            if (isSideNarrow && !cfg.CompassShowSideQuestMarkers) continue;
            if (isUnlock     && !cfg.CompassShowUnlockQuestMarkers) continue;
            if (!isMsq       && !isSideNarrow && !isUnlock) continue;

            Vector3 wpos = new(o.Position.X, o.Position.Y, o.Position.Z);
            // Same position-based key DrawMapMarkers uses, so a quest
            // whose NPC streams in/out of ObjectTable doesn't briefly
            // double-render (one entry from NPC nameplate, another from
            // EventMarker). Shared key = single tracked state.
            DrawTrackedMarker("q:" + ((int)(wpos.X / 5f)) + "_" + ((int)(wpos.Z / 5f)),
                              dl, center, cfg, camYaw, ppos, wpos, iconId, null);
        }
    }

    // ---------- Bearing / projection helpers ----------

    private static void DrawIconAtBearing(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                           float camYaw, Vector3 ppos, Vector3 wpos,
                                           uint iconId, string? labelFallback,
                                           uint overrideColor = 0,
                                           float alphaMul = 1f)
    {
        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (distSq > maxR * maxR) return;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = BearingToRel(worldBearing, camYaw);
        rel = DampenProximity(rel, distSq);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float a = _alpha * edgeAlpha * alphaMul;
        float size = cfg.CompassIconSize;
        var tl = new Vector2(x - size * 0.5f, center.Y - size * 0.5f);
        var br = new Vector2(x + size * 0.5f, center.Y + size * 0.5f);
        float dy = wpos.Y - ppos.Y;

        if (overrideColor != 0)
        {
            uint col = MultiplyAlpha(overrideColor, a);
            dl.AddCircleFilled(new Vector2(x, center.Y), size * 0.4f, col, 24);
            if (!string.IsNullOrEmpty(labelFallback))
            {
                var sz = ImGui.CalcTextSize(labelFallback);
                var p = new Vector2(x - sz.X * 0.5f, center.Y - sz.Y * 0.5f);
                dl.AddText(p, PackColor(0f, 0f, 0f, a), labelFallback);
            }
            DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
            return;
        }

        var wrap = iconId != 0 ? GetIcon(iconId) : null;
        if (wrap != null)
        {
            try
            {
                var tex = wrap.GetWrapOrEmpty();
                uint tint = PackColor(1f, 1f, 1f, a);
                dl.AddImage(tex.Handle, tl, br, Vector2.Zero, Vector2.One, tint);
                DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
                return;
            }
            catch { }
        }

        uint discCol = PackColor(cfg.CompassTickColorR, cfg.CompassTickColorG,
                                  cfg.CompassTickColorB, cfg.CompassTickColorA * a);
        dl.AddCircleFilled(new Vector2(x, center.Y), size * 0.4f, discCol, 24);
        if (!string.IsNullOrEmpty(labelFallback))
        {
            var sz = ImGui.CalcTextSize(labelFallback);
            var p = new Vector2(x - sz.X * 0.5f, center.Y - sz.Y * 0.5f);
            dl.AddText(p, 0xFF000000u, labelFallback);
        }
        DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
    }

    // Up/down chevron when the marker is meaningfully above or below
    // the player — small thin two-line glyph anchored to the icon's
    // top-right corner, matching the target-highlight chevron style.
    // 10y matches FFXIV's native compass. Lower thresholds false-trigger
    // on flat ground because EventMarker.Position.Y is the floating-icon
    // anchor above the NPC's head, not the NPC's foot position.
    private const float AltitudeThreshold = 10f; // yalms
    private static void DrawAltitudeArrow(ImDrawListPtr dl, float x, float cy,
                                           float size, float dy, float a)
    {
        if (MathF.Abs(dy) < AltitudeThreshold) return;

        float chevS  = 4f;
        float chevX  = x + size * 0.35f;
        float chevY  = cy - size * 0.35f;
        float thick  = 1.5f;
        uint  col    = PackColor(1f, 1f, 1f, a);

        if (dy > 0f)
        {
            // Chevron pointing up (tip on top): /\
            // Apex sits at (chevX + chevS, chevY); legs splay down-out.
            dl.AddLine(new Vector2(chevX,             chevY + chevS),
                       new Vector2(chevX + chevS,     chevY),         col, thick);
            dl.AddLine(new Vector2(chevX + chevS,     chevY),
                       new Vector2(chevX + chevS * 2, chevY + chevS), col, thick);
        }
        else
        {
            // Chevron pointing down (tip on bottom): \/
            // Apex sits at (chevX + chevS, chevY + chevS); legs splay up-out.
            dl.AddLine(new Vector2(chevX,             chevY),
                       new Vector2(chevX + chevS,     chevY + chevS), col, thick);
            dl.AddLine(new Vector2(chevX + chevS,     chevY + chevS),
                       new Vector2(chevX + chevS * 2, chevY),         col, thick);
        }
    }

    private static bool ProjectToBar(float rel, Configuration cfg, out float x, out float edgeAlpha)
    {
        x = 0f; edgeAlpha = 0f;
        float fovRad = MathF.Max(10f, cfg.CompassFovDegrees) * MathF.PI / 180f;
        float halfFov = fovRad * 0.5f;

        bool clamped = false;
        float clampedRel = rel;
        if (MathF.Abs(rel) > halfFov)
        {
            clampedRel = MathF.Sign(rel) * halfFov;
            clamped = true;
        }

        float t = clampedRel / halfFov;
        var vp = ImGui.GetMainViewport();
        float center = vp.Pos.X + vp.Size.X * 0.5f + cfg.CompassOffsetX;
        x = center + t * cfg.CompassWidth * 0.5f;

        float u = MathF.Abs(t);
        float fadeStart = 1f - MathF.Max(0.001f, cfg.CompassEdgeFadePct * 2f);
        if (clamped)
            edgeAlpha = 0.35f;
        else if (u < fadeStart)
            edgeAlpha = 1f;
        else
            edgeAlpha = MathF.Max(0f, 1f - (u - fadeStart) / (1f - fadeStart));
        return edgeAlpha > 0f;
    }

    private static float BearingToRel(float worldBearing, float camYaw)
    {
        float lookDir = camYaw + MathF.PI;
        return AngleDelta(worldBearing, lookDir);
    }

    private static float AngleDelta(float from, float to)
    {
        float d = to - from;
        while (d >  MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    private static float DampenProximity(float rel, float distSq)
    {
        const float threshold = 3f;
        float threshSq = threshold * threshold;
        if (distSq >= threshSq) return rel;
        float t = MathF.Sqrt(distSq) / threshold;
        return rel * t;
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
