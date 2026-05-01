# noWickyXIV

Personal-use Dalamud plugin — a fork of [Cammy](https://github.com/UnknownX7/Cammy) with a dynamic-feel camera layer ported from the user's `noWickyTPS_MP` Wicked third-person camera mod.

**This is a personal fork.** Not for redistribution, not submitted to DalamudPluginsD17.

## What it adds over Cammy

- Roll tilt — camera banks into turns based on yaw velocity
- Pitch tilt — look-at offset shifts as pitch changes
- Position float — discreet float behind the player (velocity-driven offset)
- Swivel-on-Move — auto-center yaw behind player after a movement delay
- ADS-on-RMB — held right-mouse narrows FoV/zoom for aim-zoom feel
- Sensitivity multiplier + Y axis invert
- Crosshair overlay (configurable, fades, hides in cutscenes)
- Hotkeys: F6 (settings), V (crosshair), Q (manual shoulder swap), Ctrl+1..9 (preset slots), Ctrl/Alt+scroll (live height nudge)
- Auto-shoulder swap state machine (raycast probe is a stub pending API verification)

## Documentation

Full docs in [`docs/`](docs/):

- [`docs/README.md`](docs/README.md) — index + repo layout
- [`docs/01-getting-started.md`](docs/01-getting-started.md) — local dev environment
- [`docs/02-project-layout.md`](docs/02-project-layout.md) — csproj, manifest, file roles
- [`docs/03-services.md`](docs/03-services.md) — Dalamud services we use
- [`docs/04-camera-architecture.md`](docs/04-camera-architecture.md) — the per-frame writer (heart of the fork)
- [`docs/05-debugging.md`](docs/05-debugging.md) — VS attach, logs, crash analysis
- [`docs/06-versions.md`](docs/06-versions.md) — Dalamud API level + migration notes
- [`docs/07-contributing.md`](docs/07-contributing.md) — daily edit/build/push workflow

Distilled from [dalamud.dev](https://dalamud.dev). Read [04-camera-architecture.md](docs/04-camera-architecture.md) first if you only have time for one.

## Quick start

```bash
cd "%AppData%\XIVLauncher\devPlugins\noWickyXIV"
dotnet build noWickyXIV.csproj -c Debug
```

Then in-game: `/xlsettings → Experimental → Dev Plugin Locations` add the project folder, enable in `/xlplugins`, run `/nowickyxiv`.

## Credits

- Built on top of [UnknownX7/Cammy](https://github.com/UnknownX7/Cammy) (camera struct hooks, preset system, free-cam mode)
- Math + design references the user's own `noWickyTPS_MP` mod
