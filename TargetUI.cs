using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace noWickyXIV;

// Target overlay: target name + optional cast bar with optional spell
// name. Replaces DelvUI's target/cast bar without the rest of the
// plugin's footprint.
//
// Each element has its own:
//   - Anchor mode: absolute screen pixels OR follow-the-target-bone
//   - Position: X/Y in pixels (absolute or offset, depending on mode)
//   - Color/alpha + (text only) outline color/alpha
//   - Font path + size (system .ttf/.otf, picker reuses
//     JobAura.EnumerateSystemFonts)
//   - Toggle (target name, cast bar, cast spell name independently)
//
// Visibility fade follows target presence: when a target is acquired
// the alpha ramps to 1; when dropped, it ramps to 0. Driven by the
// shared `JobAuraFadeRate` so fade timing matches the rest of the
// JobAura overlay.
public static unsafe class TargetUI
{
    // Smoothed presence alpha — 1 while target exists, 0 when none.
    private static float _presenceAlpha;

    // Cache of the last frame's target info so the fade-out can finish
    // drawing after the actual target reference goes null. Without this
    // the text cuts the moment the player de-targets even though
    // _presenceAlpha is still mid-fade.
    private static string  _cachedName;
    private static bool    _cachedNamePosValid;
    private static Vector2 _cachedNamePos;

    // Last good bone-world-position for the configured bone index.
    // Used as the fallback when GetBoneWorldPosition transiently returns
    // Vector3.Zero (which it does whenever the engine hasn't fully
    // built the skeleton this frame — happens during animation hand-
    // offs, instance loads, NPC spawns). Without this fallback the
    // anchor snaps between the bone-projected position and the root-
    // projected position frame-to-frame, which reads as jitter.
    private static Vector3 _cachedNameBoneWorld;
    private static bool    _cachedNameBoneValid;
    private static int     _cachedNameBoneIdx = -1;
    private static ulong   _cachedNameBoneOwner;

    // Smoothed displayed screen position. The bone itself jitters by
    // sub-pixel-to-low-pixel amounts every frame from idle animations
    // (breathing, weapon sway) — even with integer-pixel snap on the
    // raw projection, those values cross pixel boundaries frame-to-
    // frame and produce visible 1-2 px jitter. A short exp-lerp on
    // the displayed position averages the micro-fluctuations while
    // still tracking macro movement (target running, camera pan).
    private static Vector2 _displayedNamePos;
    private static bool    _displayedNamePosValid;

    // Cached font handles. Rebuilt only on (path, size) change so the
    // per-frame Draw is cheap.
    private static IFontHandle _nameFont;
    private static string      _nameFontPath = "";
    private static float       _nameFontSize = -1f;

    private static IFontHandle _spellFont;
    private static string      _spellFontPath = "";
    private static float       _spellFontSize = -1f;

    public static void Update()
    {
        // Smooth the presence alpha. Reads target presence each frame
        // and lerps toward target=1 / no-target=0.
        bool hasTarget = false;
        try { hasTarget = DalamudApi.TargetManager?.Target != null; } catch { }

        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }

