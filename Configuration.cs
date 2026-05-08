using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dalamud.Configuration;

namespace noWickyXIV;

// One Govee device target for LightSync. Auto-populated from
// /lightsync devices responses; user can tick which ones participate
// in event broadcasts. Non-SyncBox lights (WiFi strips, ambient
// lights) are the typical use case — they just change color on
// events with no Video-Sync restore needed.
public class LightSyncDevice
{
    public bool   Enabled  = false;
    public string Sku      = "";
    public string DeviceId = "";
    public string Name     = "";
    // LAN-API discovery — populated by /lightsync lanscan when the
    // device responds to UDP multicast on 239.255.255.250:4001.
    // Empty = LAN not detected (device might not support LAN, or
    // the "LAN Control" toggle in Govee Home is off, or the device
    // is on a different network segment).
    public string LanIp    = "";
    // When true and LanIp is set, the per-event color POST routes
    // through UDP directly (sub-30ms latency). When false or LanIp
    // is empty, falls back to Cloud REST (~300-500ms).
    public bool   UseLan   = true;

    // Segment-aware control for lights with multiple addressable
    // segments (e.g. H6056 light bars, controllers driving multi-bar
    // setups). When SegmentCount > 0, footstep alternation uses the
    // Govee Cloud `segmentedBrightness` capability to drive the
    // right/left halves separately. When 0, the device is treated
    // as a single endpoint and footstep alternation is disabled
    // (the whole device pulses uniformly).
    //
    // Splitting convention: segments [0..SegmentCount/2-1] = RIGHT,
    // segments [SegmentCount/2..SegmentCount-1] = LEFT. User can
    // swap the two halves by setting SwapSegmentSides=true if the
    // physical bars are reversed.
    public int    SegmentCount     = 0;
    public bool   SwapSegmentSides = false;
}

// Per-slot custom file path for a specific mount's audio pack.
// User-defined overrides take priority over the convention-based
// lookup (assets/mount-audio/<mountId>/<slot>.wav). Empty FilePath
// = no override → fall back to convention.
public class MountAudioSlotOverride
{
    public int    MountId  = 0;
    // Slot name — must match one of the layer base names the
    // 9-slot state machine uses: "mount", "idle", "idle2slow",
    // "slow", "revup", "mid", "top", "decel", "dismount".
    public string Slot     = "idle";
    public string FilePath = "";
}

// Per-slot timing override. DelayMs delays the sound's start
// after its trigger event fires (lets you space out e.g. mount-up
// → idle so the mount one-shot has room). FadeInMs/FadeOutMs
// control envelope ramps (loops crossfade naturally when the
// outgoing one fades out and the incoming one fades in
// simultaneously). All three default to 0/400/400 if no entry
// matches.
public class MountAudioSlotTiming
{
    public int    MountId   = 0;
    public string Slot      = "idle";
    public int    DelayMs   = 0;
    public int    FadeInMs  = 400;
    public int    FadeOutMs = 400;
    // When > 0 AND the slot is a loop, the layer uses a two-
    // instance crossfade-loop (CrossfadeLoopLayer) instead of a
    // single LoopStream. CrossfadeLoopMs controls how long before
    // each iteration's end the next instance starts ramping in;
    // both fade across that window so the seam is inaudible. Set
    // to 0 (default) to use the simpler LoopStream-with-rewind.
    public int    CrossfadeLoopMs = 0;
}

// Built-in game-state conditions a preset can auto-activate on. Lives
// alongside QoL Bar's ConditionSet so users can pick either source —
// the Condition Set dropdown surfaces both.
public enum BuiltinPresetCondition
{
    None,
    InCombat,
    Mounted,
    Passenger,
    [Display(Name = "Talking to NPC")] TalkingToNpc,
    [Display(Name = "While Running")]  WhileRunning,
    // Mounted on own mount AND moving — used for travel-camera
    // presets that should only kick in while actually riding, not
    // while standing still on a parked mount.
    [Display(Name = "Moving on Mount")] MovingMount,
    Sprinting,
}

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
    // Per-preset live height adjustment. Ctrl+scroll writes here so the
    // tuning persists with the preset itself rather than stacking on
    // top of every preset via the legacy global offset. Without this,
    // tuning height down on preset A would put preset B (which has its
    // own negative HeightOffset baseline) far below ground when
    // conditions swapped — the global stack added to whatever the
    // destination already had. Range mirrors GlobalHeightOffset's
    // -2..4 clamp.
    public float LiveHeightOffset = 0;
    public ViewBobSetting ViewBobMode = ViewBobSetting.Disabled;
    public int ConditionSet = -1;
    // Built-in game-state condition that activates this preset. Takes
    // precedence over ConditionSet when non-None — picking one in the
    // Condition Set dropdown sets this and clears ConditionSet so the
    // two triggers don't collide.
    public BuiltinPresetCondition Condition = BuiltinPresetCondition.None;

    // Per-preset Camera-Dynamics + Misc-tab snapshot. Lazy-populated by
    // PresetManager.ApplyPreset on first activation if null (migration
    // path for presets saved before this field existed). On preset
    // switch, the OUTGOING preset's Dynamics is rewritten from the live
    // Configuration values, then the INCOMING preset's Dynamics is
    // applied back into Configuration. The runtime keeps reading
    // Configuration as before — Dynamics is just the per-preset store.
    public PresetDynamicsState Dynamics = null;

    public CameraConfigPreset Clone()
    {
        var c = (CameraConfigPreset)MemberwiseClone();
        // MemberwiseClone is shallow — Dynamics would be shared between
        // the original and the clone, so editing one mutates the other.
        // Deep-copy it explicitly.
        c.Dynamics = Dynamics?.Clone();
        return c;
    }

    public bool CheckConditionSet()
    {
        // Built-in condition wins if set.
        if (Condition != BuiltinPresetCondition.None)
            return EvaluateBuiltinCondition(Condition);
        // QoL Bar set, or unconditional ("None").
        return ConditionSet < 0 || IPC.QoLBarEnabled && IPC.CheckConditionSet(ConditionSet);
    }

    // Sprint detection — checks the local player's status list for
    // Sprint (status ID 50). Used by both the BuiltinPresetCondition
    // dispatch and LightSync's foot-movement state machine.
    public static bool HasSprintStatus()
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return false;
            foreach (var s in lp.StatusList)
            {
                if (s != null && s.StatusId == 50) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool EvaluateBuiltinCondition(BuiltinPresetCondition c)
    {
        try
        {
            var cond = DalamudApi.Condition;
            return c switch
            {
                BuiltinPresetCondition.InCombat     => cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat],
                BuiltinPresetCondition.Mounted      => cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]
                                                       && !cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.RidingPillion],
                BuiltinPresetCondition.Passenger    => cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.RidingPillion],
                BuiltinPresetCondition.TalkingToNpc => cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent]
                                                       || cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent],
                // Driven from JobAura's existing motion tracker —
                // exposed via JobAura.IsMoving so we don't duplicate
                // the per-frame position-delta loop.
                BuiltinPresetCondition.WhileRunning => JobAura.IsMoving,
                // Mounted on OWN mount (not passenger) AND moving.
                // JobAura.IsMoving tracks the player's world position
                // delta, which still updates while mounted — so the
                // same tracker works.
                BuiltinPresetCondition.MovingMount  => cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted]
                                                       && !cond[Dalamud.Game.ClientState.Conditions.ConditionFlag.RidingPillion]
                                                       && JobAura.IsMoving,
                // Sprint status (ID 50) — set when the user has the
                // Sprint ability active, or peloton-class group sprint
                // statuses. Just check ID 50 here; broader status set
                // can be added later if needed.
                BuiltinPresetCondition.Sprinting    => HasSprintStatus(),
                _ => false,
            };
        }
        catch { return false; }
    }

    public void Apply(bool isLoggingIn = false) => PresetManager.ApplyPreset(this, isLoggingIn);
}

// Per-preset snapshot of Camera-Dynamics + Misc tab settings. Hotkeys
// stay global (per-preset hotkeys would mean the user wouldn't know
// which key to press in any given context). Chat-overlay style fields
// (positions, colors, font sizes, ChatBubbles) and MountAudio /
// LightSync stay global per the user's scope direction — those are
// system-wide style/system, not camera-context.
//
// Field defaults match Configuration.cs originals so a fresh
// PresetDynamicsState() yields the same initial state as a fresh
// Configuration. Migration on load: if a preset has Dynamics == null
// (saved before this field existed), PresetManager snapshots the
// current Configuration into it on first activation so the user's
// existing tuning becomes the default for that preset.
public class PresetDynamicsState
{
    // ---- Roll Tilt ----
    public bool  EnableRollTilt      = true;
    public float RollTiltMaxAngle    = 1.92f;
    public float RollTiltSensitivity = 0.2f;
    public float RollTiltOnRate      = 2.47f;
    public float RollTiltOffRate     = 1.0f;

