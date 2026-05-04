using System;
using System.Linq;

namespace noWickyXIV;

public static class PresetManager
{
    public static CameraConfigPreset CurrentPreset
    {
        get => PresetOverride ?? ActivePreset ?? DefaultPreset;
        set
        {
            ApplyPreset(PresetOverride = value);
            if (value == null)
                ActivePreset = null;
            try
            {
                noWickyXIV.Config.LastActivePresetName = value?.Name ?? "";
                noWickyXIV.Config.Save();
            }
            catch { /* defensive */ }
        }
    }

    public static void RestoreLastActivePreset()
    {
        try
        {
            var name = noWickyXIV.Config.LastActivePresetName;
            if (string.IsNullOrEmpty(name)) return;
            var preset = noWickyXIV.Config.Presets.FirstOrDefault(p => p.Name == name);
            if (preset == null) return;
            ApplyPreset(PresetOverride = preset, isLoggingIn: true);
        }
        catch { /* defensive */ }
    }

    public static CameraConfigPreset DefaultPreset { get; set; } = new();
    public static CameraConfigPreset ActivePreset { get; private set; }
    public static CameraConfigPreset PresetOverride { get; private set; }

    // ---- Effective values ----
    // Game.cs detours read these instead of preset.X directly so the
    // detours have a single source of truth. Today they always equal
    // the active preset's values — there's no per-frame transition. The
    // earlier 5-second smooth transition was removed: the engine reads
    // EffectiveHeightOffset / EffectiveSideOffset every frame to position
    // the camera, and lerping them played against alt+scroll-wheel zoom
    // and yaw input, producing a "pans back / sluggish" feel for the
    // entire transition window.
    public static float EffectiveTilt              { get; private set; }
    public static float EffectiveLookAtHeightOffset{ get; private set; }
    public static float EffectiveHeightOffset      { get; private set; }
    public static float EffectiveSideOffset        { get; private set; }

    public static unsafe void ApplyPreset(CameraConfigPreset preset, bool isLoggingIn = false)
    {
        if (preset == null) return;

        var camera = Common.CameraManager->worldCamera;
        if (camera == null) return;

        // ---- Bounds + invariants snap immediately ----
        camera->minZoom = preset.MinZoom;
        camera->maxZoom = preset.MaxZoom;
        camera->minFoV  = preset.MinFoV;
        camera->maxFoV  = preset.MaxFoV;
        Game.FoVDelta   = preset.FoVDelta;
        camera->minVRotation = preset.MinVRotation;
        camera->maxVRotation = preset.MaxVRotation;

        // ---- Camera-struct fields snap immediately ----
        float targetZoom = preset.StartZoom > 0f
            ? Math.Min(Math.Max(preset.StartZoom, preset.MinZoom), preset.MaxZoom)
            : Math.Min(Math.Max(camera->currentZoom, preset.MinZoom), preset.MaxZoom);

        float targetFoV  = preset.StartFoV  > 0f
            ? Math.Min(Math.Max(preset.StartFoV,  preset.MinFoV), preset.MaxFoV)
            : Math.Min(Math.Max(camera->currentFoV, preset.MinFoV), preset.MaxFoV);

        camera->currentZoom        = targetZoom;
        camera->currentFoV         = targetFoV;
        camera->tilt               = preset.Tilt;
        camera->lookAtHeightOffset = preset.LookAtHeightOffset;

        // ---- Effective offsets snap immediately ----
        EffectiveTilt               = preset.Tilt;
        EffectiveLookAtHeightOffset = preset.LookAtHeightOffset;
        EffectiveHeightOffset       = preset.HeightOffset;
        EffectiveSideOffset         = preset.SideOffset;

        // Snap CameraDynamics's smoothed offset copies so a preset
        // switch doesn't get caught riding a lerp out of the previous
        // preset's offsets when EnableCameraPositionSmoothing is on.
        try { CameraDynamics.SnapOffsets(); } catch { /* defensive */ }
    }

    public static void CheckCameraConditionSets(bool isLoggingIn)
    {
        var preset = noWickyXIV.Config.Presets.FirstOrDefault(preset => preset.CheckConditionSet());
        if (preset == null || preset == ActivePreset) return;

        ApplyPreset(preset, isLoggingIn);
        ActivePreset = preset;
    }

    public static unsafe void Update()
    {
        // No transition state machine — all four Effective* values pass
        // through the active preset every frame so live edits in the
        // editor still take effect immediately.
        var preset = CurrentPreset;
        if (preset != null)
        {
            EffectiveTilt               = preset.Tilt;
            EffectiveLookAtHeightOffset = preset.LookAtHeightOffset;
            EffectiveHeightOffset       = preset.HeightOffset;
            EffectiveSideOffset         = preset.SideOffset;
        }

        // Auto-swap by QoL Bar condition (only when no manual override).
        if (!DalamudApi.ClientState.IsLoggedIn || FreeCam.Enabled || PresetOverride != null) return;
        CheckCameraConditionSets(false);
    }

    public static void DisableCameraPresets()
    {
        ActivePreset = null;
        PresetOverride = null;
    }
}
