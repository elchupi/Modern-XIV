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
        uint iconId, string labelFallback, uint overrideColor = 0,
        bool skipDistance = false, uint overrideTextColor = 0, float overrideFontSize = 0f)
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
                          overrideColor: overrideColor, alphaMul: ms.Alpha,
                          skipDistance: skipDistance, overrideTextColor: overrideTextColor,
                          overrideFontSize: overrideFontSize);
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

    // ── Quest-state change detection ────────────────────────────
    // AgentMap.EventMarkers is only rebuilt by the game when the map
    // UI is interacted with. Without a nudge, the compass shows stale
    // quest icons after MSQ progression until the player opens the map.
    //
    // Fix: snapshot the current MSQ id + sequence every Update(). When
    // they change (quest turned in, new step accepted), call
    // UpdateEventMapMarkers once to rebuild the list — same path the
    // game takes when you open the map. Zero cost on frames where
    // nothing changed.
    private static ushort _prevMsqId;
    private static byte   _prevMsqSeq;
    private static int    _periodicRefreshCounter;
    private const  int    PERIODIC_REFRESH_INTERVAL = 90; // ~1.5 seconds

    public static void Update()
    {
        try
        {
            bool needsRefresh = false;

            var scenarioAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentScenarioTree.Instance();
            if (scenarioAgent != null && scenarioAgent->Data != null)
            {
                ushort msqId = 0;
                for (int i = 0; i < 3; i++)
                {
                    ushort id = scenarioAgent->Data->MainScenarioQuestIds[i];
                    if (id != 0) { msqId = id; break; }
                }

                if (msqId != 0)
                {
                    byte seq = FFXIVClientStructs.FFXIV.Client.Game.QuestManager.GetQuestSequence(msqId);
                    if (msqId != _prevMsqId || seq != _prevMsqSeq)
                    {
                        _prevMsqId  = msqId;
                        _prevMsqSeq = seq;
                        needsRefresh = true;
                    }
                }
            }

            // Periodic fallback: catches side quest / unlock quest completions
            // that don't change MSQ state. Cheap — same call the game makes on map open.
            if (!needsRefresh)
            {
                _periodicRefreshCounter++;
                if (_periodicRefreshCounter >= PERIODIC_REFRESH_INTERVAL)
                {
                    _periodicRefreshCounter = 0;
                    needsRefresh = true;
                }
            }
            else
            {
                _periodicRefreshCounter = 0;
            }

            if (!needsRefresh) return;

            var agent = AgentMap.Instance();
            if (agent == null) return;
            var ptr = (FFXIVClientStructs.STD.StdVector<FFXIVClientStructs.Interop.Pointer<FFXIVClientStructs.FFXIV.Client.Game.UI.MapMarkerData>>*)
                System.Runtime.CompilerServices.Unsafe.AsPointer(ref agent->EventMarkersPtrs);
            agent->UpdateEventMapMarkers(ptr);
        }
        catch { /* defensive — struct layouts can shift between patches */ }
    }

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
            _compassClicks.Clear();
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

            // Layer 4: click dispatch for accessibility actions (teleport
            // to aetheryte, target party member). Hover shows a hand
            // cursor + thin outline; left-click fires the registered
            // action. Skipped while FPS mouselook hides the OS cursor.
            DispatchCompassClicks(dl);
        }
        catch { }
    }

    // Per-frame list of clickable compass elements. Each entry carries
    // its own hover renderer so the visual hover state matches whatever
    // the underlying marker looks like (icon for aetherytes, colored
    // circle for party members), plus a stable key used to persist
    // per-target hover-lerp alpha across frames so the size grow eases
    // in instead of snapping. Cleared at the start of each marker
    // pass; populated by DrawParty and DrawAetherytes; consumed by
    // DispatchCompassClicks at the end of the frame.
    private static readonly List<(string key, Vector2 rectMin, Vector2 rectMax,
        Action<ImDrawListPtr, float, float> hoverRender, Action action)>
        _compassClicks = new();

    // Per-clickable hover-lerp state. Keyed by the same string the
    // _compassClicks entry uses; lerps toward 1 while hovered, 0
    // otherwise. Re-rendered into the marker via hoverRender so size
    // and halo intensity ease in instead of popping.
    private static readonly Dictionary<string, float> _clickHoverAlpha = new();
    private static readonly HashSet<string> _clickHoverSeenThisFrame = new();
    // Tracks the key of the click target currently under the cursor so
    // we only fire the hover SE once per hover-enter transition,
    // mirroring how the native UI plays the cursor tick when the
    // highlight first lands on an element.
    private static string _clickHoverActiveKey = null;

    // Edge-detected left-click. Foreground drawlist hit targets don't
    // reliably get MouseDown events through ImGui in Dalamud (the OS
    // click isn't claimed by any ImGui window), so we poll the OS
    // directly. VK_LBUTTON = 0x01; high bit set = currently down.
    private static bool _prevMouseDown;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CompassPoint lpPoint);
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CompassPoint { public int X; public int Y; }
    private const int CompassVkLButton = 0x01;
    // True while the cursor is inside a hover hitbox this frame.
    private static bool _compassHoverState;


    // Project a world position onto the compass bar and return the icon
    // rectangle. Returns false when the marker is outside the FOV cone
    // or the max range — same gating DrawIconAtBearing uses, so the
    // click hitbox lines up with what's visible.
    private static bool TryGetCompassIconRect(
        Configuration cfg, Vector2 center, float camYaw, Vector3 ppos, Vector3 wpos,
        out Vector2 rectMin, out Vector2 rectMax)
    {
        rectMin = rectMax = default;
        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (distSq > maxR * maxR) return false;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = BearingToRel(worldBearing, camYaw);
        rel = DampenProximity(rel, distSq);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return false;
        // Require the marker to be at least dimly visible — keeps the
        // click hitbox in sync with what the player can actually see.
        if (edgeAlpha < 0.2f) return false;

        float size = cfg.CompassIconSize;
        rectMin = new Vector2(x - size * 0.5f, center.Y - size * 0.5f);
        rectMax = new Vector2(x + size * 0.5f, center.Y + size * 0.5f);
        return true;
    }

    private static void DispatchCompassClicks(ImDrawListPtr dl)
    {
        // Always update hover alphas (even when count==0) so existing
        // tracked alphas decay cleanly toward 0 instead of getting
        // stuck at full when their click target disappears.
        float dtDispatch;
        try { dtDispatch = ImGui.GetIO().DeltaTime; } catch { dtDispatch = 0.016f; }
        if (dtDispatch <= 0f) dtDispatch = 0.016f;
        float hoverK = 1f - MathF.Exp(-12f * dtDispatch); // ~0.08s settle

        // Edge-detect left mouse against the OS directly — ImGui's IO
        // doesn't reliably report MouseDown/MousePos in a foreground-
        // drawlist-only context (Dalamud doesn't route the click to any
        // ImGui window, so io.MouseDown[0] never flips). Win32 polling
        // gives us ground truth.
        bool mouseDownNow = (GetAsyncKeyState(CompassVkLButton) & 0x8000) != 0;
        Vector2 mp = default;
        if (GetCursorPos(out var cp)) mp = new Vector2(cp.X, cp.Y);
        bool clickEdge = mouseDownNow && !_prevMouseDown;
        _prevMouseDown = mouseDownNow;

        bool dispatchActive = !CameraDynamics.IsMouseLookActive;

        _clickHoverSeenThisFrame.Clear();

        if (_compassClicks.Count == 0)
        {
            // Decay any leftover hover alphas.
            DecayHoverAlphas(hoverK);
            return;
        }

        Action pendingAction = null;
        string newActiveKey = null;
        foreach (var (key, min, max, hoverRender, action) in _compassClicks)
        {
            bool inside = dispatchActive
                && mp.X >= min.X && mp.X < max.X
                && mp.Y >= min.Y && mp.Y < max.Y;

            if (!_clickHoverAlpha.TryGetValue(key, out float ha)) ha = 0f;
            ha += ((inside ? 1f : 0f) - ha) * hoverK;
            _clickHoverAlpha[key] = ha;
            _clickHoverSeenThisFrame.Add(key);

            if (ha > 0.01f)
                try { hoverRender?.Invoke(dl, _alpha, ha); } catch { }

            if (inside && pendingAction == null)
            {
                newActiveKey = key;
                if (clickEdge) pendingAction = action;
            }
        }

        // Cursor on hover — edge-only writes. SetCursorType fires
        // the change-event path (and the cursor-tick SE) every call,
        // so per-frame calls produce SE spam; direct Type writes
        // don't update the visual because the game's per-frame
        // cursor logic clobbers them before render. We only have
        // clean access at the edge transitions.
        bool wantHover = newActiveKey != null;
        if (wantHover != _compassHoverState)
        {
            try
            {
                var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
                if (stage != null)
                {
                    var t = wantHover
                        ? FFXIVClientStructs.FFXIV.Component.GUI.AtkCursor.CursorType.Clickable
                        : FFXIVClientStructs.FFXIV.Component.GUI.AtkCursor.CursorType.Arrow;
                    stage->AtkCursor.SetCursorType(t, true);
                }
            }
            catch { }
            _compassHoverState = wantHover;
        }

        // No explicit hover SE: AtkCursor.SetCursorType already plays
        // the game's cursor tick on the Arrow→Clickable transition,
        // and that's the same SE we wanted. _clickHoverActiveKey is
        // still tracked in case future logic needs hover-enter events.
        _clickHoverActiveKey = newActiveKey;

        // Drop alphas for keys we didn't see this frame so the
        // dictionary doesn't grow unbounded as markers stream in/out.
        List<string> toRemove = null;
        foreach (var kv in _clickHoverAlpha)
        {
            if (_clickHoverSeenThisFrame.Contains(kv.Key)) continue;
            float ha = kv.Value;
            ha += (0f - ha) * hoverK;
            if (ha < 0.01f) (toRemove ??= new List<string>()).Add(kv.Key);
            else _clickHoverAlpha[kv.Key] = ha;
        }
        if (toRemove != null) foreach (var k in toRemove) _clickHoverAlpha.Remove(k);

        if (pendingAction != null)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "compass_teleport.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} dispatch fired (edge detected)\n");
            }
            catch { }
            try { pendingAction(); }
            catch (Exception ex)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] Compass click action threw: {ex.Message}"); } catch { }
            }
        }
    }

    // Hover SE — uses AtkStage's first AtkUnitBase to call
    // PlaySoundEffect with the cursor tick sound id (1). Defensive
    // because addons aren't guaranteed to exist on every frame.
    private static unsafe void PlayCompassHoverSound()
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
                unit->PlaySoundEffect(1);
                return;
            }
        }
        catch { }
    }

    private static void DecayHoverAlphas(float lerpK)
    {
        List<string> toRemove = null;
        foreach (var kv in _clickHoverAlpha)
        {
            float ha = kv.Value;
            ha += (0f - ha) * lerpK;
            if (ha < 0.01f) (toRemove ??= new List<string>()).Add(kv.Key);
            else _clickHoverAlpha[kv.Key] = ha;
        }
        if (toRemove != null) foreach (var rk in toRemove) _clickHoverAlpha.Remove(rk);
    }

    // Shared "selected" render — scale the icon up to 1.4× and surround
    // it with the four-direction gold halo from DrawTargetOverlay.
    // Mirrors what FFXIV's native target highlight looks like on the
    // map / world view, so hovering a compass element reads as
    // "selected" identically. `hoverLerp` (0..1) lerps the scale and
    // halo intensity so the size grow eases in instead of snapping.
    private static void RenderSelectedHalo(ImDrawListPtr dl, float x, float cy,
                                            float size, uint iconId,
                                            string circleLabel, uint circleColor,
                                            float a, float hoverLerp,
                                            uint textColor = 0xFF000000,
                                            float fontSize = 10f)
    {
        if (hoverLerp <= 0f) return;
        const float kScaleMax = 1.4f;
        float kScale = 1f + (kScaleMax - 1f) * hoverLerp;
        float sz = size * kScale;
        a *= hoverLerp;
        var tl = new Vector2(x - sz * 0.5f, cy - sz * 0.5f);
        var br = new Vector2(x + sz * 0.5f, cy + sz * 0.5f);

        if (iconId != 0)
        {
            var wrap = GetIcon(iconId);
            if (wrap != null)
            {
                try
                {
                    var tex = wrap.GetWrapOrEmpty();
                    float gs = 3f * kScale;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        float off = pass == 0 ? gs : gs * 0.5f;
                        uint pt = PackColor(1f, 1f, 0.7f, a * (pass == 0 ? 0.3f : 0.5f));
                        dl.AddImage(tex.Handle, tl + new Vector2(-off, 0), br + new Vector2(-off, 0), Vector2.Zero, Vector2.One, pt);
                        dl.AddImage(tex.Handle, tl + new Vector2(off,  0), br + new Vector2(off,  0), Vector2.Zero, Vector2.One, pt);
                        dl.AddImage(tex.Handle, tl + new Vector2(0, -off), br + new Vector2(0, -off), Vector2.Zero, Vector2.One, pt);
                        dl.AddImage(tex.Handle, tl + new Vector2(0,  off), br + new Vector2(0,  off), Vector2.Zero, Vector2.One, pt);
                    }
                    uint tint = PackColor(1f, 1f, 1f, a);
                    dl.AddImage(tex.Handle, tl, br, Vector2.Zero, Vector2.One, tint);
                    return;
                }
                catch { }
            }
        }

        // Pill path (party member etc.) — scaled, with halo ring on
        // top and bottom edges. Width tracks the label so a longer
        // nickname grows the pill horizontally rather than spilling.
        float labelPxPill = fontSize * kScale;
        float scalePill = labelPxPill / MathF.Max(1f, ImGui.GetFontSize());
        Vector2 lszPill = string.IsNullOrEmpty(circleLabel)
            ? Vector2.Zero
            : ImGui.CalcTextSize(circleLabel) * scalePill;

        float pillH   = sz * 0.8f;
        float padX    = 6f * kScale;
        float pillW   = MathF.Max(pillH * 1.5f, lszPill.X + padX * 2f);
        float rounding = pillH * 0.5f;
        var pMin = new Vector2(x - pillW * 0.5f, cy - pillH * 0.5f);
        var pMax = new Vector2(x + pillW * 0.5f, cy + pillH * 0.5f);

        uint haloCol = PackColor(1f, 1f, 0.7f, a * 0.5f);
        float haloPad = 3f * kScale;
        dl.AddRect(pMin - new Vector2(haloPad, haloPad),
                   pMax + new Vector2(haloPad, haloPad),
                   haloCol, rounding + haloPad, 0, 2f);
        dl.AddRect(pMin - new Vector2(haloPad * 0.5f, haloPad * 0.5f),
                   pMax + new Vector2(haloPad * 0.5f, haloPad * 0.5f),
                   haloCol, rounding + haloPad * 0.5f, 0, 1.5f);
        uint baseCol = MultiplyAlpha(circleColor, a);
        dl.AddRectFilled(pMin, pMax, baseCol, rounding);
        if (!string.IsNullOrEmpty(circleLabel))
        {
            var p = new Vector2(x - lszPill.X * 0.5f, cy - lszPill.Y * 0.5f);
            dl.AddText(ImGui.GetFont(), labelPxPill, p,
                       MultiplyAlpha(textColor, a), circleLabel);
        }
    }

    // Teleport via the existing TeleportMenu pipeline so housing /
    // FC-house sub-indices, recent-teleport tracking, and the cached
    // attuned-aetheryte list all get respected — same path the custom
    // teleport menu uses. Falls through silently if the world aetheryte
    // isn't in the attuned cache (player hasn't attuned to it yet).
    // Play the FFXIV menu-confirm click sound on any compass action.
    // Mirrors the SFX the native UI plays when you click a menu item.
    // Routed through the first loaded AtkUnitBase (any onscreen addon
    // is fine; the sound is global once dispatched).
    private static unsafe void PlayCompassClickSound()
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
                unit->PlaySoundEffect(8); // 8 = menu confirm / select
                return;
            }
        }
        catch { }
    }

    private static void TeleportToAetheryte(uint aetheryteId)
    {
        PlayCompassClickSound();
        string diagPath = null;
        try
        {
            diagPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "compass_teleport.txt");
        }
        catch { }

        void Log(string s)
        {
            if (diagPath == null) return;
            try { System.IO.File.AppendAllText(diagPath,
                $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
        }

        Log($"click aetheryteId={aetheryteId}");

        try
        {
            var list = TeleportMenu.GetList();
            if (list == null) { Log("GetList() returned null"); return; }
            Log($"list count={list.Count}");

            // Prefer subIndex 0 (the aetheryte itself); fall back to
            // whatever entry matches when the cache only lists shards.
            TeleportMenu.TeleportEntry chosen = null;
            int matchCount = 0;
            foreach (var e in list)
            {
                if (e.AetheryteId != aetheryteId) continue;
                matchCount++;
                if (chosen == null || e.SubIndex == 0) chosen = e;
                if (e.SubIndex == 0) break;
            }
            Log($"matches={matchCount} chosen.SubIndex={(chosen?.SubIndex.ToString() ?? "null")}");

            if (chosen == null) { Log("no match — aetheryte not in attuned cache"); return; }
            bool ok = TeleportMenu.DoTeleport(chosen);
            Log($"DoTeleport returned {ok}");
        }
        catch (Exception ex)
        {
            Log($"threw: {ex.Message}");
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] Compass aetheryte teleport failed: {ex.Message}"); } catch { }
        }
    }

    private static void TeleportToPartyMember(uint entityId, uint memberTerr)
    {
        PlayCompassClickSound();
        ushort selfTerr = 0;
        try { selfTerr = (ushort)DalamudApi.ClientState.TerritoryType; } catch { }

        if (memberTerr == selfTerr)
        {
            try
            {
                var go = DalamudApi.ObjectTable?.SearchById(entityId);
                if (go != null) DalamudApi.TargetManager.Target = go;
            }
            catch { }
            return;
        }

        try
        {
            var list = TeleportMenu.GetList();
            if (list == null) return;
            TeleportMenu.TeleportEntry best = null;
            foreach (var e in list)
            {
                if (e.TerritoryId != (ushort)memberTerr) continue;
                if (e.SubIndex != 0) continue;
                best = e;
                break;
            }
            if (best == null)
            {
                foreach (var e in list)
                {
                    if (e.TerritoryId == (ushort)memberTerr) { best = e; break; }
                }
            }
            if (best != null)
                TeleportMenu.DoTeleport(best);
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
        // No distance label here — the underlying world-marker pass
        // (DrawNpcMarkers / DrawParty / etc.) already draws "Ny" for
        // this entity, and rendering it twice produces a duplicate.
    }

    private static void DrawParty(ImDrawListPtr dl, Vector2 center, Configuration cfg,
                                   float camYaw, Vector3 ppos)
    {
        var party = DalamudApi.PartyList;
        if (party == null) return;
        ulong selfId = DalamudApi.ObjectTable?.LocalPlayer?.GameObjectId ?? 0UL;
        ushort selfTerr = 0;
        try { selfTerr = (ushort)DalamudApi.ClientState.TerritoryType; } catch { }
        int offMapIndex = 0;
        int offMapCount = 0;
        foreach (var p in party)
        {
            if (p == null) continue;
            if ((ulong)p.EntityId == selfId) continue;
            uint mt = 0;
            try { mt = p.Territory.RowId; } catch { }
            if (mt != 0 && mt != selfTerr) offMapCount++;
        }

        foreach (var p in party)
        {
            if (p == null) continue;
            if ((ulong)p.EntityId == selfId) continue;

            uint memberTerr = 0;
            try { memberTerr = p.Territory.RowId; } catch { }
            if (memberTerr == 0) continue;

            bool offMap = memberTerr != selfTerr;

            string fullName = p.Name?.TextValue ?? "";
            string label = PlayerNicknames.GetNickname(fullName);
            if (string.IsNullOrEmpty(label))
                label = string.IsNullOrEmpty(fullName) ? "P" : fullName[..1];

            Vector3 wpos;
            if (offMap)
            {
                float spread = 0.35f;
                float baseAngle = camYaw + MathF.PI;
                float fan = offMapCount > 1
                    ? baseAngle + spread * (offMapIndex - (offMapCount - 1) * 0.5f)
                    : baseAngle;
                offMapIndex++;
                float dist = 80f;
                wpos = new Vector3(
                    ppos.X + MathF.Sin(fan) * dist,
                    ppos.Y,
                    ppos.Z + MathF.Cos(fan) * dist);
            }
            else
            {
                var pos = p.Position;
                wpos = new(pos.X, pos.Y, pos.Z);
            }
            var pillCol = cfg.CompassPartyPillColor;
            var txtCol  = cfg.CompassPartyTextColor;
            DrawTrackedMarker("party:" + p.EntityId,
                              dl, center, cfg, camYaw, ppos, wpos, 0, label,
                              overrideColor: PackColor(pillCol.X, pillCol.Y, pillCol.Z, pillCol.W),
                              skipDistance: offMap,
                              overrideTextColor: PackColor(txtCol.X, txtCol.Y, txtCol.Z, txtCol.W),
                              overrideFontSize: cfg.CompassPartyFontSize);

            // Clickable: hard-target this party member (same as clicking
            // their entry in the party list).
            uint entityId = (uint)p.EntityId;
            uint partyMemberTerr = memberTerr;
            string partyLabel = label;
            if (TryGetCompassIconRect(cfg, center, camYaw, ppos, wpos,
                                       out var pMin, out var pMax))
            {
                float pCx = (pMin.X + pMax.X) * 0.5f;
                float pCy = (pMin.Y + pMax.Y) * 0.5f;
                float pSize = cfg.CompassIconSize;
                float pLabelPx = cfg.CompassPartyFontSize;
                float pScale = pLabelPx / MathF.Max(1f, ImGui.GetFontSize());
                Vector2 pSz = string.IsNullOrEmpty(partyLabel)
                    ? Vector2.Zero
                    : ImGui.CalcTextSize(partyLabel) * pScale;
                float pPillH = pSize * 0.8f;
                float pPadX  = 6f;
                float pPillW = MathF.Max(pPillH * 1.5f, pSz.X + pPadX * 2f);
                pMin = new Vector2(pCx - pPillW * 0.5f, pCy - pPillH * 0.5f);
                pMax = new Vector2(pCx + pPillW * 0.5f, pCy + pPillH * 0.5f);
                string pKey = "party:" + p.EntityId;
                uint haloCol = PackColor(pillCol.X, pillCol.Y, pillCol.Z, pillCol.W);
                uint haloTxt = PackColor(txtCol.X, txtCol.Y, txtCol.Z, txtCol.W);
                _compassClicks.Add((pKey, pMin, pMax,
                    (drawList, baseAlpha, hoverLerp) => RenderSelectedHalo(
                        drawList, pCx, pCy, pSize, 0u, partyLabel, haloCol, baseAlpha, hoverLerp, haloTxt, cfg.CompassPartyFontSize),
                    () => TeleportToPartyMember(entityId, partyMemberTerr)));
            }
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

            // Clickable: teleport to this aetheryte. BaseId on Aetheryte
            // GameObjects is the Aetheryte sheet row that Telepo expects.
            uint aetheryteId = o.BaseId;
            uint aetheryteIcon = icon;
            if (aetheryteId != 0u &&
                TryGetCompassIconRect(cfg, center, camYaw, ppos, wpos,
                                       out var aMin, out var aMax))
            {
                float aCx = (aMin.X + aMax.X) * 0.5f;
                float aCy = (aMin.Y + aMax.Y) * 0.5f;
                float aSize = cfg.CompassIconSize;
                string aKey = "aether:" + o.GameObjectId;
                _compassClicks.Add((aKey, aMin, aMax,
                    (drawList, baseAlpha, hoverLerp) => RenderSelectedHalo(
                        drawList, aCx, aCy, aSize, aetheryteIcon, null, 0u, baseAlpha, hoverLerp),
                    () => TeleportToAetheryte(aetheryteId)));
            }
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
                                           float alphaMul = 1f,
                                           bool skipDistance = false,
                                           uint overrideTextColor = 0,
                                           float overrideFontSize = 0f)
    {
        float distSq = (wpos.X - ppos.X) * (wpos.X - ppos.X)
                     + (wpos.Z - ppos.Z) * (wpos.Z - ppos.Z);
        float maxR = cfg.CompassMaxRangeYalms;
        if (!skipDistance && distSq > maxR * maxR) return;

        float worldBearing = MathF.Atan2(wpos.X - ppos.X, wpos.Z - ppos.Z);
        float rel = BearingToRel(worldBearing, camYaw);
        rel = DampenProximity(rel, distSq);
        if (!ProjectToBar(rel, cfg, out float x, out float edgeAlpha)) return;

        float a = _alpha * edgeAlpha * alphaMul;
        float size = cfg.CompassIconSize;
        var tl = new Vector2(x - size * 0.5f, center.Y - size * 0.5f);
        var br = new Vector2(x + size * 0.5f, center.Y + size * 0.5f);
        float dy = wpos.Y - ppos.Y;
        float dist = MathF.Sqrt(distSq);

        if (overrideColor != 0)
        {
            uint col = MultiplyAlpha(overrideColor, a);
            float labelPx = overrideFontSize > 0f ? overrideFontSize : 10f;
            float scale = labelPx / MathF.Max(1f, ImGui.GetFontSize());
            Vector2 sz = string.IsNullOrEmpty(labelFallback)
                ? Vector2.Zero
                : ImGui.CalcTextSize(labelFallback) * scale;

            float pillH = size * 0.8f;
            float padX  = 6f;
            float pillW = MathF.Max(pillH * 1.5f, sz.X + padX * 2f);
            float rounding = pillH * 0.5f;
            var pMin = new Vector2(x - pillW * 0.5f, center.Y - pillH * 0.5f);
            var pMax = new Vector2(x + pillW * 0.5f, center.Y + pillH * 0.5f);
            dl.AddRectFilled(pMin, pMax, col, rounding);

            if (!string.IsNullOrEmpty(labelFallback))
            {
                uint txtCol = overrideTextColor != 0
                    ? MultiplyAlpha(overrideTextColor, a)
                    : PackColor(0f, 0f, 0f, a);
                var p = new Vector2(x - sz.X * 0.5f, center.Y - sz.Y * 0.5f);
                dl.AddText(ImGui.GetFont(), labelPx, p, txtCol, labelFallback);
            }
            if (!skipDistance) DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
            if (!skipDistance) DrawDistanceLabel(dl, x, center.Y, size, dist, a);
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
                if (!skipDistance) DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
                if (!skipDistance) DrawDistanceLabel(dl, x, center.Y, size, dist, a);
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
        if (!skipDistance) DrawAltitudeArrow(dl, x, center.Y, size, dy, a);
        if (!skipDistance) DrawDistanceLabel(dl, x, center.Y, size, dist, a);
    }

    // Small distance readout sitting just above the icon. Shows yalms
    // as an integer with a "y" suffix; centered horizontally on the
    // icon. Renders with a 1px black drop shadow so it stays legible
    // against any compass background. Fades through a band so the
    // label doesn't pop when the player walks onto the marker — it
    // eases out between 1.5y and 0.5y instead of cutting at 0y.
    private static void DrawDistanceLabel(ImDrawListPtr dl, float x, float cy,
                                           float size, float dist, float a)
    {
        if (a < 0.05f) return;
        // Hide the readout for anything ≤30y — close-range markers
        // are already obvious from the icon position; the number only
        // adds value at meaningful travel distances. Fade through a
        // 30–35y band so it eases in/out instead of popping.
        const float distFadeFull  = 35f;
        const float distFadeStart = 30f;
        float t = MathF.Min(1f, MathF.Max(0f,
            (dist - distFadeStart) / (distFadeFull - distFadeStart)));
        float distFade = t * t * (3f - 2f * t);
        if (distFade < 0.02f) return;

        int rounded = (int)MathF.Round(dist);
        if (rounded <= 30) return; // never display "≤30y"
        string label = $"{rounded}y";
        var sz = ImGui.CalcTextSize(label);
        // 2px gap between text baseline and icon top.
        float ty = cy - size * 0.5f - sz.Y - 2f;
        float tx = x - sz.X * 0.5f;
        float ea = a * distFade;
        uint shadow = PackColor(0f, 0f, 0f, ea * 0.7f);
        uint col    = PackColor(1f, 1f, 1f, ea);
        dl.AddText(new Vector2(tx + 1f, ty + 1f), shadow, label);
        dl.AddText(new Vector2(tx,      ty),      col,    label);
    }

    // Up/down chevron when the marker is meaningfully above or below
    // the player — small thin two-line glyph anchored to the icon's
    // top-right corner, matching the target-highlight chevron style.
    // 10y matches FFXIV's native compass. We fade through a band around
    // the threshold (8–14y) instead of snapping so the chevron eases
    // in/out as the player climbs/descends, rather than popping.
    private const float AltitudeFadeStart = 8f;  // chevron starts fading in
    private const float AltitudeFadeFull  = 14f; // fully visible at/above
    private static void DrawAltitudeArrow(ImDrawListPtr dl, float x, float cy,
                                           float size, float dy, float a)
    {
        float adY = MathF.Abs(dy);
        if (adY < AltitudeFadeStart) return;
        float t = MathF.Min(1f,
            (adY - AltitudeFadeStart) / (AltitudeFadeFull - AltitudeFadeStart));
        // Smoothstep — softer than linear at the edges of the band.
        float alt = t * t * (3f - 2f * t);
        if (alt < 0.02f) return;

        float chevS  = 4f;
        float chevX  = x + size * 0.35f;
        float chevY  = cy - size * 0.35f;
        float thick  = 1.5f;
        uint  col    = PackColor(1f, 1f, 1f, a * alt);

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
        {
            // Marker has drifted past the FOV cone — it's pinned at the
            // nearest edge of the rail but should fade out as it goes
            // deeper behind the player, then fade back in from the
            // opposite edge as it returns. Without this the icon hard-
            // jumps from one rail edge to the other when the bearing
            // wraps through π, producing the "pop & swap" the user
            // sees while spinning the camera.
            float behindT = MathF.Min(1f,
                (MathF.Abs(rel) - halfFov) / MathF.Max(0.001f, MathF.PI - halfFov));
            // Smoothstep so the fade eases in/out rather than reading
            // as a linear ramp.
            float behindFade = behindT * behindT * (3f - 2f * behindT);
            edgeAlpha = 0.35f * (1f - behindFade);
        }
        else if (u < fadeStart)
            edgeAlpha = 1f;
        else
        {
            // In-bar edge fade — eases from 1 (at fadeStart) toward
            // the clamped-edge floor (0.35) when u reaches 1, NOT
            // toward 0. The clamped branch picks up from 0.35 and
            // fades further behind the player. Without this floor the
            // alpha dropped to ~0 right inside the rail edge and then
            // jumped back up to 0.35 the instant the marker was
            // clamped, producing the "double draw / re-fade" the user
            // sees when a marker is sweeping in from past-edge back
            // onto the rail.
            const float clampedEdgeAlpha = 0.35f;
            float t2 = (u - fadeStart) / (1f - fadeStart);
            edgeAlpha = 1f - t2 * (1f - clampedEdgeAlpha);
        }
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