    // ---- Character Roll ----
    public bool  EnableCharacterRoll      = false;
    public float CharacterRollMaxAngle    = 4.0f;
    public float CharacterRollSensitivity = 0.25f;
    public float CharacterRollOnRate      = 3.0f;
    public float CharacterRollOffRate     = 1.5f;

    // ---- Pitch Tilt ----
    public bool  EnablePitchTilt     = false;
    public float PitchTiltMaxOffset  = 0.4f;
    public float PitchTiltSmoothRate = 3.19f;

    // ---- Position Float ----
    public bool  EnablePositionFloat     = true;
    public float PositionFloatLagFactor  = 0.15f;
    public float PositionFloatSmoothTime = 0.18f;

    // ---- Yaw Lag ----
    public bool  EnableYawLag    = false;
    public float YawLagHalflife  = 0.8f;

    // ---- Swivel On Move ----
    public bool  SwivelOnMove        = false;
    public float SwivelMoveThreshold = 0.05f;
    public float SwivelDelay         = 0.15f;
    public float SwivelSpeed         = 240f;

    // ---- Auto-shoulder Swap ----
    public bool  EnableAutoShoulderSwap   = false;
    public float ShoulderLerpDuration     = 0.35f;
    public float ShoulderSwapCheckHz      = 5f;
    public float ShoulderSwapSafetyMargin = 0.4f;

    // ---- Crosshair ----
    public bool  EnableCrosshair    = false;
    public float CrosshairSize      = 8f;
    public float CrosshairThickness = 2f;
    public float CrosshairFadeSpeed = 6f;
    public float CrosshairColorR    = 1f;
    public float CrosshairColorG    = 1f;
    public float CrosshairColorB    = 1f;
    public float CrosshairColorA    = 0.85f;

    // ---- Combat Zoom ----
    public bool  EnableCombatZoom         = false;
    public float CombatZoomDistance       = 12f;
    public float CombatZoomTransitionSpeed = 4f;

    // ---- ADS ----
    public bool  EnableAdsOnRmb     = false;
    public float AdsZoomFactor      = 1.5f;
    public float AdsTransitionSpeed = 8f;

    // ---- Input Smoothing ----
    public bool  EnableInputSmoothing       = false;
    public float InputSmoothingZoomRate     = 12f;
    public float InputSmoothingRotateRate   = 22f;
    public bool  EnableCameraPositionSmoothing = false;
    public float CameraPositionSmoothingRate = 12f;

    // ---- Sensitivity ----
    public float MouseSensitivityMul = 1f;
    public bool  InvertMouseY        = false;

    // ---- Mouselook (always-on FPS-style) ----
    public bool  EnableMouseLookAlways = false;
    public float MouseLookSensitivity  = 0.005f;
    public bool  MouseLookCenterCursor = true;
    public bool  MouseLookInvertX      = false;
    public bool  MouseLookInvertY      = false;

    // ---- Close Zoom Pitch Cap ----
    public bool  EnableCloseZoomPitchCap = false;
    public float CloseZoomPitchCapZoom   = 3.0f;
    public float CloseZoomPitchCapMinRad = -0.4f;

    // ---- Misc tab fields (camera-context-relevant only) ----
    public bool  EnableHotbarFader                 = false;
    public float HotbarFaderRate                   = 6.0f;
    public float HotbarFaderCascadeDelay           = 0.95f;
    public float HotbarFaderDrawnAlpha             = 1.0f;
    public float HotbarFaderSheathedAlpha          = 0.0f;
    public bool  HotbarFaderHoverActivates         = true;
    public int   HotbarFaderComboPromptBar         = 0;
    public int   HotbarFaderAvailabilityBar        = 0;
    public float HotbarFaderAvailabilityFlashSeconds = 1.5f;

    public bool  HideTargetArrow                   = false;
    public bool  EnableThirdPersonClickTranslation = false;
    public bool  EnableFovZoomContinuation         = true;
    public float FovZoomMinFov                     = 0.25f;
    public bool  LockCameraDuringNpcDialogue       = true;

    public PresetDynamicsState Clone() => (PresetDynamicsState)MemberwiseClone();

    // Snapshot the live Configuration into a new state object —
    // used when creating a new preset and on the lazy-migration path
    // for old presets that don't have Dynamics yet.
    public static PresetDynamicsState SnapshotFrom(Configuration cfg)
    {
        var s = new PresetDynamicsState();
        ApplyConfigToState(cfg, s);
        return s;
    }

    // Copy live Configuration → state (snapshot in).
    public static void ApplyConfigToState(Configuration cfg, PresetDynamicsState s)
    {
        s.EnableRollTilt      = cfg.EnableRollTilt;
        s.RollTiltMaxAngle    = cfg.RollTiltMaxAngle;
        s.RollTiltSensitivity = cfg.RollTiltSensitivity;
        s.RollTiltOnRate      = cfg.RollTiltOnRate;
        s.RollTiltOffRate     = cfg.RollTiltOffRate;

        s.EnableCharacterRoll      = cfg.EnableCharacterRoll;
        s.CharacterRollMaxAngle    = cfg.CharacterRollMaxAngle;
        s.CharacterRollSensitivity = cfg.CharacterRollSensitivity;
        s.CharacterRollOnRate      = cfg.CharacterRollOnRate;
        s.CharacterRollOffRate     = cfg.CharacterRollOffRate;

        s.EnablePitchTilt     = cfg.EnablePitchTilt;
        s.PitchTiltMaxOffset  = cfg.PitchTiltMaxOffset;
        s.PitchTiltSmoothRate = cfg.PitchTiltSmoothRate;

        s.EnablePositionFloat     = cfg.EnablePositionFloat;
        s.PositionFloatLagFactor  = cfg.PositionFloatLagFactor;
        s.PositionFloatSmoothTime = cfg.PositionFloatSmoothTime;

        s.EnableYawLag    = cfg.EnableYawLag;
        s.YawLagHalflife  = cfg.YawLagHalflife;

        s.SwivelOnMove        = cfg.SwivelOnMove;
        s.SwivelMoveThreshold = cfg.SwivelMoveThreshold;
        s.SwivelDelay         = cfg.SwivelDelay;
        s.SwivelSpeed         = cfg.SwivelSpeed;

        s.EnableAutoShoulderSwap   = cfg.EnableAutoShoulderSwap;
        s.ShoulderLerpDuration     = cfg.ShoulderLerpDuration;
        s.ShoulderSwapCheckHz      = cfg.ShoulderSwapCheckHz;
        s.ShoulderSwapSafetyMargin = cfg.ShoulderSwapSafetyMargin;

        s.EnableCrosshair    = cfg.EnableCrosshair;
        s.CrosshairSize      = cfg.CrosshairSize;
        s.CrosshairThickness = cfg.CrosshairThickness;
        s.CrosshairFadeSpeed = cfg.CrosshairFadeSpeed;
        s.CrosshairColorR    = cfg.CrosshairColorR;
        s.CrosshairColorG    = cfg.CrosshairColorG;
        s.CrosshairColorB    = cfg.CrosshairColorB;
        s.CrosshairColorA    = cfg.CrosshairColorA;

        s.EnableCombatZoom         = cfg.EnableCombatZoom;
        s.CombatZoomDistance       = cfg.CombatZoomDistance;
        s.CombatZoomTransitionSpeed = cfg.CombatZoomTransitionSpeed;

        s.EnableAdsOnRmb     = cfg.EnableAdsOnRmb;
        s.AdsZoomFactor      = cfg.AdsZoomFactor;
        s.AdsTransitionSpeed = cfg.AdsTransitionSpeed;

        s.EnableInputSmoothing          = cfg.EnableInputSmoothing;
        s.InputSmoothingZoomRate        = cfg.InputSmoothingZoomRate;
        s.InputSmoothingRotateRate      = cfg.InputSmoothingRotateRate;
        s.EnableCameraPositionSmoothing = cfg.EnableCameraPositionSmoothing;
        s.CameraPositionSmoothingRate   = cfg.CameraPositionSmoothingRate;

        s.MouseSensitivityMul = cfg.MouseSensitivityMul;
        s.InvertMouseY        = cfg.InvertMouseY;

        s.EnableMouseLookAlways = cfg.EnableMouseLookAlways;
        s.MouseLookSensitivity  = cfg.MouseLookSensitivity;
        s.MouseLookCenterCursor = cfg.MouseLookCenterCursor;
        s.MouseLookInvertX      = cfg.MouseLookInvertX;
        s.MouseLookInvertY      = cfg.MouseLookInvertY;

        s.EnableCloseZoomPitchCap = cfg.EnableCloseZoomPitchCap;
        s.CloseZoomPitchCapZoom   = cfg.CloseZoomPitchCapZoom;
        s.CloseZoomPitchCapMinRad = cfg.CloseZoomPitchCapMinRad;

        s.EnableHotbarFader                 = cfg.EnableHotbarFader;
        s.HotbarFaderRate                   = cfg.HotbarFaderRate;
        s.HotbarFaderCascadeDelay           = cfg.HotbarFaderCascadeDelay;
        s.HotbarFaderDrawnAlpha             = cfg.HotbarFaderDrawnAlpha;
        s.HotbarFaderSheathedAlpha          = cfg.HotbarFaderSheathedAlpha;
        s.HotbarFaderHoverActivates         = cfg.HotbarFaderHoverActivates;
        s.HotbarFaderComboPromptBar         = cfg.HotbarFaderComboPromptBar;
        s.HotbarFaderAvailabilityBar        = cfg.HotbarFaderAvailabilityBar;
        s.HotbarFaderAvailabilityFlashSeconds = cfg.HotbarFaderAvailabilityFlashSeconds;

        s.HideTargetArrow                   = cfg.HideTargetArrow;
        s.EnableThirdPersonClickTranslation = cfg.EnableThirdPersonClickTranslation;
        s.EnableFovZoomContinuation         = cfg.EnableFovZoomContinuation;
        s.FovZoomMinFov                     = cfg.FovZoomMinFov;
        s.LockCameraDuringNpcDialogue       = cfg.LockCameraDuringNpcDialogue;
    }

