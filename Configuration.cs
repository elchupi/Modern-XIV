using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dalamud.Configuration;

namespace noWickyXIV;

public class CameraConfigPreset
{
    public enum ViewBobSetting
    {
        Disabled,
        [Display(Name = "First Person")] FirstPerson,
        [Display(Name = "Out of Combat")] OutOfCombat,
        Always
    }

    public string Name = "New Preset";

    public bool UseStartOnLogin = false;

    public bool UseStartZoom = false;
    public float StartZoom = 6;
    public float MinZoom = 1.5f;
    public float MaxZoom = 20;
    public float ZoomDelta = 0.75f;

    public bool UseStartFoV = false;
    public float StartFoV = 0.78f;
    public float MinFoV = 0.69f;
    public float MaxFoV = 0.78f;
    public float FoVDelta = 0.08726646751f;

    public float MinVRotation = -1.483530f;
    public float MaxVRotation = 0.785398f;

    public float HeightOffset = 0;
    public float SideOffset = 0;
    public float Tilt = 0;
    public float LookAtHeightOffset = Game.GetDefaultLookAtHeightOffset() ?? 0;
    public ViewBobSetting ViewBobMode = ViewBobSetting.Disabled;
    public int ConditionSet = -1;

    public CameraConfigPreset Clone() => (CameraConfigPreset)MemberwiseClone();

    public bool CheckConditionSet() => ConditionSet < 0 || IPC.QoLBarEnabled && IPC.CheckConditionSet(ConditionSet);

    public void Apply(bool isLoggingIn = false) => PresetManager.ApplyPreset(this, isLoggingIn);
}

public class Configuration : PluginConfiguration, IPluginConfiguration
{
    public enum DeathCamSetting
    {
        Disabled,
        Spectate,
        [Display(Name = "Free Cam")] FreeCam
    }

    public int Version { get; set; }

    public List<CameraConfigPreset> Presets = [];
    public bool EnableCameraNoClippy = false;
    public DeathCamSetting DeathCamMode = DeathCamSetting.Disabled;
    public bool EnableAdvancedFreeCamControls = false;
    public bool FadeOutAdvancedFreeCamControls = false;

    // ---- Cinematic Camera (mirrors WickedTPSConfig.cs naming + defaults) ----
    // Dynamic-feel layer composed on top of the active preset. Independent
    // of Cammy's preset system — these are global because they describe HOW
    // the camera reacts to motion, not WHAT it frames. Same toggling story
    // as Wicked: each effect has an Enable* gate and rate/magnitude knobs.
    //
    // YawLag: known broken in the Wicked impl (whiplashes, no soft landing).
    // Default OFF here too. Re-design as critically-damped spring on
    // yaw-rate-driven offset (NOT exp-decay on absolute yaw) before turning on.
    public bool  EnableYawLag        = false;
    public float YawLagHalflife      = 0.8f;

    public bool  EnableRollTilt      = true;
    public float RollTiltMaxAngle    = 1.92f;     // degrees
    public float RollTiltSensitivity = 0.2f;
    public float RollTiltOnRate      = 2.47f;
    public float RollTiltOffRate     = 1.0f;

    public bool  EnablePitchTilt     = true;
    public float PitchTiltMaxOffset  = 1.24f;
    public float PitchTiltSmoothRate = 3.19f;

    // SwivelOnMove: auto-center the camera behind the player after a
    // short delay when movement starts. Off by default in Wicked too.
    public bool  SwivelOnMove        = false;
    public float SwivelDelay         = 0.15f;
    public float SwivelSpeed         = 240f;

    // Position float behind the player (the "discreet float" feel —
    // soft smoothing on follow that's not 1:1, not zero).
    public bool  EnablePositionFloat = true;
    public float PositionFloatLagFactor = 0.15f;
    public float PositionFloatSmoothTime = 0.18f;

    // InstantMode: zero ALL the smoothing (vertical lag, lock-on blend,
    // collision smooth, etc). Wicked uses it as an emergency "remove all
    // softness" toggle. Doesn't affect the dynamic-feel knobs above.
    public bool  InstantMode         = false;

    // Ctrl+scroll height nudge step (live, in-game).
    public float HeightOffsetStep    = 0.1f;

    // Live-tweakable global height offset (Ctrl/Alt + scroll). Stacks on top
    // of the preset's HeightOffset so scrolling persists across preset switches
    // and across sessions. Range matches Wicked's clamp (-2..4).
    public float GlobalHeightOffset  = 0f;
}