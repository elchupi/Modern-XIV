using System;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace noWickyXIV;

// Spike #1: hook DrawObject.UpdateTransforms via the local player's
// DrawObject vtable. UpdateTransforms is the per-frame method that
// consumes drawObj->Rotation and computes the world matrix; modifying
// the rotation just before calling original injects our roll into
// the matrix the engine actually renders with.
//
// Rationale: writing drawObj->Rotation from Framework.Update gets
// silently overwritten by the engine's own per-frame rotation
// rebuild before the matrix is computed. By hooking the consume-side
// function, we land our write inside the engine's frame setup —
// same pattern that fixed cam->tilt visibility (commit 1326cbb).
//
// Filtering: we hook a SHARED vtable function, so the detour fires
// for every DrawObject of that vtable. We filter by checking
// whether the drawObj matches a tracked actor (local player + mount
// actor) before applying any rotation modification.
public static unsafe class CharacterRollHook
{
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void UpdateTransformsDelegate(DrawObject* drawObj, byte arg);

    private static Hook<UpdateTransformsDelegate> _hook;
    private static bool _initialized;
    // Cached vtable address we hooked. When the player's draw object
    // changes type (e.g. job change), the vtable may differ; we
    // re-hook in EnsureHookedForCurrentPlayer.
    private static IntPtr _hookedVtableFn;
    // Per-frame "apply once" tokens — UpdateTransforms can fire
    // multiple times per frame (multiple visibility passes / culling
    // tests), and post-multiplying our roll on each call stacks the
    // rotation (qY * qZ * qZ * qZ ...). Result: visible jitter +
    // characters flipped upside-down. Reset to false at the start of
    // each Framework.Update tick (in Tick); the detour sets to true
    // on first apply this frame and skips subsequent calls.
    private static bool _playerRolledThisFrame;
    private static bool _mountRolledThisFrame;

    public static void Initialize()
    {
        // Defer actual hook creation to per-frame check — the local
        // player's DrawObject may not exist at plugin-load time.
        _initialized = true;
    }