    // Copy state → live Configuration (apply on preset activation).
    public static void ApplyStateToConfig(PresetDynamicsState s, Configuration cfg)
    {
        cfg.EnableRollTilt      = s.EnableRollTilt;
        cfg.RollTiltMaxAngle    = s.RollTiltMaxAngle;
        cfg.RollTiltSensitivity = s.RollTiltSensitivity;
        cfg.RollTiltOnRate      = s.RollTiltOnRate;
        cfg.RollTiltOffRate     = s.RollTiltOffRate;

        cfg.EnableCharacterRoll      = s.EnableCharacterRoll;
        cfg.CharacterRollMaxAngle    = s.CharacterRollMaxAngle;
        cfg.CharacterRollSensitivity = s.CharacterRollSensitivity;
        cfg.CharacterRollOnRate      = s.CharacterRollOnRate;
        cfg.CharacterRollOffRate     = s.CharacterRollOffRate;

        cfg.EnablePitchTilt     = s.EnablePitchTilt;
        cfg.PitchTiltMaxOffset  = s.PitchTiltMaxOffset;
        cfg.PitchTiltSmoothRate = s.PitchTiltSmoothRate;

        cfg.EnablePositionFloat     = s.EnablePositionFloat;
        cfg.PositionFloatLagFactor  = s.PositionFloatLagFactor;
        cfg.PositionFloatSmoothTime = s.PositionFloatSmoothTime;

        cfg.EnableYawLag    = s.EnableYawLag;
        cfg.YawLagHalflife  = s.YawLagHalflife;

        cfg.SwivelOnMove        = s.SwivelOnMove;
        cfg.SwivelMoveThreshold = s.SwivelMoveThreshold;
        cfg.SwivelDelay         = s.SwivelDelay;
        cfg.SwivelSpeed         = s.SwivelSpeed;

        cfg.EnableAutoShoulderSwap   = s.EnableAutoShoulderSwap;
        cfg.ShoulderLerpDuration     = s.ShoulderLerpDuration;
        cfg.ShoulderSwapCheckHz      = s.ShoulderSwapCheckHz;
        cfg.ShoulderSwapSafetyMargin = s.ShoulderSwapSafetyMargin;

        cfg.EnableCrosshair    = s.EnableCrosshair;
        cfg.CrosshairSize      = s.CrosshairSize;
        cfg.CrosshairThickness = s.CrosshairThickness;
        cfg.CrosshairFadeSpeed = s.CrosshairFadeSpeed;
        cfg.CrosshairColorR    = s.CrosshairColorR;
        cfg.CrosshairColorG    = s.CrosshairColorG;
        cfg.CrosshairColorB    = s.CrosshairColorB;
        cfg.CrosshairColorA    = s.CrosshairColorA;

        cfg.EnableCombatZoom         = s.EnableCombatZoom;
        cfg.CombatZoomDistance       = s.CombatZoomDistance;
        cfg.CombatZoomTransitionSpeed = s.CombatZoomTransitionSpeed;

        cfg.EnableAdsOnRmb     = s.EnableAdsOnRmb;
        cfg.AdsZoomFactor      = s.AdsZoomFactor;
        cfg.AdsTransitionSpeed = s.AdsTransitionSpeed;

        cfg.EnableInputSmoothing          = s.EnableInputSmoothing;
        cfg.InputSmoothingZoomRate        = s.InputSmoothingZoomRate;
        cfg.InputSmoothingRotateRate      = s.InputSmoothingRotateRate;
        cfg.EnableCameraPositionSmoothing = s.EnableCameraPositionSmoothing;
        cfg.CameraPositionSmoothingRate   = s.CameraPositionSmoothingRate;

        cfg.MouseSensitivityMul = s.MouseSensitivityMul;
        cfg.InvertMouseY        = s.InvertMouseY;

        cfg.EnableMouseLookAlways = s.EnableMouseLookAlways;
        cfg.MouseLookSensitivity  = s.MouseLookSensitivity;
        cfg.MouseLookCenterCursor = s.MouseLookCenterCursor;
        cfg.MouseLookInvertX      = s.MouseLookInvertX;
        cfg.MouseLookInvertY      = s.MouseLookInvertY;

        cfg.EnableCloseZoomPitchCap = s.EnableCloseZoomPitchCap;
        cfg.CloseZoomPitchCapZoom   = s.CloseZoomPitchCapZoom;
        cfg.CloseZoomPitchCapMinRad = s.CloseZoomPitchCapMinRad;

        cfg.EnableHotbarFader                 = s.EnableHotbarFader;
        cfg.HotbarFaderRate                   = s.HotbarFaderRate;
        cfg.HotbarFaderCascadeDelay           = s.HotbarFaderCascadeDelay;
        cfg.HotbarFaderDrawnAlpha             = s.HotbarFaderDrawnAlpha;
        cfg.HotbarFaderSheathedAlpha          = s.HotbarFaderSheathedAlpha;
        cfg.HotbarFaderHoverActivates         = s.HotbarFaderHoverActivates;
        cfg.HotbarFaderComboPromptBar         = s.HotbarFaderComboPromptBar;
        cfg.HotbarFaderAvailabilityBar        = s.HotbarFaderAvailabilityBar;
        cfg.HotbarFaderAvailabilityFlashSeconds = s.HotbarFaderAvailabilityFlashSeconds;

        cfg.HideTargetArrow                   = s.HideTargetArrow;
        cfg.EnableThirdPersonClickTranslation = s.EnableThirdPersonClickTranslation;
        cfg.EnableFovZoomContinuation         = s.EnableFovZoomContinuation;
        cfg.FovZoomMinFov                     = s.FovZoomMinFov;
        cfg.LockCameraDuringNpcDialogue       = s.LockCameraDuringNpcDialogue;
    }
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
    // Remembers the last user-selected preset (PresetManager sets this
    // whenever CurrentPreset is assigned). Restored on plugin/login
    // start so the user doesn't have to re-pick their preset every
    // session. Empty string = no override (use auto / default).
    public string LastActivePresetName = "";

