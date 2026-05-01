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

    private static void DrawCameraDynamics()
    {
        // Section: Roll Tilt
        if (ImGuiEx.BeginGroupBox("Roll Tilt (bank into turns)"))
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
        if (ImGuiEx.BeginGroupBox("Yaw Lag (camera trails turns)"))
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "Reference impl whiplashes; rewrite needed.");
            ConfigCheckbox("Enable##YawLag", ref noWickyXIV.Config.EnableYawLag);
            ConfigSliderFloat("Halflife (s)##YawLag", ref noWickyXIV.Config.YawLagHalflife, 0.05f, 3f, 0.8f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Pitch Tilt
        if (ImGuiEx.BeginGroupBox("Pitch Tilt (look-up at low angle)"))
        {
            ConfigCheckbox("Enable##PitchTilt", ref noWickyXIV.Config.EnablePitchTilt);
            ConfigSliderFloat("Max height offset##PitchTilt",  ref noWickyXIV.Config.PitchTiltMaxOffset,  0f, 2f,  1.24f);
            ConfigSliderFloat("Tilt smooth rate##PitchTilt",   ref noWickyXIV.Config.PitchTiltSmoothRate, 0.5f, 20f, 3.19f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Position Float (the "discreet float" feel)
        if (ImGuiEx.BeginGroupBox("Position Float (discreet float behind player)"))
        {
            ConfigCheckbox("Enable##PosFloat", ref noWickyXIV.Config.EnablePositionFloat);
            ConfigSliderFloat("Lag factor##PosFloat",     ref noWickyXIV.Config.PositionFloatLagFactor,  0f,  1f,    0.15f);
            ConfigSliderFloat("Smooth time (s)##PosFloat", ref noWickyXIV.Config.PositionFloatSmoothTime, 0.01f, 1f, 0.18f);
            ImGuiEx.EndGroupBox();
        }

        // Section: Swivel-on-Move (auto-center timer)
        if (ImGuiEx.BeginGroupBox("Swivel on Move (auto-center)"))
        {
            ConfigCheckbox("Enable##Swivel", ref noWickyXIV.Config.SwivelOnMove);
            ConfigSliderFloat("Delay (s)##Swivel",          ref noWickyXIV.Config.SwivelDelay, 0f,    1f,   0.15f);
            ConfigSliderFloat("Speed (deg/s)##Swivel",      ref noWickyXIV.Config.SwivelSpeed, 30f,   720f, 240f, "%.0f");
            ImGuiEx.EndGroupBox();
        }

        // Section: Misc / overrides
        if (ImGuiEx.BeginGroupBox("Misc"))
        {
            ConfigCheckbox("Instant mode (zero all smoothing)", ref noWickyXIV.Config.InstantMode);
            ConfigSliderFloat("Ctrl+scroll height step", ref noWickyXIV.Config.HeightOffsetStep, 0.01f, 1f, 0.1f);
            ImGuiEx.EndGroupBox();
        }
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