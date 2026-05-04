using System;

namespace noWickyXIV;

// Triggers that a JobAuraVfxLayer can hook into. Limited to Kenki tier
// state and Sen flags — buff-driven triggers (Meditate/Fuka/Fugetsu/etc.)
// stay separate. JobAura.IsTriggerActive must mirror this enum.
public enum JobAuraTrigger
{
    KenkiTier1,    // Kenki >= 33%
    KenkiTier2,    // Kenki >= 66%
    KenkiTier3,    // Kenki  = 100% (sustained while capped)
    KenkiCapEdge,  // One-shot on rising edge into 100% (pair with SingleShot mode)
    Setsu,         // Setsu Sen loaded
    Getsu,         // Getsu Sen loaded
    Ka,            // Ka Sen loaded
    AllSen,        // All three Sen loaded (mangekyu ready)
    NormalHit,     // One-shot — outgoing non-crit damage from local player
    CritHit,       // One-shot — outgoing crit or direct-hit damage
    IncomingDamage,// One-shot — local player took damage
    Moving,        // Sustained-while-true — local player is moving (speed > threshold)
    Stopped,       // Sustained-while-true — local player is stationary (speed <= threshold)
}

// Two layer modes. The old "IsBurst" boolean was a poor abstraction
// (it conflated "fire once per rising edge" with "no lifecycle
// tracking"). Replaced with explicit modes:
//
//   SingleShot — fires ONCE on rising edge of the trigger. Plays for
//                RunTimeSeconds, then is done. Will not refire until
//                the trigger goes false → true again (cycle reset).
//                Use for impact effects, "skill ready" pulses, etc.
//
//   Sustained  — keeps the effect playing for as long as the trigger
//                is active. We track when the current shot's runtime
//                ends and refire automatically when both:
//                  (a) trigger is still active, and
//                  (b) the previous shot's RunTimeSeconds has elapsed.
//                This avoids duplicate stacking from re-firing every
//                frame on an active trigger. Use for buff auras,
//                "tier-met" persistent indicators, etc.
public enum JobAuraVfxMode
{
    SingleShot,
    Sustained,
}

// How a layer decides WHEN to fire.
//
//   Default — fire on the rising edge of the layer's own Trigger
//             (the existing behaviour).
//
//   Chain   — ignore the layer's Trigger for firing decisions. Instead,
//             watch for ANOTHER layer (anywhere in the list) whose Path
//             matches ChainSourcePath actually firing its vfx. When that
//             happens, this layer fires DelaySeconds later. The source
//             path is picked from a dropdown of existing layer paths.
//
//   Chained — same firing semantics as Chain, but the source path is
//             freely typed (so it can name a vfx that doesn't belong to
//             a current layer) with a quick-pick dropdown adjacent to
//             the text input. The layer's own Path (the "next
//             animation") gets the same text + quick-pick treatment.
//
// Trigger remains useful for Chain/Chained layers as a category bucket —
// the per-category UI tab uses it to group layers visually.
public enum JobAuraLayerSourceMode
{
    Default,
    Chain,
    Chained,
}

// One configurable visual-effect layer the user can add/remove from the UI.
public class JobAuraVfxLayer
{
    // Stable identity so handle tracking survives list reorders.
    public Guid Id = Guid.NewGuid();
    public bool Enabled = true;
    public string Name = "Layer";
    public JobAuraTrigger Trigger = JobAuraTrigger.KenkiTier3;
    public string Path = "";

    // Replaces old `IsBurst` boolean — see JobAuraVfxMode docs above.
    // Defaults to SingleShot (the safe choice — fires once per rising
    // edge with no risk of duplicate stacking). User can toggle to
    // Sustained per-layer when they want the avfx's natural loop.
    public JobAuraVfxMode Mode = JobAuraVfxMode.SingleShot;

    // Default = fire from layer's own Trigger.
    // Chain   = fire DelaySeconds after another layer with a matching
    //           Path (= ChainSourcePath) fires. See JobAuraLayerSourceMode.
    public JobAuraLayerSourceMode SourceMode = JobAuraLayerSourceMode.Default;

    // Source path used when SourceMode == Chain. Compared (case-insensitive)
    // against the Path of every other layer when those layers fire.
    public string ChainSourcePath = "";

    // When true, this layer's rising-edge schedule is suppressed if any
    // OTHER configured + enabled layer fired within its RunTimeSeconds
    // window. Use it to prevent the Stopped layer from firing during
    // gap-closer animations (the player's motion settles for a moment
    // while the gap closer's own effect is still running, which would
    // otherwise stack a "stopped" feedback on top of the action effect).
    public bool SuppressWhileOthersFiring = false;

    // Natural runtime of one "shot" of the avfx, in seconds. The
    // engine plays the avfx for its own internal duration regardless,
    // but we use this value for our refire/finish tracking:
    //   SingleShot: shot is "done" after this many seconds.
    //   Sustained:  next shot fires this many seconds after previous.
    // 1.0–3.0 is typical for action vfx. Set higher for ambient looping
    // effects to reduce refire frequency.
    public float RunTimeSeconds = 2.0f;

    // World-space offset in metres. Kept for forward compat but
    // not currently surfaced — actor-anchored vfx have engine-driven
    // positioning that ignores per-frame offset writes.
    public float OffsetX = 0f;
    public float OffsetY = 0f;
    public float OffsetZ = 0f;

    // Minimum interval between fires of this layer. Belt-and-braces
    // safety net. Default 0.25s.
    public float MinIntervalSeconds = 0.25f;

    // ---- Sound (per-layer) ----
    // Optional .wav path that plays when this layer fires. Empty
    // string = no sound. Volume is 0.0 to 1.0 (fed through the same
    // mci wav-scale pipeline as the existing JobAura sounds).
    public string SoundPath = "";
    public float SoundVolume = 1.0f;

    // ---- Fire delay ----
    // Seconds between the trigger going active (rising edge) and the
    // actual vfx + sound fire. Lets the user stagger overlapping
    // layers so they don't all hit the same frame. 0 = fire immediately.
    public float DelaySeconds = 0.0f;

    // ---- End trigger ID ----
    // Fired into the running vfx via CallTrigger when the layer's
    // condition goes false (rising→falling). The avfx's own timeline
    // picks up the trigger ID and runs its end-animation (graceful
    // fade-out, then engine-side self-clean). Avfx-specific — common
    // values to try are 0, 1, 2. -1 = don't dispatch any trigger
    // (vfx plays its natural duration to completion).
    public int EndTriggerId = -1;

    // ---- Compatibility shims (older configs) ----
    // Old configs serialised IsBurst (bool). Map true→SingleShot on
    // load via this property so existing user configs upgrade silently.
    [Newtonsoft.Json.JsonProperty]
    public bool IsBurst
    {
        get => Mode == JobAuraVfxMode.SingleShot;
        set => Mode = value ? JobAuraVfxMode.SingleShot : JobAuraVfxMode.Sustained;
    }
    // Old configs called this LifetimeSeconds. Same meaning now.
    [Newtonsoft.Json.JsonProperty]
    public float LifetimeSeconds
    {
        get => RunTimeSeconds;
        set { if (value > 0f) RunTimeSeconds = value; }
    }
}
