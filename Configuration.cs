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
    // Remembers the last user-selected preset (PresetManager sets this
    // whenever CurrentPreset is assigned). Restored on plugin/login
    // start so the user doesn't have to re-pick their preset every
    // session. Empty string = no override (use auto / default).
    public string LastActivePresetName = "";

    // Seconds the camera takes to transition from its current zoom /
    // FoV / tilt / look-at-height into a newly-applied preset's target
    // values. Smoothstep eased. Bounds (min/max zoom, FoV, V-rot
    // limits) snap immediately — only visible per-frame values lerp.
    public float PresetTransitionSeconds = 5.0f;
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
    public float JobAuraOffsetX      = 0f;
    public float JobAuraOffsetY      = 0.4f;
    public float JobAuraOffsetZ      = -0.15f;
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