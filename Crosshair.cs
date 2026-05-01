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
            var center = vp.Pos + vp.Size * 0.5f;

            float size = MathF.Max(2f, noWickyXIV.Config.CrosshairSize) * ImGuiHelpers.GlobalScale;
            float thickness = MathF.Max(1f, noWickyXIV.Config.CrosshairThickness);

            var col = new Vector4(
                noWickyXIV.Config.CrosshairColorR,
                noWickyXIV.Config.CrosshairColorG,
                noWickyXIV.Config.CrosshairColorB,
                noWickyXIV.Config.CrosshairColorA * _alpha);
            uint c = ImGui.GetColorU32(col);

            float gap = size * 0.3f;

            // Horizontal arms (left + right)
            dl.AddLine(new Vector2(center.X - size, center.Y),
                       new Vector2(center.X - gap,  center.Y), c, thickness);
            dl.AddLine(new Vector2(center.X + gap,  center.Y),
                       new Vector2(center.X + size, center.Y), c, thickness);
            // Vertical arms (top + bottom)
            dl.AddLine(new Vector2(center.X, center.Y - size),
                       new Vector2(center.X, center.Y - gap),  c, thickness);
            dl.AddLine(new Vector2(center.X, center.Y + gap),
                       new Vector2(center.X, center.Y + size), c, thickness);
            // Center dot
            dl.AddCircleFilled(center, 1.5f * ImGuiHelpers.GlobalScale, c);
        }
        catch { /* defensive */ }
    }

    private static bool ShouldHide()
    {
        if (PluginUI.IsVisible) return true;
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
