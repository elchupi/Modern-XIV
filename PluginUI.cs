using System;
using System.Linq;
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

        // Sticky tab bar: per-tab content lives inside a BeginChild so
        // the tab bar itself stays pinned at the top of the window
        // even when the tab content scrolls.
        if (ImGui.BeginTabBar("CammyTabs", ImGuiTabBarFlags.Reorderable))
        {
            if (ImGui.BeginTabItem("Presets"))
            {
                if (ImGui.BeginChild("##presets_scroll"))
                    DrawPresetList();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Camera Dynamics"))
            {
                if (ImGui.BeginChild("##dynamics_scroll"))
                    DrawCameraDynamics();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Effects"))
            {
                if (ImGui.BeginChild("##effects_scroll"))
                    DrawEffectsTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("VFX Replacer"))
            {
                if (ImGui.BeginChild("##vfxreplacer_scroll"))
                    DrawVfxReplacerTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("HP Ring"))
            {
                if (ImGui.BeginChild("##hpring_scroll"))
                    DrawHpRingTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Target UI"))
            {
                if (ImGui.BeginChild("##targetui_scroll"))
                    DrawTargetUITab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Compass"))
            {
                if (ImGui.BeginChild("##compass_scroll"))
                    DrawCompassTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Anim Swaps"))
            {
                if (ImGui.BeginChild("##animswap_scroll"))
                    DrawAnimSwapTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Misc"))
            {
                if (ImGui.BeginChild("##misc_scroll"))
                    DrawMiscTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Light Sync"))
            {
                if (ImGui.BeginChild("##lightsync_scroll"))
                    DrawLightSyncTab();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();

        // Render any pending custom tooltip (foreground draw list, on
        // top of all windows). Tooltip text + anchor are captured by
        // ImGuiEx.SetItemTooltip during the frame; this draws it
        // north of the hovered item with an exp-lerp fade-in/out.
        ImGuiEx.RenderPendingTooltip();
    }

    // Capture the currently-active camera state into a fresh preset.
    // Used by the "+ New Preset" button so the user gets a snapshot
    // of where the camera is right now (zoom, FoV, tilt, V-rotation
    // limits, look-at offset) instead of the all-defaults baseline.
    private static unsafe CameraConfigPreset CaptureCurrentCameraPreset()
    {
        var p = new CameraConfigPreset();
        try
        {
            var cam = Common.CameraManager != null ? Common.CameraManager->worldCamera : null;
            if (cam != null)
            {
                // Zoom — capture current zoom as Start, current min/max as bounds.
                p.UseStartZoom = true;
                p.StartZoom    = cam->currentZoom;
                p.MinZoom      = cam->minZoom;
                p.MaxZoom      = cam->maxZoom;

                // FoV — same shape.
                p.UseStartFoV  = true;
                p.StartFoV     = cam->currentFoV;
                p.MinFoV       = cam->minFoV;
                p.MaxFoV       = cam->maxFoV;

                // Vertical rotation limits + tilt + look-at height.
                p.MinVRotation = cam->minVRotation;
                p.MaxVRotation = cam->maxVRotation;
                p.Tilt         = cam->tilt;
                p.LookAtHeightOffset = cam->lookAtHeightOffset;
            }
        }
        catch { /* defensive — fall back to defaults */ }
        // Name = "<active preset> - Copy" so it's clear the new entry
        // descends from the current state. Falls back to a sequential
        // "Preset N" if there's no active preset (first-run path).
        var src = CurrentPreset;
        p.Name = src != null
            ? $"{src.Name} - Copy"
            : $"Preset {noWickyXIV.Config.Presets.Count + 1}";
        // Snapshot the current Camera-Dynamics + Misc tab state into
        // the new preset so it captures the full UI state, not just
        // the camera struct fields.
        try { p.Dynamics = PresetDynamicsState.SnapshotFrom(noWickyXIV.Config); } catch { }
        return p;
    }

    // Insert a new preset directly under the currently selected one
    // (not at the end of the list). If nothing is selected (-1), the
    // preset is appended.
    private static void InsertPresetAfterSelected(CameraConfigPreset preset)
    {
        var presets = noWickyXIV.Config.Presets;
        int insertAt = (selectedPreset >= 0 && selectedPreset < presets.Count)
            ? selectedPreset + 1
            : presets.Count;
        presets.Insert(insertAt, preset);
        // Move selection to the newly inserted entry so subsequent
        // edits target the copy, not the source.
        selectedPreset = insertAt;
    }

    private static unsafe void DrawPresetList()
    {
        var currentPreset = CurrentPreset;
        var hasSelectedPreset = currentPreset != null;

        ImGui.PushFont(UiBuilder.IconFont);

        if (ImGui.Button(FontAwesomeIcon.PlusCircle.ToIconString()))
        {
            // Snapshot the live camera into the new preset rather than
            // creating a defaults-only entry the user has to fill in.
            // Name comes out as "<active> - Copy" and the entry is
            // inserted directly under the current selection.
            InsertPresetAfterSelected(CaptureCurrentCameraPreset());
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button(FontAwesomeIcon.Copyright.ToIconString()) && hasSelectedPreset)
        {
            var copy = CurrentPreset.Clone();
            copy.Name = $"{CurrentPreset.Name} - Copy";
            InsertPresetAfterSelected(copy);
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

        // Smooth transition slider — applies to AUTO condition-driven
        // preset swaps. 0.05s ≈ instant snap; 0.5s default ≈ momentary
        // glide. Anything past ~1s starts to fight Ctrl/Alt+scroll
        // height/shoulder adjustments mid-transition.
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ConfigSliderFloat("Transition seconds##PresetTransition",
            ref noWickyXIV.Config.PresetTransitionSeconds, 0.05f, 5f, 0.5f, "%.2fs");
        ImGui.TextDisabled("How long the camera takes to ease the position offsets (Height / Side / LookAtHeight) into a newly-activated preset. Zoom/FoV/Tilt snap immediately so they don't fight live wheel input. Ctrl/Alt+scroll cancels an in-flight transition.");

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
        PresetManager.SyncConfigToActivePresetDynamics();
        noWickyXIV.Config.SaveDebounced();
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
        PresetManager.SyncConfigToActivePresetDynamics();
        noWickyXIV.Config.SaveDebounced();
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
        PresetManager.SyncConfigToActivePresetDynamics();
        noWickyXIV.Config.SaveDebounced();
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
        // MinZoom slider floor was 1.0 — lifted to 0.1 so the user
        // can pull the camera much closer to the player. The engine's
        // "pivot to overhead at min zoom" behavior kicks in roughly
        // when the configured MinZoom is hit, so giving the user a
        // smaller MinZoom delays/avoids the pivot. The pitch-clamp
        // detour below also disables the engine's auto-look-down.
        ResetSliderFloat("Minimum##Zoom", ref preset.MinZoom, 0.1f, preset.MaxZoom, 1.5f, "%.2f");
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

        ResetSliderFloat("Camera Height Offset", ref preset.HeightOffset, -2, 4, 0, "%.2f");
        ImGuiEx.SetItemTooltip("Ctrl+scroll in-game adjusts this same value.");
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

        // Display string priority:
        //   1. Built-in condition (if set)
        //   2. QoL Bar set index (if set)
        //   3. "None"
        string display;
        if (preset.Condition != BuiltinPresetCondition.None)
        {
            display = $"[Built-in] {BuiltinConditionLabel(preset.Condition)}";
        }
        else if (preset.ConditionSet >= 0)
        {
            display = preset.ConditionSet < conditionSets.Length
                ? $"[{preset.ConditionSet + 1}] {conditionSets[preset.ConditionSet]}"
                : (preset.ConditionSet + 1).ToString();
        }
        else
        {
            display = "None";
        }

        if (ImGui.BeginCombo("Condition Set", display))
        {
            // None — clears both built-in and QoL Bar selections.
            if (ImGui.Selectable("None##ConditionSet",
                    preset.Condition == BuiltinPresetCondition.None && preset.ConditionSet < 0))
            {
                preset.Condition = BuiltinPresetCondition.None;
                preset.ConditionSet = -1;
                noWickyXIV.Config.Save();
            }

            // Built-in conditions (game-state). Selecting one clears
            // the QoL Bar selection so the two triggers don't collide.
            ImGui.Separator();
            foreach (var c in (BuiltinPresetCondition[])System.Enum.GetValues(typeof(BuiltinPresetCondition)))
            {
                if (c == BuiltinPresetCondition.None) continue;
                bool selected = preset.Condition == c;
                if (ImGui.Selectable($"[Built-in] {BuiltinConditionLabel(c)}##bic_{c}", selected))
                {
                    preset.Condition = c;
                    preset.ConditionSet = -1;
                    noWickyXIV.Config.Save();
                }
            }

            // QoL Bar sets — selecting clears the built-in choice.
            if (qolBarEnabled && conditionSets.Length > 0)
            {
                ImGui.Separator();
                for (int i = 0; i < conditionSets.Length; i++)
                {
                    var name = conditionSets[i];
                    if (!ImGui.Selectable($"[{i + 1}] {name}",
                            preset.Condition == BuiltinPresetCondition.None && i == preset.ConditionSet))
                        continue;
                    preset.Condition = BuiltinPresetCondition.None;
                    preset.ConditionSet = i;
                    noWickyXIV.Config.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGuiEx.SetItemTooltip(
            "Auto-activates this preset when the chosen condition is true.\n" +
            "Built-in conditions read directly from the game's condition flags " +
            "(InCombat, Mounted, Passenger, Talking to NPC) and don't need any extra plugins.\n" +
            "QoL Bar sets are sourced from the QoL Bar plugin and let you build composite conditions.\n" +
            "Presets higher in the preset list win when multiple conditions match.");
    }

    private static string BuiltinConditionLabel(BuiltinPresetCondition c) => c switch
    {
        BuiltinPresetCondition.InCombat     => "In Combat",
        BuiltinPresetCondition.Mounted      => "Mounted",
        BuiltinPresetCondition.Passenger    => "Passenger",
        BuiltinPresetCondition.TalkingToNpc => "Talking to NPC",
        BuiltinPresetCondition.WhileRunning => "While Running",
        BuiltinPresetCondition.MovingMount  => "Moving on Mount",
        BuiltinPresetCondition.Sprinting    => "Sprinting",
        BuiltinPresetCondition.RunningInCombat => "Running in Combat",
        _                                    => c.ToString(),
    };

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
        if (save) { PresetManager.SyncConfigToActivePresetDynamics(); noWickyXIV.Config.SaveDebounced(); }
    }

    private static void ConfigCheckbox(string id, ref bool val)
    {
        if (ImGui.Checkbox(id, ref val))
        {
            PresetManager.SyncConfigToActivePresetDynamics();
            noWickyXIV.Config.SaveDebounced();
        }
    }

    private static void ConfigSliderInt(string id, ref int val, int min, int max, int reset)
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
        save |= ImGui.SliderInt(id, ref val, min, max);
        if (save) { PresetManager.SyncConfigToActivePresetDynamics(); noWickyXIV.Config.SaveDebounced(); }
    }

    // Sticky filter for the JobAura layer editor. 0 = "All triggers",
    // otherwise the index into triggerVals + 1.
    private static int _jobAuraLayerFilterIdx = 0;

    // Builds a string[] suitable for ImGui.Combo, listing every distinct
    // non-empty Path from the layer list (excluding `self`). Index 0 is
    // a placeholder ("Quick-pick…" or a custom prompt) so a user click
    // doesn't accidentally overwrite the field on first render.
    private static string[] BuildLayerPathQuickPick(
        System.Collections.Generic.IList<JobAuraVfxLayer> list,
        JobAuraVfxLayer self,
        bool includeNone = true,
        string nonePrompt = "Quick-pick…")
    {
        var pathSet = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list != null)
        {
            foreach (var l in list)
            {
                if (l == null || l == self) continue;
                if (!string.IsNullOrEmpty(l.Path)) pathSet.Add(l.Path);
                if (!string.IsNullOrEmpty(l.ChainSourcePath)) pathSet.Add(l.ChainSourcePath);
            }
        }
        // Always include the layer's own currently-selected source path
        // so a user-typed value remains visible in the dropdown even
        // when no other layer references it.
        if (self != null && !string.IsNullOrEmpty(self.ChainSourcePath))
            pathSet.Add(self.ChainSourcePath);

        int extra = includeNone ? 1 : 0;
        var arr = new string[pathSet.Count + extra];
        if (includeNone) arr[0] = nonePrompt;
        int k = extra; foreach (var p in pathSet) arr[k++] = p;
        return arr;
    }

    // Modular VFX-layer table for the Job Aura panel. Each row = one
    // user-configured layer (trigger + path + mode).
    private static void DrawJobAuraVfxLayers()
    {
        var list = noWickyXIV.Config.JobAuraVfxLayers;
        if (list == null) { list = new System.Collections.Generic.List<JobAuraVfxLayer>(); noWickyXIV.Config.JobAuraVfxLayers = list; }

        var triggerNames = Enum.GetNames(typeof(JobAuraTrigger));
        var triggerVals  = (JobAuraTrigger[])Enum.GetValues(typeof(JobAuraTrigger));
        var live = JobAura.SnapshotTriggers();

        // ---- Filter dropdown ----
        // "All triggers" + each trigger name. Layers not matching the
        // filter are hidden so the configured list doesn't sprawl.
        var filterOptions = new string[triggerNames.Length + 1];
        filterOptions[0] = "All triggers";
        for (int i = 0; i < triggerNames.Length; i++)
            filterOptions[i + 1] = triggerNames[i];

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (_jobAuraLayerFilterIdx < 0 || _jobAuraLayerFilterIdx > triggerVals.Length)
            _jobAuraLayerFilterIdx = 0;
        ImGui.Combo("Show layers for##layerfilter", ref _jobAuraLayerFilterIdx, filterOptions, filterOptions.Length);

        bool hasFilter = _jobAuraLayerFilterIdx > 0;
        JobAuraTrigger filterTrigger = hasFilter ? triggerVals[_jobAuraLayerFilterIdx - 1] : default;

        // ---- Add button — pre-fills Trigger to the active filter so a
        // layer added under "Tier1" inherits Tier1 instead of the
        // enum default. ----
        if (ImGui.Button("+ Add layer##JobAuraLayers"))
        {
            var nl = new JobAuraVfxLayer { Name = $"Layer {list.Count + 1}" };
            if (hasFilter) nl.Trigger = filterTrigger;
            list.Add(nl);
            noWickyXIV.Config.Save();
        }
        ImGui.SameLine();
        // Counts: visible-under-filter / total
        int matching = 0;
        if (hasFilter)
        {
            foreach (var l in list)
                if (l != null && l.Trigger == filterTrigger) matching++;
        }
        else matching = list.Count;
        ImGui.TextDisabled(hasFilter
            ? $"({matching} shown / {list.Count} total)"
            : $"({list.Count} configured)");

        ImGui.Separator();

        int? toRemove = null;
        // Pending up/down swap collected during the loop and applied at
        // the end so we don't mutate the list mid-iteration.
        int? swapA = null, swapB = null;

        for (int i = 0; i < list.Count; i++)
        {
            var layer = list[i];
            if (layer == null) continue;
            if (hasFilter && layer.Trigger != filterTrigger) continue;

            // Find previous/next matching layer index (under the active
            // filter) — that's what the up/down arrows will swap with,
            // so the visual order in the filtered view matches the
            // resulting list order.
            int? prevMatchingIdx = null;
            for (int j = i - 1; j >= 0; j--)
            {
                if (list[j] != null && (!hasFilter || list[j].Trigger == filterTrigger))
                { prevMatchingIdx = j; break; }
            }
            int? nextMatchingIdx = null;
            for (int j = i + 1; j < list.Count; j++)
            {
                if (list[j] != null && (!hasFilter || list[j].Trigger == filterTrigger))
                { nextMatchingIdx = j; break; }
            }

            ImGui.PushID($"layer_{layer.Id}");

            // ---- Header row: arrows + enable + name + trigger combo + mode + state pip + delete ----
            // Up / down arrows.
            if (!prevMatchingIdx.HasValue) ImGui.BeginDisabled();
            if (ImGui.ArrowButton("##up", ImGuiDir.Up) && prevMatchingIdx.HasValue)
            { swapA = i; swapB = prevMatchingIdx.Value; }
            if (!prevMatchingIdx.HasValue) ImGui.EndDisabled();
            ImGui.SameLine();
            if (!nextMatchingIdx.HasValue) ImGui.BeginDisabled();
            if (ImGui.ArrowButton("##down", ImGuiDir.Down) && nextMatchingIdx.HasValue)
            { swapA = i; swapB = nextMatchingIdx.Value; }
            if (!nextMatchingIdx.HasValue) ImGui.EndDisabled();
            ImGui.SameLine();

            bool en = layer.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { layer.Enabled = en; noWickyXIV.Config.Save(); }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            string nm = layer.Name ?? "";
            if (ImGui.InputText("##name", ref nm, 64)) { layer.Name = nm; noWickyXIV.Config.Save(); }
            ImGui.SameLine();
            // When viewing "All triggers" we surface the per-layer Trigger
            // combo so the user can reassign categories. When filtered to
            // a category, the Trigger combo is redundant (the filter has
            // already pinned the category), so we instead surface a
            // "Default vs Chain" source-mode combo. Default fires on the
            // category trigger; Chain fires after another layer's vfx
            // path plays. See JobAuraLayerSourceMode.
            if (!hasFilter)
            {
                int curIdx = Array.IndexOf(triggerVals, layer.Trigger);
                if (curIdx < 0) curIdx = 0;
                ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                if (ImGui.Combo("##trig", ref curIdx, triggerNames, triggerNames.Length))
                {
                    layer.Trigger = triggerVals[curIdx];
                    noWickyXIV.Config.Save();
                }
            }
            else
            {
                int srcIdx = (int)layer.SourceMode;
                ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
                if (ImGui.Combo("##srcmode", ref srcIdx, new[] { "Default", "Chain", "Chained" }, 3))
                {
                    layer.SourceMode = (JobAuraLayerSourceMode)srcIdx;
                    noWickyXIV.Config.Save();
                }
            }
            ImGui.SameLine();
            // Sustained toggle — default off (SingleShot). When checked,
            // mode = Sustained. Single-shot is the safe default — a
            // sustained layer relies on the avfx's natural loop and will
            // stack visibly if the chosen avfx is one-shot.
            bool sustained = layer.Mode == JobAuraVfxMode.Sustained;
            if (ImGui.Checkbox("Sustained##mode", ref sustained))
            {
                layer.Mode = sustained ? JobAuraVfxMode.Sustained : JobAuraVfxMode.SingleShot;
                noWickyXIV.Config.Save();
            }
            ImGui.SameLine();
            // Live state pip
            bool isOn = live.TryGetValue(layer.Trigger, out var onv) && onv;
            ImGui.TextColored(isOn
                ? new System.Numerics.Vector4(0.4f, 0.95f, 0.4f, 1f)
                : new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f),
                isOn ? "●" : "○");
            ImGui.SameLine();
            if (ImGui.Button("X##del")) toRemove = i;

            // Path on its own line below. Use EnterReturnsTrue so the
            // committed value only updates layer.Path on Enter (or the
            // input losing focus via the explicit AutoSelectAll on the
            // next focus). This stops the layer engine from receiving
            // partial paths mid-typing — those crashed Penumbra's
            // resource loader (dump 190359).
            //
            // Chained mode adds a small "▼" quick-pick combo next to the
            // input that fills the text field with another layer's Path
            // (and any historically known path). The text input remains
            // the source of truth — the dropdown is only a typing aid.
            bool wantQuickPick = layer.SourceMode == JobAuraLayerSourceMode.Chained;
            string path = layer.Path ?? "";
            float pathInputW = wantQuickPick
                ? ImGui.GetContentRegionAvail().X - 130 * ImGuiHelpers.GlobalScale
                : -1;
            ImGui.SetNextItemWidth(pathInputW);
            if (ImGui.InputText("##path", ref path, 256,
                ImGuiInputTextFlags.EnterReturnsTrue))
            {
                layer.Path = path;
                noWickyXIV.Config.Save();
            }
            else if (ImGui.IsItemDeactivatedAfterEdit())
            {
                // Commit on focus-loss too so users who click away
                // without pressing Enter don't lose their edits.
                layer.Path = path;
                noWickyXIV.Config.Save();
            }
            if (wantQuickPick)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                int sel = 0;
                var options = BuildLayerPathQuickPick(list, layer);
                if (ImGui.Combo("##pathpick", ref sel, options, options.Length)
                    && sel > 0)
                {
                    layer.Path = options[sel];
                    noWickyXIV.Config.Save();
                }
            }

            // ---- Chain-source picker ----
            // Shown for both Chain and Chained modes. The two modes share
            // firing semantics (this layer fires DelaySeconds after the
            // chosen source path's vfx Create succeeds anywhere in the
            // layer list). They differ in *editing*:
            //   Chain   — dropdown picks from existing layer paths only.
            //   Chained — text input the user types freely, with an
            //             adjacent quick-pick combo as a typing aid.
            if (layer.SourceMode == JobAuraLayerSourceMode.Chain)
            {
                var pathArr = BuildLayerPathQuickPick(list, layer, includeNone: true,
                    nonePrompt: "(none — pick a source path)");
                int srcSel = 0;
                if (!string.IsNullOrEmpty(layer.ChainSourcePath))
                {
                    for (int j = 1; j < pathArr.Length; j++)
                    {
                        if (string.Equals(pathArr[j], layer.ChainSourcePath, StringComparison.OrdinalIgnoreCase))
                        { srcSel = j; break; }
                    }
                }
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("Chain source path##chainpath", ref srcSel, pathArr, pathArr.Length))
                {
                    layer.ChainSourcePath = srcSel <= 0 ? "" : pathArr[srcSel];
                    noWickyXIV.Config.Save();
                }
                if (string.IsNullOrEmpty(layer.ChainSourcePath))
                    ImGui.TextDisabled("Chain layers fire DelaySeconds after the chosen source layer's vfx plays.");
            }
            else if (layer.SourceMode == JobAuraLayerSourceMode.Chained)
            {
                string src = layer.ChainSourcePath ?? "";
                float srcInputW = ImGui.GetContentRegionAvail().X - 130 * ImGuiHelpers.GlobalScale;
                ImGui.SetNextItemWidth(srcInputW);
                if (ImGui.InputTextWithHint("##chainsrctext", "vfx/.../source.avfx", ref src, 256,
                    ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    layer.ChainSourcePath = src; noWickyXIV.Config.Save();
                }
                else if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    layer.ChainSourcePath = src; noWickyXIV.Config.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                int sel = 0;
                var options = BuildLayerPathQuickPick(list, layer);
                if (ImGui.Combo("##chainsrcpick", ref sel, options, options.Length)
                    && sel > 0)
                {
                    layer.ChainSourcePath = options[sel]; noWickyXIV.Config.Save();
                }
                if (string.IsNullOrEmpty(layer.ChainSourcePath))
                    ImGui.TextDisabled("Chained: type the source vfx path the next animation should follow.");
            }

            // Sound path + volume + delay row. Sound plays alongside
            // the vfx fire, after Delay has elapsed. Leave SoundPath
            // empty for a silent layer.
            string sp = layer.SoundPath ?? "";
            float spw = ImGui.GetContentRegionAvail().X * 0.55f;
            ImGui.SetNextItemWidth(spw);
            // Commit on Enter or focus-loss only — same reason as the
            // vfx path field above. Partial paths can spam the audio
            // subsystem with bad mci open commands.
            if (ImGui.InputTextWithHint("##soundpath", "C:\\path\\to\\sound.wav (optional)", ref sp, 512,
                ImGuiInputTextFlags.EnterReturnsTrue))
            {
                layer.SoundPath = sp; noWickyXIV.Config.Save();
            }
            else if (ImGui.IsItemDeactivatedAfterEdit())
            {
                layer.SoundPath = sp; noWickyXIV.Config.Save();
            }
            ImGui.SameLine();
            float vol = layer.SoundVolume;
            float volw = ImGui.GetContentRegionAvail().X * 0.55f;
            ImGui.SetNextItemWidth(volw);
            if (ImGui.SliderFloat("##sndvol", ref vol, 0f, 1f, "vol %.2f"))
            {
                layer.SoundVolume = vol; noWickyXIV.Config.Save();
            }
            ImGui.SameLine();
            float delay = layer.DelaySeconds;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##delay", ref delay, 0f, 5f, "delay %.2fs"))
            {
                layer.DelaySeconds = delay; noWickyXIV.Config.Save();
            }

            // Per-layer debounce — minimum interval between fires.
            // Belt-and-braces safety net for combat-event triggers
            // (NormalHit / CritHit / IncomingDamage) which can fire
            // many times per second on multi-hit / DoT actions.
            float minInt = layer.MinIntervalSeconds;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("Min interval (s, 0=off)##debounce", ref minInt, 0f, 5f, "%.2fs"))
            {
                layer.MinIntervalSeconds = minInt;
                noWickyXIV.Config.Save();
            }

            // EndTriggerId — fires CallTrigger(handle, id) on the running
            // vfx when the layer's trigger goes false. The avfx's own
            // timeline picks it up and runs its end-animation (the
            // graceful fade VFXEditor's pause button can't actually
            // produce). -1 = no trigger dispatched (vfx plays its
            // natural duration to completion). Trigger IDs are avfx-
            // specific — try 0, 1, 2 and watch which one ends the
            // animation cleanly.
            int endTrig = layer.EndTriggerId;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("End trigger id (-1=off)##endtrig", ref endTrig))
            {
                layer.EndTriggerId = Math.Clamp(endTrig, -1, int.MaxValue);
                noWickyXIV.Config.Save();
            }

            // Suppress while another layer's vfx is presumed playing.
            // Useful for the Stopped trigger so it doesn't fire on top
            // of a gap-closer's own effect when the player's motion
            // momentarily settles during the action animation.
            bool supp = layer.SuppressWhileOthersFiring;
            if (ImGui.Checkbox("Suppress while another effect is running##suppress", ref supp))
            {
                layer.SuppressWhileOthersFiring = supp;
                noWickyXIV.Config.Save();
            }

            ImGui.Separator();
            ImGui.PopID();
        }
        if (toRemove.HasValue) { list.RemoveAt(toRemove.Value); noWickyXIV.Config.Save(); }
        if (swapA.HasValue && swapB.HasValue
            && swapA.Value >= 0 && swapA.Value < list.Count
            && swapB.Value >= 0 && swapB.Value < list.Count
            && swapA.Value != swapB.Value)
        {
            (list[swapA.Value], list[swapB.Value]) = (list[swapB.Value], list[swapA.Value]);
            noWickyXIV.Config.Save();
        }
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
        c.EnableThirdPersonClickTranslation = false;
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
        // Show which preset's dynamics the user is editing — edits
        // target the ACTIVE preset (the one currently driving the
        // camera), not the preset selected in the Presets tab list.
        var activePreset = PresetManager.PresetOverride ?? PresetManager.ActivePreset;
        if (activePreset != null)
            ImGui.TextDisabled($"Editing: {activePreset.Name}  (activate a different preset to edit its dynamics)");
        else
            ImGui.TextDisabled("Editing global defaults (no active preset)");
        ImGui.Spacing();

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
            // Slider ranges bumped: max-angle 10→45° and sensitivity
            // 0.5→10 because typical gameplay yaw velocity is 1-2 rad/s,
            // so even at the old sens=0.5 max only ~1° of roll
            // produced — the original defaults were tuned for very
            // subtle camera tilt, not dramatic motorcycle-style
            // banking. Higher ranges let users actually saturate
            // MaxAngle when they want it.
            ConfigSliderFloat("Max roll (deg)##RollTilt",       ref noWickyXIV.Config.RollTiltMaxAngle,    0f,    45f, 1.92f);
            ConfigSliderFloat("Roll sensitivity##RollTilt",     ref noWickyXIV.Config.RollTiltSensitivity, 0.01f, 10f, 0.2f);
            ConfigSliderFloat("Roll onset speed##RollTilt",     ref noWickyXIV.Config.RollTiltOnRate,      0.5f,  20f, 2.47f);
            ConfigSliderFloat("Roll recovery speed##RollTilt",  ref noWickyXIV.Config.RollTiltOffRate,     0.5f,  15f, 1.0f);

            ImGui.Separator();
            ImGui.TextDisabled("Character Roll — banks the player MODEL into turns,\nnot just the camera. Independent toggle so they can run together.");
            ConfigCheckbox("Enable character bank##CharRoll",  ref noWickyXIV.Config.EnableCharacterRoll);
            ConfigSliderFloat("Max roll (deg)##CharRoll",      ref noWickyXIV.Config.CharacterRollMaxAngle,    0f,    45f, 4.0f);
            ConfigSliderFloat("Roll sensitivity##CharRoll",    ref noWickyXIV.Config.CharacterRollSensitivity, 0.01f, 20.0f, 0.25f);
            ConfigSliderFloat("Char onset speed##CharRoll",    ref noWickyXIV.Config.CharacterRollOnRate,      0.5f,  20f, 3.0f);
            ConfigSliderFloat("Char recovery speed##CharRoll", ref noWickyXIV.Config.CharacterRollOffRate,     0.5f,  15f, 1.5f);
            ImGui.TextDisabled(
                "Writes a roll quaternion into the player's DrawObject every\n" +
                "frame. Visible to other players (it's a model-rotation write,\n" +
                "not a camera-only effect). Defaults conservative — push max\n" +
                "angle higher for arcade-y leans, lower for subtle bank.");
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

        // (Crosshair overlay moved to Misc tab — settings are global,
        // not per-preset.)

        // [Third-Person LMB → hotbar translator moved to the Misc tab.]
        // [Job Aura section moved to the Effects tab — see DrawEffectsTab().]

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
            // Min 0.56 — below that the delta-replay code fights the game's
            // own per-frame camera writes and the camera jitters / "tries to
            // recenter" while you move it. 0.56 was the user-determined floor.
            ConfigSliderFloat("Sensitivity multiplier##Sens", ref noWickyXIV.Config.MouseSensitivityMul, 0.56f, 4f, 1f);
            ConfigCheckbox("Invert Y axis##Sens", ref noWickyXIV.Config.InvertMouseY);
            ImGui.TextDisabled("Note: applies to mouse + gamepad uniformly. Per-device split is deferred.");

            ImGui.Separator();
            ConfigCheckbox("Smooth zoom / yaw / pitch input##InputSmooth", ref noWickyXIV.Config.EnableInputSmoothing);
            ImGui.TextDisabled("Each axis exp-lerps toward its target. Higher rate = snappier. RMB-drag bypasses rotation lerp; CombatZoom / ADS bypass zoom lerp so they don't fight the smoother.");
            ConfigSliderFloat("Zoom smoothing rate (1/s)##InputSmooth",   ref noWickyXIV.Config.InputSmoothingZoomRate,   3f, 60f, 12f, "%.1f");
            ConfigSliderFloat("Rotate smoothing rate (1/s)##InputSmooth", ref noWickyXIV.Config.InputSmoothingRotateRate, 3f, 60f, 22f, "%.1f");

            ImGui.Separator();
            ConfigCheckbox("Smooth camera position offsets##PosSmooth", ref noWickyXIV.Config.EnableCameraPositionSmoothing);
            ImGui.TextDisabled("HeightOffset / SideOffset / Ctrl+scroll height lerp into place instead of snapping. Preset switches still snap.");
            ConfigSliderFloat("Position smoothing rate (1/s)##PosSmooth", ref noWickyXIV.Config.CameraPositionSmoothingRate, 3f, 60f, 12f, "%.1f");
            ImGuiEx.EndGroupBox();
        }

        // Section: Close-zoom pitch cap
        if (DynamicsSectionMatches("Close Zoom Pitch Cap") && ImGuiEx.BeginGroupBox("Close-Zoom Pitch Cap"))
        {
            ConfigCheckbox("Enable##CloseZoomPitch", ref noWickyXIV.Config.EnableCloseZoomPitchCap);
            ImGui.TextDisabled(
                "Soft-clamps the camera's pitch floor when zoomed in close so the\n" +
                "view doesn't pivot overhead and look down at the player. The cap\n" +
                "engages when currentZoom drops below the threshold and relaxes\n" +
                "back to the preset's normal MinVRotation past it.");
            ConfigSliderFloat("Engage when zoom <##CloseZoomPitch",
                ref noWickyXIV.Config.CloseZoomPitchCapZoom, 0.5f, 8f, 3f, "%.2f m");
            ConfigSliderFloat("Pitch floor while engaged (rad)##CloseZoomPitch",
                ref noWickyXIV.Config.CloseZoomPitchCapMinRad, -1.4f, 0f, -0.4f, "%.2f rad");
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

        // [Misc subsection (Instant mode, height offset) moved to the Misc tab.]
    }

    // ---- Effects tab (Job Aura + modular VFX layers) ----
    private static void DrawEffectsTab()
    {
        if (ImGuiEx.BeginGroupBox("Job Aura (SAM Kenki)"))
        {
            ConfigCheckbox("Enable##JobAura", ref noWickyXIV.Config.EnableJobAura);
            ConfigCheckbox("Only when weapon drawn##JobAura", ref noWickyXIV.Config.JobAuraOnlyWeaponDrawn);
            ConfigCheckbox("Mute SFX##JobAura", ref noWickyXIV.Config.MuteJobAuraSfx);

            ImGui.Separator();
            ConfigCheckbox("Anchor to target (instead of player)##JobAura", ref noWickyXIV.Config.JobAuraAnchorToTarget);
            ConfigCheckbox("Anchor to bone##JobAura", ref noWickyXIV.Config.JobAuraAnchorToBone);
            ConfigSliderInt("Humanoid bone index (self / players / NPCs)##JobAura", ref noWickyXIV.Config.JobAuraBoneIndex,        0, 80, 4);
            ConfigSliderInt("Hostile combatant bone index##JobAura",                ref noWickyXIV.Config.JobAuraTargetBoneIndex, 0, 80, 1);
            ImGui.TextDisabled("Two skeleton categories: humanoid (player / NPC body shape) shares one bone index. Hostile combatant BattleNpcs — beasts, dragons, bosses — get their own slot since they may not be humanoid.");
            ConfigSliderFloat("Group scale##JobAura",  ref noWickyXIV.Config.JobAuraScale,       0.3f, 3f,   1f);

            ImGui.TextDisabled("Humanoid offset (self / player allies / friendly NPCs / pets)");
            ConfigSliderFloat("Humanoid X (m)##JobAura", ref noWickyXIV.Config.JobAuraOffsetX, -2f, 2f,  0f,    "%.2f");
            ConfigSliderFloat("Humanoid Y (m)##JobAura", ref noWickyXIV.Config.JobAuraOffsetY, -2f, 2f,  0.4f,  "%.2f");
            ConfigSliderFloat("Humanoid Z (m)##JobAura", ref noWickyXIV.Config.JobAuraOffsetZ, -2f, 2f, -0.15f, "%.2f");

            ImGui.TextDisabled("Hostile combatant offset (enemy BattleNpcs)");
            ConfigSliderFloat("Combatant X (m)##JobAuraTE", ref noWickyXIV.Config.JobAuraTargetEnemyOffsetX, -2f, 2f, 0f,   "%.2f");
            ConfigSliderFloat("Combatant Y (m)##JobAuraTE", ref noWickyXIV.Config.JobAuraTargetEnemyOffsetY, -2f, 2f, 0.4f, "%.2f");
            ConfigSliderFloat("Combatant Z (m)##JobAuraTE", ref noWickyXIV.Config.JobAuraTargetEnemyOffsetZ, -2f, 2f, 0f,   "%.2f");

            ImGui.Separator();
            ConfigSliderFloat("Sen marker padding##JobAura", ref noWickyXIV.Config.JobAuraSenPadding, 0.8f, 2.0f, 1.18f, "%.2f");
            ConfigSliderFloat("Sen marker scale##JobAura",   ref noWickyXIV.Config.JobAuraSenScale,   0.3f, 3.0f, 1.0f);
            ConfigSliderFloat("Sen cascade delay (s)##JobAura", ref noWickyXIV.Config.JobAuraSenCascadeDelay, 0f, 2f, 0.4f, "%.2f");
            ImGui.TextDisabled("Sen markers wait this long after the overlay first becomes visible before fading in.");

            ImGui.Separator();
            ConfigCheckbox("Show HP rings on party members##JobAura", ref noWickyXIV.Config.JobAuraPartyHpRings);
            ImGui.TextDisabled("Mirrors the primary HP indicator (without text) on every party member.");

            ImGui.Separator();
            ImGui.TextUnformatted("Kenki tier rings (33% / 66% / 100%)");
            // ---- Tier 1 (33%) ----
            ImGui.TextDisabled("Tier 1 — Kenki ≥ 33%");
            ConfigSliderFloat("Tier1 R##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier1ColorR, 0f, 1f, 1.00f, "%.2f");
            ConfigSliderFloat("Tier1 G##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier1ColorG, 0f, 1f, 0.85f, "%.2f");
            ConfigSliderFloat("Tier1 B##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier1ColorB, 0f, 1f, 0.40f, "%.2f");
            ConfigSliderFloat("Tier1 alpha##JobAuraTier", ref noWickyXIV.Config.JobAuraTier1Alpha,  0f, 1f, 0.55f, "%.2f");
            // ---- Tier 2 (66%) ----
            ImGui.TextDisabled("Tier 2 — Kenki ≥ 66%");
            ConfigSliderFloat("Tier2 R##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier2ColorR, 0f, 1f, 1.00f, "%.2f");
            ConfigSliderFloat("Tier2 G##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier2ColorG, 0f, 1f, 0.65f, "%.2f");
            ConfigSliderFloat("Tier2 B##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier2ColorB, 0f, 1f, 0.20f, "%.2f");
            ConfigSliderFloat("Tier2 alpha##JobAuraTier", ref noWickyXIV.Config.JobAuraTier2Alpha,  0f, 1f, 0.70f, "%.2f");
            // ---- Tier 3 (100%) ----
            ImGui.TextDisabled("Tier 3 — Kenki = 100% (pulses)");
            ConfigSliderFloat("Tier3 R##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier3ColorR, 0f, 1f, 1.00f, "%.2f");
            ConfigSliderFloat("Tier3 G##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier3ColorG, 0f, 1f, 0.30f, "%.2f");
            ConfigSliderFloat("Tier3 B##JobAuraTier",     ref noWickyXIV.Config.JobAuraTier3ColorB, 0f, 1f, 0.10f, "%.2f");
            ConfigSliderFloat("Tier3 alpha##JobAuraTier", ref noWickyXIV.Config.JobAuraTier3Alpha,  0f, 1f, 0.85f, "%.2f");

            ImGui.Separator();
            ImGui.TextUnformatted("HP indicator rings (3-layer composite around target/player)");
            // ---- Outer (backdrop) ring ----
            ImGui.TextDisabled("Outer ring — persistent backdrop");
            ConfigSliderFloat("Outer radius (× base)##JobAuraOuter",  ref noWickyXIV.Config.JobAuraHpBackdropRadiusFactor, 0.1f, 3f, 0.7425f, "%.3f");
            ConfigSliderFloat("Outer alpha##JobAuraOuter",            ref noWickyXIV.Config.JobAuraHpBackdropAlpha, 0f, 1f, 0.65f, "%.2f");
            ConfigSliderFloat("Outer R##JobAuraOuter",                ref noWickyXIV.Config.JobAuraHpBackdropColorR, 0f, 1f, 0.42f, "%.2f");
            ConfigSliderFloat("Outer G##JobAuraOuter",                ref noWickyXIV.Config.JobAuraHpBackdropColorG, 0f, 1f, 0.05f, "%.2f");
            ConfigSliderFloat("Outer B##JobAuraOuter",                ref noWickyXIV.Config.JobAuraHpBackdropColorB, 0f, 1f, 0.06f, "%.2f");
            // ---- Inner core ----
            ImGui.TextDisabled("Inner core — radius and alpha both shrink with HP%");
            ConfigSliderFloat("Inner radius (× base × HP%%)##JobAuraInner", ref noWickyXIV.Config.JobAuraHpInnerRadiusFactor, 0.05f, 2f, 0.55f, "%.3f");
            ConfigSliderFloat("Inner alpha##JobAuraInner",                  ref noWickyXIV.Config.JobAuraHpInnerAlpha, 0f, 1f, 0.85f, "%.2f");
            ConfigSliderFloat("Inner R##JobAuraInner",                      ref noWickyXIV.Config.JobAuraHpInnerColorR, 0f, 1f, 1.0f, "%.2f");
            ConfigSliderFloat("Inner G##JobAuraInner",                      ref noWickyXIV.Config.JobAuraHpInnerColorG, 0f, 1f, 0.18f, "%.2f");
            ConfigSliderFloat("Inner B##JobAuraInner",                      ref noWickyXIV.Config.JobAuraHpInnerColorB, 0f, 1f, 0.18f, "%.2f");
            // ---- Pulse ring ----
            ImGui.TextDisabled("Pulse ring — expands outward from outer edge, period scales with HP%");
            ConfigSliderFloat("Pulse expand (× outer radius)##JobAuraPulse", ref noWickyXIV.Config.JobAuraHpPulseExpandFactor, 1f, 4f, 1.95f, "%.2f");
            ConfigSliderFloat("Pulse alpha##JobAuraPulse",                   ref noWickyXIV.Config.JobAuraHpPulseAlpha,        0f, 1f, 0.85f, "%.2f");
            ConfigSliderFloat("Pulse thickness##JobAuraPulse",               ref noWickyXIV.Config.JobAuraHpPulseThickness,    0.5f, 12f, 3.5f, "%.1f");
            ConfigSliderFloat("Pulse R##JobAuraPulse",                       ref noWickyXIV.Config.JobAuraHpPulseColorR, 0f, 1f, 1.0f, "%.2f");
            ConfigSliderFloat("Pulse G##JobAuraPulse",                       ref noWickyXIV.Config.JobAuraHpPulseColorG, 0f, 1f, 0.20f, "%.2f");
            ConfigSliderFloat("Pulse B##JobAuraPulse",                       ref noWickyXIV.Config.JobAuraHpPulseColorB, 0f, 1f, 0.20f, "%.2f");

            ImGui.Separator();
            ImGui.TextUnformatted("Full-zen (AllSen) overlay rings");
            ImGui.TextDisabled("Inner ring fades alpha 0→0.5; outer ring then traces clockwise from 12 o'clock as a snake stroke 0.5→1.0.");
            // ---- AllSen inner ring ----
            ImGui.TextDisabled("Inner ring");
            ConfigSliderFloat("AllSen inner alpha##JobAuraAllSenInner",     ref noWickyXIV.Config.JobAuraAllSenInnerAlpha,     0f, 1f, 1.0f,  "%.2f");
            ConfigSliderFloat("AllSen inner thickness##JobAuraAllSenInner", ref noWickyXIV.Config.JobAuraAllSenInnerThickness, 0.5f, 16f, 5.0f, "%.1f");
            ConfigSliderFloat("AllSen inner R##JobAuraAllSenInner",         ref noWickyXIV.Config.JobAuraAllSenInnerColorR,    0f, 1f, 1.0f,  "%.2f");
            ConfigSliderFloat("AllSen inner G##JobAuraAllSenInner",         ref noWickyXIV.Config.JobAuraAllSenInnerColorG,    0f, 1f, 0.18f, "%.2f");
            ConfigSliderFloat("AllSen inner B##JobAuraAllSenInner",         ref noWickyXIV.Config.JobAuraAllSenInnerColorB,    0f, 1f, 0.18f, "%.2f");
            // ---- AllSen outer (snake) ring ----
            ImGui.TextDisabled("Outer ring (snake stroke)");
            ConfigSliderFloat("AllSen outer radius (× inner)##JobAuraAllSenOuter", ref noWickyXIV.Config.JobAuraAllSenOuterRadiusFactor, 1.0f, 2.0f, 1.10f, "%.2f");
            ConfigSliderFloat("AllSen outer alpha##JobAuraAllSenOuter",     ref noWickyXIV.Config.JobAuraAllSenOuterAlpha,     0f, 1f, 0.55f, "%.2f");
            ConfigSliderFloat("AllSen outer thickness##JobAuraAllSenOuter", ref noWickyXIV.Config.JobAuraAllSenOuterThickness, 0.5f, 16f, 2.5f, "%.1f");
            ConfigSliderFloat("AllSen outer R##JobAuraAllSenOuter",         ref noWickyXIV.Config.JobAuraAllSenOuterColorR,    0f, 1f, 0.85f, "%.2f");
            ConfigSliderFloat("AllSen outer G##JobAuraAllSenOuter",         ref noWickyXIV.Config.JobAuraAllSenOuterColorG,    0f, 1f, 0.05f, "%.2f");
            ConfigSliderFloat("AllSen outer B##JobAuraAllSenOuter",         ref noWickyXIV.Config.JobAuraAllSenOuterColorB,    0f, 1f, 0.05f, "%.2f");

            ImGui.Separator();
            ConfigCheckbox("Show HP %% text##JobAura", ref noWickyXIV.Config.JobAuraShowHpText);
            ImGui.TextDisabled("HP text font (system .ttf/.otf)");
            {
                var fonts = JobAura.EnumerateSystemFonts();
                string current = noWickyXIV.Config.JobAuraHpFontPath ?? "";
                string preview = string.IsNullOrEmpty(current) ? "(default ImGui)" : System.IO.Path.GetFileName(current);
                if (ImGui.BeginCombo("Font##JobAuraHp", preview))
                {
                    bool isDefault = string.IsNullOrEmpty(current);
                    if (ImGui.Selectable("(default ImGui)", isDefault))
                    {
                        noWickyXIV.Config.JobAuraHpFontPath = "";
                        noWickyXIV.Config.Save();
                    }
                    foreach (var path in fonts)
                    {
                        bool sel = path.Equals(current, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(System.IO.Path.GetFileName(path), sel))
                        {
                            noWickyXIV.Config.JobAuraHpFontPath = path;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }
                {
                    var io = ImGui.GetIO();
                    if (ImGui.IsItemHovered() && io.KeyCtrl && MathF.Abs(io.MouseWheel) > 0.01f && fonts.Count > 0)
                    {
                        int idx = -1;
                        for (int i = 0; i < fonts.Count; i++)
                        {
                            if (fonts[i].Equals(current, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                        }
                        int step = io.MouseWheel > 0 ? -1 : 1;
                        int next = idx < 0 ? (step > 0 ? 0 : fonts.Count - 1)
                                           : (((idx + step) % fonts.Count) + fonts.Count) % fonts.Count;
                        noWickyXIV.Config.JobAuraHpFontPath = fonts[next];
                        noWickyXIV.Config.Save();
                    }
                }
                ConfigSliderFloat("Font size (px)##JobAuraHp", ref noWickyXIV.Config.JobAuraHpFontSize, 8f, 96f, 22f, "%.0f");
            }

            ImGui.Separator();
            ConfigCheckbox  ("Fade out of combat##JobAura", ref noWickyXIV.Config.JobAuraFadeOutOfCombat);
            ConfigSliderFloat("OOC alpha##JobAura",  ref noWickyXIV.Config.JobAuraOutOfCombatAlpha, 0f,  1f,  0f,  "%.2f");
            ConfigCheckbox  ("Fade when no target##JobAura", ref noWickyXIV.Config.JobAuraFadeWhenNoTarget);
            ConfigSliderFloat("No-target alpha##JobAura", ref noWickyXIV.Config.JobAuraNoTargetAlpha, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Fade rate##JobAura",  ref noWickyXIV.Config.JobAuraFadeRate,         0.5f, 12f, 4f);

            ImGui.Separator();
            ConfigSliderFloat("Volume max-focus##JobAura", ref noWickyXIV.Config.JobAuraVolMax, 0f, 1f, 1f);
            ConfigSliderFloat("Volume pt1##JobAura",       ref noWickyXIV.Config.JobAuraVolPt1, 0f, 1f, 1f);
            ConfigSliderFloat("Volume pt2##JobAura",       ref noWickyXIV.Config.JobAuraVolPt2, 0f, 1f, 1f);
            if (ImGui.Button("Test SFX sequence##JobAura"))
                JobAura.TestPlaySequence();
            ImGui.SameLine();
            ImGui.TextDisabled("(plays max-focus + 1s later pt1/pt2 — verify audio)");

            ImGui.TextDisabled(
                "Tiers: 33% / 66% / 100% Kenki.\n" +
                "100% triggers an explosive burst ring + max-focus SFX.\n" +
                "1s after cap, pt1+pt2 SFX play together.\n" +
                "Bone index 4 ≈ upper spine; tweak to taste.\n" +
                "Visible only when on Samurai.\n" +
                "Modular per-trigger VFX layers now live in the dedicated VFX Replacer tab.");
            ImGuiEx.EndGroupBox();
        }
    }

    // ---- VFX Replacer tab (modular per-trigger layer editor) ----
    // Pulled out of the Effects tab so the layer list and its quickly-
    // growing per-row controls (path, sound, chain source, end trigger,
    // delay, debounce, ordering arrows, …) get their own scroll region
    // and don't crowd the JobAura ring/sound settings above.
    private static void DrawVfxReplacerTab()
    {
        if (ImGuiEx.BeginGroupBox("VFX Replacer (modular layers)"))
        {
            ImGui.TextDisabled("Each layer hooks a Kenki/Sen/motion/combat trigger and plays an .avfx.");
            ConfigCheckbox("Enable real .avfx (advanced — may crash game until sigs verified)##JobAura",
                ref noWickyXIV.Config.JobAuraEnableRealVfx);
            ImGui.TextDisabled("Toggle requires plugin reload. ImGui rings + audio in the Effects tab work either way.");
            ImGui.Separator();
            DrawJobAuraVfxLayers();

            ImGui.TextDisabled(
                "VFX paths: e.g. \"vfx/common/eff/dk03ht_canc0t.avfx\" (browse with VfxEditor).\n" +
                "Chain mode: pick a source path from existing layers via dropdown.\n" +
                "Chained mode: type any source path with quick-pick autocomplete adjacent.");
            ImGuiEx.EndGroupBox();
        }
    }

    // ---- Misc tab (was "Other Settings" + click translator + instant mode) ----
    // ---- HP Ring tab ----
    private static void DrawHpRingTab()
    {
        if (ImGuiEx.BeginGroupBox("HP Ring (standalone HP-driven pulse overlay)"))
        {
            ConfigCheckbox("Enable##HpRing", ref noWickyXIV.Config.EnableHpRing);
            ImGui.TextDisabled(
                "A single ring anchored on screen that pulses with HP.\n" +
                "Full HP: slow gentle pulse, low base alpha.\n" +
                "Low  HP: rapid pulse, high base alpha, ring shrinks toward center.\n" +
                "Linear interpolation across the entire HP range.");

            ImGui.Separator();
            ImGui.TextUnformatted("Position");
            ConfigCheckbox("Anchor to player bone (3D, follows character)##HpRing",
                ref noWickyXIV.Config.HpRingAnchorToBone);
            if (noWickyXIV.Config.HpRingAnchorToBone)
            {
                ConfigSliderInt  ("Player bone index##HpRing", ref noWickyXIV.Config.HpRingBoneIndex, 0, 80, 1);
                ImGui.TextDisabled("Offsets are PLAYER-LOCAL (rotate with player facing).");
                ConfigSliderFloat("Right (m, +X)##HpRing",   ref noWickyXIV.Config.HpRingOffsetRight,   -3f, 3f,  0f,    "%.2f");
                ConfigSliderFloat("Up (m, +Y)##HpRing",      ref noWickyXIV.Config.HpRingOffsetUp,      -3f, 3f,  0f,    "%.2f");
                ConfigSliderFloat("Forward (m, -Z=behind)##HpRing", ref noWickyXIV.Config.HpRingOffsetForward, -3f, 3f, -0.6f, "%.2f");
            }
            else
            {
                ConfigSliderFloat("Screen X (0=left, 1=right)##HpRing", ref noWickyXIV.Config.HpRingScreenX, 0f, 1f, 0.5f, "%.3f");
                ConfigSliderFloat("Screen Y (0=top, 1=bottom)##HpRing", ref noWickyXIV.Config.HpRingScreenY, 0f, 1f, 0.85f, "%.3f");
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Geometry");
            ConfigSliderFloat("Base radius (px, full HP)##HpRing",   ref noWickyXIV.Config.HpRingRadius,            10f, 400f, 80f, "%.0f");
            ConfigSliderFloat("Low-HP radius factor (× base)##HpRing", ref noWickyXIV.Config.HpRingLowHpRadiusFactor, 0.1f, 1f, 0.7f, "%.2f");
            ConfigSliderFloat("Line thickness##HpRing",              ref noWickyXIV.Config.HpRingThickness,         0.5f, 16f, 3f, "%.1f");
            ConfigSliderInt  ("Circle segments (resolution)##HpRing",ref noWickyXIV.Config.HpRingSegments,           8, 256, 64);

            ImGui.Separator();
            ImGui.TextUnformatted("Pulse rate (Hz — cycles per second)");
            ConfigSliderFloat("Slow pulse (full HP)##HpRing",  ref noWickyXIV.Config.HpRingSlowPulseHz, 0.05f, 5f, 0.5f, "%.2f Hz");
            ConfigSliderFloat("Fast pulse (zero HP)##HpRing",  ref noWickyXIV.Config.HpRingFastPulseHz, 0.1f, 12f, 3.0f, "%.2f Hz");

            ImGui.Separator();
            ImGui.TextUnformatted("Alpha");
            ConfigSliderFloat("Full-HP base alpha##HpRing", ref noWickyXIV.Config.HpRingFullHpBaseAlpha, 0f, 1f, 0.5f, "%.2f");
            ConfigSliderFloat("Full-HP peak alpha##HpRing", ref noWickyXIV.Config.HpRingFullHpPeakAlpha, 0f, 1f, 1.0f, "%.2f");
            ConfigSliderFloat("Low-HP  base alpha##HpRing", ref noWickyXIV.Config.HpRingLowHpBaseAlpha,  0f, 1f, 0.8f, "%.2f");
            ConfigSliderFloat("Low-HP  peak alpha##HpRing", ref noWickyXIV.Config.HpRingLowHpPeakAlpha,  0f, 1f, 1.0f, "%.2f");

            ImGui.Separator();
            ImGui.TextUnformatted("Color (RGB 0..1)");
            ConfigSliderFloat("R##HpRingCol", ref noWickyXIV.Config.HpRingColorR, 0f, 1f, 1.0f, "%.2f");
            ConfigSliderFloat("G##HpRingCol", ref noWickyXIV.Config.HpRingColorG, 0f, 1f, 0.25f, "%.2f");
            ConfigSliderFloat("B##HpRingCol", ref noWickyXIV.Config.HpRingColorB, 0f, 1f, 0.25f, "%.2f");

            ImGuiEx.EndGroupBox();
        }
    }

    // ---- Target UI tab (target name + cast bar + spell name) ----
    private static void DrawTargetUITab()
    {
        // ----- Enemy HP rings -----
        // Iterates ALL hostile enemies in the world and draws the
        // JobAura HP ring (backdrop + pulse + inner core + emanating
        // pulse) on each — no targeting required. Skips only the
        // currently-targeted enemy when JobAura is already anchored
        // to it, to avoid duplicating that one ring.
        if (ImGuiEx.BeginGroupBox("Enemy HP rings"))
        {
            ConfigCheckbox("Show HP rings on all enemies##TgtHp", ref noWickyXIV.Config.EnableHpRingOnTarget);
            ImGui.TextDisabled(
                "Draws the JobAura-style HP ring on every hostile enemy in the\n" +
                "world. Auto-skips the currently-targeted enemy when JobAura is\n" +
                "anchored to target, since that already covers it.\n" +
                "Requires the HP Ring feature to be enabled.");
            ConfigCheckbox("Show HP rings on party members##TgtHpParty", ref noWickyXIV.Config.EnableHpRingOnParty);
            ImGui.TextDisabled(
                "Same visual on every other party member.");
            ImGuiEx.EndGroupBox();
        }

        // ----- HP Vignette (red screen-edge overlay on low HP) -----
        if (ImGuiEx.BeginGroupBox("HP Vignette (red screen-edge overlay on low HP)"))
        {
            ConfigCheckbox("Enable##HpVignette", ref noWickyXIV.Config.EnableHpVignette);
            ImGui.TextDisabled(
                "Bleeds a red gradient in from the screen edges when your HP\n" +
                "drops below the threshold. Independent of the HP rings —\n" +
                "useful when the rings are too subtle to read at a glance.");
            ConfigSliderFloat("Threshold (HP frac)##HpVignette",  ref noWickyXIV.Config.HpVignetteThreshold, 0.05f, 1.0f, 0.55f, "%.2f");
            ConfigSliderFloat("Max alpha##HpVignette",            ref noWickyXIV.Config.HpVignetteMaxAlpha,  0.05f, 1.0f, 0.55f, "%.2f");
            ConfigSliderFloat("Edge thickness (frac)##HpVignette", ref noWickyXIV.Config.HpVignetteThickness, 0.05f, 0.5f, 0.20f, "%.2f");
            ConfigSliderFloat("Color R##HpVignette", ref noWickyXIV.Config.HpVignetteR, 0f, 1f, 1.0f);
            ConfigSliderFloat("Color G##HpVignette", ref noWickyXIV.Config.HpVignetteG, 0f, 1f, 0.05f);
            ConfigSliderFloat("Color B##HpVignette", ref noWickyXIV.Config.HpVignetteB, 0f, 1f, 0.05f);
            ImGuiEx.EndGroupBox();
        }

        // ----- Target name -----
        if (ImGuiEx.BeginGroupBox("Target name"))
        {
            ConfigCheckbox("Enable##TgtName", ref noWickyXIV.Config.EnableTargetName);

            string[] anchorOptions = { "Screen (absolute X/Y)", "Target bone (X/Y are offsets)" };
            int anchorIdx = Math.Clamp(noWickyXIV.Config.TargetNameAnchorMode, 0, 1);
            ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Anchor##TgtName", ref anchorIdx, anchorOptions, anchorOptions.Length))
            {
                noWickyXIV.Config.TargetNameAnchorMode = anchorIdx;
                noWickyXIV.Config.Save();
            }
            if (anchorIdx == 1)
            {
                ConfigSliderInt("Bone index##TgtName", ref noWickyXIV.Config.TargetNameBoneIndex, 0, 80, 1);
                ImGui.TextDisabled("X/Y are pixel offsets from the bone — a 960 X (Screen-mode default) puts the text far off-screen relative to the bone.");
                if (ImGui.SmallButton("Zero offsets##TgtNameBoneReset"))
                {
                    noWickyXIV.Config.TargetNameX = 0f;
                    noWickyXIV.Config.TargetNameY = -40f; // typical "above the head" offset
                    noWickyXIV.Config.Save();
                }
                ImGui.SameLine();
                ImGui.TextDisabled("(sets X=0, Y=-40 — text right above the bone)");
            }

            ConfigSliderFloat("X (px)##TgtName", ref noWickyXIV.Config.TargetNameX, -1500f, 3840f, 960f, "%.0f");
            ConfigSliderFloat("Y (px)##TgtName", ref noWickyXIV.Config.TargetNameY, -1500f, 2160f, 200f, "%.0f");

            DrawFontPicker("Font##TgtNameFont", ref noWickyXIV.Config.TargetNameFontPath);
            ConfigSliderFloat("Font size (px)##TgtName", ref noWickyXIV.Config.TargetNameFontSize, 8f, 96f, 22f, "%.0f");

            ImGui.TextDisabled("Color");
            ConfigSliderFloat("R##TgtNameCol",     ref noWickyXIV.Config.TargetNameColorR, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("G##TgtNameCol",     ref noWickyXIV.Config.TargetNameColorG, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("B##TgtNameCol",     ref noWickyXIV.Config.TargetNameColorB, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("Alpha##TgtNameCol", ref noWickyXIV.Config.TargetNameAlpha,  0f, 1f, 1f, "%.2f");

            ImGui.TextDisabled("Outline");
            ConfigSliderFloat("Outline R##TgtNameOut", ref noWickyXIV.Config.TargetNameOutlineColorR, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline G##TgtNameOut", ref noWickyXIV.Config.TargetNameOutlineColorG, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline B##TgtNameOut", ref noWickyXIV.Config.TargetNameOutlineColorB, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline alpha##TgtNameOut", ref noWickyXIV.Config.TargetNameOutlineAlpha, 0f, 1f, 1f, "%.2f");
            ImGuiEx.EndGroupBox();
        }

        // ----- Cast bar -----
        if (ImGuiEx.BeginGroupBox("Cast bar"))
        {
            ConfigCheckbox("Enable##CastBar", ref noWickyXIV.Config.EnableCastBar);

            string[] anchorOptions =
            {
                "Screen (absolute X/Y)",
                "Target bone (X/Y are offsets)",
                "Target name (X/Y are offsets from the name)",
            };
            int anchorIdx = Math.Clamp(noWickyXIV.Config.CastBarAnchorMode, 0, anchorOptions.Length - 1);
            ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Anchor##CastBar", ref anchorIdx, anchorOptions, anchorOptions.Length))
            {
                noWickyXIV.Config.CastBarAnchorMode = anchorIdx;
                noWickyXIV.Config.Save();
            }
            if (anchorIdx == 1)
                ConfigSliderInt("Bone index##CastBar", ref noWickyXIV.Config.CastBarBoneIndex, 0, 80, 1);
            if (anchorIdx == 2)
                ImGui.TextDisabled("Cast bar follows the target name; X/Y here are pixels relative to the name's anchor.");

            ConfigSliderFloat("X (center, px)##CastBar", ref noWickyXIV.Config.CastBarX, -1500f, 3840f, 960f, "%.0f");
            ConfigSliderFloat("Y (top, px)##CastBar",    ref noWickyXIV.Config.CastBarY, -1500f, 2160f, 240f, "%.0f");
            ConfigSliderFloat("Length (px)##CastBar",    ref noWickyXIV.Config.CastBarLength, 30f, 800f, 220f, "%.0f");
            ConfigSliderFloat("Height (px)##CastBar",    ref noWickyXIV.Config.CastBarHeight, 2f,  60f,  10f, "%.0f");

            ImGui.TextDisabled("Fill (progress)");
            ConfigSliderFloat("R##CastFill",     ref noWickyXIV.Config.CastBarFillR,     0f, 1f, 0.85f, "%.2f");
            ConfigSliderFloat("G##CastFill",     ref noWickyXIV.Config.CastBarFillG,     0f, 1f, 0.55f, "%.2f");
            ConfigSliderFloat("B##CastFill",     ref noWickyXIV.Config.CastBarFillB,     0f, 1f, 0.15f, "%.2f");
            ConfigSliderFloat("Alpha##CastFill", ref noWickyXIV.Config.CastBarFillAlpha, 0f, 1f, 0.95f, "%.2f");

            ImGui.TextDisabled("Background (empty)");
            ConfigSliderFloat("R##CastBg",     ref noWickyXIV.Config.CastBarBgR,     0f, 1f, 0.10f, "%.2f");
            ConfigSliderFloat("G##CastBg",     ref noWickyXIV.Config.CastBarBgG,     0f, 1f, 0.10f, "%.2f");
            ConfigSliderFloat("B##CastBg",     ref noWickyXIV.Config.CastBarBgB,     0f, 1f, 0.10f, "%.2f");
            ConfigSliderFloat("Alpha##CastBg", ref noWickyXIV.Config.CastBarBgAlpha, 0f, 1f, 0.70f, "%.2f");

            ImGui.TextDisabled("Border");
            ConfigSliderFloat("R##CastBor",     ref noWickyXIV.Config.CastBarBorderR,     0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("G##CastBor",     ref noWickyXIV.Config.CastBarBorderG,     0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("B##CastBor",     ref noWickyXIV.Config.CastBarBorderB,     0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Alpha##CastBor", ref noWickyXIV.Config.CastBarBorderAlpha, 0f, 1f, 0.85f, "%.2f");
            ImGuiEx.EndGroupBox();
        }

        // ----- Cast bar spell name -----
        if (ImGuiEx.BeginGroupBox("Cast bar — spell name"))
        {
            ConfigCheckbox("Show spell name##CastSpell", ref noWickyXIV.Config.EnableCastBarSpellName);

            ConfigSliderFloat("Offset X (px)##CastSpell", ref noWickyXIV.Config.CastBarSpellOffsetX, -300f, 300f, 0f,   "%.0f");
            ConfigSliderFloat("Offset Y (px)##CastSpell", ref noWickyXIV.Config.CastBarSpellOffsetY, -200f, 200f, -18f, "%.0f");

            DrawFontPicker("Font##CastSpellFont", ref noWickyXIV.Config.CastBarSpellFontPath);
            ConfigSliderFloat("Font size (px)##CastSpell", ref noWickyXIV.Config.CastBarSpellFontSize, 8f, 64f, 16f, "%.0f");

            ImGui.TextDisabled("Color");
            ConfigSliderFloat("R##CastSpellCol",     ref noWickyXIV.Config.CastBarSpellColorR, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("G##CastSpellCol",     ref noWickyXIV.Config.CastBarSpellColorG, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("B##CastSpellCol",     ref noWickyXIV.Config.CastBarSpellColorB, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("Alpha##CastSpellCol", ref noWickyXIV.Config.CastBarSpellAlpha,  0f, 1f, 1f, "%.2f");

            ImGui.TextDisabled("Outline");
            ConfigSliderFloat("Outline R##CastSpellOut",     ref noWickyXIV.Config.CastBarSpellOutlineColorR, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline G##CastSpellOut",     ref noWickyXIV.Config.CastBarSpellOutlineColorG, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline B##CastSpellOut",     ref noWickyXIV.Config.CastBarSpellOutlineColorB, 0f, 1f, 0f, "%.2f");
            ConfigSliderFloat("Outline alpha##CastSpellOut", ref noWickyXIV.Config.CastBarSpellOutlineAlpha,  0f, 1f, 1f, "%.2f");
            ImGuiEx.EndGroupBox();
        }
    }

    // Reusable system-font dropdown for the Target UI elements.
    // Mirrors the JobAura HP-text font picker. Ctrl+mouse-wheel while
    // hovering the combo cycles through fonts without opening it.
    private static void DrawFontPicker(string label, ref string current)
    {
        var fonts = JobAura.EnumerateSystemFonts();
        string preview = string.IsNullOrEmpty(current) ? "(default ImGui)" : System.IO.Path.GetFileName(current);
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo(label, preview))
        {
            bool isDefault = string.IsNullOrEmpty(current);
            if (ImGui.Selectable("(default ImGui)", isDefault))
            {
                current = "";
                noWickyXIV.Config.Save();
            }
            foreach (var path in fonts)
            {
                bool sel = path.Equals(current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(System.IO.Path.GetFileName(path), sel))
                {
                    current = path;
                    noWickyXIV.Config.Save();
                }
            }
            ImGui.EndCombo();
        }
        // Ctrl+wheel cycle. Up = previous font, Down = next. Wraps.
        // Activates on hover of the combo's clickable area (which is
        // the previous item).
        try
        {
            var io = ImGui.GetIO();
            if (ImGui.IsItemHovered() && io.KeyCtrl && MathF.Abs(io.MouseWheel) > 0.01f && fonts.Count > 0)
            {
                int idx = -1;
                for (int i = 0; i < fonts.Count; i++)
                {
                    if (fonts[i].Equals(current, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
                int step = io.MouseWheel > 0 ? -1 : 1;
                int next = idx < 0
                    ? (step > 0 ? 0 : fonts.Count - 1)
                    : (((idx + step) % fonts.Count) + fonts.Count) % fonts.Count;
                current = fonts[next];
                noWickyXIV.Config.Save();
            }
        }
        catch { /* defensive — ImGui IO can be unavailable mid-frame */ }
    }

    private static void DrawCompassTab()
    {
        if (ImGuiEx.BeginGroupBox("Compass overlay"))
        {
            ConfigCheckbox("Enable##Compass", ref noWickyXIV.Config.EnableCompass);
            ImGui.TextDisabled(
                "Horizontal bar pinned to the top of the screen. Cardinal\n" +
                "directions and objective icons slide across as the camera\n" +
                "rotates; alpha fades toward the left/right corners.");
            ConfigSliderFloat("Offset X (px)##Compass",   ref noWickyXIV.Config.CompassOffsetX,    -800f, 800f, 0f,   "%.0f");
            ConfigSliderFloat("Offset Y from top (px)##Compass", ref noWickyXIV.Config.CompassOffsetY, 0f, 400f, 40f, "%.0f");
            ConfigSliderFloat("Width (px)##Compass",      ref noWickyXIV.Config.CompassWidth,      100f, 2000f, 600f, "%.0f");
            ConfigSliderFloat("Height (px)##Compass",     ref noWickyXIV.Config.CompassHeight,     8f,   120f,  32f,  "%.0f");
            ConfigSliderFloat("FOV (degrees)##Compass",   ref noWickyXIV.Config.CompassFovDegrees, 30f,  360f, 180f,  "%.0f");
            ConfigSliderFloat("Edge fade %##Compass",     ref noWickyXIV.Config.CompassEdgeFadePct, 0f,  0.5f, 0.18f);
            ConfigSliderFloat("Fade speed##Compass",      ref noWickyXIV.Config.CompassFadeSpeed,  1f,   20f,  8f);
            ConfigSliderFloat("Icon size##Compass",       ref noWickyXIV.Config.CompassIconSize,   8f,   64f,  22f, "%.0f");
            ConfigSliderFloat("Max range (yalms)##Compass", ref noWickyXIV.Config.CompassMaxRangeYalms, 10f, 1000f, 200f, "%.0f");
            ImGui.Separator();
            ConfigSliderFloat("Bar R##Compass",   ref noWickyXIV.Config.CompassBarColorR, 0f, 1f, 0f);
            ConfigSliderFloat("Bar G##Compass",   ref noWickyXIV.Config.CompassBarColorG, 0f, 1f, 0f);
            ConfigSliderFloat("Bar B##Compass",   ref noWickyXIV.Config.CompassBarColorB, 0f, 1f, 0f);
            ConfigSliderFloat("Bar alpha##Compass", ref noWickyXIV.Config.CompassBarColorA, 0f, 1f, 0.45f);
            ConfigSliderFloat("Tick R##Compass",  ref noWickyXIV.Config.CompassTickColorR, 0f, 1f, 1f);
            ConfigSliderFloat("Tick G##Compass",  ref noWickyXIV.Config.CompassTickColorG, 0f, 1f, 1f);
            ConfigSliderFloat("Tick B##Compass",  ref noWickyXIV.Config.CompassTickColorB, 0f, 1f, 1f);
            ConfigSliderFloat("Tick alpha##Compass", ref noWickyXIV.Config.CompassTickColorA, 0f, 1f, 0.85f);
            ImGuiEx.EndGroupBox();
        }

        if (ImGuiEx.BeginGroupBox("Sources"))
        {
            ConfigCheckbox("Show cardinals (N/E/S/W)##Compass",        ref noWickyXIV.Config.CompassShowCardinals);
            ConfigCheckbox("Show waymarks (A/B/C/D + 1/2/3/4)##Compass", ref noWickyXIV.Config.CompassShowWaymarks);
            ConfigCheckbox("Show target##Compass",                      ref noWickyXIV.Config.CompassShowTarget);
            ConfigCheckbox("Show focus target##Compass",                ref noWickyXIV.Config.CompassShowFocusTarget);
            ConfigCheckbox("Show party members##Compass",               ref noWickyXIV.Config.CompassShowParty);
            ConfigCheckbox("Show active FATEs##Compass",                ref noWickyXIV.Config.CompassShowFates);
            ConfigCheckbox("Show nearby aetherytes##Compass",           ref noWickyXIV.Config.CompassShowAetherytes);
            ImGuiEx.EndGroupBox();
        }
    }

    private static unsafe void DrawMiscTab()
    {
        // MSQ teleport overlay (floating pill at top-center of screen).
        if (ImGuiEx.BeginGroupBox("MSQ Teleport overlay"))
        {
            ConfigCheckbox("Enable##MsqTeleport", ref noWickyXIV.Config.EnableMsqTeleport);
            ImGui.TextDisabled(
                "Floating pill at the top-center of the screen. Hover the\n" +
                "area to slide it down; click to teleport to the nearest\n" +
                "Aetheryte for your current Main Scenario Quest objective.");
            ImGuiEx.EndGroupBox();
        }

        // Quick menu launcher (bottom-right slide-up pill).
        if (ImGuiEx.BeginGroupBox("Quick menu (bottom-right launcher)"))
        {
            ConfigCheckbox("Enable##QuickMenu", ref noWickyXIV.Config.EnableQuickMenu);
            ImGui.TextDisabled(
                "Floating pill at the bottom-right of the screen. Hover\n" +
                "the corner to slide it up; click a row to fire its slash\n" +
                "command. Rows (top → bottom):\n" +
                "  /xlplugins   /nowickyxiv   /glamourer\n" +
                "  /penumbra    /vfxedit");
            ImGuiEx.EndGroupBox();
        }

        // Custom teleport menu — replaces the native Teleport window.
        if (ImGuiEx.BeginGroupBox("Custom teleport menu"))
        {
            ConfigCheckbox("Enable##CustomTeleportMenu", ref noWickyXIV.Config.EnableCustomTeleportMenu);
            ImGui.TextDisabled(
                "Replaces the game's native Teleport window with a custom\n" +
                "searchable list. Includes FC house shortcut, recently\n" +
                "visited tracking, and region-grouped aetherytes.");
            ImGuiEx.EndGroupBox();
        }

        // Cutscene letterbox removal.
        if (ImGuiEx.BeginGroupBox("Cutscene letterbox"))
        {
            ConfigCheckbox("Hide black bars##CutsceneLetterbox", ref noWickyXIV.Config.HideCutsceneLetterbox);
            ImGui.TextDisabled(
                "Removes the cinematic black bars during in-game\n" +
                "rendered cutscenes. Ideal for ultrawide (21:9)\n" +
                "monitors. Does not affect pre-rendered video cutscenes.");
            ImGuiEx.EndGroupBox();
        }

        // Enemy size clamp (duty-only).
        if (ImGuiEx.BeginGroupBox("Enemy size clamp (duty only)"))
        {
            ConfigCheckbox("Enable##EnemySizeClamp", ref noWickyXIV.Config.EnableEnemySizeClamp);
            ConfigSliderFloat("Max scale##EnemySizeClampMax", ref noWickyXIV.Config.EnemySizeClampMax, 0.5f, 10f, 3.0f);
            ImGui.TextDisabled(
                "Proportionally shrinks oversized enemy models during\n" +
                "Duty Finder content so they don't fill the screen.\n" +
                "Only active inside duties — overworld is never touched.");
            ImGuiEx.EndGroupBox();
        }

        // Third-Person LMB → hotbar translator (moved from Camera Dynamics).
        if (ImGuiEx.BeginGroupBox("Third-person LMB → hotbar translator"))
        {
            ConfigCheckbox("Enable##ClickTranslate", ref noWickyXIV.Config.EnableThirdPersonClickTranslation);
            ImGui.TextDisabled(
                "Mapping (priority order):\n" +
                "  forward + LMB  → Shift+3   (W or stick-up)\n" +
                "  back    + LMB  → Shift+1   (S or stick-down)\n" +
                "  Shift   + LMB  → 1\n" +
                "  Ctrl    + LMB  → 3\n" +
                "  LMB            → 2");
            ImGui.Separator();
            ConfigCheckbox("Positional auto-modifier##ClickTranslate", ref noWickyXIV.Config.EnablePositionalAutoLmb);
            ImGui.TextDisabled(
                "Plain LMB picks slot based on player's positional vs target:\n" +
                "  Rear   → Shift+2 (default)\n" +
                "  Flank  → Shift+3 (as if Shift+LMB)\n" +
                "  Front  → Shift+1 (as if RMB+LMB)\n" +
                "Holding Shift / Ctrl / RMB still wins manually.\n" +
                "Suppressed while True North or Meikyo Shisui is active.");
            ImGui.Separator();
            ConfigCheckbox("Positional auto-cycle (one click → full combo)##ClickTranslate", ref noWickyXIV.Config.EnablePositionalAutoCycle);
            ImGui.TextDisabled(
                "One LMB click fires the whole combo. Chord 0 (Hakaze)\n" +
                "fires on the click-time slot; chord 1 re-samples\n" +
                "positional so movement during chord 0's GCD steers the\n" +
                "branch. Chord 2 stays on chord 1's slot (combo locks):\n" +
                "  Front  → Shift+1 ×2 (hakaze → yukikaze)\n" +
                "  Rear   → Shift+2 ×3 (hakaze → jinpu → gekko)\n" +
                "  Flank  → Shift+3 ×3 (hakaze → shifu → kasha)\n" +
                "Re-clicking mid-sequence cancels and starts fresh.\n" +
                "Pacing reads live GCD; auto-fires inside FFXIV's action queue.\n" +
                "Suppressed by manual modifier or True North / Meikyo.");
            ImGuiEx.EndGroupBox();
        }

        // Hotbar Fader (hover to reveal).
        if (ImGuiEx.BeginGroupBox("Hotbar Fader (hover to reveal)"))
        {
            ConfigCheckbox("Enable##HotbarFader", ref noWickyXIV.Config.EnableHotbarFader);
            ImGui.TextDisabled(
                "Every main hotbar (1..10) plus the Duty Actions bar\n" +
                "sits at Sheathed alpha and fades up to Drawn alpha\n" +
                "while the cursor is over it, then fades back out.");
            ConfigSliderFloat("Fade rate (1/s)##HotbarFader",   ref noWickyXIV.Config.HotbarFaderRate,         1f,   20f,  6.0f,  "%.1f");
            ConfigSliderFloat("Drawn alpha##HotbarFader",       ref noWickyXIV.Config.HotbarFaderDrawnAlpha,   0f,   1f,   1.0f,  "%.2f");
            ConfigSliderFloat("Sheathed alpha##HotbarFader",    ref noWickyXIV.Config.HotbarFaderSheathedAlpha,0f,   1f,   0.0f,  "%.2f");
            ConfigCheckbox   ("Hover fades a bar back in##HotbarFader", ref noWickyXIV.Config.HotbarFaderHoverActivates);
            ImGui.Separator();
            ImGui.TextDisabled("Conditional bars (0 = disabled, 1..10 = hotbar number)");
            ConfigSliderInt  ("Combo-prompt bar##HotbarFader",          ref noWickyXIV.Config.HotbarFaderComboPromptBar,  0, 10, 0);
            ImGui.TextDisabled("Fades in while a combo is active and lands on one of its slots; fades out when the combo ends.");
            ConfigSliderInt  ("Availability-flash bar##HotbarFader",    ref noWickyXIV.Config.HotbarFaderAvailabilityBar, 0, 10, 0);
            ConfigSliderFloat("Flash hold (s)##HotbarFader",            ref noWickyXIV.Config.HotbarFaderAvailabilityFlashSeconds, 0.1f, 5f, 1.5f, "%.2f");
            ImGui.TextDisabled("Flashes in for the hold duration each time a slot transitions from cooldown to ready, then fades out.");
            ImGuiEx.EndGroupBox();
        }

        // Hide target arrow (the chevron above the current target).
        if (ImGuiEx.BeginGroupBox("Target indicators"))
        {
            ConfigCheckbox("Hide arrow above target##TargetArrow", ref noWickyXIV.Config.HideTargetArrow);
            ImGui.TextDisabled("Pins the _TargetCursor / _TargetCursorParent addon alpha to 0 each frame. Restored on toggle-off.");
            ImGuiEx.EndGroupBox();
        }

        // SAM-specific: Ikishoten ↔ Ogi Namikiri slot swap.
        if (ImGuiEx.BeginGroupBox("SAM: Ikishoten / Ogi Namikiri swap"))
        {
            ConfigCheckbox("Enable swap on Hotbar 7 Slot 3##IkiOgi", ref noWickyXIV.Config.EnableIkishotenOgiSwap);
            ImGui.TextDisabled(
                "Setup:\n" +
                "  Hotbar 7 Slot 3 → Ikishoten (the active slot)\n" +
                "  Hotbar 7 Slot 9 → Ogi Namikiri (the swap target)\n" +
                "Toggle on AFTER both slots are set. While Ogi Namikiri Ready\n" +
                "(2959) or Kaeshi: Namikiri Ready (2960) is up, slot 3 mirrors\n" +
                "slot 9. Otherwise it restores whatever you had in slot 3 when\n" +
                "you flipped the toggle. No action ids are hardcoded — both\n" +
                "ends are read live from your hotbar.");
            ImGuiEx.EndGroupBox();
        }

        // Crosshair overlay — global (does NOT swap with preset).
        if (ImGuiEx.BeginGroupBox("Crosshair overlay"))
        {
            ConfigCheckbox("Enable##Crosshair", ref noWickyXIV.Config.EnableCrosshair);
            ImGui.TextDisabled("Toggle hotkey is in Camera Dynamics → Hotkeys (default V).");
            ConfigSliderFloat("Size (px)##Crosshair",      ref noWickyXIV.Config.CrosshairSize,      2f,  40f,  8f);
            ConfigSliderFloat("Thickness##Crosshair",      ref noWickyXIV.Config.CrosshairThickness, 1f,  6f,   2f);
            ConfigSliderFloat("Fade speed##Crosshair",     ref noWickyXIV.Config.CrosshairFadeSpeed, 1f,  20f,  6f);
            ConfigSliderFloat("Offset X (px)##Crosshair",  ref noWickyXIV.Config.CrosshairOffsetX,  -800f, 800f, 0f, "%.0f");
            ConfigSliderFloat("Offset Y (px)##Crosshair",  ref noWickyXIV.Config.CrosshairOffsetY,  -600f, 600f, 0f, "%.0f");
            ImGui.Separator();
            ConfigCheckbox("Auto-target enemy under crosshair##XAutoT", ref noWickyXIV.Config.EnableCrosshairAutoTarget);
            ImGui.TextDisabled(
                "When you have NO target and the crosshair is over an enemy,\n" +
                "auto-pick that enemy. Lets queued abilities / right-click\n" +
                "attacks land without manually clicking the target first.\n" +
                "Stops the moment a target is set (manual or otherwise).");
            ConfigSliderFloat("Pick radius (px)##XAutoT",   ref noWickyXIV.Config.CrosshairAutoTargetRadius, 16f, 240f, 80f, "%.0f");
            ConfigSliderFloat("Color R##Crosshair", ref noWickyXIV.Config.CrosshairColorR, 0f, 1f, 1f);
            ConfigSliderFloat("Color G##Crosshair", ref noWickyXIV.Config.CrosshairColorG, 0f, 1f, 1f);
            ConfigSliderFloat("Color B##Crosshair", ref noWickyXIV.Config.CrosshairColorB, 0f, 1f, 1f);
            ConfigSliderFloat("Alpha##Crosshair",   ref noWickyXIV.Config.CrosshairColorA, 0f, 1f, 0.85f);
            ImGuiEx.EndGroupBox();
        }

        // Chat fader (fades chat when not typing, restores on hover / new message / typing).
        if (ImGuiEx.BeginGroupBox("Chat fader"))
        {
            ConfigCheckbox("Enable##ChatFader", ref noWickyXIV.Config.EnableChatFader);
            ImGui.TextDisabled("Fades the chat log when you're not typing. Restored when chat input is focused, when hovered, or for a few seconds after a new message arrives.");
            ConfigSliderFloat("Idle alpha##ChatFader",        ref noWickyXIV.Config.ChatFaderIdleAlpha,                  0f,   1f,   0.20f, "%.2f");
            ConfigSliderFloat("Active alpha##ChatFader",      ref noWickyXIV.Config.ChatFaderActiveAlpha,                0f,   1f,   1.00f, "%.2f");
            ConfigSliderFloat("Fade rate (1/s)##ChatFader",   ref noWickyXIV.Config.ChatFaderRate,                       1f,   20f,  6.0f,  "%.1f");
            ConfigCheckbox  ("Hover to show##ChatFader",      ref noWickyXIV.Config.ChatFaderHoverActivates);
            ConfigSliderFloat("New-msg hold (s, 0=off)##ChatFader", ref noWickyXIV.Config.ChatFaderHoldOnNewMessageSeconds, 0f, 15f, 4.0f, "%.1f");
            ImGui.Separator();
            ConfigCheckbox("Minimal mode (hide tabs + icons)##ChatFader", ref noWickyXIV.Config.ChatMinimalMode);
            ImGui.TextDisabled("Hides the tab strip and the three icon buttons next to it; only the chat lines and input box remain visible. Restored on toggle-off.");
            if (ImGui.Button("Dump chat addon node tree to log##ChatFader"))
                ChatFader.DumpChatLogNodeTree();
            ImGui.SameLine();
            ImGui.TextDisabled("Logs every ChatLog node ID + rect to PluginLog so we can identify hidden-target IDs.");

            ImGui.Separator();
            ConfigCheckbox("Hide native chat entirely##ChatHideNative", ref noWickyXIV.Config.ChatHideNative);
            ImGui.TextDisabled("Clears the ChatLog root visibility bit. The input field still works (Enter focuses it natively). Pair with the bubble overlay below.");
            ImGuiEx.EndGroupBox();
        }

        // Chat bubbles overlay (v1: read-only).
        if (ImGuiEx.BeginGroupBox("Chat bubbles overlay (read-only)"))
        {
            ConfigCheckbox("Enable##ChatBubbles", ref noWickyXIV.Config.EnableChatBubbles);
            ImGui.TextDisabled("Renders incoming chat lines as alternating left/right bubbles with the sender label below each one. Sending chat is still done through the native input (Enter).");
            ConfigSliderFloat("Anchor X (px)##ChatBubbles",  ref noWickyXIV.Config.ChatBubblesX,            0f,    3840f, 960f, "%.0f");
            ConfigSliderFloat("Anchor Y (px, bottom)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesY,    0f,    2160f, 700f, "%.0f");
            ConfigSliderFloat("Column width (px)##ChatBubbles",  ref noWickyXIV.Config.ChatBubblesColumnWidth, 200f,  1400f, 700f, "%.0f");
            ConfigSliderFloat("Bubble max width (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesMaxWidth, 100f, 800f, 360f, "%.0f");
            ConfigSliderFloat("Max age (s)##ChatBubbles",   ref noWickyXIV.Config.ChatBubblesMaxAgeSeconds,   5f,    300f, 30f, "%.0f");
            ConfigSliderFloat("Max column height (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesMaxColumnHeight, 200f, 2000f, 600f, "%.0f");
            ImGui.TextDisabled("Caps the visible bubble stack height. Older messages above this band are masked by the soft top-fade gradient and eventually skip drawing.");
            ConfigSliderFloat("Top-fade height (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesTopFadeHeight, 0f, 400f, 100f, "%.0f");
            ImGui.TextDisabled("Soft gradient band at the top of the column — bubbles whose top edge sits inside it fade toward 0 alpha so old messages disappear gradually.");
            ConfigSliderFloat("Hover-reveal height (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesHoverRevealHeight, 100f, 2160f, 800f, "%.0f");
            ImGui.TextDisabled("Hovering inside the column rect reveals every buffered message until you mouse out.");
            ConfigSliderFloat("Hover hold (s)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesHoverHoldSeconds, 0f, 5f, 1.5f, "%.1f");
            ImGui.Separator();
            ConfigCheckbox("Show typing indicators (rtyping)##ChatBubbles", ref noWickyXIV.Config.EnableTypingIndicators);
            ImGui.TextDisabled("When the rtyping plugin is installed and connected, ghost bubbles for currently-typing OTHER players appear at the bottom of the column with smooth fade. Self is excluded — you already see your text in the typing prompt.");
            ConfigSliderFloat("Typing band height (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesTypingReserveHeight, 0f, 200f, 30f, "%.0f");
            ImGui.TextDisabled("Reserved bottom band so real bubbles don't shift when the indicator fades in/out.");
            ImGui.Separator();
            ConfigCheckbox("Backfill chat history on plugin load##ChatBubbles", ref noWickyXIV.Config.ChatBubblesBackfillOnLoad);
            ImGui.TextDisabled("Reads RaptureLogModule.LogMessageData on Initialize to seed the buffer with prior messages so the overlay isn't blank. Format is undocumented and may break on patch days — flip off if that happens.");
            ConfigCheckbox("Show channel tag above bubbles##ChatBubbles", ref noWickyXIV.Config.ChatBubblesShowChannelTag);
            ImGui.TextDisabled("Renders a small bracketed tag like [PARTY], [FC], [TELL], [LS3] above each bubble in the channel's color. /say is always unmarked since it's the baseline.");

            ImGui.Separator();
            ConfigCheckbox("Play typing emote##ChatTypingEmote", ref noWickyXIV.Config.EnableTypingEmote);
            ImGui.TextDisabled("Fires the configured slash command once when the chat input gains focus. Default /tomescroll loops on its own. Set the cancel command to fire something on focus-lost, or leave empty to let movement break the pose naturally.");
            string cmd = noWickyXIV.Config.ChatTypingEmoteCommand ?? "";
            if (ImGui.InputText("Emote command##ChatTypingEmote", ref cmd, 64))
            {
                noWickyXIV.Config.ChatTypingEmoteCommand = cmd;
                noWickyXIV.Config.Save();
            }
            string cancelCmd = noWickyXIV.Config.ChatTypingEmoteCancelCommand ?? "";
            if (ImGui.InputText("Cancel command (optional)##ChatTypingEmote", ref cancelCmd, 64))
            {
                noWickyXIV.Config.ChatTypingEmoteCancelCommand = cancelCmd;
                noWickyXIV.Config.Save();
            }
            ConfigSliderFloat("Re-fire interval (s)##ChatTypingEmote", ref noWickyXIV.Config.ChatTypingEmoteRetriggerSeconds, 0.5f, 10f, 2.0f, "%.1f");
            ImGui.TextDisabled("How often the emote re-fires while typing. Restores the loop if it gets interrupted (chat prompt close+reopen, brief movement, engine cancel).");
            ImGui.TextDisabled("Self bubble color");
            ConfigSliderFloat("Self R##ChatBubbles", ref noWickyXIV.Config.ChatBubblesSelfR, 0f, 1f, 0.20f, "%.2f");
            ConfigSliderFloat("Self G##ChatBubbles", ref noWickyXIV.Config.ChatBubblesSelfG, 0f, 1f, 0.55f, "%.2f");
            ConfigSliderFloat("Self B##ChatBubbles", ref noWickyXIV.Config.ChatBubblesSelfB, 0f, 1f, 0.95f, "%.2f");
            ConfigSliderFloat("Self alpha##ChatBubbles", ref noWickyXIV.Config.ChatBubblesSelfAlpha, 0f, 1f, 0.85f, "%.2f");
            ImGui.TextDisabled("Other bubble color");
            ConfigSliderFloat("Other R##ChatBubbles", ref noWickyXIV.Config.ChatBubblesOtherR, 0f, 1f, 0.18f, "%.2f");
            ConfigSliderFloat("Other G##ChatBubbles", ref noWickyXIV.Config.ChatBubblesOtherG, 0f, 1f, 0.18f, "%.2f");
            ConfigSliderFloat("Other B##ChatBubbles", ref noWickyXIV.Config.ChatBubblesOtherB, 0f, 1f, 0.22f, "%.2f");
            ConfigSliderFloat("Other alpha##ChatBubbles", ref noWickyXIV.Config.ChatBubblesOtherAlpha, 0f, 1f, 0.85f, "%.2f");
            ImGui.Separator();
            DrawFontPicker("Font##ChatBubblesFont", ref noWickyXIV.Config.ChatBubblesFontPath);
            ConfigSliderFloat("Body font size (px)##ChatBubbles",   ref noWickyXIV.Config.ChatBubblesFontSize,       8f, 72f, 16f, "%.0f");
            ConfigSliderFloat("Sender font size (px)##ChatBubbles", ref noWickyXIV.Config.ChatBubblesSenderFontSize, 6f, 48f, 12f, "%.0f");
            ImGuiEx.EndGroupBox();
        }

        // Typing prompt overlay (shown while the engine has chat input focused).
        if (ImGuiEx.BeginGroupBox("Typing prompt overlay"))
        {
            ConfigCheckbox("Enable##ChatPrompt", ref noWickyXIV.Config.EnableChatPrompt);
            ImGui.TextDisabled("Centered ImGui box that mirrors the engine's chat input buffer while you're typing. Useful when the native chat is hidden.");
            ConfigSliderFloat("X (center, px)##ChatPrompt", ref noWickyXIV.Config.ChatPromptX, 0f, 3840f, 960f, "%.0f");
            ConfigSliderFloat("Y (center, px)##ChatPrompt", ref noWickyXIV.Config.ChatPromptY, 0f, 2160f, 540f, "%.0f");
            ConfigSliderFloat("Width (px)##ChatPrompt",     ref noWickyXIV.Config.ChatPromptWidth,    100f, 1600f, 600f, "%.0f");
            ConfigSliderFloat("Font size (px)##ChatPrompt", ref noWickyXIV.Config.ChatPromptFontSize,  10f, 96f,  22f,  "%.0f");
            ImGui.TextDisabled("Background");
            ConfigSliderFloat("Bg R##ChatPrompt", ref noWickyXIV.Config.ChatPromptBgR, 0f, 1f, 0.05f, "%.2f");
            ConfigSliderFloat("Bg G##ChatPrompt", ref noWickyXIV.Config.ChatPromptBgG, 0f, 1f, 0.05f, "%.2f");
            ConfigSliderFloat("Bg B##ChatPrompt", ref noWickyXIV.Config.ChatPromptBgB, 0f, 1f, 0.07f, "%.2f");
            ConfigSliderFloat("Bg alpha##ChatPrompt", ref noWickyXIV.Config.ChatPromptBgAlpha, 0f, 1f, 0.85f, "%.2f");
            ImGui.TextDisabled("Text");
            ConfigSliderFloat("Text R##ChatPrompt", ref noWickyXIV.Config.ChatPromptTextR, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("Text G##ChatPrompt", ref noWickyXIV.Config.ChatPromptTextG, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("Text B##ChatPrompt", ref noWickyXIV.Config.ChatPromptTextB, 0f, 1f, 1f, "%.2f");
            ConfigSliderFloat("Text alpha##ChatPrompt", ref noWickyXIV.Config.ChatPromptTextAlpha, 0f, 1f, 1f, "%.2f");
            ImGuiEx.EndGroupBox();
        }

        // Combat-event diagnostics for verifying NormalHit/CritHit/IncomingDamage bit positions.
        if (ImGuiEx.BeginGroupBox("Diagnostics"))
        {
            ConfigCheckbox("Log combat hit details##CombatDiag", ref noWickyXIV.Config.LogCombatHitDiagnostics);
            ImGui.TextDisabled("Logs every damage effect entry (type, Param0/Param1, action id, crit/dh decision).\nLeave off in normal play — it spams the log fast.");
            ImGuiEx.EndGroupBox();
        }

        // Instant mode + height offsets (moved from Camera Dynamics → Misc subsection).
        if (ImGuiEx.BeginGroupBox("Camera tweaks"))
        {
            ConfigCheckbox("Instant mode (zero all smoothing)", ref noWickyXIV.Config.InstantMode);
            ImGui.TextDisabled("Note: InstantMode is currently a no-op — FFXIV's camera struct doesn't expose smoothing rates.");
            ConfigSliderFloat("Ctrl+scroll height step", ref noWickyXIV.Config.HeightOffsetStep, 0.01f, 1f, 0.1f);
            ConfigSliderFloat("Live height offset (Ctrl/Alt+scroll)", ref noWickyXIV.Config.GlobalHeightOffset, -2f, 4f, 0f);

            ImGui.Separator();
            ConfigCheckbox("FOV-zoom continuation", ref noWickyXIV.Config.EnableFovZoomContinuation);
            ImGui.TextDisabled(
                "When zoom hits MinZoom, further Shift+Ctrl+scroll narrows FoV instead\n" +
                "of pulling the camera closer. Stops the camera from pivoting overhead\n" +
                "at extreme close zoom; gives a telephoto feel instead.");
            ConfigSliderFloat("Tightest FoV (rad)##FovZoomMin",
                ref noWickyXIV.Config.FovZoomMinFov, 0.1f, 0.78f, 0.25f, "%.2f");

            ImGui.Separator();
            ConfigCheckbox("Lock camera target during NPC dialogue", ref noWickyXIV.Config.LockCameraDuringNpcDialogue);
            ImGui.TextDisabled(
                "Prevents the engine from retargeting the camera to the NPC during\n" +
                "OccupiedInQuestEvent / OccupiedInEvent. Combined with a TalkingToNpc\n" +
                "preset condition, this makes our preset transition the only camera\n" +
                "move on dialogue start/end — no engine 'dip then snap' override.");
            ImGuiEx.EndGroupBox();
        }

        // Mount Audio — dynamic per-mount engine sounds.
        if (ImGuiEx.BeginGroupBox("Mount Audio (dynamic engine sounds)"))
        {
            ConfigCheckbox("Enable mount audio", ref noWickyXIV.Config.EnableMountAudio);
            ImGui.TextDisabled(
                "Reads local player's mount + speed each frame and crossfades user-\n" +
                "provided .ogg loops (idle / accel / cruise / decel / mount / dismount).\n" +
                "Audio files live in <plugin-dir>/assets/mount-audio/<mountId>/.\n" +
                "Missing layers are skipped silently — partial packs work.");

            ConfigSliderFloat("Master volume##MountAudio",
                ref noWickyXIV.Config.MountAudioVolume, 0f, 1f, 0.6f, "%.2f");
            ImGui.TextDisabled("Multiplied by per-layer base volume. 1.0 = full layer volume.");

            ImGui.Separator();
            ImGui.TextUnformatted("Cruise pitch (engine RPM feel)");
            ConfigSliderFloat("Speed for max pitch (m/s)##MountAudio",
                ref noWickyXIV.Config.MountAudioMaxSpeed, 5f, 50f, 24f, "%.0f m/s");
            ConfigSliderFloat("Cruise pitch at zero speed##MountAudio",
                ref noWickyXIV.Config.MountAudioCruisePitchMin, 0.5f, 1.5f, 0.85f, "%.2fx");
            ConfigSliderFloat("Cruise pitch at max speed##MountAudio",
                ref noWickyXIV.Config.MountAudioCruisePitchMax, 0.5f, 2.0f, 1.20f, "%.2fx");
            ImGui.TextDisabled(
                "The cruise loop's playback rate is interpolated from min→max as your\n" +
                "speed scales 0→max. SmbPitchShifting keeps the loop length constant\n" +
                "so the engine pitch shifts without speeding up the loop itself.");

            ImGui.Separator();
            ImGui.TextUnformatted("Sound slots — modify existing sounds + their trigger conditions");

            // Mount-id selector for which mount's slots we're editing.
            // Auto-fills from the currently mounted character on first
            // open; user can override via the input.
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Mount ID##slotEditId", ref _mountAudioEditId, 0, 0);
            if (_mountAudioEditId < 0) _mountAudioEditId = 0;
            ImGui.SameLine();
            if (ImGui.Button("Use current mount##slotUseCur"))
            {
                try
                {
                    var lp = DalamudApi.ObjectTable.LocalPlayer;
                    if (lp != null)
                    {
                        unsafe
                        {
                            var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
                            if (ch != null)
                            {
                                int mid = (int)(byte)ch->Mount.MountId;
                                if (mid > 0) _mountAudioEditId = mid;
                            }
                        }
                    }
                }
                catch { }
            }
            ImGui.SameLine();
            ImGui.TextDisabled("(decimal Mount.exh row id)");

            // Speed-band thresholds. Three sliders define the four
            // bands: Idle (< slow), Slow (slow..mid), Mid (mid..top),
            // Top (>= top). Crossing band boundaries fires the
            // appropriate transition one-shot.
            ImGui.TextUnformatted("Speed-band thresholds (m/s)");
            ConfigSliderFloat("Idle → Slow##band1",
                ref noWickyXIV.Config.MountAudioSpeedSlowMin, 0.05f, 5f, 0.5f, "%.2f m/s");
            ConfigSliderFloat("Slow → Mid##band2",
                ref noWickyXIV.Config.MountAudioSpeedMidMin, 1f, 20f, 8f, "%.2f m/s");
            ConfigSliderFloat("Mid → Top##band3",
                ref noWickyXIV.Config.MountAudioSpeedTopMin, 5f, 35f, 15f, "%.2f m/s");
            ImGui.TextDisabled(
                "Speed below Idle→Slow = idle band. Crossing it going up\n" +
                "fires idle2slow. Slow→Mid crossing going up fires revup.\n" +
                "Mid→Top is a silent loop swap. Crossing back down through\n" +
                "Idle→Slow fires decel.");

            ImGui.Spacing();
            ImGui.TextUnformatted("Sound slots");

            // Fixed 9-slot table. Each row = one of the slots the
            // state machine actually drives. Four LOOP slots map to
            // the four speed bands; five ONE-SHOT slots fire on
            // band-crossing edges + mount/dismount events.
            var slotDefs = new (string slot, string label, string trigger)[]
            {
                ("mount",     "Mount-up",        "One-shot on mount edge"),
                ("idle",      "Idle (loop)",     "Looped while speed < Idle→Slow threshold"),
                ("idle2slow", "Idle → Slow",     "One-shot when speed crosses Idle→Slow going up"),
                ("slow",      "Slow (loop)",     "Looped while speed in [Idle→Slow, Slow→Mid)"),
                ("revup",     "Slow → Mid (rev-up)", "One-shot when speed crosses Slow→Mid going up"),
                ("mid",       "Mid → Top (one-shot)", "One-shot when entering Mid/Top from below; top loop waits for this to finish"),
                ("top",       "Top (loop)",      "Looped while speed >= Mid→Top, debounced to start after mid one-shot ends"),
                ("decel",     "Slow → Idle",     "One-shot when speed drops back into idle band (slowing to a stop)"),
                ("dismount",  "Dismount",        "One-shot on dismount edge"),
            };

            for (int s = 0; s < slotDefs.Length; s++)
            {
                var def = slotDefs[s];
                ImGui.PushID($"slotrow{s}");

                // Find existing override for this (mountId, slot) — null if none.
                var ovs = noWickyXIV.Config.MountAudioOverrides;
                int existingIdx = ovs.FindIndex(o =>
                    o.MountId == _mountAudioEditId
                    && string.Equals(o.Slot, def.slot, StringComparison.OrdinalIgnoreCase));
                string path = existingIdx >= 0 ? (ovs[existingIdx].FilePath ?? "") : "";

                // Two-line row: header (label + trigger), then path input.
                ImGui.TextUnformatted(def.label);
                ImGui.SameLine();
                ImGui.TextDisabled($"  ({def.trigger})");

                ImGui.SetNextItemWidth(560 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("##path", ref path, 1024))
                {
                    if (existingIdx >= 0)
                    {
                        ovs[existingIdx].FilePath = path;
                    }
                    else if (!string.IsNullOrEmpty(path))
                    {
                        ovs.Add(new MountAudioSlotOverride
                        {
                            MountId  = _mountAudioEditId,
                            Slot     = def.slot,
                            FilePath = path,
                        });
                    }
                    noWickyXIV.Config.Save();
                }
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(path))
                {
                    string status = System.IO.File.Exists(path)
                        ? "File exists ✓ (override active)"
                        : "File NOT found ✗ (will fall back to convention)";
                    ImGui.SetTooltip($"{path}\n{status}");
                }

                if (string.IsNullOrEmpty(path))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(no file picked — slot is silent)");
                }
                else
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Clear##clr"))
                    {
                        if (existingIdx >= 0)
                        {
                            ovs.RemoveAt(existingIdx);
                            noWickyXIV.Config.Save();
                        }
                    }
                }

                // Per-slot timing controls — DelayMs / FadeInMs /
                // FadeOutMs. Find or create the matching timing
                // entry on first edit. Three small numeric inputs on
                // a single line keep the row compact.
                var timings = noWickyXIV.Config.MountAudioTimings;
                int tIdx = timings.FindIndex(t =>
                    t.MountId == _mountAudioEditId
                    && string.Equals(t.Slot, def.slot, StringComparison.OrdinalIgnoreCase));
                MountAudioSlotTiming tEntry = tIdx >= 0 ? timings[tIdx] : null;
                int delayMs   = tEntry?.DelayMs   ?? 0;
                int fadeInMs  = tEntry?.FadeInMs  ?? 400;
                int fadeOutMs = tEntry?.FadeOutMs ?? 400;
                bool tChanged = false;

                ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Delay ms##d", ref delayMs, 50, 200))
                {
                    if (delayMs < 0) delayMs = 0;
                    tChanged = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Wait this long after the trigger event before the sound starts.\n"
                        + "Useful for spacing — e.g. set 'idle' delay to 1500 to let the\n"
                        + "mount-up one-shot finish before the idle hum begins.");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Fade-in ms##fi", ref fadeInMs, 50, 200))
                {
                    if (fadeInMs < 0) fadeInMs = 0;
                    tChanged = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Fade-in ramp duration. Default 400 ms.");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Fade-out ms##fo", ref fadeOutMs, 50, 200))
                {
                    if (fadeOutMs < 0) fadeOutMs = 0;
                    tChanged = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Fade-out ramp duration. Default 400 ms.\n"
                        + "Crossfade overlap with the next loop happens naturally —\n"
                        + "this fade-out runs concurrently with the next loop's fade-in.");

                // Crossfade-loop only applies to LOOP slots (idle/
                // slow/top). For one-shots (mount, idle2slow, revup,
                // mid, decel, dismount) the field has no effect.
                bool isLoopSlot = def.slot == "idle" || def.slot == "slow" || def.slot == "top";
                int crossfadeLoopMs = tEntry?.CrossfadeLoopMs ?? 0;
                if (isLoopSlot)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputInt("X-loop ms##xl", ref crossfadeLoopMs, 100, 500))
                    {
                        if (crossfadeLoopMs < 0) crossfadeLoopMs = 0;
                        tChanged = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Two-instance crossfade-loop. When > 0, the layer plays\n" +
                            "two copies of the .wav back-to-back, overlapping by this\n" +
                            "many ms at the seam. Use this when your loop file's\n" +
                            "start and end samples don't perfectly match (typical for\n" +
                            "hand-recorded engine loops) — eliminates the audible\n" +
                            "click/blip at the loop point.\n\n" +
                            "0 = simple LoopStream rewind (fast but seam may click).\n" +
                            "500-1000 = good crossfade for engine drones.\n" +
                            "Only applies to loop slots; one-shots ignore this.");
                }

                if (tChanged)
                {
                    if (tEntry == null)
                    {
                        tEntry = new MountAudioSlotTiming
                        {
                            MountId = _mountAudioEditId,
                            Slot    = def.slot,
                        };
                        timings.Add(tEntry);
                    }
                    tEntry.DelayMs         = delayMs;
                    tEntry.FadeInMs        = fadeInMs;
                    tEntry.FadeOutMs       = fadeOutMs;
                    tEntry.CrossfadeLoopMs = crossfadeLoopMs;
                    noWickyXIV.Config.Save();
                }

                ImGui.PopID();
                ImGui.Spacing();
            }

            ImGui.Separator();
            ImGui.TextDisabled(
                "No convention-based auto-discovery. A slot stays\n" +
                "silent unless you've explicitly picked a file path\n" +
                "for it above — leaving a slot empty lets you A/B\n" +
                "test which sound is causing problems.");

            ImGui.Separator();
            ImGui.TextUnformatted("Native sound suppression (PlaySound hook)");
            ImGui.TextDisabled(
                "While mounted with a custom pack loaded, any sound whose path\n" +
                "contains one of these substrings is suppressed at the engine\n" +
                "play-call site — used to silence the game's native engine\n" +
                "loops so they don't double up with your custom audio.");

            ConfigCheckbox("Log every distinct sound path to /xllog (diagnose)",
                ref noWickyXIV.Config.LogMountSoundPaths);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Turn this on, mount the target mount, idle / drive / dismount.\n" +
                    "Each new sound path that fires is logged once. Identify the\n" +
                    "mount-engine paths in /xllog (typically contain 'mount' or\n" +
                    "the mount's internal name) and add their substrings below.\n" +
                    "Turn this OFF when done — every new path costs a log line.");

            var mutes = noWickyXIV.Config.MountAudioMutePatterns;
            int muteRemoveIdx = -1;
            for (int i = 0; i < mutes.Count; i++)
            {
                ImGui.PushID($"mp{i}");
                ImGui.SetNextItemWidth(560 * ImGuiHelpers.GlobalScale);
                string s = mutes[i] ?? "";
                if (ImGui.InputText("##pat", ref s, 256))
                {
                    mutes[i] = s;
                    noWickyXIV.Config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("X##rmpat")) muteRemoveIdx = i;
                ImGui.PopID();
            }
            if (muteRemoveIdx >= 0)
            {
                mutes.RemoveAt(muteRemoveIdx);
                noWickyXIV.Config.Save();
            }
            if (ImGui.Button("+ Add mute pattern"))
            {
                mutes.Add("");
                noWickyXIV.Config.Save();
            }
            ImGui.TextDisabled(
                "Pattern is a case-insensitive substring match on the sound\n" +
                "path. Examples: 'sound/mount/' suppresses everything in the\n" +
                "mount sound folder; 'mount_71' targets a specific mount id\n" +
                "if the path encodes it.");

            ImGuiEx.EndGroupBox();
        }

        // The original "Other Settings" content (collision toggle, death cam,
        // free cam, spectate, tilt) follows. Renders as-is.
        DrawOtherSettings();
    }

    private static void DrawLightSyncTab()
    {
        ImGui.TextWrapped(
            "Govee event-driven lighting. The plugin sends an HTTP override to your Sync Box "
            + "when a game event fires (death, low HP, duty pop, tells, etc.) and restores it "
            + "to Video Sync mode after the event window closes.");
        ImGui.Spacing();

        ConfigCheckbox("Enable Light Sync", ref noWickyXIV.Config.EnableLightSync);

        ImGui.Spacing();
        ImGui.TextUnformatted("Backend");
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var modes = new[] { "Cloud", "Chroma" };
        int modeIdx = Array.IndexOf(modes, noWickyXIV.Config.LightSyncMode);
        if (modeIdx < 0) modeIdx = 0;
        if (ImGui.Combo("##LightSyncMode", ref modeIdx, modes, modes.Length))
        {
            noWickyXIV.Config.LightSyncMode = modes[modeIdx];
            noWickyXIV.Config.Save();
        }
        ImGui.TextDisabled(
            "Cloud  — Govee REST. Per-color override; no Video Sync auto-restore on H6603.\n" +
            "Chroma — Local Razer Chroma SDK (localhost:54235). Requires Razer Synapse 3 +\n" +
            "         Chroma Connect AND Govee Desktop with Chroma toggle on. The H6603\n" +
            "         auto-reverts to Video Sync when our session releases — same path\n" +
            "         Apex / LoL use, no Cloud rate limit, no manual restore needed.");

        ImGui.Separator();

        // API key — paste field. Stored in local Configuration.json.
        // No masking ImGui input flag is reliably exposed by the
        // binding here, so we just label it loud and let the user
        // see it. Local file, no transmission outside Govee's API.
        ImGui.TextUnformatted("API Key");
        ImGui.SameLine();
        ImGui.TextDisabled("(Govee Home → Profile → Apply for API Key)");

        ImGui.SetNextItemWidth(380 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##LightSyncApiKey", ref noWickyXIV.Config.LightSyncApiKey, 128))
            noWickyXIV.Config.Save();
        ImGui.SameLine();
        if (ImGui.Button("Clear##LightSyncApiKey"))
        {
            noWickyXIV.Config.LightSyncApiKey = "";
            noWickyXIV.Config.Save();
        }

        ImGui.Spacing();

        // SKU and MAC — typed in for now; the device picker comes after
        // we see what /lightsync devices returns for H6603 specifically.
        ImGui.TextUnformatted("Device SKU");
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##LightSyncSku", ref noWickyXIV.Config.LightSyncDeviceSku, 16))
            noWickyXIV.Config.Save();

        ImGui.TextUnformatted("Device ID");
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##LightSyncMac", ref noWickyXIV.Config.LightSyncDeviceMac, 64))
            noWickyXIV.Config.Save();
        ImGui.TextDisabled(
            "Single-device fallback used when no devices are ticked in the list below.");

        // ---- Multi-device target list ----
        ImGui.Separator();
        ImGui.TextUnformatted("Devices");
        ImGui.TextDisabled(
            "Auto-populated from /lightsync devices. Tick the lights you want events\n" +
            "to drive. Each enabled device receives a parallel colorRgb POST per\n" +
            "event (~300-500ms latency). Lights stay on the most-recent event color\n" +
            "until the next event overrides — no Video Sync restore needed for\n" +
            "non-SyncBox lights.");

        var devices = noWickyXIV.Config.LightSyncDevices;
        if (devices == null || devices.Count == 0)
        {
            ImGui.TextDisabled("No devices discovered yet. Click 'Discover devices' below.");
        }
        else
        {
            // Header row
            ImGui.TextDisabled("  on  device                              backend  LAN IP");
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                ImGui.PushID($"lsdev_{d.DeviceId}");

                if (ImGui.Checkbox("##en", ref d.Enabled))
                    noWickyXIV.Config.Save();

                ImGui.SameLine();
                ImGui.TextUnformatted($"{d.Name}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({d.Sku})");

                // Backend tag + LAN IP edit on a second line, indented
                // so the device row stays the visually dominant
                // element.
                ImGui.SameLine(380 * ImGuiHelpers.GlobalScale);
                bool hasLan = !string.IsNullOrEmpty(d.LanIp);
                if (hasLan && d.UseLan)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f), "LAN");
                }
                else if (hasLan)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.4f, 1f), "Cloud (LAN avail)");
                }
                else
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1f), "Cloud");
                }

                ImGui.SameLine(540 * ImGuiHelpers.GlobalScale);
                ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("##lanip", ref d.LanIp, 24))
                    noWickyXIV.Config.Save();
                ImGui.SameLine();
                if (ImGui.Checkbox("Use LAN##uselan", ref d.UseLan))
                    noWickyXIV.Config.Save();

                // Segment-aware footstep alternation. When SegmentCount
                // > 1, footstep right/left alternates by addressing
                // segment halves via the Govee Cloud
                // segmentedBrightness capability instead of treating
                // the device as a single endpoint. Used for one-
                // device, multi-bar / multi-segment setups (e.g.
                // H6056 bars driven by a multi-segment controller).
                ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Segments##segcount", ref d.SegmentCount, 1, 1))
                {
                    if (d.SegmentCount < 0) d.SegmentCount = 0;
                    noWickyXIV.Config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Total addressable segments on this device. " +
                        "Set to 0 to disable per-segment control. " +
                        "When > 1 and this is the only enabled device, " +
                        "the first half of segments = RIGHT foot, " +
                        "second half = LEFT foot during running/walking " +
                        "alternation. (Cloud-only — adds ~200ms latency " +
                        "vs LAN single-endpoint sends.)");
                ImGui.SameLine();
                if (ImGui.Checkbox("Swap sides##swapseg", ref d.SwapSegmentSides))
                    noWickyXIV.Config.Save();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Flip which segment half maps to right vs left, if the bars are physically reversed.");

                ImGui.PopID();
            }

            ImGui.Spacing();
            ImGui.TextDisabled(
                "LAN routing requires 'LAN Control' enabled per-device in Govee Home,\n" +
                "the device on the same network segment as this PC, and Windows\n" +
                "Firewall to allow inbound UDP 4002. Failed LAN sends fall through\n" +
                "to Cloud automatically — events still fire.");
        }

        ImGui.Separator();

        ImGui.TextUnformatted("Test");
        ImGui.TextDisabled("Or run /lightsync devices and /lightsync test red from chat.");

        if (ImGui.Button("Discover devices (logs to /xllog)"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync devices");
        ImGui.SameLine();
        if (ImGui.Button("LAN scan (sub-30ms targeting)"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync lanscan");
        ImGui.SameLine();
        if (ImGui.Button("LAN sweep (when multicast is blocked)"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync lansweep");
        ImGui.SameLine();
        if (ImGui.Button("ARP dump (find IP manually)"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync arpdump");
        ImGui.SameLine();
        if (ImGui.Button("Test red"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync test red");
        ImGui.SameLine();
        if (ImGui.Button("Test blue"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync test blue");
        ImGui.SameLine();
        if (ImGui.Button("Restore Video"))
            DalamudApi.CommandManager.ProcessCommand("/lightsync restore");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Default flash duration (ms)",
            ref noWickyXIV.Config.LightSyncDefaultFlashMs, 100, 5000, 1500);

        ImGui.Spacing();
        ImGui.TextUnformatted("Restore method");
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        var methods = new[] { "Snapshot", "HdmiSource", "Manual" };
        int methodIdx = Array.IndexOf(methods, noWickyXIV.Config.LightSyncRestoreMethod);
        if (methodIdx < 0) methodIdx = 0;
        if (ImGui.Combo("##LightSyncRestoreMethod", ref methodIdx, methods, methods.Length))
        {
            noWickyXIV.Config.LightSyncRestoreMethod = methods[methodIdx];
            noWickyXIV.Config.Save();
        }
        ImGui.TextDisabled(
            "Snapshot (recommended): save Video Sync as a Snapshot in the Govee Home app,\n" +
            "  then run /lightsync devices and paste the snapshot id below. This is the\n" +
            "  actual mechanism the Home app uses for Video mode on H6603.\n" +
            "HdmiSource: works on H6602 only; on H6603 returns 200 but doesn't re-engage.\n" +
            "Manual: don't auto-restore — use /lightsync restore or the Home app.");

        if (noWickyXIV.Config.LightSyncRestoreMethod == "Snapshot")
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Video Sync snapshot id");
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("##LightSyncSnapshotId", ref noWickyXIV.Config.LightSyncSnapshotId))
                noWickyXIV.Config.Save();
            ImGui.TextDisabled(
                "Steps:\n" +
                "  1. Govee Home app → put H6603 in Video mode → save as a Snapshot.\n" +
                "  2. Run /lightsync devices here. The new snapshot appears in the\n" +
                "     dynamic_scene/snapshot capability's options[] with a numeric id.\n" +
                "  3. Paste that id above.");
        }
        else if (noWickyXIV.Config.LightSyncRestoreMethod == "HdmiSource")
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("HDMI source for restore");
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("##LightSyncHdmi", ref noWickyXIV.Config.LightSyncHdmiSource, 1, 4, "HDMI %d"))
                noWickyXIV.Config.Save();
        }

        // ---- Per-event configuration ----
        ImGui.Separator();
        ImGui.TextUnformatted("Events");
        ImGui.TextDisabled("Each event fires a one-shot color override on the Sync Box. With Restore method = Manual, the box stays on the most-recent event color until you /lightsync restore or restart Video mode in the Govee Home app.");
        ImGui.Spacing();

        DrawLightSyncEventRow("Death",
            ref noWickyXIV.Config.LightSyncEventDeath,
            ref noWickyXIV.Config.LightSyncEventDeathColor,
            ref noWickyXIV.Config.LightSyncEventDeathDurationMs);
        DrawLightSyncEventRow("Low HP",
            ref noWickyXIV.Config.LightSyncEventLowHp,
            ref noWickyXIV.Config.LightSyncEventLowHpColor,
            ref noWickyXIV.Config.LightSyncEventLowHpDurationMs);
        // Slider exposes the threshold as an integer percent so the
        // display reads "30%" instead of "0%" (the previous SliderFloat
        // with %.0f%% printed the raw float — 0.30 → "0%"). Range is
        // 10..50% — anything below 10 is essentially-dead and 50 is
        // the user's stated upper bound.
        {
            int pctInt = (int)MathF.Round(noWickyXIV.Config.LightSyncEventLowHpThreshold * 100f);
            if (pctInt < 10) pctInt = 10;
            if (pctInt > 50) pctInt = 50;
            ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("Low-HP threshold##LightSyncLowHpThreshold",
                    ref pctInt, 10, 50, "%d%%"))
            {
                noWickyXIV.Config.LightSyncEventLowHpThreshold = Math.Clamp(pctInt / 100f, 0.10f, 0.50f);
                noWickyXIV.Config.Save();
            }
        }
        // Low-HP pulse pattern (kept simple — comma-separated brightness
        // values entered as text since List<int> editor would balloon
        // the UI; defaults to "25,50,75").
        {
            string patternText = string.Join(",", noWickyXIV.Config.LightSyncEventLowHpPulse);
            ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("Low-HP pulse pattern (% values, comma-sep)##LightSyncLowHpPulse",
                    ref patternText, 64))
            {
                var list = new System.Collections.Generic.List<int>();
                foreach (var s in patternText.Split(','))
                {
                    if (int.TryParse(s.Trim(), out var v))
                        list.Add(Math.Clamp(v, 1, 100));
                }
                if (list.Count > 0) noWickyXIV.Config.LightSyncEventLowHpPulse = list;
                noWickyXIV.Config.Save();
            }
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
            ConfigSliderInt("Low-HP pulse step (ms)##LightSyncLowHpPulseStep",
                ref noWickyXIV.Config.LightSyncEventLowHpPulseStepMs, 50, 1000, 200);
        }

        // Tell — one-shot quick pulse 3x brightness on/off
        DrawLightSyncEventRow("Tell received",
            ref noWickyXIV.Config.LightSyncEventTell,
            ref noWickyXIV.Config.LightSyncEventTellColor,
            ref noWickyXIV.Config.LightSyncEventTellDurationMs);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Tell pulse count##LightSyncTellPulseCount",
            ref noWickyXIV.Config.LightSyncEventTellPulseCount, 1, 10, 3);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Tell pulse step (ms)##LightSyncTellPulseStep",
            ref noWickyXIV.Config.LightSyncEventTellPulseStepMs, 50, 500, 100);

        // Duty pop — alternating-group flash
        DrawLightSyncEventRow("Duty pop",
            ref noWickyXIV.Config.LightSyncEventDutyPop,
            ref noWickyXIV.Config.LightSyncEventDutyPopColor,
            ref noWickyXIV.Config.LightSyncEventDutyPopDurationMs);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Duty alt count##LightSyncDutyAltCount",
            ref noWickyXIV.Config.LightSyncEventDutyPopAltCount, 1, 10, 2);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Duty alt step (ms)##LightSyncDutyAltStep",
            ref noWickyXIV.Config.LightSyncEventDutyPopAltStepMs, 50, 500, 150);

        // Critical hit — orange→red fade. Triggered externally via
        // LightSync.OnCritHit() once we wire it from CombatEvents.
        ImGui.Separator();
        if (ImGui.Checkbox("Critical hit (orange → red fade)##LightSyncCrit",
                ref noWickyXIV.Config.LightSyncEventCrit))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncCritStartColor",
            ref noWickyXIV.Config.LightSyncEventCritStartColor);
        ImGui.SameLine();
        ImGui.TextDisabled("→");
        ImGui.SameLine();
        DrawLightSyncColorEdit("##LightSyncCritEndColor",
            ref noWickyXIV.Config.LightSyncEventCritEndColor);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("##LightSyncCritFade",
            ref noWickyXIV.Config.LightSyncEventCritFadeMs, 100, 2000, 300);

        // Riding — continuous cyan with speed-scaled brightness
        ImGui.Separator();
        if (ImGui.Checkbox("Riding (mounted + moving)##LightSyncRiding",
                ref noWickyXIV.Config.LightSyncEventRiding))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncRidingColor",
            ref noWickyXIV.Config.LightSyncEventRidingColor);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderFloat("Max speed (m/s)##LightSyncRidingMaxSpeed",
            ref noWickyXIV.Config.LightSyncEventRidingMaxSpeed, 1f, 50f, 14f, "%.0f");
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Min brightness (%)##LightSyncRidingMin",
            ref noWickyXIV.Config.LightSyncEventRidingMinBright, 1, 100, 10);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Max brightness (%)##LightSyncRidingMax",
            ref noWickyXIV.Config.LightSyncEventRidingMaxBright, 1, 100, 100);

        // Running — continuous warm white with footstep cadence pulse
        ImGui.Separator();
        if (ImGui.Checkbox("Running on foot##LightSyncRunning",
                ref noWickyXIV.Config.LightSyncEventRunning))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncRunningColor",
            ref noWickyXIV.Config.LightSyncEventRunningColor);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Step peak (%)##LightSyncRunningPeak",
            ref noWickyXIV.Config.LightSyncEventRunningPulsePeak, 1, 100, 75);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Step low (%)##LightSyncRunningLow",
            ref noWickyXIV.Config.LightSyncEventRunningPulseLow, 1, 100, 30);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Step cadence (ms)##LightSyncRunningStep",
            ref noWickyXIV.Config.LightSyncEventRunningPulseStepMs, 100, 1000, 350);

        // Walking — softer step-pulse for slower foot movement
        ImGui.Separator();
        if (ImGui.Checkbox("Walking on foot##LightSyncWalking",
                ref noWickyXIV.Config.LightSyncEventWalking))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncWalkingColor",
            ref noWickyXIV.Config.LightSyncEventWalkingColor);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Walk peak (%)##LightSyncWalkPeak",
            ref noWickyXIV.Config.LightSyncEventWalkingPulsePeak, 1, 100, 55);
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Walk low (%)##LightSyncWalkLow",
            ref noWickyXIV.Config.LightSyncEventWalkingPulseLow, 1, 100, 35);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Walk cadence (ms)##LightSyncWalkStep",
            ref noWickyXIV.Config.LightSyncEventWalkingPulseStepMs, 100, 2000, 550);
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        ConfigSliderFloat("Walk/run speed split (m/s)##LightSyncWalkThreshold",
            ref noWickyXIV.Config.LightSyncWalkSpeedThreshold, 1f, 10f, 4.5f, "%.1f");
        ImGui.TextDisabled(
            "Below this smoothed-speed threshold, foot movement uses the walking pulse.\n" +
            "FFXIV defaults: /walk ≈ 3.5 m/s, run ≈ 5 m/s, so 4.5 cleanly splits them.");

        // Sprinting — continuous light green while Sprint buff active
        ImGui.Separator();
        if (ImGui.Checkbox("Sprinting (Sprint buff active)##LightSyncSprinting",
                ref noWickyXIV.Config.LightSyncEventSprinting))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncSprintingColor",
            ref noWickyXIV.Config.LightSyncEventSprintingColor);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Brightness (%)##LightSyncSprintingBright",
            ref noWickyXIV.Config.LightSyncEventSprintingBrightness, 1, 100, 90);

        // In-combat — continuous yellow while ConditionFlag.InCombat
        ImGui.Separator();
        if (ImGui.Checkbox("In combat##LightSyncCombat",
                ref noWickyXIV.Config.LightSyncEventCombat))
            noWickyXIV.Config.Save();
        ImGui.SameLine(220 * ImGuiHelpers.GlobalScale);
        DrawLightSyncColorEdit("##LightSyncCombatColor",
            ref noWickyXIV.Config.LightSyncEventCombatColor);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ConfigSliderInt("Brightness (%)##LightSyncCombatBright",
            ref noWickyXIV.Config.LightSyncEventCombatBrightness, 1, 100, 80);
    }

    // Helper for inline color picker on per-event rows that don't
    // use DrawLightSyncEventRow (continuous events, crit fade, etc.)
    private static void DrawLightSyncColorEdit(string id, ref int rgb)
    {
        var col = new System.Numerics.Vector3(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8)  & 0xFF) / 255f,
            ( rgb        & 0xFF) / 255f);
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.ColorEdit3(id, ref col, ImGuiColorEditFlags.NoInputs))
        {
            rgb = (Math.Clamp((int)(col.X * 255f), 0, 255) << 16)
                | (Math.Clamp((int)(col.Y * 255f), 0, 255) << 8)
                |  Math.Clamp((int)(col.Z * 255f), 0, 255);
            noWickyXIV.Config.Save();
        }
    }

    // Renders one event row: enable checkbox + RGB color picker + ms slider.
    private static void DrawLightSyncEventRow(string label, ref bool enabled, ref int rgb, ref int durationMs)
    {
        var pushId = ImGui.GetID(label);
        ImGui.PushID(label);
        if (ImGui.Checkbox(label, ref enabled))
            noWickyXIV.Config.Save();

        ImGui.SameLine(180 * ImGuiHelpers.GlobalScale);
        var col = new System.Numerics.Vector3(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8)  & 0xFF) / 255f,
            ( rgb        & 0xFF) / 255f);
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.ColorEdit3("##color", ref col, ImGuiColorEditFlags.NoInputs))
        {
            rgb = (Math.Clamp((int)(col.X * 255f), 0, 255) << 16)
                | (Math.Clamp((int)(col.Y * 255f), 0, 255) << 8)
                |  Math.Clamp((int)(col.Z * 255f), 0, 255);
            noWickyXIV.Config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("##duration", ref durationMs, 100, 5000, "%dms"))
            noWickyXIV.Config.Save();
        ImGui.PopID();
    }

    // Render a hotkey row: shows current binding, "Set" button to capture next
    // key pressed, "Clear" to unbind, "Reset" to default. Stores raw VirtualKey int.
    private static int _hotkeyCapturingFor; // 0 = nothing, else field-id we're capturing for; we use the address-of-int via a static set-target
    // Selected mount id for the per-slot Mount Audio editor. Defaults
    // to 71 (Fenrir) since that's the currently set up pack, but the
    // "Use current mount" button in the panel sets it from the live
    // player.
    private static int _mountAudioEditId = 71;
    private static System.Action<int> _hotkeyCaptureCallback;
    // Tracks which VK codes were already down WHEN capture started so
    // we don't immediately rebind to whatever was held while clicking
    // "Set" (e.g. Mouse1/LMB latching from the click itself, or Ctrl
    // held while opening the panel). Capture only commits when a key
    // transitions UP→DOWN inside the capture window.
    private static bool[] _hotkeyDownAtCaptureStart = new bool[256];

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool VkDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

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

            // ESC cancels — checked first so it never gets bound.
            if (VkDown(0x1B))
            {
                _hotkeyCapturingFor = 0;
            }
            else
            {
                // Poll Win32 GetAsyncKeyState directly so we can see
                // every key (Dalamud's IKeyState only tracks the
                // engine-registered subset and silently ignored most
                // VK codes — that's the bug the user hit). Only
                // commit on a UP→DOWN transition for a key that was
                // NOT already down when capture began, otherwise the
                // click on "Set" itself, or held modifiers, would
                // rebind instantly.
                int pressed = ScanFirstFreshlyPressedVk();
                if (pressed != 0)
                {
                    vk = pressed;
                    noWickyXIV.Config.Save();
                    _hotkeyCapturingFor = 0;
                }
            }
        }
        else
        {
            if (ImGui.Button($"{display}##{label}"))
            {
                _hotkeyCapturingFor = label.GetHashCode();
                // Snapshot which keys are currently down so we can
                // require a fresh UP→DOWN transition before commit.
                for (int v = 0; v < _hotkeyDownAtCaptureStart.Length; v++)
                    _hotkeyDownAtCaptureStart[v] = VkDown(v);
            }
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

    private static int ScanFirstFreshlyPressedVk()
    {
        // Walk standard keyboard range. Skip mouse buttons (0x01..0x06)
        // entirely — binding mouse to a hotkey row isn't useful here,
        // and the LMB click that opened the capture would otherwise
        // self-bind. ESC handled by the caller. Tab/Enter/raw Shift/
        // raw Ctrl/Alt left out to avoid accidental binds; the
        // L/R-specific shift/ctrl/alt VKs (0xA0..0xA5) are also skipped
        // for the same reason — bind a real key, modifiers are
        // composed via the calling code.
        for (int v = 0x07; v <= 0xFE; v++)
        {
            if (v >= 0x01 && v <= 0x06) continue;       // mouse buttons
            if (v == 0x09 || v == 0x0D) continue;         // Tab, Enter
            if (v == 0x10 || v == 0x11 || v == 0x12) continue; // Shift, Ctrl, Alt
            if (v >= 0xA0 && v <= 0xA5) continue;         // L/R Shift, Ctrl, Alt
            if (v == 0x1B) continue;                       // ESC (cancel)
            bool nowDown = VkDown(v);
            // Reset the "was down at start" latch as soon as the user
            // releases the key, so subsequent re-presses count as
            // fresh.
            if (!nowDown && _hotkeyDownAtCaptureStart[v])
                _hotkeyDownAtCaptureStart[v] = false;
            if (nowDown && !_hotkeyDownAtCaptureStart[v])
                return v;
        }
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

    // ── Animation Swaps tab ─────────────────────────────────────
    private static (byte id, string name)[] _raceCache;
    private static (uint id, string name)[] _jobCache;

    private static unsafe void DrawAnimSwapTab()
    {
        ConfigCheckbox("Enable animation swaps", ref noWickyXIV.Config.EnableAnimationSwaps);
        ImGui.TextDisabled(
            "Transfer run/walk/idle animations from one race to another.\n" +
            "Pick your race and the race whose animations you want.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var rules = noWickyXIV.Config.AnimationSwapRules;

        // Add button.
        if (ImGui.Button("+ Add Rule"))
        {
            rules.Add(new AnimationSwapRule());
            noWickyXIV.Config.Save();
        }

        ImGui.Spacing();

        // Draw each rule.
        int removeIdx = -1;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ImGui.PushID(i);

            string srcName = GetRaceName(rule.SourceRace);
            string tgtName = GetRaceName(rule.TargetRace);
            string terrTag = rule.TerritoryId != 0 ? $"  [{rule.TerritoryName}]" : "";
            string header = $"{srcName}  →  {tgtName}{terrTag}##rule";
            bool open = ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowItemOverlap);

            // Delete button.
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF4444FFu);
            if (ImGui.SmallButton("X"))
                removeIdx = i;
            ImGui.PopStyleColor();

            if (open)
            {
                ImGui.Indent(10f);

                // Enabled.
                if (ImGui.Checkbox("Enabled", ref rule.Enabled))
                    noWickyXIV.Config.Save();

                // Source race (what you ARE).
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("My Race", srcName))
                {
                    EnsureRaceCache();
                    foreach (var (id, name) in _raceCache)
                    {
                        if (ImGui.Selectable(name, rule.SourceRace == id))
                        {
                            rule.SourceRace = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                // Target race (whose animations to USE).
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("Use Anims From", tgtName))
                {
                    EnsureRaceCache();
                    foreach (var (id, name) in _raceCache)
                    {
                        if (id == 0) continue; // "Any" doesn't make sense as target
                        if (ImGui.Selectable(name, rule.TargetRace == id))
                        {
                            rule.TargetRace = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                ImGui.Spacing();

                // Movement type toggles.
                if (ImGui.Checkbox("Walk / Run", ref rule.SwapWalk))
                    noWickyXIV.Config.Save();
                ImGui.SameLine();
                if (ImGui.Checkbox("Idle", ref rule.SwapIdle))
                    noWickyXIV.Config.Save();
                ImGui.SameLine();
                if (ImGui.Checkbox("Female Anims", ref rule.UseFemaleAnims))
                    noWickyXIV.Config.Save();

                ImGui.Spacing();

                // Territory filter.
                string terrDisplay = rule.TerritoryId == 0
                    ? "Any"
                    : $"{rule.TerritoryName} ({rule.TerritoryId})";
                ImGui.TextDisabled($"Territory: {terrDisplay}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Set to Current##raceTerr"))
                {
                    ushort tid = AnimationSwap.GetCurrentTerritory();
                    rule.TerritoryId = tid;
                    rule.TerritoryName = AnimationSwap.LookupTerritoryName(tid);
                    noWickyXIV.Config.Save();
                }
                if (rule.TerritoryId != 0)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear##raceTerr"))
                    {
                        rule.TerritoryId = 0;
                        rule.TerritoryName = "";
                        noWickyXIV.Config.Save();
                    }
                }

                ImGui.Unindent(10f);
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        if (removeIdx >= 0)
        {
            rules.RemoveAt(removeIdx);
            noWickyXIV.Config.Save();
        }

        // ── Job animation swaps ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ConfigCheckbox("Enable job animation swaps", ref noWickyXIV.Config.EnableJobAnimationSwaps);
        ImGui.TextDisabled("Swap weapon hold, movement, and auto-attack animations between jobs.");

        ImGui.Spacing();

        var jobRules = noWickyXIV.Config.JobAnimSwapRules;

        if (ImGui.Button("+ Add Job Rule"))
        {
            jobRules.Add(new JobAnimSwapRule());
            noWickyXIV.Config.Save();
        }

        ImGui.Spacing();

        int removeJobIdx = -1;
        for (int i = 0; i < jobRules.Count; i++)
        {
            var jr = jobRules[i];
            ImGui.PushID(1000 + i);

            string srcJobName = GetJobName(jr.SourceJob);
            string holdJobName = jr.HoldTargetJob == 0 ? "None" : GetJobName(jr.HoldTargetJob);
            string moveJobName = jr.MoveTargetJob == 0 ? "None" : GetJobName(jr.MoveTargetJob);
            string atkJobName = jr.AttackTargetJob == 0 ? "None" : GetJobName(jr.AttackTargetJob);
            string jobTerrTag = jr.TerritoryId != 0 ? $"  [{jr.TerritoryName}]" : "";
            string jobHeader = $"{srcJobName}  →  Hold: {holdJobName} / Move: {moveJobName} / Atk: {atkJobName}{jobTerrTag}##jobrule";
            bool jobOpen = ImGui.CollapsingHeader(jobHeader,
                ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowItemOverlap);

            // Delete button.
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF4444FFu);
            if (ImGui.SmallButton("X"))
                removeJobIdx = i;
            ImGui.PopStyleColor();

            if (jobOpen)
            {
                ImGui.Indent(10f);

                if (ImGui.Checkbox("Enabled", ref jr.Enabled))
                    noWickyXIV.Config.Save();

                // Source job (what you ARE).
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("My Job", srcJobName))
                {
                    EnsureJobCache();
                    // "Any" option.
                    if (ImGui.Selectable("Any", jr.SourceJob == 0))
                    {
                        jr.SourceJob = 0;
                        noWickyXIV.Config.Save();
                    }
                    foreach (var (id, name) in _jobCache)
                    {
                        if (ImGui.Selectable(name, jr.SourceJob == id))
                        {
                            jr.SourceJob = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                // Weapon hold target job.
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("Weapon Hold From", holdJobName))
                {
                    EnsureJobCache();
                    if (ImGui.Selectable("None", jr.HoldTargetJob == 0))
                    {
                        jr.HoldTargetJob = 0;
                        noWickyXIV.Config.Save();
                    }
                    foreach (var (id, name) in _jobCache)
                    {
                        if (ImGui.Selectable(name, jr.HoldTargetJob == id))
                        {
                            jr.HoldTargetJob = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                // Movement target job.
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("Movement From", moveJobName))
                {
                    EnsureJobCache();
                    if (ImGui.Selectable("None", jr.MoveTargetJob == 0))
                    {
                        jr.MoveTargetJob = 0;
                        noWickyXIV.Config.Save();
                    }
                    foreach (var (id, name) in _jobCache)
                    {
                        if (ImGui.Selectable(name, jr.MoveTargetJob == id))
                        {
                            jr.MoveTargetJob = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                // Auto-attack target job.
                ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("Auto-Attack From", atkJobName))
                {
                    EnsureJobCache();
                    if (ImGui.Selectable("None", jr.AttackTargetJob == 0))
                    {
                        jr.AttackTargetJob = 0;
                        noWickyXIV.Config.Save();
                    }
                    foreach (var (id, name) in _jobCache)
                    {
                        if (ImGui.Selectable(name, jr.AttackTargetJob == id))
                        {
                            jr.AttackTargetJob = id;
                            noWickyXIV.Config.Save();
                        }
                    }
                    ImGui.EndCombo();
                }

                ImGui.Spacing();

                // Territory filter.
                string jobTerrDisplay = jr.TerritoryId == 0
                    ? "Any"
                    : $"{jr.TerritoryName} ({jr.TerritoryId})";
                ImGui.TextDisabled($"Territory: {jobTerrDisplay}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Set to Current##jobTerr"))
                {
                    ushort tid = AnimationSwap.GetCurrentTerritory();
                    jr.TerritoryId = tid;
                    jr.TerritoryName = AnimationSwap.LookupTerritoryName(tid);
                    noWickyXIV.Config.Save();
                }
                if (jr.TerritoryId != 0)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear##jobTerr"))
                    {
                        jr.TerritoryId = 0;
                        jr.TerritoryName = "";
                        noWickyXIV.Config.Save();
                    }
                }

                ImGui.Unindent(10f);
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        if (removeJobIdx >= 0)
        {
            jobRules.RemoveAt(removeJobIdx);
            noWickyXIV.Config.Save();
        }

        // ── Glamourer territory automation ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ConfigCheckbox("Enable Glamourer territory automation", ref noWickyXIV.Config.EnableGlamourerTerritoryAuto);
        ImGui.TextDisabled("Apply a Glamourer design when entering a territory.\nReverts to your base Glamourer automation elsewhere.");

        ImGui.Spacing();

        var glamOverrides = noWickyXIV.Config.GlamourerTerritoryOverrides;

        if (ImGui.Button("+ Add Territory Override"))
        {
            glamOverrides.Add(new GlamourerTerritoryOverride());
            noWickyXIV.Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh Designs"))
            GlamourerBridge.RefreshDesignCache();
        ImGui.SameLine();
        if (ImGui.SmallButton("Revert Now"))
            GlamourerBridge.ManualRevert();

        string activeLabel = GlamourerBridge.ActiveDesignName != null
            ? $"Active: {GlamourerBridge.ActiveDesignName}"
            : "Active: (base automation)";
        ImGui.TextDisabled($"{activeLabel}  |  {GlamourerBridge.Status}");

        ImGui.Spacing();

        // Fetch design list for dropdown.
        var glamDesigns = GlamourerBridge.GetDesignList();
        string[] designNames = null;
        if (glamDesigns != null && glamDesigns.Count > 0)
        {
            designNames = glamDesigns.Values.OrderBy(n => n).ToArray();
        }

        // ── Duty glam (single design for all duties) ──
        ImGui.TextUnformatted("Duty Glamour:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
        if (designNames != null && designNames.Length > 0)
        {
            string dutyDesign = noWickyXIV.Config.GlamourerDutyDesign ?? "";
            string dutyPreview = string.IsNullOrEmpty(dutyDesign) ? "(none)" : dutyDesign;
            if (ImGui.BeginCombo("##dutyGlam", dutyPreview))
            {
                if (ImGui.Selectable("(none)", string.IsNullOrEmpty(dutyDesign)))
                {
                    noWickyXIV.Config.GlamourerDutyDesign = "";
                    noWickyXIV.Config.Save();
                    GlamourerBridge.ForceReevaluate();
                }
                foreach (var dn in designNames)
                {
                    if (ImGui.Selectable(dn, dutyDesign == dn))
                    {
                        noWickyXIV.Config.GlamourerDutyDesign = dn;
                        noWickyXIV.Config.Save();
                        GlamourerBridge.ForceReevaluate();
                    }
                }
                ImGui.EndCombo();
            }
        }
        else
        {
            string dutyInput = noWickyXIV.Config.GlamourerDutyDesign ?? "";
            ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("##dutyGlamText", ref dutyInput, 256))
            {
                noWickyXIV.Config.GlamourerDutyDesign = dutyInput;
                noWickyXIV.Config.Save();
                GlamourerBridge.ForceReevaluate();
            }
        }
        ImGui.TextDisabled("Applied in any duty unless a territory rule matches.");

        ImGui.Spacing();

        int removeGlamIdx = -1;
        for (int i = 0; i < glamOverrides.Count; i++)
        {
            var ov = glamOverrides[i];
            ImGui.PushID(2000 + i);

            string terrLabel = ov.TerritoryId != 0
                ? $"{ov.TerritoryName} ({ov.TerritoryId})"
                : "(no territory set)";
            string designLabel = !string.IsNullOrEmpty(ov.DesignName) ? ov.DesignName : "(no design)";
            string glamHeader = $"{terrLabel}  →  {designLabel}##glamoverride";
            bool glamOpen = ImGui.CollapsingHeader(glamHeader,
                ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowItemOverlap);

            // Delete button.
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF4444FFu);
            if (ImGui.SmallButton("X"))
                removeGlamIdx = i;
            ImGui.PopStyleColor();

            if (glamOpen)
            {
                ImGui.Indent(10f);

                // Territory.
                string terrDisplay = ov.TerritoryId != 0
                    ? $"{ov.TerritoryName} ({ov.TerritoryId})"
                    : "None";
                ImGui.TextDisabled($"Territory: {terrDisplay}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Set to Current##glamTerr"))
                {
                    ushort tid = AnimationSwap.GetCurrentTerritory();
                    ov.TerritoryId = tid;
                    ov.TerritoryName = AnimationSwap.LookupTerritoryName(tid);
                    noWickyXIV.Config.Save();
                    GlamourerBridge.ForceReevaluate();
                }

                // Design dropdown.
                ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
                if (designNames != null && designNames.Length > 0)
                {
                    if (ImGui.BeginCombo("Design", string.IsNullOrEmpty(ov.DesignName) ? "(select)" : ov.DesignName))
                    {
                        foreach (var dn in designNames)
                        {
                            if (ImGui.Selectable(dn, ov.DesignName == dn))
                            {
                                ov.DesignName = dn;
                                noWickyXIV.Config.Save();
                                GlamourerBridge.ForceReevaluate();
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
                else
                {
                    // Fallback: text input if Glamourer IPC unavailable.
                    string designInput = ov.DesignName ?? "";
                    ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("Design##glamText", ref designInput, 256))
                    {
                        ov.DesignName = designInput;
                        noWickyXIV.Config.Save();
                        GlamourerBridge.ForceReevaluate();
                    }
                }

                // Test button — try applying this design right now.
                if (!string.IsNullOrEmpty(ov.DesignName))
                {
                    if (ImGui.SmallButton("Test Apply##glamTest"))
                        GlamourerBridge.TestApply(ov.DesignName);
                }

                ImGui.Unindent(10f);
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        if (removeGlamIdx >= 0)
        {
            glamOverrides.RemoveAt(removeGlamIdx);
            noWickyXIV.Config.Save();
            GlamourerBridge.ForceReevaluate();
        }

        // ── Diagnostic section ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Live status.
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
                if (ch != null)
                {
                    ushort slot0 = ch->Timeline.TimelineSequencer.GetSlotTimeline(0);
                    string key = AnimationSwap.LookupTimelineKey(slot0);
                    byte race = ch->DrawData.CustomizeData.Race;
                    byte sex = ch->DrawData.CustomizeData.Sex;
                    ImGui.TextDisabled(
                        $"Slot 0 = {slot0} \"{key}\"  |  " +
                        $"Race = {AnimationSwap.LookupRaceName(race)} ({(sex == 0 ? "M" : "F")})");
                }
            }
        }
        catch { ImGui.TextDisabled("(unavailable)"); }

        if (AnimationSwap.VisualRaceId != 0)
            ImGui.TextDisabled(
                $"Visual: {AnimationSwap.LookupRaceName(AnimationSwap.VisualRaceId)} " +
                $"({AnimationSwap.VisualModelCode})");

        ImGui.TextDisabled($"Vtable calls: {AnimationSwap.TotalHookCalls}  |  " +
                           $"Penumbra swaps: {AnimationSwap.TotalSwaps}");
        ImGui.TextDisabled($"Status: {AnimationSwap.ResourceHookStatus}");

        // Buttons.
        if (ImGui.SmallButton("Force Redraw"))
            AnimationSwap.ForceRedraw();
        ImGui.SameLine();
        if (ImGui.SmallButton("Dump Diagnostic"))
            AnimationSwap.FlushDiag();
        ImGui.SameLine();
        ImGui.TextDisabled("animswap_diag.txt");
    }

    private static void EnsureRaceCache()
    {
        if (_raceCache != null) return;
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Race>();
            if (sheet == null) { _raceCache = new[] { ((byte)0, "Any") }; return; }

            var list = new System.Collections.Generic.List<(byte id, string name)>();
            list.Add((0, "Any"));
            foreach (var row in sheet)
            {
                string name = row.Masculine.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(((byte)row.RowId, name));
            }
            _raceCache = list.ToArray();
        }
        catch { _raceCache = new[] { ((byte)0, "Any") }; }
    }

    private static string GetRaceName(byte raceId)
    {
        if (raceId == 0) return "Any";
        EnsureRaceCache();
        foreach (var (id, name) in _raceCache)
            if (id == raceId) return name;
        return $"Race #{raceId}";
    }

    private static void EnsureJobCache()
    {
        if (_jobCache != null) return;
        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
            if (sheet == null) { _jobCache = Array.Empty<(uint, string)>(); return; }

            var list = new System.Collections.Generic.List<(uint id, string name)>();
            foreach (var row in sheet)
            {
                if (!AnimationSwap.JobWeaponFolder.ContainsKey(row.RowId)) continue;
                string name = row.Abbreviation.ExtractText();
                string full = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;
                string display = !string.IsNullOrEmpty(full)
                    ? $"{name} ({full})"
                    : name;
                list.Add((row.RowId, display));
            }
            _jobCache = list.ToArray();
        }
        catch { _jobCache = Array.Empty<(uint, string)>(); }
    }

    private static string GetJobName(uint jobId)
    {
        if (jobId == 0) return "Any";
        EnsureJobCache();
        foreach (var (id, name) in _jobCache)
            if (id == jobId) return name;
        return $"Job #{jobId}";
    }

    // ── Custom Teleport Menu ──────────────────────────────────────
    // Slick floating panel with rounded corners, border, and fade-in
    // animation matching the MSQ pill style. Uses raw draw-list for the
    // frame and styled ImGui widgets for interactive content inside.

    private static string _teleportSearch = "";
    private static float  _tpFadeT;               // 0→1 fade-in
    private static bool   _tpWasOpen;              // edge-detect open→close
    private static bool   _tpFocusSearch;          // auto-focus search on open

    // Keyboard navigation state.
    private static int  _tpNavIdx = -1;            // -1 = no selection (search focused)
    private static bool _tpNavActive;              // true once user presses ↓ (search defocused)
    private static int  _tpNavCount;               // total navigable items last frame
    private static int  _tpDrawNavI;               // per-frame counter incremented during draw
    private static bool _tpNavScrollTo;            // scroll the selected item into view
    private static TeleportMenu.TeleportEntry _tpNavSelectedEntry; // captured during draw for confirm
    private static bool _tpKeyDownPrev, _tpKeyUpPrev, _tpKeyFPrev, _tpKeyEnterPrev; // edge-detect for raw keys
    private static string _tpLastSearch = "";       // detect typing → cancel nav

    // Layout constants.
    private const float TP_WIDTH     = 420f;
    private const float TP_HEIGHT    = 580f;
    private const float TP_ROUNDING  = 12f;
    private const float TP_PAD       = 14f;
    private const float TP_BORDER    = 1.5f;
    private const float TP_FADE_RATE = 8f;         // exp-lerp 1/s

    public static void DrawTeleportMenu()
    {
        if (!noWickyXIV.Config.EnableCustomTeleportMenu) return;

        bool wantOpen = TeleportMenu.IsWindowOpen;

        // ── Fade animation ────────────────────────────────────
        var io = ImGui.GetIO();
        float dt = io.DeltaTime;
        float target = wantOpen ? 1f : 0f;
        float k = 1f - MathF.Exp(-TP_FADE_RATE * dt);
        _tpFadeT += (target - _tpFadeT) * k;

        // Snap thresholds.
        if (_tpFadeT < 0.01f && !wantOpen) _tpFadeT = 0f;
        if (_tpFadeT > 0.99f && wantOpen)  _tpFadeT = 1f;

        // Edge: just opened → reset search, request focus, reset nav.
        if (wantOpen && !_tpWasOpen)
        {
            _teleportSearch = "";
            _tpFocusSearch = true;
            _tpNavIdx = -1;
            _tpNavActive = false;
        }
        _tpWasOpen = wantOpen;

        // Nothing to draw while fully hidden.
        if (_tpFadeT <= 0f) return;

        float alpha = _tpFadeT;
        float scale = ImGuiHelpers.GlobalScale;
        float pw = TP_WIDTH * scale;
        float ph = TP_HEIGHT * scale;
        float pad = TP_PAD * scale;
        float rounding = TP_ROUNDING * scale;

        // Use saved position if available, otherwise center on screen.
        var disp = io.DisplaySize;
        var cfg = noWickyXIV.Config;
        float posX = cfg.TeleportMenuX >= 0 ? cfg.TeleportMenuX : (disp.X - pw) * 0.5f;
        float posY = cfg.TeleportMenuY >= 0 ? cfg.TeleportMenuY : (disp.Y - ph) * 0.5f;

        // Slight slide-up on open (pill-style reveal).
        float slideOffset = (1f - alpha) * 18f * scale;

        // During fade-in, force position (slide animation from saved pos).
        // Once fully open, let the user drag freely.
        bool fadingIn = wantOpen && _tpFadeT < 1f;
        if (fadingIn)
            ImGui.SetNextWindowPos(new Vector2(posX, posY + slideOffset));

        // ── ImGui window (transparent, no decoration) ─────────
        ImGui.SetNextWindowSize(new Vector2(pw, ph), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, rounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u); // transparent — we draw our own bg

        var flags = ImGuiWindowFlags.NoDecoration
                  | ImGuiWindowFlags.NoNav
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.Begin("##noWickyTeleport", flags);

        // Get actual window position (respects user dragging).
        var wp = ImGui.GetWindowPos();
        posX = wp.X;
        posY = wp.Y;

        // Persist position when user drags (only when fully open, not during fade).
        if (!fadingIn && (cfg.TeleportMenuX != posX || cfg.TeleportMenuY != posY))
        {
            cfg.TeleportMenuX = posX;
            cfg.TeleportMenuY = posY;
            cfg.SaveDebounced();
        }

        // ── Draw background + border via draw list ────────────
        var dl = ImGui.GetWindowDrawList();
        var min = new Vector2(posX, posY);
        var max = new Vector2(posX + pw, posY + ph);

        // Background.
        uint bgCol = TpPackRgba(0.06f, 0.06f, 0.10f, 0.94f * alpha);
        dl.AddRectFilled(min, max, bgCol, rounding);

        // Border.
        uint borderCol = TpPackRgba(0.75f, 0.60f, 0.20f, 0.65f * alpha);
        dl.AddRect(min, max, borderCol, rounding, ImDrawFlags.None, TP_BORDER * scale);

        // ── Title bar (centered) ──────────────────────────────
        {
            var titleText = "Teleport";
            var titleW = ImGui.CalcTextSize(titleText).X;
            ImGui.SetCursorPosX((pw - titleW) * 0.5f);
            ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(1f, 0.85f, 0.35f, alpha));
            ImGui.TextUnformatted(titleText);
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();

        // Thin separator line under title.
        {
            float sepY = ImGui.GetCursorScreenPos().Y;
            uint sepCol = TpPackRgba(1f, 1f, 1f, 0.08f * alpha);
            dl.AddLine(
                new Vector2(posX + pad, sepY),
                new Vector2(posX + pw - pad, sepY),
                sepCol, 1f * scale);
            ImGui.Dummy(new Vector2(0, 4f * scale));
        }

        // ── Search box ────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.FrameBg, TpPackRgba(0.12f, 0.12f, 0.18f, 0.9f * alpha));
        ImGui.PushStyleColor(ImGuiCol.Border, TpPackRgba(0.5f, 0.4f, 0.15f, 0.4f * alpha));
        ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(1f, 1f, 1f, alpha));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f * scale, 6f * scale));
        ImGui.SetNextItemWidth(pw - pad * 2f);

        if (_tpFocusSearch && !_tpNavActive)
        {
            ImGui.SetKeyboardFocusHere();
            _tpFocusSearch = false;
        }
        ImGui.InputTextWithHint("##TpSearch", "Search aetherytes...", ref _teleportSearch, 128);
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);

        var filter = _teleportSearch.Trim();

        // ── Keyboard navigation (↑↓ = move, Shift+F = confirm) ──
        // Uses raw Win32 GetAsyncKeyState with edge-detect because
        // ImGui.IsKeyPressed is eaten by the active InputText widget.
        _tpDrawNavI = 0;
        _tpNavSelectedEntry = null;

        bool downNow = VkDown(0x28); // VK_DOWN
        bool upNow   = VkDown(0x26); // VK_UP
        bool fNow    = VkDown(0x46); // VK_F
        bool enterNow = VkDown(0x0D); // VK_RETURN
        bool downEdge = downNow && !_tpKeyDownPrev;
        bool upEdge   = upNow   && !_tpKeyUpPrev;
        bool fEdge    = fNow    && !_tpKeyFPrev;
        bool enterEdge = enterNow && !_tpKeyEnterPrev;
        _tpKeyDownPrev = downNow;
        _tpKeyUpPrev   = upNow;
        _tpKeyFPrev    = fNow;
        _tpKeyEnterPrev = enterNow;

        if (wantOpen)
        {
            if (downEdge)
            {
                _tpNavIdx = Math.Min(_tpNavIdx + 1, Math.Max(_tpNavCount - 1, 0));
                _tpNavActive = true;
                _tpNavScrollTo = true;
            }
            if (upEdge)
            {
                if (_tpNavIdx > 0)
                {
                    _tpNavIdx--;
                    _tpNavScrollTo = true;
                }
                else if (_tpNavIdx == 0)
                {
                    // Up from first item → back to search box.
                    _tpNavIdx = -1;
                    _tpNavActive = false;
                    _tpFocusSearch = true;
                }
            }

            // Typing in search cancels nav mode (user went back to filtering).
            if (_tpNavActive && _teleportSearch != _tpLastSearch)
            {
                _tpNavActive = false;
                _tpNavIdx = -1;
                _tpFocusSearch = true;
            }
            _tpLastSearch = _teleportSearch;
        }

        ImGui.Dummy(new Vector2(0, 6f * scale));

        // ── Scrollable body ───────────────────────────────────
        float remainH = (posY + ph - pad) - ImGui.GetCursorScreenPos().Y;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, TpPackRgba(0.06f, 0.06f, 0.10f, 0.4f * alpha));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, TpPackRgba(0.6f, 0.5f, 0.2f, 0.4f * alpha));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, TpPackRgba(0.8f, 0.65f, 0.25f, 0.6f * alpha));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, TpPackRgba(0.95f, 0.75f, 0.3f, 0.8f * alpha));

        if (ImGui.BeginChild("##TpBody", new Vector2(pw - pad * 2f, remainH), false))
        {
            // Style for rows.
            ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(1f, 1f, 1f, alpha));
            ImGui.PushStyleColor(ImGuiCol.Header, TpPackRgba(0.75f, 0.6f, 0.2f, 0.15f * alpha));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, TpPackRgba(0.75f, 0.6f, 0.2f, 0.30f * alpha));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, TpPackRgba(0.75f, 0.6f, 0.2f, 0.45f * alpha));

            // ── FC House shortcut ─────────────────────────────
            if (string.IsNullOrEmpty(filter) || MatchesFilter("Free Company Estate Hall", filter))
            {
                bool hasSaved = noWickyXIV.Config.FcHouseAetheryteId != 0;
                bool capturing = TeleportMenu.CapturingFcHouse;
                string fcLabel = capturing
                    ? "Waiting for teleport...##fchouse"
                    : hasSaved
                        ? "FC House##fchouse"
                        : "FC House (not set)##fchouse";

                // FC button takes most of the width; "Set" button on the right.
                float setW = ImGui.CalcTextSize("Set").X + 16f * scale;
                float fcW = ImGui.GetContentRegionAvail().X - setW - 6f * scale;

                bool fcNav = _tpDrawNavI == _tpNavIdx;
                var fcBtnCol = fcNav
                    ? TpPackRgba(0.75f, 0.55f, 0.22f, 0.95f * alpha)
                    : TpPackRgba(0.55f, 0.35f, 0.12f, 0.85f * alpha);
                ImGui.PushStyleColor(ImGuiCol.Button, fcBtnCol);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TpPackRgba(0.70f, 0.45f, 0.18f, 0.90f * alpha));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, TpPackRgba(0.85f, 0.55f, 0.22f, 0.95f * alpha));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
                if (ImGui.Button(fcLabel, new Vector2(fcW, 30 * scale)))
                    TeleportMenu.TeleportToFcHouse();
                if (fcNav && _tpNavScrollTo) { ImGui.SetScrollHereY(); _tpNavScrollTo = false; }
                _tpDrawNavI++;
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);

                ImGui.SameLine();

                // "Set" button — starts capture mode.
                ImGui.PushStyleColor(ImGuiCol.Button, TpPackRgba(0.25f, 0.25f, 0.35f, 0.8f * alpha));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TpPackRgba(0.35f, 0.35f, 0.50f, 0.9f * alpha));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, TpPackRgba(0.45f, 0.45f, 0.60f, 0.95f * alpha));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
                if (ImGui.Button(capturing ? "...##fcSet" : "Set##fcSet", new Vector2(setW, 30 * scale)))
                {
                    if (!capturing)
                        TeleportMenu.StartFcCapture();
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);

                ImGui.Dummy(new Vector2(0, 2f * scale));
            }

            // ── Housing entries (personal, apartment, FC) ─────
            var housing = TeleportMenu.GetHousingEntries();
            if (housing.Count > 0)
            {
                foreach (var h in housing)
                {
                    if (!string.IsNullOrEmpty(filter) && !MatchesFilter(h.AetheryteName, filter) && !MatchesFilter(h.AreaName, filter))
                        continue;
                    DrawTeleportRow(h, alpha, scale);
                }
            }

            // ── Recently visited ──────────────────────────────
            if (string.IsNullOrEmpty(filter))
            {
                var recents = TeleportMenu.GetRecentTeleports();
                if (recents.Count > 0)
                {
                    ImGui.Dummy(new Vector2(0, 2f * scale));
                    DrawTpSectionSep(dl, alpha, scale, "Recently Visited");
                    foreach (var r in recents)
                        DrawTeleportRow(r, alpha, scale);
                }
            }

            // ── Favorites ─────────────────────────────────────
            if (string.IsNullOrEmpty(filter))
            {
                var all = TeleportMenu.GetList();
                var favs = all?.Where(e => e.IsFavorite && !e.IsFcHouse && !e.IsPersonalHouse && !e.IsApartment).ToList();
                if (favs != null && favs.Count > 0)
                {
                    ImGui.Dummy(new Vector2(0, 2f * scale));
                    DrawTpSectionSep(dl, alpha, scale, "Favorites");
                    foreach (var f in favs)
                        DrawTeleportRow(f, alpha, scale);
                }
            }

            // ── Full list grouped by region ───────────────────
            var grouped = TeleportMenu.GetGroupedList();
            if (grouped != null)
            {
                ImGui.Dummy(new Vector2(0, 2f * scale));
                DrawTpSectionSep(dl, alpha, scale, "All Aetherytes");

                foreach (var (region, entries) in grouped)
                {
                    var visible = string.IsNullOrEmpty(filter)
                        ? entries
                        : entries.Where(e => MatchesFilter(e.AetheryteName, filter) || MatchesFilter(e.AreaName, filter)).ToList();

                    if (visible.Count == 0) continue;

                    // Default open on first appearance; user can collapse.
                    // Search forces all open so matches are visible.
                    // Keyboard nav forces open when the selection is inside this group.
                    bool searching = !string.IsNullOrEmpty(filter);
                    bool navInGroup = _tpNavActive && _tpNavIdx >= _tpDrawNavI && _tpNavIdx < _tpDrawNavI + visible.Count;
                    if (searching || navInGroup)
                        ImGui.SetNextItemOpen(true);
                    else
                        ImGui.SetNextItemOpen(true, (ImGuiCond)8); // Appearing

                    ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(0.95f, 0.78f, 0.30f, alpha));
                    bool open = ImGui.CollapsingHeader(region);
                    ImGui.PopStyleColor();

                    if (open)
                    {
                        foreach (var entry in visible)
                            DrawTeleportRow(entry, alpha, scale);
                    }
                    else
                    {
                        // Keep nav indices stable even when collapsed.
                        _tpDrawNavI += visible.Count;
                    }
                }
            }

            ImGui.PopStyleColor(4); // row styles
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(5); // scrollbar + child bg

        // Finalize nav count for next frame's clamping.
        _tpNavCount = _tpDrawNavI;

        // Shift+F or Enter confirms the keyboard-selected entry.
        bool confirm = _tpNavActive && _tpNavIdx >= 0
            && ((VkDown(0x10) && fEdge) || enterEdge);
        if (confirm)
        {
            if (_tpNavSelectedEntry != null)
                TeleportMenu.DoTeleport(_tpNavSelectedEntry);
            else if (_tpNavIdx == 0 && noWickyXIV.Config.FcHouseAetheryteId != 0)
                TeleportMenu.TeleportToFcHouse();
        }

        ImGui.End();
        ImGui.PopStyleColor(); // WindowBg
        ImGui.PopStyleVar(4);  // WindowPadding, BorderSize, Rounding, MinSize

        // ── Close on Escape ───────────────────────────────────
        if (wantOpen && ImGui.IsKeyPressed(ImGuiKey.Escape))
            TeleportMenu.OnWindowClosed();
    }

    private static void DrawTeleportRow(TeleportMenu.TeleportEntry entry, float alpha, float scale)
    {
        bool navSel = _tpDrawNavI == _tpNavIdx;

        ImGui.PushID((int)(entry.AetheryteId * 256 + entry.SubIndex));

        float availW = ImGui.GetContentRegionAvail().X;
        string costText = $"{entry.GilCost} gil";
        float costW = ImGui.CalcTextSize(costText).X;

        // Bright highlight for keyboard-selected row.
        if (navSel)
            ImGui.PushStyleColor(ImGuiCol.Header, TpPackRgba(0.85f, 0.70f, 0.25f, 0.55f * alpha));

        if (ImGui.Selectable($"  {entry.AetheryteName}##tp", navSel))
            TeleportMenu.DoTeleport(entry);

        if (navSel)
            ImGui.PopStyleColor();

        if (navSel)
        {
            _tpNavSelectedEntry = entry;
            if (_tpNavScrollTo) { ImGui.SetScrollHereY(); _tpNavScrollTo = false; }
        }

        // Right-aligned cost on the same line.
        ImGui.SameLine(availW - costW);
        ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(0.7f, 0.7f, 0.6f, 0.7f * alpha));
        ImGui.TextUnformatted(costText);
        ImGui.PopStyleColor();

        ImGui.PopID();
        _tpDrawNavI++;
    }

    /// <summary>Draws a labeled thin line separator for a section.</summary>
    private static void DrawTpSectionSep(ImDrawListPtr dl, float alpha, float scale, string label)
    {
        float y = ImGui.GetCursorScreenPos().Y + 2f * scale;
        float x0 = ImGui.GetCursorScreenPos().X;
        float x1 = x0 + ImGui.GetContentRegionAvail().X;
        uint lineCol = TpPackRgba(1f, 1f, 1f, 0.06f * alpha);
        dl.AddLine(new Vector2(x0, y), new Vector2(x1, y), lineCol, 1f * scale);
        ImGui.Dummy(new Vector2(0, 6f * scale));
        ImGui.PushStyleColor(ImGuiCol.Text, TpPackRgba(0.8f, 0.7f, 0.35f, 0.6f * alpha));
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2f * scale));
    }

    private static bool MatchesFilter(string text, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return text != null && text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static uint TpPackRgba(float r, float g, float b, float a)
    {
        byte br = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, r)) * 255f);
        byte bg = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, g)) * 255f);
        byte bb = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, b)) * 255f);
        byte ba = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, a)) * 255f);
        return ((uint)ba << 24) | ((uint)bb << 16) | ((uint)bg << 8) | br;
    }
}