    // Seconds the camera takes to transition from its current zoom /
    // FoV / tilt / look-at-height into a newly-applied preset's target
    // values. Smoothstep eased. Bounds (min/max zoom, FoV, V-rot
    // limits) snap immediately — only visible per-frame values lerp.
    // How long the camera takes to ease the position offsets
    // (Height/Side/LookAtHeight) into a newly-activated preset.
    // Default 0.5s = momentary. Anything longer than ~1s starts to
    // fight live Ctrl/Alt+scroll height/shoulder adjustments — the
    // transition's contribution can outrun the user's input mid-
    // glide. If you want a cinematic ease-in, set 1-2s and avoid
    // scrolling through the transition window.
    public float PresetTransitionSeconds = 0.5f;
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

    // CharacterRoll: writes roll into the player character's
    // DrawObject quaternion so the MODEL banks into turns alongside
    // the camera. Independent of the camera's RollTilt — the camera
    // can roll without the character or both can roll together.
    // Defaults are conservative; max 4° lean produces a perceptible
    // but not arcade-y bank.
    public bool  EnableCharacterRoll      = false;
    // ON-FOOT bank — applied when player is not mounted.
    public float CharacterRollMaxAngle    = 4.0f;  // degrees
    public float CharacterRollSensitivity = 0.25f;
    // MOUNTED bank — applied when on a mount. Defaults higher than
    // on-foot since vehicle/bike-style mounts read more naturally
    // with a stronger lean.
    public float CharacterRollMaxAngleMounted    = 18.0f; // degrees
    public float CharacterRollSensitivityMounted = 5.0f;
    public float CharacterRollOnRate      = 3.0f;
    public float CharacterRollOffRate     = 1.5f;

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

    // ---- Close-zoom pitch cap ----
    // Replaced by FOV-zoom continuation below — the cap fought the
    // engine's wall-collision push (engine pulls zoom in on
    // collision → cap engages → camera springs up) and didn't
    // actually solve the "pivot to overhead at close zoom" feel
    // because the geometry pivots regardless of pitch floor. Kept
    // for users who specifically want a pitch ceiling at close zoom
    // but defaulted off.
    public bool  EnableCloseZoomPitchCap     = false;
    public float CloseZoomPitchCapZoom       = 3.0f;
    public float CloseZoomPitchCapMinRad     = -0.4f;

    // ---- NPC dialogue camera lock ----
    // When ON, GetCameraTargetDetour forces the camera target back to
    // the local player whenever ConditionFlag.OccupiedInQuestEvent /
    // OccupiedInEvent is true. The engine's dialogue camera takeover
    // (retarget to NPC + auto-zoom + pitch shift) is what produced
    // the "dips underground on dialogue start" visual; this lock
    // hands camera control back to our preset transitions
    // (TalkingToNpc condition) instead.
    public bool  LockCameraDuringNpcDialogue = true;

    // ---- FOV zoom continuation ----
    // Standard FFXIV camera math (cam = lookAt + rotate * -dist)
    // pivots around the lookAt point, so reducing distance produces
    // a "swing under" feel instead of "pull camera in." When the
    // user scrolls past MinZoom, we keep currentZoom locked at
    // MinZoom and narrow currentFoV instead — the camera position
    // doesn't move at all, but the view zooms in optically (telephoto
    // feel). Scroll-out restores FoV first, then backs the camera
    // off normally.
    public bool  EnableFovZoomContinuation   = true;
    // Smallest FoV the continuation can narrow to. The preset's own
    // MinFoV typically clamps at ~0.69 rad (40°); this lets FoV go
    // tighter for the telephoto feel. 0.25 rad ≈ 14° (cinematic close).
    public float FovZoomMinFov               = 0.25f;

    // ---- Mount audio (dynamic engine sounds) ----
    // Reads local player's mount + speed each frame and crossfades
    // user-provided .ogg loops (idle / accel / cruise / decel /
    // mount / dismount). Audio files live in
    // <plugin-dir>/assets/mount-audio/<mountId>/. Missing layers
    // are skipped silently; user can ship a partial pack.
    public bool  EnableMountAudio        = false;
    public float MountAudioVolume        = 0.6f;   // 0..1, multiplied with per-layer
    public float MountAudioMaxSpeed      = 24f;    // m/s anchor for cruise pitch (above = max pitch)
    public float MountAudioCruisePitchMin = 0.85f; // pitch at speed = 0
    public float MountAudioCruisePitchMax = 1.20f; // pitch at speed = MountAudioMaxSpeed

    // Speed-band thresholds (m/s) for the 9-slot state machine.
    // Below SlowMin = idle band. Crossing SlowMin going up fires the
    // idle2slow one-shot, then the slow loop runs. Crossing MidMin
    // going up fires the revup one-shot, then the mid loop runs.
    // Above TopMin = top band. Crossing back down fires the decel
    // one-shot. User-tunable to match each mount's feel.
    public float MountAudioSpeedSlowMin  = 0.5f;
    public float MountAudioSpeedMidMin   = 8.0f;
    public float MountAudioSpeedTopMin   = 15.0f;

    // Per-slot file path overrides for mount audio. When an entry
    // here matches the (mountId, slot) being loaded, its FilePath
    // wins over the assets/mount-audio/<mountId>/<slot>.* convention.
    // Lets the user point at any wav anywhere on disk without
    // copying files into the assets dir.
    public List<MountAudioSlotOverride> MountAudioOverrides = new();

    // Per-slot timing (delay / fade-in / fade-out). When an entry
    // here matches the (mountId, slot) being triggered, those values
    // override the defaults used by the state machine.
    public List<MountAudioSlotTiming> MountAudioTimings = new();

    // ---- Native mount sound suppression via PlaySound hook ----
    // When LogMountSoundPaths is true, every distinct sound path
    // passing through SoundManager.PlaySound is logged once to
    // /xllog so the user can identify the engine's mount sound
    // file paths. Path substrings added to MountAudioMutePatterns
    // are then suppressed (PlaySound is skipped entirely) while the
    // player is mounted with a custom audio pack loaded.
    public bool         LogMountSoundPaths     = false;
    public List<string> MountAudioMutePatterns = new();

    // ---- Mount momentum (analog-stick magnitude envelope) ----
    // Emits a virtual XInput gamepad with left-stick magnitude that
    // tapers from full → zero on release (coast) and faster → zero
    // on S-held (brake). Server reads the analog magnitude and
    // computes real movement speed → other clients see the coast,
    // hitbox tracks correctly, walls catch as normal.
    //
    // Enabled per-mount (only motorcycle / vehicle mounts where
    // momentum makes sense). Mount IDs listed in
    // MountMomentumIds — user adds whichever mount(s) they want
    // to feel momentum. Default empty so on-foot + non-vehicle
    // mounts behave normally.
    public bool      EnableMountMomentum    = false;
    public List<int> MountMomentumIds       = new();
    // Lerp rates in seconds: time from 0 → 1 magnitude (or vice
    // versa). Lower = snappier, higher = more inertia.
    public float MountMomentumAccelSec      = 0.5f;  // rev-up: how long to reach full speed from rest
    public float MountMomentumCoastSec      = 0.6f;  // release: how long to coast from full → 0
    public float MountMomentumBrakeSec      = 0.25f; // S-held: faster than coast, but not instant

    // ---- Mount idle-animation freeze on mount-up ----
    // When the player mounts up, freeze the mount's idle animation
    // (e.g. motorcycle vibration) for a short window so the audio
    // "turn-on" one-shot reads as actually starting the engine
    // instead of overlaying on an already-jiggling bike. After the
    // window expires the animation resumes normally and idle.ogg
    // takes over the audio bed.
    public bool  EnableMountAnimationFreeze   = true;
    public int   MountAnimationFreezeMs       = 400;

    // Ctrl+scroll height nudge step (live, in-game).
    public float HeightOffsetStep    = 0.1f;

    // Live-tweakable global height offset (Ctrl/Alt + scroll). Stacks on top
    // of the preset's HeightOffset so scrolling persists across preset switches
    // and across sessions. Range matches Wicked's clamp (-2..4).
    public float GlobalHeightOffset  = 0f;

