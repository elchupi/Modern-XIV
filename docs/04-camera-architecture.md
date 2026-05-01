# Camera Dynamics Architecture

The single most important file in this fork is [`CameraDynamics.cs`](../CameraDynamics.cs). It runs once per `Framework.Update` and is responsible for layering the dynamic-feel camera tweaks on top of Cammy's existing per-preset framing.

## Per-frame call order

```
noWickyXIV.Update()                         (Hypostasis hooks Framework.Update)
├── FreeCam.Update()                        Cammy free-cam logic; bails early if not active
├── PresetManager.Update()                  Auto-swap by QoL Bar condition sets
├── InputHandler.Update()                   Hotkey edge-detection (Ctrl/Alt+scroll, F6, V, Q, Ctrl+1..9)
└── CameraDynamics.Update()                 ← this is where the dynamic feel lives
    ├── UpdateSensitivity(cam, tps)         FIRST — scales user H/V deltas before anything else writes
    ├── UpdateRollTilt(cam, tps, dt)        writes cam->tilt
    ├── UpdatePitchTilt(cam, tps, dt)       additive on cam->lookAtHeightOffset
    ├── UpdatePositionFloat(dt)             builds _floatOffset; applied later in Game.cs
    ├── UpdateAds(cam, tps, dt)             writes cam->currentFoV + cam->currentZoom while RMB held
    ├── UpdateAutoShoulderSwap(cam, tps, dt) lerps _shoulderDisplay (via Game.cs uses GetActiveSideOffset())
    ├── UpdateSwivelOnMove(cam, tps, dt)    writes cam->currentHRotation toward player.Rotation+π
    └── UpdateInstantModeNote()             one-shot log warning (no-op feature; FFXIV lacks the fields)

noWickyXIV.Draw()                           (Hypostasis hooks UiBuilder.Draw)
├── FreeCam.Draw()                          Cammy free-cam ImGui overlay
├── PluginUI.Draw()                         The settings window
└── Crosshair.Draw()                        ImGui foreground draw-list (cross arms + dot)
```

## Why this order

**Sensitivity is first** because it does delta-replay scaling (`new = prev + (curr - prev) * mul`). If we wrote to `currentHRotation` before reading the user's delta — e.g. SwivelOnMove rotating us toward the player's facing — that swivel would be treated as user input on the NEXT frame and double-scaled. Running sensitivity first captures only the genuine input delta, then later writes don't disturb the next-frame baseline.

**RollTilt before PitchTilt** is arbitrary; they touch different fields (`tilt` vs `lookAtHeightOffset`).

**PositionFloat doesn't write to the camera struct here** — it builds a `Vector3 _floatOffset` that [`Game.GetCameraPositionDetour`](../Game.cs) reads and applies during the position-compute call. That's the correct integration point because position is computed by the game's vtable function, not by writing struct fields.

**SwivelOnMove writes `currentHRotation`** — this is a yaw rotation. Goes after sensitivity so our writes aren't replayed-scaled.

**Auto-shoulder swap** keeps a state machine but doesn't touch struct fields directly. The lerped `_shoulderDisplay` value is exposed via `CameraDynamics.GetActiveSideOffset(presetSideOffset)`, which `Game.GetCameraPositionDetour` calls in place of `preset.SideOffset`.

## The FFXIV camera struct

Camera writes target [`Hypostasis/Game/Structures/GameCamera.cs`](../Hypostasis/Game/Structures/GameCamera.cs):

| Field (offset) | Type | Range | What we use it for |
|---|---|---|---|
| `currentHRotation` (0x140) | float | [-π, π], default π | Yaw — read by sensitivity scaling, written by SwivelOnMove |
| `currentVRotation` (0x144) | float | [minVRotation, maxVRotation] | Pitch — read by PitchTilt + sensitivity, written by sensitivity |
| `minVRotation` / `maxVRotation` (0x158/0x15C) | float | typical -1.48..0.78 | Pitch clamp — read by PitchTilt to normalize |
| `currentZoom` (0x124) | float | [minZoom, maxZoom] | Zoom — read by ADS, written by ADS while held |
| `currentFoV` (0x130) | float | [minFoV, maxFoV] | FoV — same as zoom; ADS narrows it |
| `tilt` (0x170) | float | radians | Roll — written by RollTilt (degrees converted via × π/180) |
| `lookAtHeightOffset` (0x234) | float | depends on preset | Look-at vertical offset — additive write by PitchTilt |
| `viewX/Y/Z` (0x1C0+) | float | world-space | Camera position — Game.cs adds PositionFloat offset |
| `mode` (0x180) | int | 0=1st person, 1=3rd person | Gating — most dynamics only fire when `mode == 1` |

Fields **NOT exposed** by the struct that Wicked uses: `CharacterPositionSmoothSpeed`, `PositionSmoothBlendSpeed`, `RotationLagSpeed`, etc. This is why **InstantMode is a no-op** — there's nothing to zero.

