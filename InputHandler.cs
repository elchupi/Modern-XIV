using System;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Hypostasis.Game.Structures;

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
    // Legacy flag — kept for compat with anything else that might read it,
    // but our zoom suppression now uses the stateless hook in Game.cs that
    // checks modifier state directly. The flag is no longer load-bearing.
    public static bool SuppressNextZoom { get; private set; }

    // Re-entrancy: when true, the getMouseWheelStatus hook returns the real
    // wheel value (so our own handler reads it). When false, game-side
    // callers get 0 unless they're holding the Shift+Ctrl zoom chord.
    public static bool WheelHandlerActive { get; private set; }

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
        UpdateCursorReleaseHotkey();
        UpdateTeleportMenuHotkey();
        ClickTranslator.Update();
    }

    // ---- F7 (configurable): toggle cursor between mouselook + UI mode ----
    private static void UpdateCursorReleaseHotkey()
    {
        int vk = noWickyXIV.Config.CursorReleaseHotkey;
        if (vk == 0) return;
        if (EdgePressed(vk))
            CameraDynamics.ToggleCursorRelease();
    }

    // ---- Shift+M (configurable): toggle custom teleport menu ----
    private static void UpdateTeleportMenuHotkey()
    {
        if (!noWickyXIV.Config.EnableCustomTeleportMenu) return;
        int vk = noWickyXIV.Config.TeleportMenuHotkey;
        if (vk == 0) return;
        if (!ShiftHeld) return;
        if (EdgePressed(vk))
        {
            // Consume the key so the game doesn't also open the map (M default).
            try { DalamudApi.KeyState[vk] = false; } catch { }

            if (TeleportMenu.IsWindowOpen)
                TeleportMenu.OnWindowClosed();
            else
            {
                TeleportMenu.IsWindowOpen = true;
                TeleportMenu.SearchFilter = "";
                TeleportMenu.RefreshCache();
            }
        }
    }

    // ---- Wheel + modifier readers (game-side, ImGui-independent) ----
    // ImGui IO.MouseWheel was unreliable from Framework.Update — Dalamud's
    // ImGui input pump may not have populated it for the current tick. Using
    // the game's own wheel reader (InputData.GetMouseWheelStatus) and Win32
    // GetAsyncKeyState for modifiers makes both reads real-time and removes
    // the ordering dependency entirely.
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool KeyHeld(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static bool ShiftHeld => KeyHeld(0x10) || KeyHeld(0xA0) || KeyHeld(0xA1);
    private static bool CtrlHeld  => KeyHeld(0x11) || KeyHeld(0xA2) || KeyHeld(0xA3);
    private static bool AltHeld   => KeyHeld(0x12) || KeyHeld(0xA4) || KeyHeld(0xA5);

    // De-dup wheel ticks: GetMouseWheelStatus may report the same direction
    // across consecutive frames after a single notch; we want one apply per
    // event. Track the previous-frame value and only fire on transitions /
    // first non-zero.
    private static sbyte _wheelPrev;

    // ---- Scroll-wheel modifier matrix ----
    //   plain scroll  → cycle hostile NPCs (nearest-first)
    //   Shift + scroll → cycle party members (slot order)
    //   Ctrl  + scroll → height (GlobalHeightOffset)
    //   Alt   + scroll → shoulder (active preset's SideOffset)
    //   Shift+Ctrl     → zoom (the ONLY way to zoom; handled by game)
    //   any other combo → ignored
    private static void UpdateScrollHeight()
    {
        try
        {
            sbyte wheelStatus;
            WheelHandlerActive = true;
            try
            {
                try { wheelStatus = InputData.GetMouseWheelStatus(); }
                catch { return; }
            }
            finally { WheelHandlerActive = false; }
            // Edge-detect: only fire when wheel transitions from 0 (or sign-flip).
            sbyte prev = _wheelPrev;
            _wheelPrev = wheelStatus;
            if (wheelStatus == 0) return;
            if (wheelStatus == prev) return; // already fired for this hold

            float wheel = wheelStatus; // -1, 0, +1
            int dirRaw = wheel > 0 ? 1 : -1;

            // Chat scroll: if the cursor is over the bubble column,
            // route the wheel into ChatBubbles for history scroll and
            // suppress everything else (target-cycle, height-offset,
            // etc.) — those would also fire on plain wheel and
            // shouldn't while reading chat.
            if (ChatBubbles.IsCursorOverColumn())
            {
                ChatBubbles.OnWheel(dirRaw);
                SuppressNextZoom = true;
                return;
            }

            bool shift = ShiftHeld, ctrl = CtrlHeld, alt = AltHeld;

            // Shift+Ctrl is reserved for zoom. Default behavior: let
            // the engine zoom. With FOV-zoom continuation: take full
            // ownership and apply currentZoom (when above MinZoom)
            // OR currentFoV-narrow (when at MinZoom). Avoids the
            // pivot-to-overhead feel at extreme close zoom by
            // freezing camera position and "zooming in" optically.
            if (shift && ctrl)
            {
                if (noWickyXIV.Config.EnableFovZoomContinuation)
                    HandleFovZoomWheel(wheel);
                return;
            }

            // Ambiguous combos: skip
            if (ctrl && alt) return;
            if (shift && alt) return;

            // Sign convention: scroll-up = +1 (next), scroll-down = -1 (prev).
            int dir = wheel > 0 ? 1 : -1;

            if (!shift && !ctrl && !alt)
            {
                // PLAIN scroll → cycle hostile NPC target
                TargetCycle.CycleEnemy(dir);
                SuppressNextZoom = true;
                return;
            }

            if (shift && !ctrl && !alt)
            {
                // SHIFT+scroll → cycle party member target
                TargetCycle.CyclePartyMember(dir);
                SuppressNextZoom = true;
                return;
            }

            if (ctrl)
            {
                // HEIGHT — write to the active preset's HeightOffset
                // (the same field the editor's "Camera Height Offset"
                // slider shows). User-visible: scroll up/down adjusts
                // the saved baseline directly so the editor reflects
                // the change immediately and the value persists.
                // Cancel any in-flight transition so the lerp doesn't
                // fight the user's manual nudge.
                PresetManager.CancelTransitionToTarget();
                float step = noWickyXIV.Config.HeightOffsetStep;
                var active = PresetManager.CurrentPreset;

                if (active != null && active != PresetManager.DefaultPreset)
                {
                    float next = active.HeightOffset + wheel * step;
                    if (next < -10f) next = -10f;
                    if (next >  10f) next =  10f;
                    if (Math.Abs(next - active.HeightOffset) > 0.0001f)
                    {
                        active.HeightOffset = next;
                        noWickyXIV.Config.SaveDebounced();
                    }
                }
                else
                {
                    float next = noWickyXIV.Config.GlobalHeightOffset + wheel * step;
                    if (next < -10f) next = -10f;
                    if (next >  10f) next =  10f;
                    if (Math.Abs(next - noWickyXIV.Config.GlobalHeightOffset) > 0.0001f)
                    {
                        noWickyXIV.Config.GlobalHeightOffset = next;
                        noWickyXIV.Config.SaveDebounced();
                    }
                }
                SuppressNextZoom = true;
            }
            else if (alt)
            {
                // SHOULDER — active preset's SideOffset. Reuses HeightOffsetStep
                // for now (typical 0.1 m steps feel right for shoulder too).
                // Inverted relative to height: scroll-down = +SideOffset (right
                // shoulder), scroll-up = -SideOffset (left).
                // Cancel any in-flight transition so we don't fight the lerp.
                PresetManager.CancelTransitionToTarget();
                var preset = PresetManager.CurrentPreset;
                if (preset == null) return;
                float step = noWickyXIV.Config.HeightOffsetStep;
                float next = preset.SideOffset - wheel * step;
                // Cammy's SideOffset is unbounded but +/-2 is plenty extreme.
                if (next < -2f) next = -2f;
                if (next >  2f) next =  2f;

                if (Math.Abs(next - preset.SideOffset) > 0.0001f)
                {
                    preset.SideOffset = next;
                    noWickyXIV.Config.SaveDebounced();
                    // Don't call preset.Apply() — that re-snaps the
                    // entire camera state, including writing
                    // cam->lookAtHeightOffset = preset.LookAtHeightOffset
                    // which wipes PitchTilt's accumulated offset and
                    // shows up as a vertical jump on every Alt+scroll
                    // tick. PresetManager.Update's outside-transition
                    // pass mirrors preset.SideOffset → EffectiveSideOffset
                    // each frame anyway, so the new value flows through
                    // without snapping the look-at field.
                }
                SuppressNextZoom = true;
            }
        }
        catch { /* defensive */ }
    }

    // FOV-zoom continuation. Takes ownership of the Shift+Ctrl+wheel
    // chord:
    //   currentZoom > MinZoom → normal zoom (decrease/increase distance)
    //   currentZoom at MinZoom + scrolling in → narrow currentFoV
    //   currentFoV narrowed + scrolling out → restore FoV first,
    //                                          THEN zoom out
    // Net effect: when zooming in past the configured min distance,
    // the camera position freezes and the view continues to "zoom"
    // optically via FoV. No pivot to overhead, no fight with wall
    // collision.
    private static unsafe void HandleFovZoomWheel(float wheel)
    {
        try
        {
            var cm = Common.CameraManager;
            if (cm == null) return;
            var cam = cm->worldCamera;
            if (cam == null) return;
            var preset = PresetManager.CurrentPreset;
            if (preset == null) return;

            // FFXIV default: scroll up (wheel +1) = zoom in (currentZoom decreases).
            bool zoomingIn = wheel > 0;
            float zoomStep = preset.ZoomDelta;
            float fovStep  = preset.FoVDelta;
            float minZoom  = preset.MinZoom;
            float maxZoom  = preset.MaxZoom;
            float minFoV   = MathF.Min(preset.MinFoV, noWickyXIV.Config.FovZoomMinFov);
            float maxFoV   = preset.MaxFoV;

            const float zoomSlop = 0.01f;
            const float fovSlop  = 0.001f;
            bool atMinZoom    = cam->currentZoom <= minZoom + zoomSlop;
            bool fovNarrowed  = cam->currentFoV  <  maxFoV  - fovSlop;

            if (zoomingIn)
            {
                if (atMinZoom)
                {
                    // Zoom in past min: narrow FoV instead.
                    cam->currentFoV = MathF.Max(minFoV, cam->currentFoV - fovStep);
                }
                else
                {
                    cam->currentZoom = MathF.Max(minZoom, cam->currentZoom - zoomStep);
                }
            }
            else
            {
                if (fovNarrowed)
                {
                    // Zoom out: restore FoV before changing distance.
                    cam->currentFoV = MathF.Min(maxFoV, cam->currentFoV + fovStep);
                }
                else
                {
                    cam->currentZoom = MathF.Min(maxZoom, cam->currentZoom + zoomStep);
                }
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
