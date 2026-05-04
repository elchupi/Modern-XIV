# 09 — VFX integration pitfalls (read before touching `VfxBridge.cs` or `JobAura.cs`)

This is the hard-won list of things that crashed the game while wiring `ActorVfxCreate` / `ActorVfxRemove` for the SAM Job Aura layer system. Every item below cost at least one client crash to discover. Re-read before adding any new vfx feature.

## 1. Do NOT hook `ActorVfxRemove` if VFXEditor might be installed

**Symptom:** Instant CLR `AccessViolationException` at address `0x12345679` (Dalamud's freed-memory canary) on client launch. Stack trace shows:

```
at System.Runtime.InteropServices.Marshal.ReadInt64(IntPtr, Int32)
at VfxEditor.Interop.ResourceLoader..ctor()
at VfxEditor.Plugin..ctor(...)
```

**Cause:** VFXEditor's `ResourceLoader..ctor` walks function prologue bytes via `Marshal.ReadIntPtr( base + Marshal.ReadInt32(base) + 4 )` to follow `rel32` displacements (the standard "decode this `MOV rax,[rip+disp32]` and dereference its target" pattern). When our `Dalamud.Hooking.Hook<>.Enable()` patches the prologue with a `JMP rel32` to our trampoline, VFXEditor's read decodes our trampoline displacement instead of the original target, dereferences a bogus address, and AVs.

**Why it's deterministic:** `noWickyXIV` is a `devPlugin`. `DevPluginLoadLocations` is processed before `installedPlugins`, so our hook is always installed before VFXEditor's ctor runs.

**Verified via:** `dalamud_appcrash_20260503_045114_700_128148.dmp`, two consecutive client restart attempts.

**Resolution:** Don't hook `ActorVfxRemove`. Resolve the function pointer via sig-scan, call it directly. Use a deterministic owner-side state machine to guarantee the handle is still live when you call Remove (see item 2).

**Reference:** [VFXEditor `ResourceLoader.Vfx.cs`](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/ResourceLoader.Vfx.cs).

---

## 2. Do NOT call `ActorVfxRemove` reactively from per-frame loops

**Symptom:** Sporadic CLR crash at `0x12345679` after some duration of normal play. Often during rapid trigger oscillation (Kenki bouncing across the 33% boundary repeatedly).

**Cause:** Use-after-free. The engine frees actor-anchored vfx on its own schedule (zone load, draw-object reload, character teardown). Reactive Remove paths — calling Remove after a `vfx->ParentObject` mismatch, after a kill-timer expiry from sustained-state polling, after a fade-out completing many frames after the trigger went false — all give the engine multiple frames between our Create and our Remove during which it can legitimately free the vfx without us noticing.

**Resolution:** Drive Remove from the deterministic owner-side trigger transition (the user-driven falling edge, plus plugin Dispose). Pre-emptively NULL the cached handle WITHOUT calling Remove on the three engine-side free events:

1. Zone change (compare to `_lastZone`)
2. Local player address change (compare to `_lastPlayerAddr` — catches logout/login, character swap, mount/dismount, gear/glamour reload)
3. Plugin reload (handled implicitly by Dispose iterating handles for Remove first, BEFORE the engine has a chance to free)

See `JobAura.cs` `DropAllHandlesNoRemove()` and the zone/player-addr wipe blocks at the top of the layer engine.

---

## 3. Do NOT trust `Scale = 0` to hide a particle-emitter avfx

**Symptom:** Setting `vfx->Scale = (0, 0, 0)` makes no visual change. Particles continue emitting at the actor.

**Cause:** Particle systems, beam emitters, and light emitters in actor-anchored avfx have internal sizing or absolute world-space coordinates and ignore the parent VfxObject scale. Confirmed on `vfx/action/ab_2kt008/eff/ab_2kt008c0t.avfx`.

**Resolution:** Use `Scale` ONLY for cosmetic fade-in/fade-out (e.g., 600ms cubic ramp before Remove). Never trust it as a hide. Real stop = real Remove (see item 2).

---

## 4. Do NOT trust position writes to hide actor-anchored avfx

**Symptom:** Writing `vfx->Position = (0, -100000, 0)` once produces no visible effect. Writing per-frame produces flicker but particles still appear at the actor.

**Cause:** The engine re-anchors actor-vfx position every tick from the actor's transform. Even per-frame writes from `Framework.Update` race against the engine's transform pass. Particles emitted in any given frame still spawn at the actor location regardless of where we put the emitter that frame.

**Resolution:** Same as item 3 — position writes are useful for offsets while visible (per-frame writes overcome recompute), but useless as a hide.

---

## 5. Do NOT use `vfx->ParentObject == player->DrawObject` as a liveness check

**Symptom:** Healthy spawned handles get reported as invalid ~13ms after every spawn, causing infinite invalidate→drop→respawn loops. Screen strobes the avfx forever.

**Cause:** The relationship between `VfxObject.ParentObject` and the player's `DrawObject` is more complex than direct equality. Actor-anchored vfx likely parent to a character submesh node or weapon node, not the top-level draw object. The check is wrong even for healthy, freshly-spawned vfx.

**Resolution:** No reliable in-struct liveness check exists. The struct is sparse (no `Active`/`Enabled`/`Lifetime`/`RenderFlags` fields — see [`VfxObject.cs`](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/VfxObject.cs)). Use the deterministic owner-side state machine from item 2 instead.

---

## 6. `bin/Release` is NOT what Dalamud loads — check `dalamudConfig.json`

**Symptom:** Source edits + `dotnet build -c Release` succeeds, plugin reload shows zero behavior change. Spent 10+ minutes confused.

**Cause:** Dalamud's `dalamudConfig.json` `DevPluginLoadLocations` pins the exact DLL path. For this project it's `bin\Debug\noWickyXIV.dll`. Building Release puts the new bytes somewhere Dalamud never looks.

**Check:**

```
C:\Users\akkis\AppData\Roaming\XIVLauncher\dalamudConfig.json
  → "DevPluginLoadLocations" → "Path"
```

**Resolution:** Always build the config that matches `Path`. For this project: `dotnet build ... -c Debug`.

---

## 7. `ActorVfxCreate` does NOT play the avfx's bundled `.scd` audio

**Symptom:** User says "you copied the path from VFXEditor, why isn't the sound playing?"

**Cause:** The `.scd` audio referenced inside an `.avfx` is dispatched by the action animation timeline (server-side action effect dispatch, then client-side animation event), NOT by the raw vfx spawn. `ActorVfxCreate` produces visuals only. Verified via VFXEditor source — `PlaySoundSig` and `InitSoundSig` are separate sigs, called independently of vfx spawn.

**Resolution:** To play the bundled sound, resolve `PlaySound` separately (sig from VFXEditor `Constants.cs`: `PlaySoundSig = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? FE C2"` — note this is a CALL site, you'll need to resolve through the displacement to get the function start). Same applies to screenshakes — they're dispatched by the animation system, not part of avfx at all. VFXEditor doesn't reference any shake sig.

---

## 8. `ActorVfxRemove` second arg MUST be `(char)1`

**Symptom:** Crashes when calling Remove with `0` or `(ushort)0` even when the handle is verified live.

**Cause:** `(char)1` is "full cleanup" mode per VFXEditor's `ActorVfx.Remove()`:

```csharp
public override void Remove() {
    Plugin.ResourceLoader.ActorVfxRemove( Vfx, ( char )1 );
}
```

Other values trigger different code paths in the engine that may not handle our usage pattern.

**Resolution:** Always `(char)1`. Delegate signature: `delegate IntPtr ActorVfxRemoveDelegate(IntPtr vfx, byte a2);` — pass `1`.

---

## 9. Wrong `ActorVfxCreate` delegate signature crashes

**Symptom:** Garbage handle returned, then crashes when we try to use it.

**Cause:** Win64 calling convention puts the first four float args in `XMM0–XMM3`, not the integer registers. If the delegate declares the 4th parameter as `int unk1` instead of `float a4`, the function reads register garbage from `RCX` (or whatever int register the marshaller chose), interprets it as a float, and the resulting computation is meaningless.

**Resolution:** Match VFXEditor's verified signature exactly:

```csharp
private delegate IntPtr ActorVfxCreateDelegate(
    byte* path, GameObject* caster, GameObject* target,
    float a4, ushort a5, ushort a6, ushort a7);
```

The `float a4` is critical. Don't change it.

---

## 11. "Proactive" Remove on a deterministic falling-edge ALSO crashes

**Symptom:** Native `AccessViolationException` at `ffxiv_dx11.exe+39188C` (engine's ActorVfxRemove path), unrecoverable. Stack:

```
at noWickyXIV.VfxBridge.Remove(IntPtr handle)
at noWickyXIV.JobAura.Update()
```

Crashes after 600ms cubic fade-out completes and we call `_remove(handle, 1)`.

**Cause:** Earlier theory was "engine only frees actor-vfx on zone change / draw-object reload, so calling Remove on a falling-edge trigger after a 600ms fade is safe". **Wrong.** The engine reclaims actor-vfx on many mid-zone events: gear/glamour swap, weapon sheath/draw, animation state changes, mount/dismount, possibly job swap, possibly entering certain content. 600ms is plenty of window for one of these to free our vfx between Create and Remove.

Native AVs from interop calls do NOT propagate to managed try/catch in .NET 10. Defensive `try { _remove(...); } catch { }` does nothing.

**Resolution:** There is no safe way to call `ActorVfxRemove` without VFXEditor's hook-based liveness sync. With VFXEditor installed (which precludes our own hook — see item 1), the only crash-free option is:

- Never call Remove. Accept that layers stay alive until zone change / character teardown.

For real "stop on falling edge" behavior, the path forward is to pivot the spawn from `ActorVfxCreate` to `StaticVfxCreate`. Static vfx are world-space (we own the transform completely — write player position per frame to fake actor-attachment), and `StaticVfxRemove` is reliable because the engine doesn't autonomously reclaim them. Tracked separately as a future task.

Verified via: `dalamud_appcrash_20260503_051841_405_97996.dmp`.

---

## 10. Don't sig-scan the same function pattern twice

**Symptom:** Two different addresses returned for the same sig in different code paths. Inconsistent crash behavior.

**Cause:** Multiple sig-scans of the same pattern early in plugin life can overlap with Dalamud's own scanning passes and produce different addresses (likely because the runtime patches some bytes — JIT stubs, GC barriers — between scans).

**Resolution:** Resolve once at `Initialize`. Cache the address and the delegate. Never re-scan.

---

## Quick reference — the safe pattern

```csharp
// At init
var addrCreate = scanner.ScanText(SIG_CREATE);
var addrRemove = scanner.ScanText(SIG_REMOVE);
_create = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(addrCreate);
_remove = Marshal.GetDelegateForFunctionPointer<ActorVfxRemoveDelegate>(addrRemove);
// NO HOOK.

// Per frame in JobAura.Update, BEFORE accessing any cached handle:
if (curZone != _lastZone) DropAllHandlesNoRemove();
if (curPlayerAddr != _lastPlayerAddr) DropAllHandlesNoRemove();

// On user-driven falling edge:
//   start cubic fade (writes Scale per frame, cosmetic only)
//   when fade reaches ~0:
//     VfxBridge.Remove(handle);   // safe — engine hasn't had a chance to free
//     drop from all dicts

// On plugin Dispose:
//   foreach handle: VfxBridge.Remove(handle); // safe — game still running
```

## Sources

- [VFXEditor `Interop/Structs/Vfx/ActorVfx.cs`](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Structs/Vfx/ActorVfx.cs)
- [VFXEditor `Interop/Structs/Vfx/BaseVfx.cs`](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Structs/Vfx/BaseVfx.cs)
- [VFXEditor `Spawn/VfxSpawn.cs`](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Spawn/VfxSpawn.cs)
- [VFXEditor `Interop/ResourceLoader.Vfx.cs`](https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/ResourceLoader.Vfx.cs)
- [FFXIVClientStructs `VfxObject.cs`](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/VfxObject.cs)
- [xiv.dev AVFX format reference](https://xiv.dev/game-data/visual-effects/avfx-files)
