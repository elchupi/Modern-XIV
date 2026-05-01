# Getting Started

> Source: [dalamud.dev/faq/getting-started](https://dalamud.dev/faq/getting-started/), [dalamud.dev/plugin-development/getting-started](https://dalamud.dev/plugin-development/getting-started)

## Prerequisites

| Component | Required version | Status in this project |
|---|---|---|
| **XIVLauncher** | Latest | Already installed at `%LocalAppData%\XIVLauncher\` |
| **Dalamud dev SDK** | Auto-pulled by XIVLauncher | Present at `%AppData%\XIVLauncher\addon\Hooks\dev\` |
| **.NET SDK** | 10.0.x | Pinned to 10.0.0+ via [`global.json`](../global.json) (`rollForward: latestFeature`) |
| **IDE** | Visual Studio 2022/2026 or JetBrains Rider | Any C# editor with `.csproj` support works |
| **Game launched once with Dalamud enabled** | Required to materialize the dev SDK | Verified — `Hooks/dev/Dalamud.Boot.dll` exists |

Dalamud v14 (current) requires .NET 10 specifically per [the v14 release notes](https://dalamud.dev/versions/v14/). Older SDKs will fail with TFM mismatch errors.

## Repo location and dev-plugin loading

The project lives **directly inside** Dalamud's default dev-plugin scan folder:

```
C:\Users\akkis\AppData\Roaming\XIVLauncher\devPlugins\noWickyXIV\
```

Build output goes to `bin/Debug/noWickyXIV.dll` (+ `noWickyXIV.json` manifest + `noWickyXIV.deps.json`). Dalamud auto-detects this layout — no copy step needed.

To make Dalamud actually load it:

1. Launch FFXIV via XIVLauncher with Dalamud enabled.
2. In-game: `/xlsettings` → **Experimental** tab.
3. Under **Dev Plugin Locations**, add either:
   - The build output: `C:\Users\akkis\AppData\Roaming\XIVLauncher\devPlugins\noWickyXIV\bin\Debug\`
   - Or the project root and let Dalamud recurse: `C:\Users\akkis\AppData\Roaming\XIVLauncher\devPlugins\noWickyXIV\`
4. **Save & Close**. Then **Settings → Plugins → Dev Plugins** tab. `noWickyXIV` should appear; toggle it on.

`/nowickyxiv` opens the config window once it loads.

## First build

```bash
cd "C:/Users/akkis/AppData/Roaming/XIVLauncher/devPlugins/noWickyXIV"
dotnet build noWickyXIV.csproj -c Debug
```

Expected: `Build succeeded. 0 Errors.` Warnings (~10) are upstream from Cammy/Hypostasis (unused fields in IL-injected structs); ignore.

## Hot reload

Dalamud's dev-plugin mode supports hot reloading. Workflow:

1. Make changes in source.
2. `dotnet build` — output overwrites the loaded DLL (Dalamud allows this since the file is mapped, not locked).
3. In-game: `/xlplugins` → find noWickyXIV → click **Disable** then **Enable**, OR use the `Reload` button in the dev plugin list.

For changes that reset state (config schema additions, new detours), restart the game instead. Hot reload is best for UI tweaks and per-frame logic.

## /xldev menu

The dev menu offers handy shortcuts:

- `/xldev` toggles the dev menu bar at the top of the game viewport.
- **Dalamud → Open Log** — view the live Dalamud + plugin log
- **Dalamud → Enable AntiDebug** — toggle FFXIV's antidebug protection (must be **disabled** before attaching a debugger)
- **Plugins → Plugin Statistics** — per-plugin frame-time impact (reference for performance tuning)

## Troubleshooting first-time setup

| Symptom | Likely cause | Fix |
|---|---|---|
| `dotnet build` fails: "TFM net10.0 not supported" | .NET 10 SDK missing | `winget install Microsoft.DotNet.SDK.10` |
| Plugin doesn't appear in Dev Plugins list | Path not added to Dev Plugin Locations | `/xlsettings → Experimental` |
| Plugin appears but won't enable | Manifest mismatch (wrong `InternalName`) | Check `noWickyXIV.json` `InternalName` matches `noWickyXIV.dll` filename |
| Loads but `/nowickyxiv` does nothing | Old `/cammy` still bound | Reload plugin to re-register the slash command |
| Build copies bin output but game has stale DLL | Dalamud cached the assembly | `/xlplugins → Disable → Enable` |
