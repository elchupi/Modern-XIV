using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace noWickyXIV;

// Live in-game keybinds that aren't bound to the FFXIV input system.
//
// - Ctrl/Alt + scroll: nudges Configuration.GlobalHeightOffset by HeightOffsetStep
// - F6 (configurable): toggles the settings panel
// - Crosshair toggle (V default): flips Configuration.EnableCrosshair
// - Shoulder swap (configurable): flips sign of active preset's SideOffset
// - Ctrl+1..9 (when enabled): activates preset slot N
//
// Input read pattern:
// - Mouse wheel + ImGui modifiers via ImGui IO (Dalamud always pumps Win32 input
//   into ImGui regardless of focus, so this works during gameplay).
// - Keyboard hotkeys via Dalamud's IKeyState. We edge-detect against a per-key
//   previous-frame cache to fire ONCE per press.
public static class InputHandler
{
    public static bool SuppressNextZoom { get; private set; }

    // Edge-detect cache for keyboard hotkeys. Kept by virtual-key int.
    private static readonly System.Collections.Generic.Dictionary<int, bool> _keyPrev = new();

    public static void Update()
    {
        SuppressNextZoom = false;

        UpdateScrollHeight();
        UpdateSettingsHotkey();
        UpdateCrosshairHotkey();
        UpdateShoulderSwapHotkey();
        UpdatePresetSlotHotkeys();
    }

    // ---- Ctrl + scroll = height nudge; Alt + scroll = shoulder nudge ----
    // Mirrors Wicked's bindings (Ctrl+scroll for HeightOffset). Alt+scroll
    // drives the active preset's SideOffset (Cammy's "Camera Side Offset").
    // Both suppress the game's normal zoom-on-scroll for the same frame.
    private static void UpdateScrollHeight()
    {
        try
        {
            var io = ImGui.GetIO();
            float wheel = io.MouseWheel;
            if (Math.Abs(wheel) < 0.001f) return;

            // Ambiguous combos: skip
            if (io.KeyCtrl && io.KeyAlt) return;

            if (io.KeyCtrl)
            {
                // HEIGHT — global offset persisted across preset switches
                float step = noWickyXIV.Config.HeightOffsetStep;
                float next = noWickyXIV.Config.GlobalHeightOffset + wheel * step;
                if (next < -2f) next = -2f;
                if (next >  4f) next =  4f;

                if (Math.Abs(next - noWickyXIV.Config.GlobalHeightOffset) > 0.0001f)
                {
                    noWickyXIV.Config.GlobalHeightOffset = next;
                    noWickyXIV.Config.Save();
                }
                SuppressNextZoom = true;
            }
            else if (io.KeyAlt)
            {
                // SHOULDER — active preset's SideOffset. Reuses HeightOffsetStep
                // for now (typical 0.1 m steps feel right for shoulder too).
                var preset = PresetManager.CurrentPreset;
                if (preset == null) return;
                float step = noWickyXIV.Config.HeightOffsetStep;
                float next = preset.SideOffset + wheel * step;
                // Cammy's SideOffset is unbounded but +/-2 is plenty extreme.
                if (next < -2f) next = -2f;
                if (next >  2f) next =  2f;

                if (Math.Abs(next - preset.SideOffset) > 0.0001f)
                {
                    preset.SideOffset = next;
                    noWickyXIV.Config.Save();
                    try { preset.Apply(); } catch { }
                }
                SuppressNextZoom = true;
            }
        }
        catch { /* defensive */ }
    }

    // ---- F6 (configurable): toggle settings panel ----
    private static void UpdateSettingsHotkey()
    {
        int vk = noWickyXIV.Config.SettingsHotkey;
        if (vk == 0) return;
        if (EdgePressed(vk))
            PluginUI.IsVisible = !PluginUI.IsVisible;
    }

    // ---- V (configurable): toggle crosshair ----
    private static void UpdateCrosshairHotkey()
    {
        int vk = noWickyXIV.Config.CrosshairHotkey;
        if (vk == 0) return;
        if (EdgePressed(vk))
        {
            noWickyXIV.Config.EnableCrosshair = !noWickyXIV.Config.EnableCrosshair;
            noWickyXIV.Config.Save();
        }
    }

    // ---- Shoulder swap (configurable): flip active preset's SideOffset ----
    private static void UpdateShoulderSwapHotkey()
    {
        int vk = noWickyXIV.Config.ShoulderSwapHotkey;
        if (vk == 0) return;
        if (!EdgePressed(vk)) return;

        var preset = PresetManager.CurrentPreset;
        if (preset == null) return;
        preset.SideOffset = -preset.SideOffset;
        noWickyXIV.Config.Save();
        try { preset.Apply(); } catch { }
    }

    // ---- Ctrl+1..9: load preset slot N ----
    private static void UpdatePresetSlotHotkeys()
    {
        if (!noWickyXIV.Config.PresetHotkeysEnabled) return;
        // Only fire while Ctrl is held — avoids "1" doing anything during chat etc.
        try { if (!ImGui.GetIO().KeyCtrl) return; } catch { return; }

        var slots = noWickyXIV.Config.PresetHotkeys;
        if (slots == null) return;
        int n = Math.Min(slots.Count, noWickyXIV.Config.Presets.Count);
        for (int i = 0; i < n; i++)
        {
            int vk = slots[i];
            if (vk == 0) continue;
            if (!EdgePressed(vk)) continue;
            var p = noWickyXIV.Config.Presets[i];
            if (p == null) continue;
            PresetManager.CurrentPreset = p;
            try { p.Apply(); } catch { }
            return; // one slot per frame
        }
    }

    // Edge-detect: returns true on the frame the key transitions released->held.
    // Uses Dalamud's IKeyState (indexer: bool = currently held this frame).
    private static bool EdgePressed(int vk)
    {
        bool now;
        try { now = DalamudApi.KeyState[vk]; }
        catch { return false; }

        bool was = _keyPrev.TryGetValue(vk, out var p) && p;
        _keyPrev[vk] = now;
        return now && !was;
    }
}
