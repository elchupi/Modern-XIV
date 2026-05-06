using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dalamud.Configuration;

namespace noWickyXIV;

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
    public ViewBobSetting ViewBobMode = ViewBobSetting.Disabled;
    public int ConditionSet = -1;
    // Built-in game-state condition that activates this preset. Takes
    // precedence over ConditionSet when non-None — picking one in the
    // Condition Set dropdown sets this and clears ConditionSet so the
    // two triggers don't collide.
    public BuiltinPresetCondition Condition = BuiltinPresetCondition.None;

    public CameraConfigPreset Clone() => (CameraConfigPreset)MemberwiseClone();

    public bool CheckConditionSet()
    {
        // Built-in condition wins if set.
        if (Condition != BuiltinPresetCondition.None)
            return EvaluateBuiltinCondition(Condition);
        // QoL Bar set, or unconditional ("None").
        return ConditionSet < 0 || IPC.QoLBarEnabled && IPC.CheckConditionSet(ConditionSet);
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
                _ => false,
            };
        }
        catch { return false; }
    }

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