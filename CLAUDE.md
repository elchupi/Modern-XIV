# CLAUDE.md — noWickyXIV operational reference

> Read this first. ~5 minute read. Operational ground truth for a future Claude session entering this project cold. The verbose `docs/` folder is reference material; this file is what you actually need to act.

## What this is

Personal-use Dalamud plugin. Fork of [UnknownX7/Cammy](https://github.com/UnknownX7/Cammy) with a dynamic-feel camera layer ported from the user's `noWickyTPS_MP` mod (a Wicked third-person camera mod for *No Rest For The Wicked*). Private repo: [`ebhemmanuel/noWickyXIV`](https://github.com/ebhemmanuel/noWickyXIV).

**The user is not redistributing this.** No public release polish. No DalamudPluginsD17 PRs. Don't suggest those.

## Project rooted at

```
%AppData%\XIVLauncher\devPlugins\noWickyXIV\
```

This is *Dalamud's default dev-plugin folder*. Build output at `bin/Debug/` is auto-loaded by Dalamud — **no copy step**. Build = deployed.

## Build + push loop

```bash
cd "C:/Users/akkis/AppData/Roaming/XIVLauncher/devPlugins/noWickyXIV"
dotnet build noWickyXIV.csproj -c Debug
# in-game: /xlplugins → Disable + Enable to hot-reload
git add -A && git commit -m "..." && git push origin main
```

`gh` is authenticated as `ebhemmanuel`. .NET 10 SDK is installed and pinned via `global.json`.

## Don't violate these (saved as memories)

1. **Don't deviate from the explicit ask.** No adjacent cleanups, no "while I'm here" refactors, no proposing better alternatives unless asked. The user has called this out as a recurring frustration.
2. **YawLag is broken — leave it disabled.** Wicked's impl whiplashes (no soft delay, no landing). The settings exist in [`Configuration.cs`](Configuration.cs) but `EnableYawLag = false` and there's no implementation in [`CameraDynamics.cs`](CameraDynamics.cs). To wire it: redesign as critically-damped spring on yaw-rate-driven OFFSET (not exp-decay on absolute yaw target). Don't ship the math from Wicked; it's the broken impl.

## Per-frame architecture (the part that matters)

Single entry point: `noWickyXIV.Update()` (subscribed to `Framework.Update` via Hypostasis base class). Calls in this order:

```
FreeCam.Update()           Cammy free-cam; bails when not active
PresetManager.Update()     QoL Bar condition-set auto-swap
InputHandler.Update()      Hotkey edge-detect + Ctrl/Alt+scroll height
CameraDynamics.Update()    The dynamic-feel layer ↓
  ├ UpdateSensitivity()    FIRST — delta-replay scaling on H/V rotation
  ├ UpdateRollTilt()       writes cam->tilt
  ├ UpdatePitchTilt()      additive on cam->lookAtHeightOffset
  ├ UpdatePositionFloat()  builds _floatOffset (applied by Game.cs)
  ├ UpdateAds()            cam->currentFoV/currentZoom while RMB held
  ├ UpdateAutoShoulderSwap() lerps _shoulderDisplay; raycast probe is STUB
  ├ UpdateSwivelOnMove()   writes cam->currentHRotation toward player.Rotation+π
  └ UpdateInstantModeNote() one-shot log; feature is a no-op
```

**Sensitivity must be first.** It captures user delta against the previous frame's rotation; if other writes (Swivel, etc.) ran first, those would be replayed-scaled the next frame. Don't reorder.

## FFXIV camera struct fields (cheat sheet)

`Common.CameraManager->worldCamera` → `Hypostasis.Game.Structures.GameCamera*`.

| Field (offset) | Type | Range | Use |
|---|---|---|---|
| `currentHRotation` (0x140) | float | [-π, π] | Yaw. Wraps. |
| `currentVRotation` (0x144) | float | [minVRot, maxVRot] | Pitch. |
| `minVRotation` / `maxVRotation` (0x158/0x15C) | float | typical -1.48..0.78 | Pitch clamp. |
| `currentZoom` (0x124) | float | [minZoom, maxZoom] | Zoom. |
| `currentFoV` (0x130) | float | [minFoV, maxFoV] | FoV (radians). |
| `tilt` (0x170) | float | radians | **Roll.** Game expects radians; we store config in degrees, multiply by π/180 on write. |
| `lookAtHeightOffset` (0x234) | float | depends | Look-at vertical offset. Cammy already writes it from `preset.LookAtHeightOffset`; we add PitchTilt on top. |
| `viewX/Y/Z` (0x1C0/0x1C4/0x1C8) | float | world-space | Camera world position. PositionFloat additive offset is added in `Game.GetCameraPositionDetour`. |
| `mode` (0x180) | int | 0=1st person, 1=3rd person | Most dynamics gate on `mode == 1`. |

**Fields that DON'T exist** (Wicked has them, FFXIV doesn't): `CharacterPositionSmoothSpeed`, `PositionSmoothBlendSpeed`, `RotationLagSpeed`, etc. This is why `InstantMode` is a no-op.

