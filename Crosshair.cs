using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;

namespace noWickyXIV;

// Centered crosshair overlay drawn via ImGui's foreground draw list.
// Ports the visual intent of WickedTPS.cs:2237-2335 — three pieces (cross
// arms + center dot), color/size/alpha configurable, fades on toggle, hides
// in cutscenes / settings UI.
public static class Crosshair
{
    private static float _alpha; // 0..1 fade

    /// <summary>Screen-space center of the crosshair reticle, including
    /// the user's X/Y offset. Used by auto-target hit-testing to find
    /// the enemy under the reticle.</summary>
    public static Vector2 GetScreenCenter()
    {
        try
        {
            var vp = ImGui.GetMainViewport();
            return vp.Pos + vp.Size * 0.5f
                 + new Vector2(noWickyXIV.Config.CrosshairOffsetX,
                               noWickyXIV.Config.CrosshairOffsetY);
        }
        catch { return Vector2.Zero; }
    }

    /// <summary>True while the reticle is at least partially visible
    /// on screen — gates the auto-target action so a hidden reticle
    /// doesn't keep firing target-picks.</summary>
    public static bool IsVisible => _alpha > 0.05f;

    // ---- Auto-target under crosshair ----
    // When the user has no current target AND the reticle is sitting
    // on an enemy on screen, auto-pick that enemy so the user's
    // queued action / right-click attack lands. Stops once a target
    // is set (by us or the user); doesn't fight an explicit pick.
    public static void Update()
    {
        if (!noWickyXIV.Config.EnableCrosshair) return;
        if (!noWickyXIV.Config.EnableCrosshairAutoTarget) return;
        if (ShouldHide()) return;
        if (!IsVisible) return; // reticle faded out — don't pick

        try
        {
            var current = DalamudApi.TargetManager?.Target;
            // Only auto-pick when nothing is already targeted. Lets
            // the user manually un-target (Esc) to "release" the
            // auto-pick and re-acquire whatever's under the reticle.
            if (current != null) return;

            var crossPos = GetScreenCenter();
            float bestDist2 = noWickyXIV.Config.CrosshairAutoTargetRadius
                            * noWickyXIV.Config.CrosshairAutoTargetRadius;
            Dalamud.Game.ClientState.Objects.Types.IGameObject best = null;

            ulong selfId = DalamudApi.ObjectTable?.LocalPlayer?.GameObjectId ?? 0UL;
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null || !obj.IsValid()) continue;
                if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn) continue;
                if (!bn.IsTargetable) continue;
                if (bn.CurrentHp <= 0 || bn.MaxHp <= 0) continue;
                if (bn.GameObjectId == selfId) continue;
                // Skip friendly NPCs (BattleNpcKind 2). Anything else
                // is fair game.
                if ((byte)bn.BattleNpcKind == 2) continue;

                // Project the actor's chest height to screen and pick
                // the closest one to the crosshair within the radius.
                var world = new Vector3(
                    bn.Position.X,
                    bn.Position.Y + 1.0f,
                    bn.Position.Z);
                if (!DalamudApi.GameGui.WorldToScreen(world, out var screen)) continue;

                float dx = screen.X - crossPos.X;
                float dy = screen.Y - crossPos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    best = bn;
                }
            }

            if (best != null)
                DalamudApi.TargetManager.Target = best;
        }
        catch { /* defensive */ }
    }

    public static void Draw()
    {
        float dt;
        try { dt = ImGui.GetIO().DeltaTime; } catch { dt = 0.016f; }
        if (dt <= 0f) dt = 0.016f;

        bool wantOn = noWickyXIV.Config.EnableCrosshair && !ShouldHide();
        float target = wantOn ? 1f : 0f;
        float rate = MathF.Max(0.5f, noWickyXIV.Config.CrosshairFadeSpeed);
        _alpha += (target - _alpha) * (1f - MathF.Exp(-rate * dt));

        if (_alpha < 0.01f) return;

        try
        {
            var dl = ImGui.GetForegroundDrawList();
            var vp = ImGui.GetMainViewport();
            var center = vp.Pos + vp.Size * 0.5f
                       + new Vector2(noWickyXIV.Config.CrosshairOffsetX,
                                     noWickyXIV.Config.CrosshairOffsetY);

            float size = MathF.Max(2f, noWickyXIV.Config.CrosshairSize) * ImGuiHelpers.GlobalScale;
            float thickness = MathF.Max(1f, noWickyXIV.Config.CrosshairThickness);

            var col = new Vector4(
                noWickyXIV.Config.CrosshairColorR,
                noWickyXIV.Config.CrosshairColorG,
                noWickyXIV.Config.CrosshairColorB,
                noWickyXIV.Config.CrosshairColorA * _alpha);
            uint c = ImGui.GetColorU32(col);

            // Ring crosshair — single circle outline at the configured
            // size (size = radius). Replaces the 4-arm cross that the
            // earlier port used; cleaner reticle for a TPS aim point.
            dl.AddCircle(center, size, c, 0, thickness);
        }
        catch { /* defensive */ }
    }

    private static bool ShouldHide()
    {
        // Intentionally NOT hidden while the plugin's settings window
        // is open — the user needs to see the reticle to dial in
        // Offset X / Offset Y sliders against the actual crosshair
        // position. Cutscene gates are kept since the engine fades
        // gameplay UI during those.
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
