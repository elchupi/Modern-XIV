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

    // PitchTilt: was defaulted to true with Wicked's PitchTiltMaxOffset=1.24,
    // but FFXIV's pitch convention is INVERTED from Unity (positive
    // currentVRotation = looking up; Wicked treats negative as up). At default
    // forward gaze that math produces a constant ~0.6m upward look-at offset
    // = "camera always looks up" bug. Default off until the formula in
    // CameraDynamics.UpdatePitchTilt is reworked for FFXIV's sign convention.
    public bool  EnablePitchTilt     = false;
    public float PitchTiltMaxOffset  = 0.4f;   // smaller default for FFXIV's tighter pitch range
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

    // ---- Hotkeys (Phase B) ----
    // VirtualKey int values; 0 = unbound. Default F6 matches Wicked's KeyMenu.
    public int   SettingsHotkey      = 0x75; // VirtualKey.F6
    public int   ShoulderSwapHotkey  = 0;    // unbound; user assigns
    public int   CrosshairHotkey     = 0x56; // VirtualKey.V
    // Preset slot hotkeys (Ctrl+1..9). Indexed 0..8 for slots 1..9.
    public bool      PresetHotkeysEnabled = false;
    public List<int> PresetHotkeys = new() { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

    // ---- ADS zoom on RMB (Phase B) ----
    public bool  EnableAdsOnRmb      = false;
    public float AdsZoomFactor       = 1.5f;
    public float AdsTransitionSpeed  = 8f;

    // ---- Crosshair (Phase D — fields added early so V hotkey toggles a real value) ----
    public bool  EnableCrosshair     = false;
    public float CrosshairSize       = 8f;        // half-arm length, px (scaled)
    public float CrosshairThickness  = 2f;
    public float CrosshairFadeSpeed  = 6f;
    // RGBA 0..1
    public float CrosshairColorR     = 1f;
    public float CrosshairColorG     = 1f;
    public float CrosshairColorB     = 1f;
    public float CrosshairColorA     = 0.85f;

    // ---- Sensitivity (Phase E — fields added early so panel can show them) ----
    public float MouseSensitivityMul   = 1f;
    public float GamepadSensitivityMul = 1f;
    public bool  InvertMouseY          = false;
    public bool  InvertGamepadY        = false;

    // ---- Always-on mouselook (Phase F: FPS-style camera lock) ----
    // When enabled, the mouse drives camera rotation continuously (as if RMB
    // were always held). The "cursor toggle" hotkey (default F7) temporarily
    // releases the cursor for UI interaction; press again to re-grab.
    public bool  EnableMouseLookAlways    = false;
    public float MouseLookSensitivity     = 0.005f;  // radians per pixel of delta
    public bool  MouseLookInvertX         = false;   // mouse-right → camera-left when true
    public bool  MouseLookInvertY         = false;
    public bool  MouseLookCenterCursor    = true;    // re-center each frame so cursor never reaches screen edge
    public int   CursorReleaseHotkey      = 0x76;    // VirtualKey.F7

    // ---- Auto-shoulder swap (Phase C) ----
    // STATE: state machine + UI toggle implemented; the raycast probe itself
    // is a TODO pending verification of the BGCollisionModule API shape in
    // the active FFXIVClientStructs version. Manual shoulder swap hotkey
    // (Phase B) covers the immediate use case in the meantime.
    public bool  EnableAutoShoulderSwap   = false;
    public float ShoulderLerpDuration     = 0.35f;
    public float ShoulderSwapSafetyMargin = 0.4f;  // metres of clearance to keep
    public float ShoulderSwapCheckHz      = 5f;    // probe frequency

    // ---- SwivelOnMove implementation knobs (Phase D) ----
    // Wicked has SwivelOnMove/Delay/Speed already; we add a movement-magnitude
    // threshold below which we don't auto-center, to avoid jitter when standing
    // still or in cutscenes.
    public float SwivelMoveThreshold = 0.05f; // m/s
}