using System;
using Dalamud.Bindings.ImGui;

namespace noWickyXIV;

// Live in-game keybinds that aren't bound to the FFXIV input system.
// Mirrors the WickedTPS Ctrl+scroll height tweak: hold Ctrl OR Alt, scroll
// wheel up/down nudges the camera's height offset by HeightOffsetStep.
//
// Read mouse wheel + modifiers from ImGui's per-frame IO -- Dalamud always
// pumps Win32 input into ImGui regardless of whether an ImGui window has
// focus, so this works during normal gameplay too. The game's native
// scroll-zoom is suppressed for the SAME frame in Game.GetZoomDeltaDetour
// (returns 0 while modifier is held + wheel != 0) so we don't get both.
public static class InputHandler
{
    // Set true on a frame where Ctrl/Alt + scroll fired; consumed by
    // Game.GetZoomDeltaDetour the same frame so the zoom is suppressed.
    public static bool SuppressNextZoom { get; private set; }

    public static void Update()
    {
        SuppressNextZoom = false;

        try
        {
            var io = ImGui.GetIO();
            float wheel = io.MouseWheel;
            if (Math.Abs(wheel) < 0.001f) return;
            if (!io.KeyCtrl && !io.KeyAlt) return;

            float step = noWickyXIV.Config.HeightOffsetStep;
            float next = noWickyXIV.Config.GlobalHeightOffset + wheel * step;
            // Clamp matches Wicked (PlayerCamera tolerates roughly this range).
            if (next < -2f) next = -2f;
            if (next >  4f) next =  4f;

            if (Math.Abs(next - noWickyXIV.Config.GlobalHeightOffset) > 0.0001f)
            {
                noWickyXIV.Config.GlobalHeightOffset = next;
                noWickyXIV.Config.Save();
            }
            SuppressNextZoom = true;
        }
        catch
        {
            // Defensive: if ImGui IO isn't available this frame, swallow.
        }
    }
}
