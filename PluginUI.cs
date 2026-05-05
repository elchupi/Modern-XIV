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

            if (ImGui.BeginTabItem("Misc"))
            {
                if (ImGui.BeginChild("##misc_scroll"))
                    DrawMiscTab();
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
        p.Name = $"Preset {noWickyXIV.Config.Presets.Count + 1}";
        return p;
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
            noWickyXIV.Config.Presets.Add(CaptureCurrentCameraPreset());
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

        // Smooth transition slider — applies to ANY preset activation
        // (manual click, auto condition-set swap, or login restore).
        // 0.05s ≈ instant snap; 5s default = slow cinematic ease-in.
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ConfigSliderFloat("Transition seconds##PresetTransition",
            ref noWickyXIV.Config.PresetTransitionSeconds, 0.05f, 15f, 5f, "%.2fs");
        ImGui.TextDisabled("How long the camera takes to ease into a newly-applied preset's zoom / FoV / tilt / look-at-height.");

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
        if (save) noWickyXIV.Config.Save();
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

    private static unsafe void DrawMiscTab()
    {
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
            ImGuiEx.EndGroupBox();
        }

        // Hotbar Fader (cascade fade-in/out on weapon-drawn).
        if (ImGuiEx.BeginGroupBox("Hotbar Fader (weapon-drawn cascade)"))
        {
            ConfigCheckbox("Enable##HotbarFader", ref noWickyXIV.Config.EnableHotbarFader);
            ImGui.TextDisabled(
                "Fades hotbars 1, 7, 10 in cascade order on weapon draw,\n" +
                "and reverse-cascade fades them out on sheath.\n" +
                "Cascade delay = gap between each bar starting (default 0.95s).");
            ConfigSliderFloat("Cascade delay (s)##HotbarFader", ref noWickyXIV.Config.HotbarFaderCascadeDelay, 0f,   3f,   0.95f, "%.2f");
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
            ImGuiEx.EndGroupBox();
        }

        // The original "Other Settings" content (collision toggle, death cam,
        // free cam, spectate, tilt) follows. Renders as-is.
        DrawOtherSettings();
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