    // ---- Govee Light Sync (event-driven RGB) ----
    // API key is stored as plain text in the local Configuration.json
    // file. Same exposure as any other Dalamud plugin's stored creds —
    // no worse than the user typing it into the Govee Home app itself.
    public bool   EnableLightSync      = false;
    // Multi-device targets. Auto-populated from /lightsync devices
    // responses. Each entry has its own enable flag so the user can
    // pick which lights receive event flashes (e.g. all the WiFi
    // strips that aren't part of the SyncBox's scene). The legacy
    // single-target fields below are kept as a fallback when this
    // list is empty.
    public List<LightSyncDevice> LightSyncDevices = new();
    // Idle-dim mode: when ON, lights stay at 0% brightness whenever
    // no event is active, "pseudo-off." Events bring brightness up
    // to the per-event level for their duration; on expiry brightness
    // drops back to 0. Low-HP pulse cycles its own pattern while
    // active and falls to 0 on recovery.
    public bool  LightSyncIdleDim       = true;
    public int   LightSyncEventBright   = 100; // 1..100, brightness during a non-pulse event flash
    // Backend the events route through:
    //   "Cloud"  — Govee Cloud REST. Per-color override, no auto
    //              restore on H6603 (Govee API limitation).
    //   "Chroma" — Razer Chroma SDK on localhost:54235. Requires
    //              Razer Synapse 3 + Chroma Connect AND Govee
    //              Desktop with Chroma toggle on. The H6603 then
    //              auto-reverts to Video Sync when our session
    //              releases — same path Apex / LoL use.
    // Cloud is the default because the H6603 (the SKU most likely
    // targeted) explicitly doesn't support Razer Chroma per Govee's
    // own product page — confirmed via testing. Users with H6602 or
    // other Chroma-supported Govee SKUs can switch this to Chroma in
    // the Light Sync tab.
    public string LightSyncMode        = "Cloud";
    public string LightSyncApiKey      = "";
    public string LightSyncDeviceSku   = "H6603"; // Gaming Sync Box Kit (AI 4K)
    public string LightSyncDeviceMac   = "";       // populated via /lightsync devices
    // Default restore mode after an event override fires. "Video" sends
    // the box back to HDMI capture; "Previous" reads + restores whatever
    // mode was active when the event triggered (Apex/LoL pattern).
    public string LightSyncRestoreMode = "Previous";
    // Default flash duration (ms) for one-shot events when the per-event
    // map below doesn't override it.
    public int    LightSyncDefaultFlashMs = 1500;
    // Which HDMI input the SyncBox should switch back to after a
    // restore. H6603 has no dreamViewToggle capability that actually
    // works — selecting an hdmiSource is what re-engages Video Sync
    // capture from that input. 1..4 valid; pick whichever your box
    // is configured to use as its primary capture source.
    public int    LightSyncHdmiSource = 1;
    // Restore method. Govee deliberately doesn't expose Video Sync as
    // a public capability value, so the actual community-verified path
    // (used by govee2mqtt, hacs-govee, etc.) is:
    //   1. User saves "Video Sync" as a Snapshot in the Govee Home app.
    //   2. /user/devices now returns the snapshot in dynamic_scene/snapshot
    //      options[] with a numeric value.id.
    //   3. We POST that id via dynamic_scene/snapshot to restore.
    //
    //   "Snapshot"   — recommended. Recalls the saved Snapshot id below.
    //   "HdmiSource" — selects HDMI input. Works on H6602; on H6603
    //                  returns 200 but doesn't actually re-engage.
    //   "Manual"     — don't auto-restore. User does it themselves.
    public string LightSyncRestoreMethod = "Snapshot";
    // Snapshot id captured from /user/devices (dynamic_scene/snapshot
    // options[]). 0 means not yet configured.
    public int    LightSyncSnapshotId    = 0;

    // ---- Per-event toggles + colors ----
    // Death: rising-edge of LocalPlayer.IsDead.
    public bool LightSyncEventDeath           = true;
    public int  LightSyncEventDeathColor      = 0xCC0000;
    public int  LightSyncEventDeathDurationMs = 2500;

    // Low HP: HP percentage below threshold. Sets color (red) on
    // entry and pulses brightness through the configured pattern
    // continuously until HP recovers above threshold + 5%, then
    // restores brightness to 100%. Color stays red until another
    // event overrides it.
    public bool       LightSyncEventLowHp           = true;
    public int        LightSyncEventLowHpColor      = 0xFF2020;
    public int        LightSyncEventLowHpDurationMs = 1500; // legacy, unused now (kept so existing JSON deserializes)
    public float      LightSyncEventLowHpThreshold  = 0.30f;
    public List<int>  LightSyncEventLowHpPulse      = new() { 25, 50, 75 };
    public int        LightSyncEventLowHpPulseStepMs = 200;

    // Tell received: XivChatType.TellIncoming. Skipped for self.
    // Fires a quick on/off pulse (count × stepMs each on, then
    // stepMs off) ending dimmed.
    public bool LightSyncEventTell            = true;
    public int  LightSyncEventTellColor       = 0xFF40C0;
    public int  LightSyncEventTellDurationMs  = 1200; // legacy, unused since pulse model
    public int  LightSyncEventTellPulseCount  = 3;
    public int  LightSyncEventTellPulseStepMs = 100;

    // Duty pop: rising-edge of ConditionFlag.WaitingForDutyFinder
    // turning OFF (the queue popping = condition clearing).
    // Fires an alternating-group flash: enabled devices are split
    // into two groups (by index), groups swap on/off N times.
    // Falls back to a regular pulse when only one device is enabled.
    public bool LightSyncEventDutyPop          = true;
    public int  LightSyncEventDutyPopColor     = 0xFFD000;
    public int  LightSyncEventDutyPopDurationMs = 3000; // legacy, unused since alt model
    public int  LightSyncEventDutyPopAltCount   = 2;
    public int  LightSyncEventDutyPopAltStepMs  = 150;

    // Riding: continuous speed-driven event while mounted + moving.
    // Color = cyan, brightness scales linearly from RidingMinBright
    // (at the motion threshold) to RidingMaxBright (at RidingMaxSpeed
    // m/s and beyond). Lower priority than Low HP — Low-HP pulse
    // takes precedence when both are active.
    public bool  LightSyncEventRiding          = true;
    public int   LightSyncEventRidingColor     = 0x00E0FF; // cyan
    public float LightSyncEventRidingMaxSpeed  = 14f;       // m/s — typical FFXIV mount cruise
    public int   LightSyncEventRidingMinBright = 10;        // % at motion threshold
    public int   LightSyncEventRidingMaxBright = 100;       // % at MaxSpeed

    // Running on foot: continuous footstep-cadence brightness pulse
    // while NOT mounted and moving. Color = neutral warm white,
    // brightness alternates between Low and Peak every StepMs to
    // read as actual footsteps. Lowest priority of the continuous
    // events (mount riding wins if mounted; low-HP wins always).
    public bool  LightSyncEventRunning             = true;
    public int   LightSyncEventRunningColor        = 0xFFE0B0; // neutral warm white
    public int   LightSyncEventRunningPulsePeak    = 75;
    public int   LightSyncEventRunningPulseLow     = 30;
    public int   LightSyncEventRunningPulseStepMs  = 350;     // ~170 BPM step cadence

    // Walking (slower foot movement — typically /walk toggled on,
    // or briefly while accelerating from rest). Same step-pulse
    // mechanism as running but with a smaller swing and slower
    // cadence so it reads as gentler bounces.
    public bool  LightSyncEventWalking             = true;
    public int   LightSyncEventWalkingColor        = 0xFFE0B0; // same warm white default
    public int   LightSyncEventWalkingPulsePeak    = 55;
    public int   LightSyncEventWalkingPulseLow     = 35;
    public int   LightSyncEventWalkingPulseStepMs  = 550;     // slower cadence
    // Speed (m/s) below which foot movement is treated as walking.
    // FFXIV defaults: /walk ≈ 3.5 m/s, run ≈ 5 m/s, so 4.5 splits.
    public float LightSyncWalkSpeedThreshold       = 4.5f;

    // Sprinting (Sprint status active, ID 50). Continuous light
    // green; no pulse, just a steady glow at the configured
    // brightness. Higher priority than walking/running because
    // the buff is the explicit signal.
    public bool  LightSyncEventSprinting           = true;
    public int   LightSyncEventSprintingColor      = 0x80FF80; // light green
    public int   LightSyncEventSprintingBrightness = 90;

    // Critical hit: one-shot color sting on outgoing crits. Flashes
    // start color (orange) then fades to end color (red) over
    // FadeMs, then drops brightness to 0. Triggered externally by
    // CombatEvents (or any other module) calling LightSync.OnCritHit.
    public bool  LightSyncEventCrit              = true;
    public int   LightSyncEventCritStartColor    = 0xFF8800; // orange
    public int   LightSyncEventCritEndColor      = 0xFF0000; // red
    public int   LightSyncEventCritFadeMs        = 300;      // total flash duration