        float rate = MathF.Max(0.5f, noWickyXIV.Config.JobAuraFadeRate);
        float k = 1f - MathF.Exp(-rate * dt);
        float t = hasTarget ? 1f : 0f;
        _presenceAlpha += (t - _presenceAlpha) * k;
    }

    public static void Draw()
    {
        // Cheap bail when nothing's enabled or visible.
        var cfg = noWickyXIV.Config;
        if (!cfg.EnableTargetName && !cfg.EnableCastBar) return;
        if (_presenceAlpha < 0.01f) return;

        // NOTE: don't return on a null target — _presenceAlpha may
        // still be mid-fade-out from the previous target, and we want
        // the text to ride that ramp to 0 instead of cutting. The
        // cast bar IS gated below since cast info evaporates with
        // the target.
        var tgt = DalamudApi.TargetManager?.Target;

        // Loading-screen guard mirrors JobAura's wipe behaviour.
        try
        {
            var cond = DalamudApi.Condition;
            if (cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
             || cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                return;
        }
        catch { }

        // Refresh the cache while we have a live target so the next
        // frame's fade-out has something to draw.
        if (tgt != null)
        {
            try { _cachedName = tgt.Name?.TextValue ?? _cachedName; }
            catch { /* keep previous cache */ }
        }

        var fg = ImGui.GetForegroundDrawList();

        // Resolve the target-name anchor up-front so the cast bar can
        // take a "relative to the name" anchor without us having to
        // duplicate the bone projection / cache logic.
        Vector2 nameAnchor = ResolveTargetNameAnchor(tgt, cfg);

        if (cfg.EnableTargetName)
            DrawTargetName(fg, tgt, cfg, nameAnchor);
        if (cfg.EnableCastBar && tgt != null)
            DrawCastBar(fg, tgt, cfg, nameAnchor);
    }

    // Computes the target-name anchor in screen-space pixels, taking
    // the configured anchor mode and the bone-position cache into
    // account. Used by both DrawTargetName and DrawCastBar (when
    // CastBarAnchorMode == 2 / TargetName).
    //
    // For bone-anchor mode, the raw projected position is exp-lerped
    // each frame into _displayedNamePos so idle-animation micro-
    // jitter on the bone (breathing, weapon sway) doesn't surface as
    // pixel-boundary snap on the rendered text.
    private static Vector2 ResolveTargetNameAnchor(
        Dalamud.Game.ClientState.Objects.Types.IGameObject tgt,
        Configuration cfg)
    {
        if (cfg.TargetNameAnchorMode == 1)
        {
            // Target lost: freeze the displayed position at exactly
            // where it was last frame. Do NOT keep lerping toward
            // _cachedNamePos — even though that "looks right", the
            // lerp always lags the raw bone read, so on the de-target
            // frame _displayedNamePos and _cachedNamePos differ by
            // a few pixels, and continuing the lerp drags the text
            // visibly toward a "ghost" position before fading out.
            // Freezing makes the fade-out happen exactly where the
            // text was when the target was released.
            if (tgt == null)
            {
                if (_displayedNamePosValid)
                    return new Vector2(MathF.Round(_displayedNamePos.X), MathF.Round(_displayedNamePos.Y));
                if (_cachedNamePosValid)
                    return new Vector2(MathF.Round(_cachedNamePos.X), MathF.Round(_cachedNamePos.Y));
                // Nothing cached at all — first frame after enable
                // with no prior target. Use the configured screen
                // offset so we at least don't render at (0,0).
                return new Vector2(cfg.TargetNameX, cfg.TargetNameY);
            }

            Vector2 rawTarget;
            if (TryProjectBone(tgt, cfg.TargetNameBoneIndex, out var anchor))
            {
                rawTarget = new Vector2(anchor.X + cfg.TargetNameX, anchor.Y + cfg.TargetNameY);
                _cachedNamePos = rawTarget;
                _cachedNamePosValid = true;
            }
            else if (_cachedNamePosValid)
            {
                rawTarget = _cachedNamePos;
            }
            else
            {
                return new Vector2(cfg.TargetNameX, cfg.TargetNameY);
            }

            // Exp-lerp the displayed position toward the raw target.
            // Rate ≈ 25/s (~28 ms halflife) — quick enough to track
            // a moving target, slow enough to filter idle-animation
            // sub-pixel wobble. Snap to integer at the end so font
            // rendering stays pixel-aligned.
            float dt = 0.016f;
            try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
            const float smoothRate = 25f;
            float k = 1f - MathF.Exp(-smoothRate * MathF.Max(0.001f, dt));

            if (!_displayedNamePosValid)
            {
                _displayedNamePos = rawTarget;
                _displayedNamePosValid = true;
            }
            else
            {
                // Reset on big jumps (target switch, screen-edge wrap)
                // so we don't visibly drag through the world. 80 px
                // is small enough to catch genuine transitions, large
                // enough to ignore animation wobble.
                if (Vector2.Distance(rawTarget, _displayedNamePos) > 80f)
                    _displayedNamePos = rawTarget;
                else
                    _displayedNamePos += (rawTarget - _displayedNamePos) * k;
            }
            return new Vector2(MathF.Round(_displayedNamePos.X), MathF.Round(_displayedNamePos.Y));
        }

        // Screen-anchor mode: position is fixed pixels, no smoothing
        // needed.
        _displayedNamePosValid = false;
        return new Vector2(cfg.TargetNameX, cfg.TargetNameY);
    }

    // ---- Target name ----
    private static void DrawTargetName(
        ImDrawListPtr fg,
        Dalamud.Game.ClientState.Objects.Types.IGameObject tgt,
        Configuration cfg,
        Vector2 pos)
    {
        // pos is the resolved target-name anchor (screen pixels) from
        // ResolveTargetNameAnchor, which already handles both anchor
        // modes and the bone-position cache.

        EnsureFont(ref _nameFont, ref _nameFontPath, ref _nameFontSize,
                   cfg.TargetNameFontPath, cfg.TargetNameFontSize);

        // Use the live name when we have a target, otherwise the cached
        // string from the last frame that had one.
        string label;
        if (tgt != null)
        {
            try { label = tgt.Name?.TextValue ?? _cachedName ?? ""; }
            catch { label = _cachedName ?? ""; }
        }
        else
        {
            label = _cachedName ?? "";
        }
        if (string.IsNullOrEmpty(label)) return;

        bool pushed = false;
        try
        {
            if (_nameFont != null && _nameFont.Available)
            {
                _nameFont.Push();
                pushed = true;
            }
            var ts = ImGui.CalcTextSize(label);
            // Center horizontally on the requested position.
            var textPos = new Vector2(pos.X - ts.X * 0.5f, pos.Y);
            DrawTextOutlined(fg, textPos, label,
                colorR: cfg.TargetNameColorR, colorG: cfg.TargetNameColorG,
                colorB: cfg.TargetNameColorB, colorA: cfg.TargetNameAlpha * _presenceAlpha,
                outR: cfg.TargetNameOutlineColorR, outG: cfg.TargetNameOutlineColorG,
                outB: cfg.TargetNameOutlineColorB, outA: cfg.TargetNameOutlineAlpha * _presenceAlpha);
        }
        finally
        {
            if (pushed) _nameFont?.Pop();
        }
    }

    // ---- Cast bar ----
    private static void DrawCastBar(
        ImDrawListPtr fg,
        Dalamud.Game.ClientState.Objects.Types.IGameObject tgt,
        Configuration cfg,
        Vector2 nameAnchor)
    {
        // Need cast info — only IBattleChara exposes it. Skip if the
        // target isn't a battle entity (e.g. NPC/aetheryte).
        if (tgt is not Dalamud.Game.ClientState.Objects.Types.IBattleChara bc) return;
        if (!bc.IsCasting) return;

        float total = bc.TotalCastTime;
        float elapsed = bc.CurrentCastTime;
        if (total <= 0f) return;
        float pct = MathF.Max(0f, MathF.Min(1f, elapsed / total));

        Vector2 origin;
        switch (cfg.CastBarAnchorMode)
        {
            case 1:  // TargetBone: X/Y are offsets from target bone
                if (!TryProjectBone(tgt, cfg.CastBarBoneIndex, out var bone)) return;
                origin = new Vector2(bone.X + cfg.CastBarX, bone.Y + cfg.CastBarY);
                break;
            case 2:  // TargetName: X/Y are offsets from the target name
                origin = new Vector2(nameAnchor.X + cfg.CastBarX, nameAnchor.Y + cfg.CastBarY);
                break;
            default: // Screen: X/Y are absolute screen pixels
                origin = new Vector2(cfg.CastBarX, cfg.CastBarY);
                break;
        }

        // Center the bar horizontally on the origin point (so the X
        // slider points at the bar's middle, not its left edge).
        float length = MathF.Max(20f, cfg.CastBarLength);
        float height = MathF.Max(2f, cfg.CastBarHeight);
        var topLeft  = new Vector2(origin.X - length * 0.5f, origin.Y);
        var botRight = new Vector2(topLeft.X + length, topLeft.Y + height);

        // Background.
        var bg = new Vector4(cfg.CastBarBgR, cfg.CastBarBgG, cfg.CastBarBgB,
                             cfg.CastBarBgAlpha * _presenceAlpha);
        fg.AddRectFilled(topLeft, botRight, ImGui.GetColorU32(bg));

        // Fill (progress).
        var fill = new Vector4(cfg.CastBarFillR, cfg.CastBarFillG, cfg.CastBarFillB,
                               cfg.CastBarFillAlpha * _presenceAlpha);
        var fillRight = new Vector2(topLeft.X + length * pct, botRight.Y);
        fg.AddRectFilled(topLeft, fillRight, ImGui.GetColorU32(fill));

        // Border (1px outline).
        var border = new Vector4(cfg.CastBarBorderR, cfg.CastBarBorderG, cfg.CastBarBorderB,
                                 cfg.CastBarBorderAlpha * _presenceAlpha);
        fg.AddRect(topLeft, botRight, ImGui.GetColorU32(border));

        // Optional spell-name label.
        if (cfg.EnableCastBarSpellName)
        {
            string spellName = ResolveActionName(bc);
            if (!string.IsNullOrEmpty(spellName))
            {
                EnsureFont(ref _spellFont, ref _spellFontPath, ref _spellFontSize,
                           cfg.CastBarSpellFontPath, cfg.CastBarSpellFontSize);
                bool pushed = false;
                try
                {
                    if (_spellFont != null && _spellFont.Available)
                    {
                        _spellFont.Push();
                        pushed = true;
                    }
                    var labelPos = new Vector2(
                        topLeft.X + cfg.CastBarSpellOffsetX,
                        topLeft.Y + cfg.CastBarSpellOffsetY);
                    DrawTextOutlined(fg, labelPos, spellName,
                        colorR: cfg.CastBarSpellColorR, colorG: cfg.CastBarSpellColorG,
                        colorB: cfg.CastBarSpellColorB, colorA: cfg.CastBarSpellAlpha * _presenceAlpha,
                        outR: cfg.CastBarSpellOutlineColorR, outG: cfg.CastBarSpellOutlineColorG,
                        outB: cfg.CastBarSpellOutlineColorB, outA: cfg.CastBarSpellOutlineAlpha * _presenceAlpha);
                }
                finally
                {
                    if (pushed) _spellFont?.Pop();
                }
            }
        }
    }

    // Resolves the casting action's display name from the Action sheet.
    // Returns "" on failure (NPC casts are usually Action sheet entries
    // too; mount/companion etc. fall through to "").
    private static string ResolveActionName(Dalamud.Game.ClientState.Objects.Types.IBattleChara bc)
    {
        try
        {
            uint id = bc.CastActionId;
            if (id == 0) return "";
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row = sheet?.GetRow(id);
            if (row == null) return "";
            return row.Value.Name.ExtractText();
        }
        catch { return ""; }
    }

    // Projects target's bone to screen-space pixels. Returns false
    // when projection is offscreen.
    //
    // Stability: GetBoneWorldPosition transiently returns Vector3.Zero
    // during animation hand-offs / skeleton rebuilds. If we treated
    // that as "use root position", the anchor would snap from bone-
    // projected pixels to root-projected pixels every flaky frame and
    // back — which is what the user reports as "jittery". We instead
    // re-use the last good bone world-pos for the same (target, bone)
    // pair, and only fall back to root if we've never had a good one.
    //
    // Result is also rounded to the nearest integer pixel so sub-pixel
    // fluctuations don't smear text rendering frame-to-frame.
    private static bool TryProjectBone(
        Dalamud.Game.ClientState.Objects.Types.IGameObject tgt,
        int boneIdx,
        out Vector2 screen)
    {
        screen = default;
        try
        {
            Vector3 root = new Vector3(tgt.Position.X, tgt.Position.Y, tgt.Position.Z);
            Vector3 world = root;
            bool gotBone = false;

            try
            {
                var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)tgt.Address;
                if (go != null && go->DrawObject != null
                    && Hypostasis.Game.Common.getWorldBonePosition.IsValid)
                {
                    var bw = Hypostasis.Game.Common.GetBoneWorldPosition(go, (uint)Math.Max(0, boneIdx));
                    if (bw != Vector3.Zero)
                    {
                        world = bw;
                        gotBone = true;
                        _cachedNameBoneWorld = bw;
                        _cachedNameBoneValid = true;
                        _cachedNameBoneIdx   = boneIdx;
                        _cachedNameBoneOwner = tgt.GameObjectId;
                    }
                }
            }
            catch { /* fall back below */ }

            // No fresh bone read — use the last cached bone position
            // for the same (target, bone) pair. Stale-pos readback
            // looks slightly behind the actual target for one frame,
            // but that's far less jarring than snapping to the root.
            if (!gotBone
                && _cachedNameBoneValid
                && _cachedNameBoneIdx == boneIdx
                && _cachedNameBoneOwner == tgt.GameObjectId)
            {
                world = _cachedNameBoneWorld;
            }

            if (!DalamudApi.GameGui.WorldToScreen(world, out screen)) return false;
            // No integer-pixel snap here — ResolveTargetNameAnchor
            // exp-lerps the projected position to filter idle-
            // animation jitter and rounds at the very end. Snapping
            // here would feed stair-stepped pixel positions into the
            // lerp and lose smoothing precision.
            return true;
        }
        catch { return false; }
    }

    // Cheap text-with-outline rendering: draws the fill on top of four
    // 1px-offset shadow copies. Good enough at small sizes; cheaper
    // than ImGui's stroke call.
    private static void DrawTextOutlined(
        ImDrawListPtr dl, Vector2 pos, string text,
        float colorR, float colorG, float colorB, float colorA,
        float outR, float outG, float outB, float outA)
    {
        if (outA > 0.01f)
        {
            uint outline = ImGui.GetColorU32(new Vector4(outR, outG, outB, outA));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    dl.AddText(new Vector2(pos.X + dx, pos.Y + dy), outline, text);
                }
        }
        if (colorA > 0.01f)
        {
            uint fg = ImGui.GetColorU32(new Vector4(colorR, colorG, colorB, colorA));
            dl.AddText(pos, fg, text);
        }
    }

    // Rebuilds a font handle when path/size changes. Atomic — the old
    // handle is disposed before the new one's allocated.
    //
    // Dalamud's font atlas builds asynchronously: NewDelegateFontHandle
    // queues an atlas rebuild and returns a handle whose .Available
    // becomes true once the rebuild completes (typically the same
    // frame, but not always). Calling Push() before .Available is
    // true silently falls back to the default font — and prior to
    // this diagnostic, we had no visibility into whether the handle
    // ever became Available. If you pick a font and the text still
    // looks like the default, check `/xllog` filtered to `noWickyXIV`
    // for the build/availability log lines below.
    private static void EnsureFont(
        ref IFontHandle handle, ref string loadedPath, ref float loadedSize,
        string requestedPath, float requestedSizePx)
    {
        float size = MathF.Max(6f, requestedSizePx);
        string path = requestedPath ?? "";
        if (path == loadedPath && MathF.Abs(size - loadedSize) < 0.01f) return;

        try { handle?.Dispose(); } catch { }
        handle = null;
        loadedPath = path;
        loadedSize = size;

        if (string.IsNullOrEmpty(path)) return;
        if (!System.IO.File.Exists(path))
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] TargetUI font path not found: {path}"); } catch { }
            return;
        }

        try
        {
            var atlas = DalamudApi.PluginInterface.UiBuilder.FontAtlas;
            handle = atlas.NewDelegateFontHandle(e =>
            {
                e.OnPreBuild(tk =>
                {
                    try
                    {
                        tk.AddFontFromFile(path,
                            new SafeFontConfig
                            {
                                SizePx       = size,
                                OversampleH  = 2,
                                OversampleV  = 1,
                            });
                    }
                    catch (Exception inner)
                    {
                        try { DalamudApi.PluginLog.Warning(
                            $"[noWickyXIV] TargetUI font OnPreBuild threw for {path}: {inner.Message}"); } catch { }
                    }
                });
            });

            // Fire-and-forget: kick the build, log when it actually
            // resolves. Doesn't block the calling thread. Awaiting
            // happens in a non-unsafe helper so the C# compiler is
            // happy (this class is `unsafe` and async-await is illegal
            // inside unsafe scopes).
            WaitForFontBuildAndLog(handle, path, size);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] TargetUI font NewDelegateFontHandle threw for {path} @ {size}px: {ex.Message}"); } catch { }
            handle = null;
        }
    }

    public static void Dispose()
    {
        try { _nameFont?.Dispose(); } catch { }
        try { _spellFont?.Dispose(); } catch { }
        _nameFont = _spellFont = null;
    }

    // ContinueWith instead of async/await because the enclosing class
    // is `unsafe` and `await` isn't allowed inside an unsafe scope.
    private static void WaitForFontBuildAndLog(IFontHandle handle, string path, float size)
    {
        try
        {
            var task = handle.WaitAsync();
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    try { DalamudApi.PluginLog.Warning(
                        $"[noWickyXIV] TargetUI font build FAILED for {path}: {t.Exception?.GetBaseException().Message}"); } catch { }
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] TargetUI font WaitAsync threw for {path}: {ex.Message}"); } catch { }
        }
    }
}
