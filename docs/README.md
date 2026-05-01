# noWickyXIV — Project Documentation

Project-specific reference material distilled from the official [Dalamud developer documentation](https://dalamud.dev). Where applicable, each section quotes or summarizes from dalamud.dev and ties the concept to where it lives in **this** repo.

This is reading order:

1. [Getting Started](01-getting-started.md) — local dev environment, prereqs, build + load
2. [Project Layout](02-project-layout.md) — csproj, manifest, Dalamud.NET.Sdk, file roles
3. [Dalamud Services in this project](03-services.md) — which services we use and where
4. [Camera Dynamics Architecture](04-camera-architecture.md) — how the per-frame writer layers the dynamic feel
5. [Debugging](05-debugging.md) — Visual Studio attach, logs, common crash flows
6. [Dalamud API versions](06-versions.md) — what API level we target and why
7. [Contributing / Daily Workflow](07-contributing.md) — build → deploy → push, hot reload
8. [Game Interaction Patterns](08-patterns.md) — polling vs hooks, calling game code, signatures vs addresses, RE workflow, addon services

If you only read one file, read [`04-camera-architecture.md`](04-camera-architecture.md) — that's the heart of the fork.

---

## What this project is

noWickyXIV is a **personal-use fork of [Cammy](https://github.com/UnknownX7/Cammy)** that adds the dynamic-feel camera layer from the user's Wicked third-person camera mod (`noWickyTPS_MP`). It's a Dalamud plugin: a managed C# DLL loaded into FFXIV by [XIVLauncher](https://goatcorp.github.io/) + Dalamud at runtime.

Repo: [`ebhemmanuel/noWickyXIV`](https://github.com/ebhemmanuel/noWickyXIV) (private).

## Where the code lives

```
%AppData%\XIVLauncher\devPlugins\noWickyXIV\
├── noWickyXIV.cs            # IDalamudPlugin entrypoint (extends Hypostasis DalamudPlugin<>)
├── noWickyXIV.csproj        # Targets Dalamud.NET.Sdk/15.0.0
├── noWickyXIV.json          # Plugin manifest
├── noWickyXIV.sln
├── global.json              # .NET 10 SDK pin (rollForward: latestFeature)
├── Configuration.cs         # IPluginConfiguration + CameraConfigPreset (per-preset zoom/FoV/etc)
├── Game.cs                  # Hypostasis-injected detours over GameCamera vtable
├── PresetManager.cs         # Preset apply / cycle / per-frame condition-set check
├── PluginUI.cs              # ImGui config window (Presets, Camera Dynamics, Other Settings)
├── FreeCam.cs               # Free-cam mode (inherited from Cammy)
├── IPC.cs                   # Inter-plugin communication (QoLBar)
├── InputHandler.cs          # Hotkey edge-detection + Ctrl/Alt+scroll height
├── CameraDynamics.cs        # Per-frame writer: roll/pitch/float/swivel/ADS/sensitivity/auto-shoulder
├── Crosshair.cs             # ImGui foreground crosshair overlay
├── Hypostasis/              # Vendored helper library (struct definitions + plugin scaffold)
└── docs/                    # This directory
```

## What's implemented vs deferred

See [04-camera-architecture.md § Status](04-camera-architecture.md#feature-status) for a table. Quick summary:

- **Live**: roll tilt, pitch tilt, position float, ADS-on-RMB, sensitivity scaling + Y invert, swivel-on-move, crosshair, all hotkeys (F6 / V / Q / Ctrl+1..9), Ctrl/Alt+scroll height, auto-shoulder lerp state machine.
- **Stubbed**: auto-shoulder *raycast probe* (state machine ships, `TryProbeWall` returns false until BGCollisionModule API shape is verified).
- **Deferred**: yaw lag (Wicked impl whiplashes; needs spring-on-offset rewrite), instant mode (FFXIV camera struct doesn't expose smoothing-rate fields).