    // In-combat: continuous color while ConditionFlag.InCombat is
    // true. Higher priority than riding/running (overrides them
    // while engaged); lower priority than Low HP (low-HP red wins).
    // Color = yellow by default — the combat-engaged warning tone.
    public bool  LightSyncEventCombat            = true;
    public int   LightSyncEventCombatColor       = 0xFFC000; // amber-yellow
    public int   LightSyncEventCombatBrightness  = 80;

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

    // ---- Input smoothing (zoom + yaw + pitch lerps) ----
    // Optional. Each per-frame write to currentZoom/H/VRotation is
    // detected via a last-written-value comparison; the smoothed value
    // exp-lerps toward the new target. Higher Rate = snappier (less
    // smoothing); the rate is in 1/seconds (e.g., 12 → ~80 ms halflife).
    public bool  EnableInputSmoothing       = false;
    public float InputSmoothingZoomRate     = 12f;
    public float InputSmoothingRotateRate   = 22f;

    // Camera POSITION smoothing — exp-lerps HeightOffset / SideOffset
    // / GlobalHeightOffset toward their configured targets each frame.
    // Drives the slider drag / Ctrl+scroll feel without smoothing the
    // preset-switch snap (PresetManager calls SnapOffsets to bypass).
    public bool  EnableCameraPositionSmoothing = false;
    public float CameraPositionSmoothingRate   = 12f;  // 1/s, ~80 ms halflife
    public float GamepadSensitivityMul = 1f;
    public bool  InvertMouseY          = false;
    public bool  InvertGamepadY        = false;

    // ---- Third-person click translator ----
    // Routes LMB to hotbar-style numeric keys with modifier-driven slot selection.
    //   LMB                → 2
    //   Shift + LMB        → 1
    //   Ctrl  + LMB        → 3
    //   S (back) + LMB     → Shift+1
    //   W (fwd)  + LMB     → Shift+3   (W wins if user holds W+Shift+LMB)
    public bool EnableThirdPersonClickTranslation = false;

    // ---- Combat zoom (auto-pull-back during fights) ----
    // When enabled, currentZoom lerps toward CombatZoomDistance while the
    // ConditionFlag.InCombat is set, then back to the captured baseline
    // (the zoom you had right before combat started) when combat ends.
    public bool  EnableCombatZoom         = false;
    public float CombatZoomDistance       = 12f;   // target distance during combat
    public float CombatZoomTransitionSpeed = 4f;   // exp-lerp rate; bigger = snappier

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

    // ---- HP Ring (standalone HP-driven pulse overlay) ----
    // Independent of JobAura — a single screen-anchored ring that
    // pulses on a sine wave. At full HP the pulse is slow and the
    // base alpha is low (calm). At zero HP the pulse is rapid, the
    // base alpha is high, and the ring shrinks toward the center
    // (urgent). Linear interpolation across the entire HP range.
    public bool  EnableHpRing             = false;
    // Position as fractions of the viewport — (0.5, 0.5) = center.
    public float HpRingScreenX            = 0.5f;
    public float HpRingScreenY            = 0.85f;
    public float HpRingRadius             = 80f;     // pixels at full HP
    public float HpRingLowHpRadiusFactor  = 0.7f;    // multiplier at 0% HP
    public float HpRingThickness          = 3f;      // line width
    public int   HpRingSegments           = 64;      // circle resolution
    // Pulse rate (Hz) at full HP and at zero HP. Lerps linearly.
    public float HpRingSlowPulseHz        = 0.5f;
    public float HpRingFastPulseHz        = 3.0f;
    // Base / peak alpha at full HP vs at zero HP.
    public float HpRingFullHpBaseAlpha    = 0.5f;
    public float HpRingFullHpPeakAlpha    = 1.0f;
    public float HpRingLowHpBaseAlpha     = 0.8f;
    public float HpRingLowHpPeakAlpha     = 1.0f;
    // Ring color (RGB 0..1).
    public float HpRingColorR             = 1.0f;
    public float HpRingColorG             = 0.25f;
    public float HpRingColorB             = 0.25f;
    // ---- Bone anchoring ----
    // When enabled, the ring is positioned in 3D world space attached
    // to a player bone, projected to screen each frame. Lets you put
    // the ring behind the player (negative forward offset), above their
    // head, etc., and have it follow the camera + character properly.
    public bool  HpRingAnchorToBone       = false;
    public int   HpRingBoneIndex          = 1;       // 1 ≈ root/spine; tweak per skeleton

    // ---- Job Aura target-bone split (enemy vs allied player) ----
    // Different skeletons place bone indices at different heights.
    // Targeting a player ally puts the existing single TargetBoneIndex
    // visibly lower than the same index on an enemy, because allied
    // (player) skeletons tag their bones differently. This separate
    // "player target" index lets the user dial in a higher anchor
    // point when the target is another player.
    public int   JobAuraTargetBoneIndexPlayer = 4;   // higher up the body for player targets

    // ---- Hotbar Fader (cascade fade-in/out on weapon-drawn) ----
    // Hotbars 1, 7, 10 fade in cascade order when the weapon is drawn,
    // and reverse-cascade fade out when sheathed. CascadeDelay is the
    // gap between each slot starting (default 0.95s ~= one bar fully
    // resolves before the next begins). Rate is the exponential lerp
    // rate per second (higher = snappier within each slot's fade).
    public bool  EnableHotbarFader        = false;
    public float HotbarFaderRate          = 6.0f;     // exp rate per second
    public float HotbarFaderCascadeDelay  = 0.95f;    // seconds between slot starts
    public float HotbarFaderDrawnAlpha    = 1.0f;     // target when weapon out
    public float HotbarFaderSheathedAlpha = 0.0f;     // target when sheathed
    // When true, hovering the cursor over a faded bar's rect forces it
    // back to DrawnAlpha (overrides cascade hold + sheathed target).
    public bool  HotbarFaderHoverActivates = true;
    // Hotbar number (1..10) of the bar to fade in whenever an active
    // combo lands on one of its slots. 0 = disabled. The bar fades
    // back out as soon as the combo ends (ability used or combo timer
    // expires) since the override condition flips false.
    public int   HotbarFaderComboPromptBar  = 0;
    // Hotbar number (1..10) of the bar to flash in whenever any of its
    // action slots transitions from "on cooldown" to "ready". 0 =
    // disabled. The flash holds for HotbarFaderAvailabilityFlashSeconds
    // and then fades back out via the same exp rate.
    public int   HotbarFaderAvailabilityBar = 0;
    public float HotbarFaderAvailabilityFlashSeconds = 1.5f;

    // Hide the chevron/arrow indicator above the current target.
    // Pure presentation toggle — no gameplay impact. Restored on
    // toggle-off and on plugin Dispose.
    public bool  HideTargetArrow = false;

    // ---- Chat fade ----
    // Fades the FFXIV chat log when not typing. Hover-to-show + an
    // optional brighten-on-new-message window keep important chat
    // visible without the user having to focus the input.
    public bool  EnableChatFader                  = false;
    public float ChatFaderIdleAlpha               = 0.20f; // alpha when not typing/hovered
    public float ChatFaderActiveAlpha             = 1.00f; // alpha when typing / hovered / new msg
    public float ChatFaderRate                    = 6.0f;  // 1/s exp lerp rate
    public bool  ChatFaderHoverActivates          = true;
    public float ChatFaderHoldOnNewMessageSeconds = 4.0f;  // 0 = disabled
    // Minimal mode: hide chat tabs + the three icons next to them so
    // only the chat lines and input box are visible.
    public bool  ChatMinimalMode                  = false;
    // Hide the entire native chat (root visibility cleared). Only flip
    // this on when the bubble overlay is active or you have another
    // way of reading chat — the input field still works (Enter focuses
    // it natively) but the visible window goes away.
    public bool  ChatHideNative                   = false;