## Feature status (current truth)

| Feature | Status | Where |
|---|---|---|
| RollTilt | ✅ Live | `CameraDynamics.UpdateRollTilt` |
| PitchTilt | ✅ Live | `CameraDynamics.UpdatePitchTilt` |
| PositionFloat | ✅ Live | `CameraDynamics.UpdatePositionFloat` + `Game.GetCameraPositionDetour` |
| SwivelOnMove | ✅ Live | `CameraDynamics.UpdateSwivelOnMove` |
| ADS-on-RMB | ✅ Live | `CameraDynamics.UpdateAds` |
| Sensitivity + Y invert | ✅ Live | `CameraDynamics.UpdateSensitivity` |
| Crosshair overlay | ✅ Live | `Crosshair.cs` (ImGui foreground draw list) |
| Manual shoulder swap (hotkey) | ✅ Live | `InputHandler.UpdateShoulderSwapHotkey` (default unbound; user assigns) |
| Auto-shoulder swap | 🟡 NOT NEEDED | Wicked needed wall-side detection because *No Rest For The Wicked*'s camera doesn't natively pull in / shift on walls. **FFXIV's native camera already handles wall collision** (zoom auto-pulls in, etc.) so the auto-shoulder feature is solving a problem that doesn't exist here. State machine + `TryProbeWall` stub remain in `CameraDynamics.cs` as dormant code; the UI toggle in `PluginUI.cs` defaults off. **Do not implement the BGCollisionModule raycast** — there's nothing for it to fix. Manual shoulder swap (Q hotkey) is the only shoulder feature that delivers value here. |
| Hotkeys (F6 / V / Q / Ctrl+1..9) | ✅ Live | `InputHandler.cs` |
| Ctrl/Alt+scroll height | ✅ Live | `InputHandler.UpdateScrollHeight` + `Game.GetZoomDeltaDetour` zero-suppression |
| YawLag | 🟡 DEFERRED | DO NOT IMPLEMENT WITHOUT REDESIGN — see "Don't violate" above |
| InstantMode | 🟡 NO-OP | FFXIV camera struct lacks the smoothing-rate fields Wicked zeroes; toggle stays in UI for symmetry but does nothing. Logged once per session. |

## Adding a feature (recipe)

1. Field in [`Configuration.cs`](Configuration.cs) — bool toggle + magnitude floats; defaults that produce identity behavior when off.
2. UI row in [`PluginUI.cs`](PluginUI.cs) `DrawCameraDynamics()` — wrap in `BeginGroupBox(...)`, gate via `DynamicsSectionMatches(name)` so search filter works.
3. Add to `ResetAllDynamicsToDefaults()` in same file.
4. Per-frame method in [`CameraDynamics.cs`](CameraDynamics.cs). Use `ExpDecay` for smoothing; `AngleDelta` for wrap-aware yaw deltas.
5. Wire into `Update()` in the right slot. **After** `UpdateSensitivity` if you write to `currentHRotation` / `currentVRotation`.
6. `dotnet build`, hot-reload, test by feel.

## Adding a hotkey (recipe)

1. `int MyHotkey = (int)VirtualKey.X;` in `Configuration.cs`.
2. `HotkeyRow("My feature", ref ..., defaultVk: ...)` in the panel's Hotkeys section.
3. New `private static void UpdateMyHotkey()` in `InputHandler.cs` using `EdgePressed(vk)`.
4. Call from `InputHandler.Update()`.