## Math primitives

Two helpers used everywhere in `CameraDynamics.cs`:

```csharp
// Frame-rate-independent lerp toward target. Identity at rate=0 or dt=0.
// At rate=R, half-life = ln(2)/R seconds.
float ExpDecay(float current, float target, float rate, float dt)
    => current + (target - current) * (1f - MathF.Exp(-rate * dt));

// Wrap-aware delta in [-π, π] range — handles the seam at ±π.
float AngleDelta(float prev, float curr) {
    float d = curr - prev;
    while (d >  MathF.PI) d -= 2f * MathF.PI;
    while (d < -MathF.PI) d += 2f * MathF.PI;
    return d;
}
```

Plus a `Vector3` overload `ExpDecayV` for the position float.

## Feature status

| Feature | File:method | Status | Notes |
|---|---|---|---|
| RollTilt | `CameraDynamics.UpdateRollTilt` | ✅ Live | Yaw-velocity → roll degrees, asymmetric on/off rates, decays to 0 when disabled or in 1st person |
| PitchTilt | `CameraDynamics.UpdatePitchTilt` | ✅ Live | Additive `lookAtHeightOffset` based on inverse-lerp of pitch in [minVRot, maxVRot] |
| PositionFloat | `CameraDynamics.UpdatePositionFloat` + `Game.GetCameraPositionDetour` | ✅ Live | `_floatOffset = ExpDecay(_floatOffset, -velocity * lagFactor, 1/smoothTime, dt)`, clamped to ±0.5m |
| SwivelOnMove | `CameraDynamics.UpdateSwivelOnMove` | ✅ Live | Tracks player position; after `SwivelDelay` of continuous movement, rotates yaw toward `player.Rotation + π` at `SwivelSpeed` deg/s |
| ADS-on-RMB | `CameraDynamics.UpdateAds` | ✅ Live | RMB held → exp-lerp FoV/zoom toward `base/factor`; release → lerp back to baseline. Baseline captured on rising edge |
| Sensitivity multiplier + Y invert | `CameraDynamics.UpdateSensitivity` | ✅ Live | Delta-replay scaling on `currentHRotation`/`currentVRotation` |
| Auto-shoulder swap | `CameraDynamics.UpdateAutoShoulderSwap` + `TryProbeWall` | 🟡 NOT NEEDED | State machine + lerp ports cleanly, but **FFXIV's native camera already handles wall collision** (zoom auto-pulls in when geometry intrudes between camera and player). Auto-shoulder-swap was solving a problem from *No Rest For The Wicked* — that game doesn't have native wall collision, so Wicked needed to detect walls and shift the shoulder. Here it's redundant. The probe stub stays, dormant; toggle defaults off. **Don't wire the raycast.** |
| Manual shoulder swap (hotkey) | `InputHandler.UpdateShoulderSwapHotkey` | ✅ Live | Flips active preset's `SideOffset` sign on press. The actually-useful shoulder feature here. |
| Crosshair overlay | `Crosshair.Draw` | ✅ Live | ImGui foreground draw-list, fades per `CrosshairFadeSpeed`, hides in cutscenes |
| F6 panel toggle | `InputHandler.UpdateSettingsHotkey` | ✅ Live | Default F6, configurable |
| V crosshair toggle | `InputHandler.UpdateCrosshairHotkey` | ✅ Live | Default V, configurable |
| Ctrl+1..9 preset slots | `InputHandler.UpdatePresetSlotHotkeys` | ✅ Live | Opt-in via `PresetHotkeysEnabled`, 9 slots |
| Ctrl/Alt+scroll height | `InputHandler.UpdateScrollHeight` | ✅ Live | Nudges `GlobalHeightOffset` by `HeightOffsetStep` per scroll tick, clamped -2..4. Suppresses native zoom while modifier held via `Game.GetZoomDeltaDetour` |
| YawLag | (not implemented) | 🟡 Deferred | Wicked's impl whiplashes; needs spring-on-yaw-rate-offset rewrite |
| InstantMode | (no-op) | 🟡 Deferred | FFXIV camera struct doesn't expose smoothing-rate fields |

## Configuration → field map

Each runtime feature reads from `noWickyXIV.Config.X`. The fields are defined in [`Configuration.cs`](../Configuration.cs); panel rows in [`PluginUI.cs`](../PluginUI.cs) `DrawCameraDynamics()`.

When adding a new field:
1. Add property to `Configuration.cs` with a sane default.
2. Add a panel row using `ConfigSliderFloat` or `ConfigCheckbox` (defined in PluginUI.cs).
3. Add to `ResetAllDynamicsToDefaults()` so the reset button restores it.
4. Read it from the appropriate per-frame method in `CameraDynamics.cs`.

JSON deserialization handles missing fields automatically (defaults from initializer), so old configs continue to work after schema additions.