    // ---- Chat bubbles overlay (v1: read-only) ----
    public bool  EnableChatBubbles                = false;
    public float ChatBubblesX                     = 960f;
    public float ChatBubblesY                     = 700f;     // bottom of the bubble stack
    public float ChatBubblesColumnWidth           = 700f;     // overall column the bubbles align inside
    public float ChatBubblesMaxWidth              = 360f;     // max bubble body width before wrap
    public float ChatBubblesMaxAgeSeconds         = 30f;
    public float ChatBubblesSelfR                 = 0.20f;
    public float ChatBubblesSelfG                 = 0.55f;
    public float ChatBubblesSelfB                 = 0.95f;
    public float ChatBubblesSelfAlpha             = 0.85f;
    public float ChatBubblesOtherR                = 0.18f;
    public float ChatBubblesOtherG                = 0.18f;
    public float ChatBubblesOtherB                = 0.22f;
    public float ChatBubblesOtherAlpha            = 0.85f;
    // Show the bracketed channel tag ([PARTY], [FC], [TELL], etc.) above
    // each bubble. /say is always unmarked (baseline conversational
    // channel) regardless of this toggle.
    public bool  ChatBubblesShowChannelTag        = true;
    // Hover-reveal: hovering within this many pixels above the anchor
    // (centered on the column) reveals every buffered message at full
    // alpha, ignoring the per-entry max-age filter.
    public float ChatBubblesHoverRevealHeight     = 800f;
    public float ChatBubblesHoverHoldSeconds      = 1.5f;
    // How tall (in px) the soft gradient mask at the top of the
    // column extends. Bubbles whose top edge sits inside this band
    // fade toward 0 alpha so older messages disappear gracefully
    // instead of being hard-clipped at the column edge.
    public float ChatBubblesTopFadeHeight         = 100f;
    // Maximum visible height of the bubble column (in px, measured up
    // from the anchor). Bubbles past this height get progressively
    // more masked by the top-fade gradient and eventually skip
    // drawing entirely. Without this cap, a long history could spam
    // the entire screen — the cap defines a "container" that the
    // top-fade mask sits at the top of.
    public float ChatBubblesMaxColumnHeight       = 600f;
    // rtyping integration: poll the rtyping plugin's IPC channels to
    // surface "X is typing…" ghost bubbles at the bottom of the
    // column. No-op when rtyping isn't installed or isn't connected
    // to its server.
    public bool  EnableTypingIndicators           = true;
    // Reserved vertical space at the bottom of the bubble column for
    // typing indicators. Real bubbles start above this band — it
    // stays empty when no one is typing, but the column geometry
    // doesn't shift when the indicator fades in/out.
    public float ChatBubblesTypingReserveHeight   = 30f;
    // Backfill chat history on plugin load by parsing the engine's
    // RaptureLogModule.LogMessageData buffer. Format is undocumented
    // and may break on patch days — flip off if a future patch
    // produces nonsense bubbles, then re-enable when the parser is
    // updated.
    public bool  ChatBubblesBackfillOnLoad        = true;

    // Sends a slash command (default /tomescroll) once when the chat
    // input is focused. /tomescroll is a self-looping pose so a
    // single fire keeps the animation playing for as long as the
    // player stays still. If it ever stops on its own, the rising-
    // edge gate re-fires next time the user opens chat.
    public bool   EnableTypingEmote               = false;
    public string ChatTypingEmoteCommand          = "/tomescroll";
    // Re-fire interval in seconds. /tomescroll claims to self-loop
    // but real-world testing shows the loop can be interrupted by
    // the user closing/reopening the chat prompt, by movement, or
    // by other engine events. Periodic re-trigger means any such
    // interruption is restored within `ChatTypingEmoteRetriggerSeconds`.
    public float  ChatTypingEmoteRetriggerSeconds = 2.0f;
    // Optional command to send when the chat input loses focus —
    // empty = no cancel (player moves naturally to break the pose).
    public string ChatTypingEmoteCancelCommand    = "";
    // Font picker — same shape as the TargetUI font controls. Empty
    // path = default ImGui font; size is in pixels.
    public string ChatBubblesFontPath             = "";
    public float  ChatBubblesFontSize             = 16f;
    public float  ChatBubblesSenderFontSize       = 12f;  // sender label below the bubble

    // Custom typing prompt — rendered as an ImGui overlay when chat
    // input is focused. Mirrors the engine's text input so the user
    // can SEE what they're typing even when the native chat is
    // hidden. Sending still goes through the engine on Enter.
    public bool  EnableChatPrompt                 = false;
    public float ChatPromptX                      = 960f;
    public float ChatPromptY                      = 540f;
    public float ChatPromptWidth                  = 600f;
    public float ChatPromptFontSize               = 22f;
    public float ChatPromptBgR                    = 0.05f;
    public float ChatPromptBgG                    = 0.05f;
    public float ChatPromptBgB                    = 0.07f;
    public float ChatPromptBgAlpha                = 0.85f;
    public float ChatPromptTextR                  = 1f;
    public float ChatPromptTextG                  = 1f;
    public float ChatPromptTextB                  = 1f;
    public float ChatPromptTextAlpha              = 1f;

    // Diagnostic: log every damage effect entry (type, Param0/1,
    // value, action id, fromMe/toMe, crit/dh decision) so we can
    // verify the bit positions used by NormalHit/CritHit detection
    // when something stops triggering. Off in normal play.
    public bool  LogCombatHitDiagnostics = false;

    // Sen marker cascade delay — seconds the Sen markers wait after the
    // target overlay first becomes visible before they begin fading in,
    // so the rings / HP indicator land first and the Sen markers
    // cascade in afterwards.
    public float JobAuraSenCascadeDelay = 0.4f;

    // Hostile-target cascade: when targeting a non-enemy (friendly NPC,
    // ally player, etc.) the Kenki rings and Sen markers fade out in
    // sequence; on retargeting an enemy they cascade back in. Per-slot
    // delay between adjacent elements; total cascade ≈ 4×delay seconds.
    public float JobAuraHostileCascadeDelay = 0.08f;

    // When true, draws the same HP indicator ring(s) on party members
    // (anchored to their bone slot). Uses the same colors/sizes as the
    // player's HP indicator.
    public bool  JobAuraPartyHpRings    = false;

    // ==== Target UI overlay (replaces DelvUI's target/cast bar) ====
    // Anchor modes: 0 = absolute screen pixels (X/Y are screen coords),
    //               1 = anchored to target bone (X/Y are offsets in
    //                   pixels from the bone's projected screen pos).
    // Each element (target name, cast bar) has its own anchor + bone idx
    // so you can mix freely (e.g. cast bar floats above target's head,
    // target name pinned to a fixed screen slot).
    // Target name display.
    public bool   EnableTargetName            = false;
    public int    TargetNameAnchorMode        = 0;      // 0=Screen, 1=TargetBone
    public int    TargetNameBoneIndex         = 1;
    public float  TargetNameX                 = 960f;   // screen px (mode=0) or offset px (mode=1)
    public float  TargetNameY                 = 200f;
    public string TargetNameFontPath          = "";
    public float  TargetNameFontSize          = 22f;
    public float  TargetNameColorR            = 1.0f;
    public float  TargetNameColorG            = 1.0f;
    public float  TargetNameColorB            = 1.0f;
    public float  TargetNameAlpha             = 1.0f;
    public float  TargetNameOutlineColorR     = 0f;
    public float  TargetNameOutlineColorG     = 0f;
    public float  TargetNameOutlineColorB     = 0f;
    public float  TargetNameOutlineAlpha      = 1.0f;

    // Cast bar.
    public bool   EnableCastBar               = false;
    public int    CastBarAnchorMode           = 0;     // 0=Screen, 1=TargetBone
    public int    CastBarBoneIndex            = 1;
    public float  CastBarX                    = 960f;
    public float  CastBarY                    = 240f;
    public float  CastBarLength               = 220f;
    public float  CastBarHeight               = 10f;
    public float  CastBarFillR                = 0.85f;
    public float  CastBarFillG                = 0.55f;
    public float  CastBarFillB                = 0.15f;
    public float  CastBarFillAlpha            = 0.95f;
    public float  CastBarBgR                  = 0.10f;
    public float  CastBarBgG                  = 0.10f;
    public float  CastBarBgB                  = 0.10f;
    public float  CastBarBgAlpha              = 0.70f;
    public float  CastBarBorderR              = 0f;
    public float  CastBarBorderG              = 0f;
    public float  CastBarBorderB              = 0f;
    public float  CastBarBorderAlpha          = 0.85f;

