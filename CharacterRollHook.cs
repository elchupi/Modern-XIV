using System;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace noWickyXIV;

// Hooks DrawObject.UpdateTransforms via vtable[7] on the local
// player's draw object. Writes the roll quaternion late in the
// engine's frame setup — writes from Framework.Update get clobbered
// when the engine IS rebuilding rotations, but for stationary actors
// the engine skips that rebuild and our Tick write IS the only one
// that runs (which is why the bank used to stay stuck at an angle
// after motion stopped).
//
// Two write points:
//   - UpdateTransforms detour (active animation: engine rebuilds rotation,
//     we write our value over its rebuild within the same frame).
//   - Framework.Update (Tick) ForceWriteRotation: covers stationary-actor
//     gap where UpdateTransforms isn't called and the last detour write
//     would otherwise persist past _charRollCurrent's decay to 0.
//
// Filter target by drawObj POINTER (vtable is shared, fires for every
// actor) — match player + mount actor (slot[1] when present) so the
// bike body itself banks alongside the rider.
public static unsafe class CharacterRollHook
{
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void UpdateTransformsDelegate(DrawObject* drawObj, byte arg);

    private static Hook<UpdateTransformsDelegate> _hook;
    private static bool _initialized;
    private static IntPtr _hookedVtableFn;

    // Stationary-actor write state — last quaternion we wrote per
    // target so ForceWriteRotation is idempotent (no churn at zero).
    private static Quaternion _lastPlayerWritten;
    private static Quaternion _lastMountWritten;
    private static bool _lastPlayerInit;
    private static bool _lastMountInit;

    public static void Initialize() => _initialized = true;

    public static void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        _hookedVtableFn = IntPtr.Zero;
        _initialized = false;
        _lastPlayerInit = false;
        _lastMountInit = false;
    }

    public static void Tick()
    {
        if (!_initialized) return;
        if (!noWickyXIV.Config.EnableCharacterRoll) return;
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)lp.Address;
            if (go == null) return;
            var drawObj = go->DrawObject;
            if (drawObj == null) return;

            // Vtable hook install (re-resolve only when fn pointer changes).
            var vtablePtr = *(IntPtr*)drawObj;
            if (vtablePtr != IntPtr.Zero)
            {
                var fnPtr = ((IntPtr*)vtablePtr)[7]; // UpdateTransforms
                if (fnPtr != IntPtr.Zero && fnPtr != _hookedVtableFn)
                {
                    try { _hook?.Disable(); } catch { }
                    try { _hook?.Dispose(); } catch { }
                    _hook = DalamudApi.GameInteropProvider.HookFromAddress<UpdateTransformsDelegate>(
                        fnPtr, UpdateTransformsDetour);
                    _hook.Enable();
                    _hookedVtableFn = fnPtr;
                }
            }

            // Stationary-actor refresh: write the current rotation
            // every Framework.Update tick, idempotent on the value.
            // When the engine isn't rebuilding (stationary actor),
            // this is the ONLY write that runs and it lets the
            // visible angle decay smoothly with _charRollCurrent.
            ForceWriteRotation();
        }
        catch { }
    }

    private static void UpdateTransformsDetour(DrawObject* drawObj, byte arg)
    {
        try
        {
            int target = MatchTarget(drawObj);
            if (target != 0)
            {
                float rollDeg = CameraDynamics.GetCharacterRollCurrentDegrees();
                // Apply on every UpdateTransforms call — rebuild-from-
                // scratch is idempotent, no per-frame token needed.
                // Always write so recovery to zero stays visible
                // (skipping when |rollDeg|<0.01 left a stale banked
                // quat in the rotation field after recovery).
                float yaw = ReadActorYaw(target);
                float halfYaw  = yaw * 0.5f;
                float halfRoll = rollDeg * (MathF.PI / 180f) * 0.5f;
                float cy = MathF.Cos(halfYaw),  sy = MathF.Sin(halfYaw);
                float cr = MathF.Cos(halfRoll), sr = MathF.Sin(halfRoll);
                // qY * qZ — yaw first, then Z-roll in body frame.
                drawObj->Rotation = new Quaternion(
                    sy * sr, sy * cr, cy * sr, cy * cr);
            }
        }
        catch { }

        try { _hook?.Original(drawObj, arg); }
        catch { }
    }

    private static void ForceWriteRotation()
    {
        try
        {
            float rollDeg = CameraDynamics.GetCharacterRollCurrentDegrees();
            float halfRoll = rollDeg * (MathF.PI / 180f) * 0.5f;
            float cr = MathF.Cos(halfRoll), sr = MathF.Sin(halfRoll);

            // Player drawObj.
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)lp.Address;
                if (go != null && go->DrawObject != null)
                {
                    float halfYaw = lp.Rotation * 0.5f;
                    float cy = MathF.Cos(halfYaw), sy = MathF.Sin(halfYaw);
                    var q = new Quaternion(sy * sr, sy * cr, cy * sr, cy * cr);
                    if (!_lastPlayerInit || !QuatNearlyEqual(_lastPlayerWritten, q))
                    {
                        go->DrawObject->Rotation = q;
                        _lastPlayerWritten = q;
                        _lastPlayerInit = true;
                    }
                }
            }

            // Mount drawObj (slot[1] when present).
            try
            {
                var mountObj = DalamudApi.ObjectTable[1];
                if (mountObj != null)
                {
                    var mgo = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)mountObj.Address;
                    if (mgo != null && mgo->DrawObject != null)
                    {
                        float halfYaw = mountObj.Rotation * 0.5f;
                        float cy = MathF.Cos(halfYaw), sy = MathF.Sin(halfYaw);
                        var q = new Quaternion(sy * sr, sy * cr, cy * sr, cy * cr);
                        if (!_lastMountInit || !QuatNearlyEqual(_lastMountWritten, q))
                        {
                            mgo->DrawObject->Rotation = q;
                            _lastMountWritten = q;
                            _lastMountInit = true;
                        }
                    }
                }
            }
            catch { }
        }
        catch { }
    }

    private static int MatchTarget(DrawObject* drawObj)
    {
        if (drawObj == null) return 0;
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

    private static float ReadActorYaw(int target)
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
                var lp2 = DalamudApi.ObjectTable.LocalPlayer;
                if (lp2 != null) return lp2.Rotation;
            }
        }
        catch { }
        return 0f;
    }

    private static bool QuatNearlyEqual(Quaternion a, Quaternion b)
    {
        const float eps = 0.0005f;
        return MathF.Abs(a.X - b.X) < eps
            && MathF.Abs(a.Y - b.Y) < eps
            && MathF.Abs(a.Z - b.Z) < eps
            && MathF.Abs(a.W - b.W) < eps;
    }
}