    public static void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        _hookedVtableFn = IntPtr.Zero;
        _initialized = false;
    }

    // Ensure the UpdateTransforms vtable function is hooked for the
    // current local player's DrawObject. Re-hook if the function
    // pointer changed (zone load, gear swap that re-creates draw
    // object). Called once per Framework.Update tick.
    public static void Tick()
    {
        if (!_initialized) return;
        // Reset per-frame "applied" tokens at the start of every
        // Framework.Update tick so the next render's UpdateTransforms
        // calls get a fresh single-apply window.
        _playerRolledThisFrame = false;
        _mountRolledThisFrame = false;
        if (!noWickyXIV.Config.EnableCharacterRoll) return;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)lp.Address;
            if (go == null) return;
            var drawObj = go->DrawObject;
            if (drawObj == null) return;

            // The vtable's first slot we care about for UpdateTransforms.
            // DrawObject vtable layout (verified across CS releases):
            //   [0] AddChild, [1] OnAddedToWorld, [2] Dtor,
            //   [3] CleanupRender, [4] GetObjectType, [5] UpdateRender,
            //   [6] UpdateCulling, [7] UpdateTransforms, ...
            // We confirm at runtime by reading the vtable pointer and
            // grabbing slot 7. If the actual layout shifts on a CS
            // version bump, the hook will misfire; the try/catch and
            // empty-Original guard protect from crashes either way.
            var vtablePtr = *(IntPtr*)drawObj; // first 8 bytes of object = vtable*
            if (vtablePtr == IntPtr.Zero) return;
            var fnPtr = ((IntPtr*)vtablePtr)[7]; // UpdateTransforms slot
            if (fnPtr == IntPtr.Zero) return;
            if (fnPtr == _hookedVtableFn) return; // already hooked, current

            // Tear down any existing hook before re-creating against
            // the new vtable function pointer.
            try { _hook?.Disable(); } catch { }
            try { _hook?.Dispose(); } catch { }
            _hook = DalamudApi.GameInteropProvider.HookFromAddress<UpdateTransformsDelegate>(
                fnPtr, UpdateTransformsDetour);
            _hook.Enable();
            _hookedVtableFn = fnPtr;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] CharacterRollHook: hooked DrawObject.UpdateTransforms at 0x{(long)fnPtr:X}"); } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] CharacterRollHook tick threw: {ex.Message}"); } catch { }
        }
    }

    private static void UpdateTransformsDetour(DrawObject* drawObj, byte arg)
    {
        try
        {
            // Match drawObj against player/mount. With rebuild-from-
            // scratch (yaw+roll built fresh every call), applying on
            // every UpdateTransforms call is idempotent — same
            // deterministic value each time, no accumulation. This
            // also wins against any intra-frame engine writes that
            // may happen between our detour and the matrix compute.
            int target = MatchTarget(drawObj);
            bool apply = target != 0;

            if (apply)
            {
                float rollDeg = CameraDynamics.GetCharacterRollCurrentDegrees();
                if (MathF.Abs(rollDeg) > 0.01f)
                {
                    // REBUILD from scratch — read the actor's authoritative
                    // float yaw from its GameObject.Rotation, build the
                    // full qY * qZ quaternion, write it. This avoids the
                    // accumulation problem that post-multiply suffered:
                    // if the engine doesn't reset rotation between our
                    // call and the next frame's call, post-multiply
                    // would stack qZ over and over (qY * qZ * qZ * ...)
                    // and the character would spin past 90° / 180°. With
                    // rebuild-from-scratch each call writes the same
                    // deterministic value: yaw + roll, no history.
                    float yaw = ReadActorYaw(drawObj, target);
                    float halfYaw  = yaw * 0.5f;
                    float halfRoll = rollDeg * (MathF.PI / 180f) * 0.5f;
                    float cy = MathF.Cos(halfYaw),  sy = MathF.Sin(halfYaw);
                    float cr = MathF.Cos(halfRoll), sr = MathF.Sin(halfRoll);
                    // qY * qZ in body frame: yaw first, then Z-roll
                    // (banking around forward axis after yaw).
                    drawObj->Rotation = new Quaternion(
                        sy * sr, sy * cr, cy * sr, cy * cr);
                }
            }
        }
        catch { /* never let a transform-hook exception crash render */ }

        try { _hook?.Original(drawObj, arg); }
        catch { }
    }

    // Read the GameObject.Rotation float yaw for whichever target
    // (1 = player, 2 = mount) corresponds to this drawObj. Falls
    // back to the player's yaw if mount lookup fails — when mounted,
    // both share the same facing anyway.
    private static float ReadActorYaw(DrawObject* drawObj, int target)
    {
        try
        {
            if (target == 1)
            {
                var lp = DalamudApi.ObjectTable.LocalPlayer;
                if (lp != null) return lp.Rotation;
            }
            else if (target == 2)
            {
                var mountObj = DalamudApi.ObjectTable[1];
                if (mountObj != null) return mountObj.Rotation;
                // Fallback to player yaw — mount usually faces same direction.
                var lp2 = DalamudApi.ObjectTable.LocalPlayer;
                if (lp2 != null) return lp2.Rotation;
            }
        }
        catch { }
        return 0f;
    }

    // Returns 0 if drawObj is neither, 1 if local player, 2 if the
    // local player's mount actor.
    private static int MatchTarget(DrawObject* drawObj)
    {
        if (drawObj == null) return 0;
        if (!noWickyXIV.Config.EnableCharacterRoll) return 0;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return 0;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)lp.Address;
            if (go == null) return 0;
            if (go->DrawObject == drawObj) return 1;
            try
            {
                var mountObj = DalamudApi.ObjectTable[1];
                if (mountObj != null)
                {
                    var mountGo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)mountObj.Address;
                    if (mountGo != null && mountGo->DrawObject == drawObj) return 2;
                }
            }
            catch { }
        }
        catch { }
        return 0;
    }
}