    // Cast spell name (optional sub-toggle of cast bar).
    public bool   EnableCastBarSpellName      = true;
    public float  CastBarSpellOffsetX         = 0f;     // relative to the cast bar's TOP-LEFT
    public float  CastBarSpellOffsetY         = -18f;
    public string CastBarSpellFontPath        = "";
    public float  CastBarSpellFontSize        = 16f;
    public float  CastBarSpellColorR          = 1.0f;
    public float  CastBarSpellColorG          = 1.0f;
    public float  CastBarSpellColorB          = 1.0f;
    public float  CastBarSpellAlpha           = 1.0f;
    public float  CastBarSpellOutlineColorR   = 0f;
    public float  CastBarSpellOutlineColorG   = 0f;
    public float  CastBarSpellOutlineColorB   = 0f;
    public float  CastBarSpellOutlineAlpha    = 1.0f;

    // ---- JobAura Kenki tier ring colors ----
    // The three concentric Kenki rings (33% / 66% / 100%) drawn around
    // the anchor. Defaults match the original hard-coded ramp: pale
    // amber → warm amber → red-orange, each tier with its own alpha.
    public float JobAuraTier1ColorR = 1.00f;
    public float JobAuraTier1ColorG = 0.85f;
    public float JobAuraTier1ColorB = 0.40f;
    public float JobAuraTier1Alpha  = 0.55f;
    public float JobAuraTier2ColorR = 1.00f;
    public float JobAuraTier2ColorG = 0.65f;
    public float JobAuraTier2ColorB = 0.20f;
    public float JobAuraTier2Alpha  = 0.70f;
    public float JobAuraTier3ColorR = 1.00f;
    public float JobAuraTier3ColorG = 0.30f;
    public float JobAuraTier3ColorB = 0.10f;
    public float JobAuraTier3Alpha  = 0.85f;

    // ---- JobAura HP indicator ring styling (target / player anchor) ----
    // The HP indicator near the anchor is composed of three concentric
    // rings the user can fully restyle. Defaults match the original
    // hard-coded look (dark-red backdrop, bright inner core scaling
    // with HP, expanding pulse ring).
    public float JobAuraHpBackdropRadiusFactor = 0.7425f;  // × baseR (= 0.55 × 1.35 originally)
    public float JobAuraHpBackdropColorR       = 0.42f;
    public float JobAuraHpBackdropColorG       = 0.05f;
    public float JobAuraHpBackdropColorB       = 0.06f;
    public float JobAuraHpBackdropAlpha        = 0.65f;     // multiplier × hpA
    public float JobAuraHpInnerRadiusFactor    = 0.55f;     // × baseR × HP%
    public float JobAuraHpInnerColorR          = 1.0f;
    public float JobAuraHpInnerColorG          = 0.18f;
    public float JobAuraHpInnerColorB          = 0.18f;
    public float JobAuraHpInnerAlpha           = 0.85f;     // multiplier × hpA × HP%
    public float JobAuraHpPulseExpandFactor    = 1.95f;     // peak radius = backdrop × this
    public float JobAuraHpPulseColorR          = 1.0f;
    public float JobAuraHpPulseColorG          = 0.20f;
    public float JobAuraHpPulseColorB          = 0.20f;
    public float JobAuraHpPulseAlpha           = 0.85f;     // multiplier × hpA × (1 − pulseT)
    public float JobAuraHpPulseThickness       = 3.5f;

    // ---- AllSen "full zen" ring colors + fade-in ----
    // The "mangekyu ready" double-ring drawn around the anchor when all
    // three Sen are loaded. Inner ring fades in first (alpha ramp); the
    // outer ring then traces around the circle like a snake until it
    // closes. On fade-out the snake retraces backwards.
    public float JobAuraAllSenInnerColorR    = 1.0f;
    public float JobAuraAllSenInnerColorG    = 0.18f;
    public float JobAuraAllSenInnerColorB    = 0.18f;
    public float JobAuraAllSenInnerAlpha     = 1.0f;     // multiplier (0..1)
    public float JobAuraAllSenInnerThickness = 5.0f;
    public float JobAuraAllSenOuterColorR    = 0.85f;
    public float JobAuraAllSenOuterColorG    = 0.05f;
    public float JobAuraAllSenOuterColorB    = 0.05f;
    public float JobAuraAllSenOuterAlpha     = 0.55f;    // multiplier
    public float JobAuraAllSenOuterThickness = 2.5f;
    public float JobAuraAllSenOuterRadiusFactor = 1.10f; // outer = inner × this
    // Offset is in PLAYER-LOCAL coords — rotated by the player's yaw
    // each frame so "forward = -Z" stays behind the player as they turn.
    //   Right   = +X (player's right)
    //   Up      = +Y (world up)
    //   Forward = +Z (player's facing); use negative to place behind.
    public float HpRingOffsetRight        = 0f;
    public float HpRingOffsetUp           = 0f;
    public float HpRingOffsetForward      = -0.6f;   // behind the player by default

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

    // ---- Job aura (SAM Kenki tiers + max-cap audio cue) ----
    public bool  EnableJobAura      = false;
    public bool  MuteJobAuraSfx     = false;
    // Visual placement. Anchor to a bone on the player's skeleton (default
    // ON, bone index 4 ≈ upper spine/back) and apply an additive offset in
    // world meters. Scale multiplies all ring radii.
    public bool  JobAuraAnchorToBone = true;
    public int   JobAuraBoneIndex    = 4;
    // Self anchor offset (when JobAuraAnchorToTarget is OFF — overlay
    // sits on the local player).
    public float JobAuraOffsetX      = 0f;
    public float JobAuraOffsetY      = 0.4f;
    public float JobAuraOffsetZ      = -0.15f;
    // Player-ally target offset (when target is a Pc / friendly
    // player). Skeletons place the same bone at a different absolute
    // height than enemies; this lets you dial it independently.
    public float JobAuraTargetPlayerOffsetX = 0f;
    public float JobAuraTargetPlayerOffsetY = 0.4f;
    public float JobAuraTargetPlayerOffsetZ = 0f;
    // Enemy target offset (BattleNpc).
    public float JobAuraTargetEnemyOffsetX  = 0f;
    public float JobAuraTargetEnemyOffsetY  = 0.4f;
    public float JobAuraTargetEnemyOffsetZ  = 0f;
    public float JobAuraScale        = 1f;
    public bool  JobAuraFadeOutOfCombat = true;
    public float JobAuraOutOfCombatAlpha = 0f;   // target alpha multiplier when OOC
    public float JobAuraFadeRate         = 4f;   // exp rate
    public bool  JobAuraFadeWhenNoTarget = true;
    public float JobAuraNoTargetAlpha    = 0f;   // residual visibility when no target
    // When true, aura only renders / plays SFX while weapon is drawn.
    public bool  JobAuraOnlyWeaponDrawn  = true;
    // Per-clip volume, 0..1.
    public float JobAuraVolMax = 1f;
    public float JobAuraVolPt1 = 1f;
    public float JobAuraVolPt2 = 1f;
    // Target-anchor: when true, rings draw on the current target instead of
    // the player. Bone index/offsets still apply (relative to target).
    public bool  JobAuraAnchorToTarget = false;
    // Separate bone index for targets — different skeletons mean a single
    // index doesn't put us in a consistent place across all enemies. Bone 1
    // is typically near the pelvis on FFXIV skeletons (player/NPC/monster),
    // making it a reasonable "center of body" default.
    public int   JobAuraTargetBoneIndex = 1;
    // Real .avfx triggers. Empty path = no real VFX (ImGui rings still draw).
    // Provide a path that exists in your client, e.g. via VfxEditor lookup.
    // (Legacy single-path fields, kept for back-compat — superseded by
    // JobAuraVfxLayers.)
    public string JobAuraVfxMeditatePath = "";
    public string JobAuraVfxAllSenPath   = "";
    public string JobAuraVfxBurstPath    = "";
    // Modular VFX layer list — user-managed in the UI. Each layer hooks a
    // trigger (Kenki tier or Sen state) and either sustains while active
    // or fires one-shot on the rising edge.
    public System.Collections.Generic.List<JobAuraVfxLayer> JobAuraVfxLayers = new();
    // Real avfx triggering — now StaticVfx-based with explicit lifecycle,
    // so safe to default on. Toggle off if a future patch breaks the sigs.
    public bool JobAuraEnableRealVfx = true;
    // Sen marker tuning
    public float JobAuraSenPadding = 1.18f; // multiplier on largest-ring radius
    public float JobAuraSenScale   = 1.0f;  // multiplier on dot size
    // HP-text font (system .ttf path + size). Empty path = default ImGui font.
    public string JobAuraHpFontPath = "";
    public float  JobAuraHpFontSize = 22f;
    public bool   JobAuraShowHpText = true;
}