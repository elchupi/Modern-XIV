# Debugging

> Sources: [dalamud.dev/faq/debug](https://dalamud.dev/faq/debug), [dalamud.dev/plugin-development/reverse-engineering](https://dalamud.dev/plugin-development/reverse-engineering/) (canonical), [dalamud.dev/faq/reverse-engineering](https://dalamud.dev/faq/reverse-engineering/) (legacy FAQ)
>
> Note: the `/faq/*` pages are explicitly labeled "Development FAQ (Legacy)" on dalamud.dev. The canonical reverse-engineering content lives under `/plugin-development/reverse-engineering/` — see [08-patterns.md § Reverse engineering](08-patterns.md#reverse-engineering-tools) for the up-to-date treatment.

## Live log (your first stop)

```
%AppData%\XIVLauncher\dalamud.log
```

Plugin-specific log:
```
%AppData%\XIVLauncher\pluginLogs\noWickyXIV.log
```

When the game's running, `/xldev → Dalamud → Open Log` opens the live log viewer in-game with filtering.

Hypostasis writes verbose load-time info there. Look for `[noWickyXIV]` lines for our own logs (we currently emit one: the InstantMode no-op note).

## Attaching Visual Studio

Per dalamud.dev:

1. **Disable AntiDebug**: in-game `/xldev → Dalamud → Enable AntiDebug` (toggle so it's off).
2. **VS prep**: Tools → Get Tools and Features → ensure "Just-In-Time debugger" is installed. Debug → Options → Debugging → General → uncheck "Enable Just My Code".
3. **Attach**: Debug → Attach to Process.
   - Click **Select** in the "Attach to" section.
   - Check **Debug these code types** and select both **Managed (.NET core, .NET 5+)** and **Native**.
   - Pick `ffxiv_dx11.exe` from the process list.
4. **Suppress noisy CLR exceptions**: Debug → Windows → Exception Settings → uncheck "Common Language Runtime Exceptions" (FFXIV throws and catches a lot of internal CLR exceptions you don't want to break on).

After attaching, set breakpoints in your `.cs` files. If a breakpoint shows hollow (unverified), the build output isn't matching what's loaded — re-check `bin/Debug/noWickyXIV.dll` is the version Dalamud loaded (compare timestamps / reload the plugin).

## When the game crashes

If FFXIV exits suddenly:

1. **Check Dalamud log** — managed exceptions throw a stack trace into the log. Look for the last few lines.
2. **Check Windows Event Viewer** — Application log, source `Application Error`. Captures `Faulting module name`, `Exception code`, `Fault offset`. This reveals native-side crashes that don't appear in the Dalamud log.

   ```powershell
   Get-EventLog -LogName Application -Newest 10 |
     Where-Object {$_.Source -eq 'Application Error'} |
     Select-Object -First 1 -ExpandProperty Message
   ```

3. **For full call stacks** — capture a `.dmp` with ProcDump (`procdump64.exe -ma <pid> dumpfile.dmp` on the running process, or `-e` for crash-time capture). Open in WinDbg with `!analyze -v` for the analysis.

Common crash exception codes:
- `0xc0000005` — access violation. Usually a null/freed pointer dereference. In our codepaths: most often hitting `cam->X` after the game has invalidated the camera (check for `cam == null` before dereferencing).
- `0xc0000409` — stack buffer overrun (GS_COOKIE_ALTERED). Native-side fast-fail; bypasses LocalDumps. Need ProcDump with `-e` (no exception filter) to capture.
- `0xc00000fd` — stack overflow. Recursion in a hot path.

## Useful WinDbg commands

After loading a `.dmp`:
```
!analyze -v          ; full crash analysis
.ecxr                ; switch context to the exception
k                    ; current thread call stack
~* k                 ; all threads
lm                   ; loaded modules
```

## Hot reload caveats

Hot reload (Disable + Enable in dev plugins list) preserves nothing — every static field gets re-initialized from its default. State you want to survive a reload (e.g. `_lastPlayerPos`, `_floatOffset`) gets reset, which is fine for camera dynamics but might surprise if you add a feature with stateful animation.

If you're hot-reloading and seeing weird state, suspect: leftover hooks (Hypostasis tracks installed hooks; reload should dispose them — verify in the log), event subscriptions that didn't unsubscribe, or static caches that don't refresh from `Configuration` on reload.

## Reverse engineering (when you're stuck)

Full treatment is in [08-patterns.md § Reverse engineering tools](08-patterns.md#reverse-engineering-tools) — that's the canonical doc map. Quick version:

1. **Open `ffxiv_dx11.exe` in IDA Pro or Ghidra** (Ghidra is free).
2. **Apply the FFXIVClientStructs script** to populate community findings — this names known structs/functions automatically.
3. **Find the function by signature** (a unique byte pattern of instruction bytes). Use `ISigScanner.ScanText("AA BB ?? CC DD ...")` at runtime — `??` is a wildcard for bytes that vary. Per dalamud.dev: *"a signature will only break if Square Enix changes the code the signature represents."*
4. **Hook with `IGameInteropProvider.HookFromSignature`** (or, in our project, the Hypostasis `[HypostasisSignatureInjection]` attribute that does this declaratively).

Hypostasis examples in our code:
```csharp
// Game.cs
[HypostasisSignatureInjection("F3 0F 59 35 ?? ?? ?? ?? F3 0F 10 45 ??", Static = true, Required = true)]
private static float* foVDeltaPtr;
```

The signature points at the bytes around a memory load instruction; Hypostasis resolves the address at runtime and gives us the field pointer.

## Performance profiling

`/xldev → Plugins → Plugin Statistics` shows per-plugin frame-time impact. Our target: stay well under 1ms per frame in `Update()`. Current measured cost: ~0.05ms (negligible) — the math is simple float ops, no allocations, no scanning.

If a future feature pushes that up, suspect: per-frame `LINQ` (allocates), per-frame `ToString` formatting, per-frame `Dictionary` resizing, or running expensive scans without throttle.
