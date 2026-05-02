using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace noWickyXIV;

public static class PluginUI
{
    private static bool isVisible = false;
    public static bool IsVisible
    {
        get => isVisible;
        set => isVisible = value;
    }

    private static int selectedPreset = -1;
    private static CameraConfigPreset CurrentPreset => 0 <= selectedPreset && selectedPreset < noWickyXIV.Config.Presets.Count ? noWickyXIV.Config.Presets[selectedPreset] : null;

    public static void Draw()
    {
        if (!isVisible) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(700, 710) * ImGuiHelpers.GlobalScale, new Vector2(9999));
        ImGui.Begin("noWickyXIV Configuration", ref isVisible);
        ImGuiEx.AddDonationHeader();

        if (ImGui.BeginTabBar("CammyTabs"))
        {
            if (ImGui.BeginTabItem("Presets"))
            {
                DrawPresetList();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Camera Dynamics"))
            {
                DrawCameraDynamics();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Other Settings"))
            {
                DrawOtherSettings();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static void DrawPresetList()
    {
        var currentPreset = CurrentPreset;
        var hasSelectedPreset = currentPreset != null;

        ImGui.PushFont(UiBuilder.IconFont);

        if (ImGui.Button(FontAwesomeIcon.PlusCircle.ToIconString()))
        {
            noWickyXIV.Config.Presets.Add(new());
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button(FontAwesomeIcon.Copyright.ToIconString()) && hasSelectedPreset)
        {
            noWickyXIV.Config.Presets.Add(CurrentPreset.Clone());
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button(FontAwesomeIcon.ArrowCircleUp.ToIconString()) && hasSelectedPreset)
        {
            var preset = CurrentPreset;
            noWickyXIV.Config.Presets.RemoveAt(selectedPreset);

            selectedPreset = Math.Max(selectedPreset - 1, 0);

            noWickyXIV.Config.Presets.Insert(selectedPreset, preset);
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button(FontAwesomeIcon.ArrowCircleDown.ToIconString()) && hasSelectedPreset)
        {
            var preset = CurrentPreset;
            noWickyXIV.Config.Presets.RemoveAt(selectedPreset);

            selectedPreset = Math.Min(selectedPreset + 1, noWickyXIV.Config.Presets.Count);

            noWickyXIV.Config.Presets.Insert(selectedPreset, preset);
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();

        ImGui.Button(FontAwesomeIcon.TimesCircle.ToIconString());
        if (hasSelectedPreset && ImGui.BeginPopupContextItem(ImU8String.Empty, ImGuiPopupFlags.MouseButtonLeft))
        {
            if (ImGui.Selectable(FontAwesomeIcon.TrashAlt.ToIconString()))
            {
                noWickyXIV.Config.Presets.RemoveAt(selectedPreset);
                selectedPreset = Math.Min(selectedPreset, noWickyXIV.Config.Presets.Count - 1);
                currentPreset = CurrentPreset;
                hasSelectedPreset = currentPreset != null;
                noWickyXIV.Config.Save();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();

        ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());

        ImGui.PopFont();

        ImGuiEx.SetItemTooltip("You can CTRL + Left Click sliders to input values manually.");

        ImGui.BeginChild("CammyPresetList", new Vector2(250 * ImGuiHelpers.GlobalScale, 0), true);

        for (int i = 0; i < noWickyXIV.Config.Presets.Count; i++)
        {
            var preset = noWickyXIV.Config.Presets[i];

            ImGui.PushID(i);

            var isActive = preset == PresetManager.ActivePreset;
            var isOverride = preset == PresetManager.PresetOverride;

            if (isActive || isOverride)
                ImGui.PushStyleColor(ImGuiCol.Text, !isOverride ? 0xFF00FF00 : 0xFFFFAF00);

            if (ImGui.Selectable(preset.Name, selectedPreset == i))
                selectedPreset = i;

            if (isActive || isOverride)
                ImGui.PopStyleColor();

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                PresetManager.CurrentPreset = !isOverride ? preset : null;

            ImGui.PopID();
        }

        ImGui.EndChild();

        if (!hasSelectedPreset) return;

        ImGui.SameLine();
        ImGui.BeginChild("CammyPresetEditor", Vector2.Zero, true);
        DrawPresetEditor(currentPreset);
        ImGui.EndChild();
    }

    private static void ResetSliderFloat(string id, ref float val, float min, float max, float reset, string format)
    {
        var save = false;

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.UndoAlt.ToIconString()}##{id}"))
        {
            val = reset;
            save = true;
        }
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 150 * ImGuiHelpers.GlobalScale);
        save |= ImGui.SliderFloat(id, ref val, min, max, format);

        if (!save) return;
        noWickyXIV.Config.Save();
        if (CurrentPreset == PresetManager.CurrentPreset)
            CurrentPreset.Apply();
    }

    private static void AddSubtractAction(string id, float step, Action<float> action)
    {
        var save = false;

        ImGui.BeginGroup();
        ImGui.PushButtonRepeat(true);
        if (ImGui.ArrowButton($"##Subtract{id}", ImGuiDir.Down))
        {
            action(-step);
            save = true;
        }
        ImGui.SameLine();
        if (ImGui.ArrowButton($"##Add{id}", ImGuiDir.Up))
        {
            action(step);
            save = true;
        }
        ImGui.PopButtonRepeat();
        ImGui.SameLine();
        ImGui.TextUnformatted(id);
        ImGui.EndGroup();

        if (!save) return;
        noWickyXIV.Config.Save();
        if (CurrentPreset == PresetManager.CurrentPreset)
            CurrentPreset.Apply();
    }

    private static void ResetSliderFloat(string id, ref float val, float min, float max, Func<float> reset, string format)
    {
        var save = false;

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.UndoAlt.ToIconString()}##{id}"))
        {
            val = reset();
            save = true;
        }
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 150 * ImGuiHelpers.GlobalScale);
        save |= ImGui.SliderFloat(id, ref val, min, max, format);

        if (!save) return;
        noWickyXIV.Config.Save();
        if (CurrentPreset == PresetManager.CurrentPreset)
            CurrentPreset.Apply();
    }

    private static void DrawPresetEditor(CameraConfigPreset preset)
    {
        if (ImGui.InputText("Name", ref preset.Name, 64))
            noWickyXIV.Config.Save();

        ImGui.Spacing();

        ImGui.Columns(3, ImU8String.Empty, false);
        if (ImGui.Checkbox("Starting Zoom##Use", ref preset.UseStartZoom))
            noWickyXIV.Config.Save();
        ImGui.NextColumn();
        if (ImGui.Checkbox("Starting FoV##Use", ref preset.UseStartFoV))
            noWickyXIV.Config.Save();
        if (preset.UseStartZoom || preset.UseStartFoV)
        {
            ImGui.NextColumn();
            if (ImGui.Checkbox("Only on Login", ref preset.UseStartOnLogin))
                noWickyXIV.Config.Save();
        }
        ImGui.Columns(1);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var arrowOffset = ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().ItemSpacing.X + 25 * ImGuiHelpers.GlobalScale;
        ImGui.Spacing();
        ImGui.SameLine(arrowOffset);
        AddSubtractAction("Zoom", 0.1f, x =>
        {
            preset.StartZoom += x;
            preset.MinZoom += x;
            preset.MaxZoom += x;
        });

        if (preset.UseStartZoom)
            ResetSliderFloat("Starting##Zoom", ref preset.StartZoom, preset.MinZoom, preset.MaxZoom, 6, "%.2f");
        ResetSliderFloat("Minimum##Zoom", ref preset.MinZoom, 1, preset.MaxZoom, 1.5f, "%.2f");
        ResetSliderFloat("Maximum##Zoom", ref preset.MaxZoom, preset.MinZoom, 100, 20, "%.2f");
        ResetSliderFloat("Delta##Zoom", ref preset.ZoomDelta, 0, 5, 0.75f, "%.2f");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Spacing();
        ImGui.SameLine(arrowOffset);
        AddSubtractAction("Field of View", 0.01f, x =>
        {
            preset.StartFoV += x;
            preset.MinFoV += x;
            preset.MaxFoV += x;
        });
        ImGuiEx.SetItemTooltip("In some weather, the FoV will cause lag or crash if the total is 3.14.");

        if (preset.UseStartFoV)
            ResetSliderFloat("Starting##FoV", ref preset.StartFoV, preset.MinFoV, preset.MaxFoV, 0.78f, "%f");
        ResetSliderFloat("Minimum##FoV", ref preset.MinFoV, 0.01f, preset.MaxFoV, 0.69f, "%f");
        ResetSliderFloat("Maximum##FoV", ref preset.MaxFoV, preset.MinFoV, 3, 0.78f, "%f");
        ResetSliderFloat("Delta##FoV", ref preset.FoVDelta, 0, 0.5f, 0.08726646751f, "%f");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ResetSliderFloat("Minimum V Rotation", ref preset.MinVRotation, -1.569f, preset.MaxVRotation, -1.483530f, "%f");
        ResetSliderFloat("Maximum V Rotation", ref preset.MaxVRotation, preset.MinVRotation, 1.569f, 0.785398f, "%f");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ResetSliderFloat("Camera Height Offset", ref preset.HeightOffset, -1, 1, 0, "%.2f");
        ResetSliderFloat("Camera Side Offset", ref preset.SideOffset, -1, 1, 0, "%.2f");
        ResetSliderFloat("Tilt", ref preset.Tilt, -MathF.PI, MathF.PI, 0, "%f");
        ImGuiEx.SetItemTooltip("Not meant for general gameplay use! Will be moved to a separate feature in a later update.");
        ResetSliderFloat("Look at Height Offset", ref preset.LookAtHeightOffset, -10, 10, () => Game.GetDefaultLookAtHeightOffset() ?? 0, "%f");

        if (ImGuiEx.EnumCombo("View Bobbing", ref preset.ViewBobMode))
            noWickyXIV.Config.Save();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var qolBarEnabled = IPC.QoLBarEnabled;
        var conditionSets = qolBarEnabled ? IPC.QoLBarConditionSets : [];
        var display = preset.ConditionSet >= 0
            ? preset.ConditionSet < conditionSets.Length
                ? $"[{preset.ConditionSet + 1}] {conditionSets[preset.ConditionSet]}"
                : (preset.ConditionSet + 1).ToString()
            : "None";

        if (ImGui.BeginCombo("Condition Set", display))
        {
            if (ImGui.Selectable("None##ConditionSet", preset.ConditionSet < 0))
            {
                preset.ConditionSet = -1;
                noWickyXIV.Config.Save();
            }

            if (qolBarEnabled)
            {
                for (int i = 0; i < conditionSets.Length; i++)
                {
                    var name = conditionSets[i];
                    if (!ImGui.Selectable($"[{i + 1}] {name}", i == preset.ConditionSet)) continue;
                    preset.ConditionSet = i;
                    noWickyXIV.Config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGuiEx.SetItemTooltip("Uses a QoL Bar Condition Set to automatically swap to this preset." +
            "\nPresets higher in the list will have priority over lower ones." +
            "\nCondition Sets should be made using the QoL Bar plugin config." +
            "\nPlease see the \"Other Settings\" tab to verify if QoL Bar was detected.");
    }

    // ---- Wicked-style settings rows for global Configuration fields ----
    // Cammy's existing ResetSliderFloat is per-preset; these helpers are
    // for the global Configuration entries (mirroring Wicked's pattern of
    // typed entries with defaults and live save).

    private static void ConfigSliderFloat(string id, ref float val, float min, float max, float reset, string format = "%.2f")
    {
        var save = false;
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.UndoAlt.ToIconString()}##{id}"))
        {
            val = reset;
            save = true;
        }
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 150 * ImGuiHelpers.GlobalScale);
        save |= ImGui.SliderFloat(id, ref val, min, max, format);
        if (save) noWickyXIV.Config.Save();
    }

    private static void ConfigCheckbox(string id, ref bool val)
    {
        if (ImGui.Checkbox(id, ref val))
            noWickyXIV.Config.Save();
    }

    private static string _dynamicsSearch = string.Empty;

    private static bool DynamicsSectionMatches(string name)
    {
        if (string.IsNullOrEmpty(_dynamicsSearch)) return true;
        return name.IndexOf(_dynamicsSearch, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ResetAllDynamicsToDefaults()
    {
        var c = noWickyXIV.Config;
        c.EnableRollTilt = true;
        c.RollTiltMaxAngle = 1.92f;  c.RollTiltSensitivity = 0.2f;
        c.RollTiltOnRate = 2.47f;    c.RollTiltOffRate = 1.0f;
        c.EnableYawLag = false;      c.YawLagHalflife = 0.8f;
        c.EnablePitchTilt = true;    c.PitchTiltMaxOffset = 1.24f; c.PitchTiltSmoothRate = 3.19f;
        c.EnablePositionFloat = true;
        c.PositionFloatLagFactor = 0.15f;  c.PositionFloatSmoothTime = 0.18f;
        c.SwivelOnMove = false;
        c.SwivelDelay = 0.15f;       c.SwivelSpeed = 240f;        c.SwivelMoveThreshold = 0.05f;
        c.EnableAdsOnRmb = false;
        c.AdsZoomFactor = 1.5f;      c.AdsTransitionSpeed = 8f;
        c.EnableCombatZoom = false;
        c.CombatZoomDistance = 12f;  c.CombatZoomTransitionSpeed = 4f;
        c.EnableAutoShoulderSwap = false;
        c.ShoulderLerpDuration = 0.35f;  c.ShoulderSwapSafetyMargin = 0.4f; c.ShoulderSwapCheckHz = 5f;
        c.EnableCrosshair = false;
        c.CrosshairSize = 8f; c.CrosshairThickness = 2f; c.CrosshairFadeSpeed = 6f;
        c.CrosshairColorR = 1f; c.CrosshairColorG = 1f; c.CrosshairColorB = 1f; c.CrosshairColorA = 0.85f;
        c.InstantMode = false;
        c.HeightOffsetStep = 0.1f;   c.GlobalHeightOffset = 0f;
        c.MouseSensitivityMul = 1f; c.GamepadSensitivityMul = 1f;
        c.InvertMouseY = false;     c.InvertGamepadY = false;
        c.EnableMouseLookAlways = false;
        c.MouseLookSensitivity = 0.005f; c.MouseLookInvertY = false;
        c.MouseLookCenterCursor = true;
        c.CursorReleaseHotkey = 0x76; // F7
        c.Save();
    }

    private static void DrawCameraDynamics()
    {
        // Search + reset-all toolbar
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 180 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Search##dynamics", ref _dynamicsSearch, 64);
        ImGui.SameLine();
        if (ImGui.Button("Reset all to defaults"))
            ResetAllDynamicsToDefaults();
        ImGui.Spacing();

        // Section: Roll Tilt
        if (DynamicsSectionMatches("Roll Tilt") && ImGuiEx.BeginGroupBox("Roll Tilt (bank into turns)"))
        {
            ConfigCheckbox("Enable##RollTilt", ref noWickyXIV.Config.EnableRollTilt);
            ConfigSliderFloat("Max roll (deg)##RollTilt",       ref noWickyXIV.Config.RollTiltMaxAngle,    0f,    10f, 1.92f);
            ConfigSliderFloat("Roll sensitivity##RollTilt",     ref noWickyXIV.Config.RollTiltSensitivity, 0.01f, 0.5f, 0.2f);
            ConfigSliderFloat("Roll onset speed##RollTilt",     ref noWickyXIV.Config.RollTiltOnRate,      0.5f,  20f, 2.47f);
            ConfigSliderFloat("Roll recovery speed##RollTilt",  ref noWickyXIV.Config.RollTiltOffRate,     0.5f,  15f, 1.0f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Yaw Lag — disabled by default until impl is rewritten as
        // damped spring (current Wicked impl whiplashes; see project memory).
        if (DynamicsSectionMatches("Yaw Lag") && ImGuiEx.BeginGroupBox("Yaw Lag (camera trails turns)"))
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "Reference impl whiplashes; rewrite needed.");
            ConfigCheckbox("Enable##YawLag", ref noWickyXIV.Config.EnableYawLag);
            ConfigSliderFloat("Halflife (s)##YawLag", ref noWickyXIV.Config.YawLagHalflife, 0.05f, 3f, 0.8f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Pitch Tilt
        if (DynamicsSectionMatches("Pitch Tilt") && ImGuiEx.BeginGroupBox("Pitch Tilt (look-up at low angle)"))
        {
            ConfigCheckbox("Enable##PitchTilt", ref noWickyXIV.Config.EnablePitchTilt);
            ConfigSliderFloat("Max height offset##PitchTilt",  ref noWickyXIV.Config.PitchTiltMaxOffset,  0f, 2f,  1.24f);
            ConfigSliderFloat("Tilt smooth rate##PitchTilt",   ref noWickyXIV.Config.PitchTiltSmoothRate, 0.5f, 20f, 3.19f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Position Float (the "discreet float" feel)
        if (DynamicsSectionMatches("Position Float") && ImGuiEx.BeginGroupBox("Position Float (discreet float behind player)"))
        {
            ConfigCheckbox("Enable##PosFloat", ref noWickyXIV.Config.EnablePositionFloat);
            ConfigSliderFloat("Lag factor##PosFloat",     ref noWickyXIV.Config.PositionFloatLagFactor,  0f,  1f,    0.15f);
            ConfigSliderFloat("Smooth time (s)##PosFloat", ref noWickyXIV.Config.PositionFloatSmoothTime, 0.01f, 1f, 0.18f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Swivel-on-Move (auto-center timer)
        if (DynamicsSectionMatches("Swivel") && ImGuiEx.BeginGroupBox("Swivel on Move (auto-center)"))
        {
            ConfigCheckbox("Enable##Swivel", ref noWickyXIV.Config.SwivelOnMove);
            ConfigSliderFloat("Delay (s)##Swivel",          ref noWickyXIV.Config.SwivelDelay, 0f,    1f,   0.15f);
            ConfigSliderFloat("Speed (deg/s)##Swivel",      ref noWickyXIV.Config.SwivelSpeed, 30f,   720f, 240f, "%.0f");
            ConfigSliderFloat("Movement threshold (m/s)##Swivel", ref noWickyXIV.Config.SwivelMoveThreshold, 0.01f, 1f, 0.05f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Auto-shoulder swap (Phase C — UI + state machine, raycast TODO)
        if (DynamicsSectionMatches("Shoulder") && ImGuiEx.BeginGroupBox("Auto-shoulder swap"))
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "Experimental — wall raycast not wired yet (manual swap hotkey works).");
            ConfigCheckbox("Enable##AutoShoulder", ref noWickyXIV.Config.EnableAutoShoulderSwap);
            ConfigSliderFloat("Lerp duration (s)##AutoShoulder", ref noWickyXIV.Config.ShoulderLerpDuration,    0.05f, 1.5f, 0.35f);
            ConfigSliderFloat("Safety margin (m)##AutoShoulder", ref noWickyXIV.Config.ShoulderSwapSafetyMargin, 0.0f,  1.5f, 0.4f);
            ConfigSliderFloat("Probe rate (Hz)##AutoShoulder",   ref noWickyXIV.Config.ShoulderSwapCheckHz,     1f,   20f,  5f, "%.0f");
            ImGuiEx.EndGroupBox();
        }

        // Section: Crosshair overlay (Phase D)
        if (DynamicsSectionMatches("Crosshair") && ImGuiEx.BeginGroupBox("Crosshair overlay"))
        {
            ConfigCheckbox("Enable##Crosshair", ref noWickyXIV.Config.EnableCrosshair);
            ImGui.TextDisabled("Toggle hotkey is in the Hotkeys section (default V).");
            ConfigSliderFloat("Size (px)##Crosshair",      ref noWickyXIV.Config.CrosshairSize,      2f,  40f,  8f);
            ConfigSliderFloat("Thickness##Crosshair",      ref noWickyXIV.Config.CrosshairThickness, 1f,  6f,   2f);
            ConfigSliderFloat("Fade speed##Crosshair",     ref noWickyXIV.Config.CrosshairFadeSpeed, 1f,  20f,  6f);
            // Color sliders (R/G/B/A as separate floats so they fit ConfigSliderFloat)
            ConfigSliderFloat("Color R##Crosshair", ref noWickyXIV.Config.CrosshairColorR, 0f, 1f, 1f);
            ConfigSliderFloat("Color G##Crosshair", ref noWickyXIV.Config.CrosshairColorG, 0f, 1f, 1f);
            ConfigSliderFloat("Color B##Crosshair", ref noWickyXIV.Config.CrosshairColorB, 0f, 1f, 1f);
            ConfigSliderFloat("Alpha##Crosshair",   ref noWickyXIV.Config.CrosshairColorA, 0f, 1f, 0.85f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Combat Zoom
        if (DynamicsSectionMatches("Combat") && ImGuiEx.BeginGroupBox("Combat Zoom (auto-pull-back in combat)"))
        {
            ConfigCheckbox("Enable##CombatZoom", ref noWickyXIV.Config.EnableCombatZoom);
            ConfigSliderFloat("Combat distance##CombatZoom",     ref noWickyXIV.Config.CombatZoomDistance,        1.5f, 40f, 12f, "%.1f");
            ConfigSliderFloat("Transition speed##CombatZoom",    ref noWickyXIV.Config.CombatZoomTransitionSpeed, 0.5f, 20f, 4f);
            ImGui.TextDisabled("Captures baseline zoom on combat enter; restores on exit.");
            ImGuiEx.EndGroupBox();
        }

        // Section: ADS (zoom-on-RMB)
        if (DynamicsSectionMatches("ADS") && ImGuiEx.BeginGroupBox("ADS (hold RMB to zoom in)"))
        {
            ConfigCheckbox("Enable##Ads", ref noWickyXIV.Config.EnableAdsOnRmb);
            ConfigSliderFloat("Zoom factor##Ads",       ref noWickyXIV.Config.AdsZoomFactor,      1.05f, 4f, 1.5f);
            ConfigSliderFloat("Transition speed##Ads",  ref noWickyXIV.Config.AdsTransitionSpeed, 1f,    20f, 8f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Sensitivity (Phase E)
        if (DynamicsSectionMatches("Sensitivity") && ImGuiEx.BeginGroupBox("Input Sensitivity"))
        {
            ConfigSliderFloat("Sensitivity multiplier##Sens", ref noWickyXIV.Config.MouseSensitivityMul, 0.1f, 4f, 1f);
            ConfigCheckbox("Invert Y axis##Sens", ref noWickyXIV.Config.InvertMouseY);
            ImGui.TextDisabled("Note: applies to mouse + gamepad uniformly. Per-device split is deferred.");
            ImGuiEx.EndGroupBox();
        }

        // Section: Always-on Mouselook (FPS-style camera lock)
        if (DynamicsSectionMatches("Mouselook") && ImGuiEx.BeginGroupBox("Always-on Mouselook (FPS-style)"))
        {
            ConfigCheckbox("Enable##Mouselook", ref noWickyXIV.Config.EnableMouseLookAlways);
            ImGui.TextDisabled("Mouse drives camera continuously. Hotkey (default F7) frees the cursor for UI.");
            ConfigSliderFloat("Sensitivity (rad/px)##Mouselook", ref noWickyXIV.Config.MouseLookSensitivity, 0.0005f, 0.02f, 0.005f, "%.4f");
            ConfigCheckbox("Invert X##Mouselook", ref noWickyXIV.Config.MouseLookInvertX);
            ConfigCheckbox("Invert Y##Mouselook", ref noWickyXIV.Config.MouseLookInvertY);
            ConfigCheckbox("Re-center cursor each frame##Mouselook", ref noWickyXIV.Config.MouseLookCenterCursor);
            ImGuiEx.EndGroupBox();
        }

        // Section: Hotkeys (Phase B)
        if (DynamicsSectionMatches("Hotkey") && ImGuiEx.BeginGroupBox("Hotkeys"))
        {
            HotkeyRow("Settings panel", ref noWickyXIV.Config.SettingsHotkey,    defaultVk: 0x75 /* F6 */);
            HotkeyRow("Cursor toggle",  ref noWickyXIV.Config.CursorReleaseHotkey, defaultVk: 0x76 /* F7 */);
            HotkeyRow("Crosshair",      ref noWickyXIV.Config.CrosshairHotkey,   defaultVk: 0x56 /* V */);
            HotkeyRow("Shoulder swap",  ref noWickyXIV.Config.ShoulderSwapHotkey, defaultVk: 0);
            ConfigCheckbox("Enable Ctrl+1..9 preset-slot hotkeys", ref noWickyXIV.Config.PresetHotkeysEnabled);
            ImGuiEx.EndGroupBox();
        }

        // Section: Misc / overrides
        if (DynamicsSectionMatches("Misc") && ImGuiEx.BeginGroupBox("Misc"))
        {
            ConfigCheckbox("Instant mode (zero all smoothing)", ref noWickyXIV.Config.InstantMode);
            ImGui.TextDisabled("Note: InstantMode is currently a no-op — FFXIV's camera struct doesn't expose smoothing rates.");
            ConfigSliderFloat("Ctrl+scroll height step", ref noWickyXIV.Config.HeightOffsetStep, 0.01f, 1f, 0.1f);
            // Live global height offset (Ctrl/Alt + scroll updates this).
            // Slider also lets the user nudge it directly from the panel.
            ConfigSliderFloat("Live height offset (Ctrl/Alt+scroll)", ref noWickyXIV.Config.GlobalHeightOffset, -2f, 4f, 0f);
            ImGuiEx.EndGroupBox();
        }
    }

    // Render a hotkey row: shows current binding, "Set" button to capture next
    // key pressed, "Clear" to unbind, "Reset" to default. Stores raw VirtualKey int.
    private static int _hotkeyCapturingFor; // 0 = nothing, else field-id we're capturing for; we use the address-of-int via a static set-target
    private static System.Action<int> _hotkeyCaptureCallback;

    private static void HotkeyRow(string label, ref int vk, int defaultVk)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(180 * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);

        string display = vk == 0 ? "<unbound>" : VkName(vk);
        bool isCapturing = _hotkeyCapturingFor != 0 && _hotkeyCapturingFor == label.GetHashCode();
        if (isCapturing)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.2f, 1f), "press a key…");
            // Poll Dalamud's IKeyState for any key pressed this frame.
            int pressed = ScanFirstPressedVk();
            if (pressed != 0)
            {
                vk = pressed;
                noWickyXIV.Config.Save();
                _hotkeyCapturingFor = 0;
            }
            // ESC cancels
            try { if (DalamudApi.KeyState[0x1B]) _hotkeyCapturingFor = 0; } catch { }
        }
        else
        {
            if (ImGui.Button($"{display}##{label}"))
                _hotkeyCapturingFor = label.GetHashCode();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"x##{label}"))
        {
            vk = 0;
            noWickyXIV.Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"reset##{label}"))
        {
            vk = defaultVk;
            noWickyXIV.Config.Save();
        }
    }

    private static int ScanFirstPressedVk()
    {
        // Scan a sensible range (letters, numbers, F-keys, common others).
        try
        {
            for (int v = 0x08; v <= 0xDE; v++)
            {
                // Skip mouse buttons (0x01..0x06) and common modifiers (handled
                // inline by Ctrl-aware slot hotkeys), Tab+Enter+Shift left out
                // to avoid accidental binds during Set click.
                if (v == 0x09 || v == 0x0D || v == 0x10 || v == 0x11 || v == 0x12 || v == 0x1B) continue;
                if (DalamudApi.KeyState[v]) return v;
            }
        }
        catch { }
        return 0;
    }

    private static string VkName(int vk)
    {
        // Cheap formatter for the common range; falls back to the hex code.
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();           // 0..9
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();           // A..Z
        if (vk >= 0x70 && vk <= 0x7B) return $"F{vk - 0x6F}";                 // F1..F12
        return vk switch
        {
            0x08 => "Backspace", 0x14 => "CapsLock", 0x20 => "Space",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".",
            0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => $"VK_0x{vk:X2}"
        };
    }

    private static unsafe void DrawOtherSettings()
    {
        var save = false;

        if (ImGuiEx.BeginGroupBox("Miscellaneous Options", 0.5f))
        {
            if (Game.cameraNoClippyReplacer.IsValid)
            {
                if (ImGui.Checkbox("Disable Camera Collision", ref noWickyXIV.Config.EnableCameraNoClippy))
                {
                    if (!FreeCam.Enabled)
                        Game.cameraNoClippyReplacer.Toggle();
                    save = true;
                }
            }

            ImGui.TextUnformatted("Death Cam Mode");
            ImGuiEx.Prefix(true);
            save |= ImGuiEx.EnumCombo("##DeathCam", ref noWickyXIV.Config.DeathCamMode);

            ImGuiEx.EndGroupBox();
        }

        ImGui.SameLine();

        if (ImGuiEx.BeginGroupBox("Other", 0.5f))
        {
            ImGui.TextUnformatted("QoL Bar Status:");
            ImGui.SameLine();
            if (!IPC.QoLBarEnabled)
                ImGui.TextColored(new Vector4(1, 0, 0, 1), "Disabled");
            else
                ImGui.TextColored(new Vector4(0, 1, 0, 1), "Enabled");

            var _ = Game.EnableSpectating;
            if (ImGui.Checkbox("Spectate Focus / Soft Target", ref _))
                Game.EnableSpectating = _;

            var __ = FreeCam.Enabled;
            if (ImGui.Checkbox("Free Cam", ref __))
                FreeCam.Toggle();
            ImGuiEx.SetItemTooltip(FreeCam.ControlsString);

            save |= ImGui.Checkbox("Enable Advanced Free Cam Controls", ref noWickyXIV.Config.EnableAdvancedFreeCamControls);

            ImGuiEx.EndGroupBox();
        }

        if (ImGuiEx.BeginGroupBox())
        {
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button(FontAwesomeIcon.UndoAlt.ToIconString()))
                Common.CameraManager->worldCamera->tilt = 0;
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60 * ImGuiHelpers.GlobalScale);
            ImGui.SliderFloat("Tilt", ref Common.CameraManager->worldCamera->tilt, -MathF.PI, MathF.PI, "%f");
            ImGuiEx.EndGroupBox();
        }

        if (save)
            noWickyXIV.Config.Save();
    }
}