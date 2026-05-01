# Game Interaction Patterns

> Sources: [dalamud.dev/plugin-development/interaction](https://dalamud.dev/plugin-development/interaction/), [interaction/expanding-game-events](https://dalamud.dev/plugin-development/interaction/expanding-game-events/), [interaction/calling-game-code](https://dalamud.dev/plugin-development/interaction/calling-game-code/), [reverse-engineering](https://dalamud.dev/plugin-development/reverse-engineering/), [reverse-engineering/using-custom-cs](https://dalamud.dev/plugin-development/reverse-engineering/using-custom-cs/), [api/IGameInteropProvider](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IGameInteropProvider/)

This is the canonical-docs distillation of "how plugins interact with the game". It maps the Dalamud guidance to where we apply (or don't apply) it in this project.

## The three-tier preference order

Per the Interacting With The Game guide, **prefer this order for any new feature**:

1. **Dalamud APIs** — `Dalamud.Plugin.Services.*` (see [03-services.md](03-services.md)). Stable across API bumps. Use first when the feature is in scope of an existing service.
2. **FFXIVClientStructs** — community-maintained C# bindings to game structs/functions. Lets you treat the game as a library. Used heavily here via Hypostasis (`Common.CameraManager->worldCamera->X`, `Framework.Instance()->BGCollisionModule`, etc.).
3. **Raw memory / signature scans** — only when both above don't expose what you need. The escape hatch.

Most of our project sits in tier 2 (vendored Hypostasis wraps ClientStructs structs) with a few tier-1 calls (`DalamudApi.Framework`, `IClientState`, `IKeyState`).

## Polling vs hooking

Per Expanding On Game Events, two strategies for reacting to game state:

### Polling (what we do)

Watch state every frame; act on changes. **This is exclusively what `noWickyXIV` does** — `CameraDynamics.Update()` runs once per `Framework.Update`, reads camera struct fields, computes deltas, writes back.

Pros:
- Simple, no hook installation
- No risk of "if my code throws, the game crashes" — exceptions inside `Framework.Update` callbacks are caught by Dalamud
- Easy to disable a feature (just don't call its update method this frame)

Cons:
- Pays cost every frame even when nothing changes (mitigated here: our checks are cheap arithmetic, not scanning)
- Edge-detection requires explicit prev-state caching (see `_keyPrev` in `InputHandler.cs`)

### Hooking (what we don't do, but might)

Replace a game function with a detour that runs your code instead. Used when you need to **observe a specific event the game fires** (action use, animation start, addon open) or **mutate the game's behavior** at a precise call site.

Per the docs: hooks are *"highly invasive"*. Critical rules:

- **Wrap the entire detour body in try/catch.** Per dalamud.dev: *"If the code inside your hook throws an exception, you will most likely crash the game."*
- **Hooks are blocking.** The game waits for your detour to finish. Slow code = stuttering.
- **Multiple plugins may hook the same function.** Order is inverse-load (last enabled runs first). Don't modify args or skip the original call unless you're sure no other plugin needs the side effects.
- **Always Dispose hooks on plugin unload.** Otherwise you leak game-side state.

We don't currently install any hooks ourselves — Cammy's Game.cs uses Hypostasis's signature-injection attributes (`[HypostasisSignatureInjection]`, `[HypostasisInjection]`) which manage hook lifecycle for us under the hood. If we ever needed to hook directly, the API to use is `IGameInteropProvider.HookFromSignature` / `HookFromAddress` / `HookFromSymbol`.

### Decision rule for noWickyXIV

For camera-feel features: **always polling**. The camera is read every frame anyway; a hook would just intercept the same data path and add risk.

For event-driven features (e.g. "do X when player exits combat"): hooks make sense IF a Dalamud service doesn't already cover it. `ICondition` + polling its flags-changed events is usually enough.

## Calling game code

Per Calling The Game's Code:

```csharp
// Easiest path: use FFXIVClientStructs methods directly
var ps = PlayerState.Instance();
bool mentor = ps->IsMentor();
```

This is what `Game.cs` does throughout — `Framework.Instance()->BGCollisionModule`, `GameObjectManager.Instance()->Objects.IndexSorted[0]`, etc.

When ClientStructs **doesn't** expose a function, the four ways to call it (in order of cleanness):

1. **`[Signature("...")]` + `IGameInteropProvider.InitializeFromAttributes()`** — declarative, recommended
2. **`delegate* unmanaged<>` function pointers** — terse, no separate delegate declaration
3. **`[HypostasisSignatureInjection]`** — what our project uses (Hypostasis's flavor)
4. **Manual `ISigScanner.ScanText()`** — for cases where attributes don't fit

We have working examples of #3 in [`Game.cs`](../Game.cs):

```csharp
[HypostasisSignatureInjection("F3 0F 59 35 ?? ?? ?? ?? F3 0F 10 45 ??", Static = true, Required = true)]
private static float* foVDeltaPtr;
```

Hypostasis resolves the address at startup; we just read/write `*foVDeltaPtr`.

## Signatures vs addresses

Per the canonical RE doc:

> "A signature will only break if Square Enix changes the code the signature represents."

Addresses (raw offsets into `ffxiv_dx11.exe`) **change every patch**. Signatures (byte patterns matching the function's instructions) **survive most patches**. Always prefer signatures.

Wildcards (`??`) in a signature mark bytes that vary — typically immediates (string offsets, jump targets) that are stable in semantics but change with each build. Anything that's a fixed instruction byte stays as `0x..`.

If a signature breaks after a game patch:
- Fix is to find the function in the new binary (in IDA / Ghidra) and recompute a fresh signature.
- Most signatures we use are inherited from FFXIVClientStructs / Cammy / Hypostasis — they get patched upstream.
- For our own custom signatures (we have none currently), we'd need to maintain them.

## Reverse engineering tools

Per the canonical RE guide:

| Tool | What it's for |
|---|---|
| **Hex-Rays IDA Pro** | Industry-standard disassembler. Expensive |
| **Ghidra** | Free disassembler, "powerful and extensible, if not a little clunky" |
| **Binary Ninja** | Alternative disassembler; less common in the FFXIV community |
| **Cheat Engine** | Live memory editor / scanner |
| **x64dbg** | Live debugger |
| **ReClass.NET + XivReClassPlugin** | Live struct reconstruction; integrates with FFXIVClientStructs database |

Workflow: open `ffxiv_dx11.exe` in IDA/Ghidra → apply the FFXIVClientStructs script (auto-names known functions/structs) → identify the target by static analysis → verify behavior with Cheat Engine or x64dbg in a live game → compute a signature → use it.

## Custom FFXIVClientStructs (advanced)

Per [reverse-engineering/using-custom-cs](https://dalamud.dev/plugin-development/reverse-engineering/using-custom-cs/), if you need a struct/function that ClientStructs doesn't expose, you can:

1. Build a local fork of FFXIVClientStructs with your additions.
2. Override Dalamud's bundled version in your csproj:
   ```xml
   <Use_Dalamud_FFXIVClientStructs>false</Use_Dalamud_FFXIVClientStructs>
   <ProjectReference Include="...\FFXIVClientStructs.csproj" Private="True" />
   ```
3. Initialize the resolver manually in your plugin constructor.
4. **Ideally, contribute the additions back upstream** so users don't carry your custom build.

We don't currently need this. If we ever wired the BGCollisionModule raycast (we won't — see CLAUDE.md for why), and the Raycast signature wasn't in shipped ClientStructs, this would be the path. But: FFXIV handles wall collision natively, so this is moot.

## Addon-related services (currently unused but noted)

Two how-to guides describe services that we may use in future feature work:

### `IAddonLifecycle` ([how-to](https://dalamud.dev/plugin-development/how-tos/AddonLifecycle/))

Listen for game-UI lifecycle events (PreDraw / PostUpdate / PreRefresh / PostSetup) by addon name without manual hooking:

```csharp
AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "FieldMarker", OnPreDraw);
```

Useful if we ever want to react to FFXIV opening/closing a specific addon (e.g. "hide crosshair when ConfigSystem opens").

### `IAddonEventManager` ([how-to](https://dalamud.dev/plugin-development/how-tos/AddonEventManager/))

Add custom click/hover/etc. event handlers to existing game UI nodes. **Critical**: register handlers on `AtkResNode*`, never on `Component*` (registering a Component crashes the game).

Auto-cleans up when the addon closes (non-persistent) or when our plugin unloads. For persistent addons, retain the event handle and call `RemoveEvent()` manually.

We don't touch native UI in the camera fork, so neither service is wired. Mentioned here so future-me knows they exist before reaching for `[HypostasisSignatureInjection]` to hook addon code manually.

## Migration: `Dalamud.NET.Sdk`

Per [how-tos/v12-SDK-migration](https://dalamud.dev/plugin-development/how-tos/v12-SDK-migration/), the modern setup eliminates manual dependency declarations. Old plugins used `Microsoft.NET.Sdk` + `DalamudPackager` package + explicit references to `Dalamud.dll`, `ImGui.NET`, `Lumina`, `Newtonsoft.Json`, etc. The migration:

1. Change `<Project Sdk="Microsoft.NET.Sdk">` → `<Project Sdk="Dalamud.NET.Sdk/12.0.2">` (or current — we're on `15.0.0`).
2. Delete the `DalamudPackager` package reference.
3. Delete explicit `Dalamud.dll` / `ImGui.NET` / `Lumina` references — included in SDK.
4. Delete `<DalamudLibPath>` property group.
5. Keep only `<Version>` in the property group.

Our [`noWickyXIV.csproj`](../noWickyXIV.csproj) is already in the modern shape (Cammy was migrated upstream before we forked). If we ever pull Cammy commits that revert this, ignore them and stay on `Dalamud.NET.Sdk/15.0.0`.

## Hook safety summary (the part to memorize)

> "Hooking is _highly invasive_." — dalamud.dev

Rules, in order of importance:

1. **try/catch the entire detour body.** Always. Exception → game crash.
2. **Call the original at least once** (`hook.Original(args)`) unless you're explicitly cancelling. Other plugins may have chained on top.
3. **Don't mutate args.** They flow to other hooked plugins.
4. **Keep the detour cheap.** Synchronous + blocks the game thread.
5. **Dispose on plugin unload.** Hypostasis manages this for `[HypostasisSignatureInjection]`-installed hooks. For raw `IGameInteropProvider.HookFromSignature` you own the lifecycle.

Our project doesn't directly install hooks (Hypostasis attributes do it for us), but if we ever do, these rules apply.
