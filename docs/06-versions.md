# Dalamud API Versions & Channels

> Source: [dalamud.dev/versions](https://dalamud.dev/versions/), [dalamud.dev/versions/v14](https://dalamud.dev/versions/v14/)

## What we target

Per [`noWickyXIV.csproj`](../noWickyXIV.csproj), we use `Dalamud.NET.Sdk/15.0.0` — the **Dalamud SDK package** version, which determines API surface and target framework. The SDK currently targets **API Level 14** (released August 2025 with Patch 7.4, requires .NET 10).

Per [`global.json`](../global.json), we pin .NET SDK 10.0.0+ with `rollForward: latestFeature` — meaning 10.0.x patches still satisfy.

> "Starting with v9, the API Level matches the major version number (e.g., Dalamud 10.0.0 has API Level 10). The current API Level is 14, introduced in version 14.0.0.0 for Patch 7.4 with .NET 10.0." — dalamud.dev/versions

## What changed in v14 that affects us

From [v14 release notes](https://dalamud.dev/versions/v14/), notable for this project:

| Change | Impact on noWickyXIV |
|---|---|
| Namespace consolidation: all service interfaces moved to `Dalamud.Plugin.Services` | Hypostasis already uses these namespaces; no changes needed |
| New `IPlayerState` service | We still use the deprecated `IClientState.LocalPlayer`. Migration is a future task — replace `DalamudApi.ClientState.LocalPlayer.Position` with the `IPlayerState` equivalent |
| New `IUnlockState` (experimental) | Not relevant for camera |
| New `IReliableFileStorage` | Not relevant — we don't do custom file I/O (Dalamud manages our config) |
| Dalamud.NET.Sdk 14+ supports manifest fields directly in `.csproj` (no JSON manifest required) | We still ship `noWickyXIV.json`; could be migrated to csproj `<PropertyGroup>` someday for marginal cleanup |
| `.NET 10` required, C# 14 features available | Already on .NET 10 |
| **SharpDX removed** — use `TerraFX.Interop.Windows` instead | We don't use SharpDX directly. Anything in vendored Hypostasis would be Hypostasis's concern |
| `IClientState.LocalPlayer` deprecated | Quiet warning today; flagged for migration |
| `LocalContentId` deprecated | We don't use it |

## Channels

Three channels are available (settable via XIVLauncher launcher → Dalamud Settings → Channel):

| Channel | What it is | Relevance |
|---|---|---|
| **Release** | Latest stable tagged release. Default for most users | Correct choice for daily play |
| **Canary** | Newer stable releases pushed to a small subset first | Useful only if you want bleeding-edge stability tests |
| **Staging (`stg`)** | Built from `master` HEAD, pre-release | **Only switch here if you're tracking unreleased Dalamud APIs** — your plugin must compile against the Dalamud version installed |

If you upgrade Dalamud channels, the SDK packages on NuGet might lag — keep `Dalamud.NET.Sdk/15.0.0` aligned with the actual Dalamud you're loading into. Mismatch usually surfaces as "API level X required, plugin expects Y" load errors.

## When Dalamud bumps

When a new API level lands, the workflow is:

1. Read the version's release notes (e.g. `dalamud.dev/versions/v15/`) for breaking changes.
2. Bump `Dalamud.NET.Sdk` reference in `.csproj` to match.
3. Check `global.json` if .NET runtime version changed.
4. Build — fix any compile errors from removed/renamed APIs.
5. Verify load — old plugin manifests with stale `DalamudApiLevel` get rejected; DalamudPackager should auto-set the new value at build.

For a personal-use plugin like this one, you might just stay on a working version until something forces an upgrade. Public-distribution plugins must keep up.

## How we know what's loaded

The Dalamud log header at game start prints:
```
[INF] Dalamud version 14.x.x.x, API level 14
[INF] Loaded plugin noWickyXIV (DalamudApiLevel: 14)
```

If the API level prints don't match between Dalamud and our plugin, the plugin won't load (or will warn). Check the log if anything's suspicious.