## Loading the plugin in-game (the part I got wrong first try)

Dalamud's "Dev Plugin Locations" input wants the **full path to the .dll file**, NOT the folder. Older Dalamud versions accepted folder paths; current ones reject with "not a valid path to a potential Dev Plugin".

Correct entry to paste into `/xlsettings` → Experimental → Dev Plugin Locations:
```
C:\Users\akkis\AppData\Roaming\XIVLauncher\devPlugins\noWickyXIV\bin\Debug\noWickyXIV.dll
```
After adding + Save & Close, the plugin appears in `/xlplugins` → Installed Plugins (with a dev-build tag). Toggle on; `/nowickyxiv` opens the config.

## Things you'll keep forgetting (genuinely)

- The user's name/handle is **ebhemmanuel** (GitHub).
- Slash command is **`/nowickyxiv`** (lowercase, no underscores). Old `/cammy` was renamed at fork.
- `Common` is `Hypostasis.Game.Common`, NOT a custom local class. `Common.CameraManager` is its singleton accessor.
- `DalamudApi.X` (capital A in Api despite C# convention saying ApI is wrong) is Hypostasis's vendored static service container. Use it, don't roll your own.
- Cammy's class was renamed `Cammy` → `noWickyXIV`. **References to `noWickyXIV.Config` are normal**, not bugs. The class IS named with lowercase `n`.
- `Configuration` defaults itself to good identity behavior — adding new fields just means `dotnet build`; old config JSON files load with new defaults filled in.
- `gh repo create --private` was used; repo is private. **Don't make it public.**
- The plan we worked from is `C:\Users\akkis\.claude\plans\luminous-tumbling-crystal.md` — has the full Phase A-E breakdown plus Wicked reference line numbers.

## When in doubt about Dalamud APIs

The verbose docs are in [`docs/`](docs/) (8 files). Source of truth is [dalamud.dev](https://dalamud.dev). For deep reference:
- [`docs/04-camera-architecture.md`](docs/04-camera-architecture.md) — full per-frame architecture
- [`docs/03-services.md`](docs/03-services.md) — every Dalamud service we use + the rest available
- [`docs/08-patterns.md`](docs/08-patterns.md) — **canonical** Dalamud patterns: polling vs hooks, calling game code, signatures, RE workflow, AddonLifecycle / AddonEventManager. Read this before reaching for `IGameInteropProvider` directly.
- [`docs/05-debugging.md`](docs/05-debugging.md) — VS attach, exception codes, ProcDump
- [`docs/06-versions.md`](docs/06-versions.md) — API level + migration notes

**Important:** dalamud.dev's `/faq/*` pages (debug, reverse-engineering, getting-started, etc.) are explicitly labeled "Development FAQ (Legacy)". The canonical current docs live under `/plugin-development/*`. When researching, prefer the latter — `08-patterns.md` distills it.

But you'll usually be fine reading the source. Files are short. The math in `CameraDynamics.cs` is commented inline.

## Pending work, prioritized

1. **YawLag rewrite** — if user asks. Spring-on-offset, not exp-decay-on-target.
2. **`IPlayerState` migration** — `IClientState.LocalPlayer` is deprecated as of v14. Low priority (still works); update when Dalamud removes the compat layer.

That's it. Everything else in the project is functional or intentionally inert.

## Things NOT to work on

- **Auto-shoulder swap raycast** — FFXIV handles wall detection natively. The `TryProbeWall` stub looks like pending work but isn't; it was ported from Wicked where the host game lacks native wall collision. Don't wire `BGCollisionModule.Raycast` here. Don't suggest it.
- **InstantMode wiring** — the FFXIV camera struct doesn't expose smoothing-rate fields. Toggle is in the UI for symmetry with Wicked but does nothing. Don't try to "fix" it; there's nothing to fix.
- **YawLag using Wicked's math** — see "Don't violate" rule #2.

## Memory pointers (already saved across sessions)

- `feedback_no_deviation.md` — don't deviate from explicit asks
- `project_yawlag_broken.md` — YawLag whiplashes, current Wicked impl is broken

These auto-load. Don't re-save unless the rule changes.
