# Project Layout & Configuration

> Source: [dalamud.dev/plugin-development/project-layout](https://dalamud.dev/plugin-development/project-layout), [dalamud.dev/plugin-development/plugin-metadata](https://dalamud.dev/plugin-development/plugin-metadata)

## The csproj

[`noWickyXIV.csproj`](../noWickyXIV.csproj) — minimal, all heavy lifting done by `Dalamud.NET.Sdk`:

```xml
<Project Sdk="Dalamud.NET.Sdk/15.0.0">
    <PropertyGroup>
        <Version>0.1.0.0</Version>
        <Nullable>disable</Nullable>
        <AppendPlatformToOutputPath>false</AppendPlatformToOutputPath>
    </PropertyGroup>
</Project>
```

What `Dalamud.NET.Sdk` does for you (per dalamud.dev):

- Sets `<TargetFramework>` to whatever the active Dalamud requires (currently `net10.0`).
- Adds package references to `DalamudPackager`, FFXIVClientStructs, and the Dalamud assemblies in `%AppData%\XIVLauncher\addon\Hooks\dev\`.
- Auto-injects manifest fields: `InternalName` (= AssemblyName), `AssemblyVersion`, `DalamudApiLevel`.
- Copies the manifest JSON next to the DLL on build.
- Configures output path so dev-plugin loading works without extra effort.

**If you want to add a NuGet dependency**: add `<ItemGroup><PackageReference Include="..." /></ItemGroup>` like a normal csproj. The SDK's defaults compose with explicit additions.

## Internal name (don't change this)

> "Once set this value **may not be changed**, so it's important to choose a name that you will be happy with." — dalamud.dev/plugin-development/project-layout

`InternalName` for this project is **`noWickyXIV`** (= AssemblyName = `.csproj` filename without extension). It's used for:
- The DLL filename
- The plugin's config directory: `%AppData%\XIVLauncher\pluginConfigs\noWickyXIV\`
- Log entries (e.g. `[noWickyXIV] ...` in Dalamud log)
- The slash command we registered (`/nowickyxiv`)

## Manifest — [`noWickyXIV.json`](../noWickyXIV.json)

Dalamud loads a JSON or YAML manifest alongside the DLL.

### Required fields (per dalamud.dev)

- `Name` — display name in the plugin list
- `Author` — your handle
- `Description` — long description
- `Punchline` — one-line tagline shown in the install card

### Optional fields (we use)

| Field | Our value | Purpose |
|---|---|---|
| `RepoUrl` | `https://github.com/ebhemmanuel/noWickyXIV` | Linked from plugin info |
| `CategoryTags` | `["utility"]` | Plugin Installer category filter |
| `Changelog` | "Initial fork from Cammy." | Shown in the installer when version bumps |

### Optional fields (we don't use, but available)

- `ApplicableVersion` — gate by FFXIV patch version
- `Tags` — searchable keywords beyond category
- `LoadRequiredState` — `0=load on game start, 1=load only when logged in`
- `LoadSync` — load synchronously vs async
- `CanUnloadAsync` — allow unloading on a thread other than the game thread
- `LoadPriority` — higher loads first when multiple plugins compete
- `ImageUrls` / `IconUrl` — plugin install card art (would only matter for public release)
- `AcceptsFeedback` / `FeedbackMessage` — in-game feedback channel

### Auto-populated (DON'T put these in the JSON)

`AssemblyVersion`, `InternalName`, `DalamudApiLevel` — DalamudPackager fills them at build. We had `AssemblyVersion: 0.1.0.0` in the manifest from the original Cammy fork; harmless but redundant since the csproj `<Version>` is canonical.

## File roles

```
noWickyXIV.cs           # IDalamudPlugin entrypoint. Inherits Hypostasis's
                        # DalamudPlugin<Configuration> base — a thin wrapper that
                        # auto-injects services via DalamudApi static fields and
                        # provides Update()/Draw() lifecycle overrides.
                        #
                        # Our Update() chain:
                        #   FreeCam.Update() → PresetManager.Update()
                        #   → InputHandler.Update() → CameraDynamics.Update()
                        #
                        # Our Draw() chain:
                        #   FreeCam.Draw() → PluginUI.Draw() → Crosshair.Draw()

Configuration.cs        # IPluginConfiguration. Two classes:
                        #   - CameraConfigPreset: per-preset camera config (zoom, FoV,
                        #     pitch limits, height/side offset, view bob mode, etc.).
                        #     Cammy's original.
                        #   - Configuration: top-level. Holds the preset list +
                        #     all GLOBAL config (dynamic-feel knobs, hotkeys, sensitivity,
                        #     crosshair, ADS, etc.). Persisted as JSON.

Game.cs                 # Hypostasis-injected detours over GameCamera vtable methods.
                        # Functions hooked: SetCameraLookAt, GetCameraPosition,
                        # GetCameraTarget, CanChangePerspective, GetZoomDelta, plus
                        # GameCamera.UpdateLookAtHeightOffset (static fn). Adds our
                        # PositionFloat + GlobalHeightOffset on top of preset.HeightOffset
                        # in GetCameraPositionDetour.

PresetManager.cs        # Apply/load/cycle camera presets. Reads QoL Bar condition
                        # sets via IPC.cs and auto-swaps when conditions change.

PluginUI.cs             # ImGui config window. Three tabs: Presets, Camera Dynamics
                        # (the new one), Other Settings. The Camera Dynamics tab has
                        # search/reset-all controls + Wicked-style grouped sections.

InputHandler.cs         # Per-frame keybind reader. Edge-detects via cached prev state.
                        # Handles: Ctrl/Alt+scroll height, F6 (settings), V (crosshair),
                        # Q (shoulder swap, default unbound), Ctrl+1..9 (preset slots).

CameraDynamics.cs       # Per-frame writer for the dynamic-feel layer. Order matters
                        # (sensitivity first, then dynamics writes). See 04-camera-architecture.md.

Crosshair.cs            # ImGui foreground draw-list overlay (cross arms + center dot)
                        # with per-frame fade and cutscene/quest-event hide gating.

FreeCam.cs              # Cammy's free-cam mode. Untouched in our fork.

IPC.cs                  # Inter-plugin communication, specifically QoLBar integration
                        # for condition-set-driven preset auto-swap. Cammy's original.
```

## Configuration storage

Dalamud writes the config to:
```
%AppData%\XIVLauncher\pluginConfigs\noWickyXIV\noWickyXIV.json
```

We never touch this path directly — `noWickyXIV.Config.Save()` (provided by Hypostasis's `DalamudPlugin<>` base) writes through `IDalamudPluginInterface.SavePluginConfig()`. On next plugin load the same interface's `GetPluginConfig()` returns the deserialized `Configuration`.

**Migration**: when adding new fields to `Configuration` (we did 30+ in this fork), JSON deserialization defaults missing fields to their C# default-value initializer. So adding `public bool EnableRollTilt = true;` to a fresh-loaded config that doesn't have that key means it loads as `true` — no migration code needed.
