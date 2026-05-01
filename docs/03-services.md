# Dalamud Services Used by noWickyXIV

> Source: [dalamud.dev/api/Dalamud.Plugin.Services](https://dalamud.dev/api/Dalamud.Plugin.Services/), [dalamud.dev/api/Dalamud.Plugin/Interfaces/IDalamudPluginInterface](https://dalamud.dev/api/Dalamud.Plugin/Interfaces/IDalamudPluginInterface/)

Dalamud exposes ~45 services in `Dalamud.Plugin.Services`. This project uses a small subset; the rest are listed at the bottom for context.

## Service injection model

Per dalamud.dev: *"All services are now accessed via interfaces rather than directly via the implementing type."* Two injection patterns:

1. **Constructor injection** — request services as `IDalamudPluginInterface`, `IFramework`, etc. parameters. Dalamud fills them.
2. **Static `[PluginService]` fields** — populate via `IDalamudPluginInterface.Inject(this)`. This is what Hypostasis's `DalamudApi` class does.

We use approach #2 via Hypostasis. Every `DalamudApi.X` call in our code is one of these injected services:

```csharp
// Hypostasis/Dalamud/DalamudApi.cs (vendored)
public static IClientState ClientState { get; private set; }
public static IFramework Framework { get; private set; }
public static IKeyState KeyState { get; private set; }
public static IPluginLog PluginLog { get; private set; }
// ... ~20 more
```

Services we touch from our code:

## `IFramework` — the per-frame heartbeat

> "This class represents the Framework of the native game client." — dalamud.dev

**Where we use it**:
- [`CameraDynamics.cs`](../CameraDynamics.cs) reads `DalamudApi.Framework.UpdateDelta.TotalSeconds` for the per-frame `dt`.
- The `Update()` loop in [`noWickyXIV.cs`](../noWickyXIV.cs) is invoked by `IFramework.Update` (subscribed by Hypostasis's base class).

**Threading rule** (from dalamud.dev): all our per-frame writes happen on the framework thread. Don't `Task.Wait()` from Update — it can deadlock. Use `Run()` for things that need to come back on the framework thread after `await`.

Properties used:
- `UpdateDelta` — `TimeSpan` since last update; we cap to 100ms to avoid spikes after pauses.
- (we don't use `LastUpdate`, `IsInFrameworkUpdateThread`, `IsFrameworkUnloading` but they're available)

## `IClientState` — player + login state

> "This class represents the state of the game client at the time of access."

**Where we use it**:
- [`CameraDynamics.UpdateSwivelOnMove`](../CameraDynamics.cs) reads `DalamudApi.ClientState.LocalPlayer.Position` to compute movement velocity.
- [`CameraDynamics.UpdatePositionFloat`](../CameraDynamics.cs) reads the same to drive the float-behind-player feel.
- [`Game.cs`](../Game.cs) (Cammy's original) uses `LocalPlayer.Address` to gate UpdateLookAtHeightOffset.

**Caveat per [v14 release notes](https://dalamud.dev/versions/v14/)**: `IClientState.LocalPlayer` is **deprecated** in v14 in favor of `IPlayerState`. We're still on the old API. When we migrate, replace the calls with the equivalent `IPlayerState` accessors.

## `IKeyState` — raw keyboard state

> "Wrapper around the game keystate buffer."

**Where we use it**:
- [`InputHandler.cs`](../InputHandler.cs) — `DalamudApi.KeyState[vk]` for hotkey detection (F6, V, Q, Ctrl+1..9).
- [`PluginUI.cs`](../PluginUI.cs) `ScanFirstPressedVk()` — the hotkey-rebind dialog scans the keyboard each frame to capture a new binding.

**Indexer semantics**: `KeyState[vk]` returns `bool` indicating "key is currently held". We edge-detect ourselves using a `Dictionary<int, bool> _keyPrev` cache.

## `IPluginLog` — opinionated logging

> "An opinionated service to handle logging for plugins."

**Where we use it**:
- [`CameraDynamics.UpdateInstantModeNote`](../CameraDynamics.cs) — `DalamudApi.PluginLog.Information(...)` for the once-per-session warning that InstantMode is a no-op.

**Convention** (per dalamud.dev): each plugin gets its own log file at `%AppData%\XIVLauncher\pluginLogs\noWickyXIV.log`. Levels: Verbose → Debug → Information → Warning → Error → Fatal. Use sparingly; heavy logging during gameplay impacts frame rate.

Hypostasis also exposes `DalamudApi.LogInfo(...)`, `LogWarning(...)`, etc. as wrappers — equivalent.

## `ICondition` — player state flags

> "Provides access to conditions (generally player state)."

**Where we use it**:
- [`Crosshair.cs ShouldHide()`](../Crosshair.cs) — checks `OccupiedInCutSceneEvent`, `WatchingCutscene`, `WatchingCutscene78`, `OccupiedInQuestEvent` to suppress the overlay during scripted content.

`ConditionFlag` enum is in `Dalamud.Game.ClientState.Conditions`. ~80 flags total, all bool.

## `ITargetManager` — Target / FocusTarget / SoftTarget

> "Get and set various kinds of targets for the player."

**Where we use it**:
- [`Game.GetCameraTargetDetour`](../Game.cs) (Cammy's original spectate code) — `DalamudApi.TargetManager.FocusTarget`, `SoftTarget`, `Target`. We didn't add new uses.

## `IObjectTable` — spawned game objects

> "This collection represents the currently spawned FFXIV game objects."

**Where we use it**:
- [`Game.cs`](../Game.cs) `GameObjectManager.Instance()->Objects.IndexSorted[0].Value` for the local player address (Cammy's original code path uses this for fallback). Also `DalamudApi.ObjectTable.LocalPlayer?.Address` for hookpoint gating.

## `IGameGui` — addon visibility

**Where we use it**:
- [`FreeCam.cs`](../FreeCam.cs) `DalamudApi.GameGui.GetAddonByName("Title", 1)` to detect the title screen for free-cam menu mode (Cammy's original code).

## `IDalamudPluginInterface` — the plugin's own facade

> See dalamud.dev's [interface reference](https://dalamud.dev/api/Dalamud.Plugin/Interfaces/IDalamudPluginInterface/) for the full surface (~50 members).

What we use indirectly via Hypostasis's `DalamudPlugin<TConfig>` base:

- `SavePluginConfig(IPluginConfiguration)` — `noWickyXIV.Config.Save()` calls this.
- `GetPluginConfig()` — called once at boot to deserialize the JSON.
- `UiBuilder` — Hypostasis subscribes our `Draw()` method to its draw event.
- `IsDevMenuOpen` — used by some Cammy paths to gate dev features.

## Services available but unused

For reference (full list at [dalamud.dev/api/Dalamud.Plugin.Services](https://dalamud.dev/api/Dalamud.Plugin.Services/)):

| Service | One-liner |
|---|---|
| `IAddonEventManager` | Listen for ImGui-style events on game addons |
| `IAddonLifecycle` | Hook addon lifecycle (PreSetup/PostUpdate/etc.) |
| `IAetheryteList` | Teleport-window aetheryte data |
| `IAgentLifecycle` | Hook game-agent lifecycle |
| `IBuddyList` | Squadron / trust party members |
| `IChatGui` | Send / receive chat messages |
| `ICommandManager` | Register slash commands (we use it indirectly via Hypostasis's `[PluginCommand]` attribute) |
| `IConsole` | Register `/xldev`-style console commands + variables |
| `IContextMenu` | Add entries to right-click menus |
| `IDataManager` | Lumina-backed game data lookup |
| `IDtrBar` | Server info bar widgets |
| `IDutyState` | Duty enter/leave events |
| `IFateTable` | Active FATE list |
| `IFlyTextGui` | Damage-number-style overlays |
| `IGameConfig` | FFXIV's own settings (`/systemconfig`-equivalent) |
| `IGameInteropProvider` | Create function hooks (we don't use directly; Hypostasis abstracts it) |
| `IGameInventory` | Inventory change events |
| `IGameLifecycle` | Cancellation tokens for game events |
| `IGamepadState` | Gamepad axes + buttons (we DON'T use; sensitivity multiplier is input-source-agnostic) |
| `IJobGauges` | Job-specific gauge state (Astro cards, MCH heat, etc.) |
| `IMarketBoard` | Market board events |
| `INamePlateGui` | Modify name-plate rendering |
| `INotificationManager` | Toast-style notifications |
| `IPartyFinderGui` | Party Finder window state |
| `IPartyList` | Party / alliance member list |
| `IPlayerState` | (v14+) replaces `IClientState.LocalPlayer` |
| `IReliableFileStorage` | (v14+) crash-resilient file I/O |
| `ISelfTestRegistry` | Plugin self-tests |
| `ISeStringEvaluator` | Evaluate game string macros |
| `ISigScanner` | Find functions by signature |
| `ITextureProvider` / `ITextureReadbackProvider` / `ITextureSubstitutionProvider` | Texture load + ImGui rendering + replacement |
| `ITitleScreenMenu` | Title-screen menu items |
| `IToastGui` | Native toast popups |
| `IUnlockState` | (v14+) check unlock state for content |
