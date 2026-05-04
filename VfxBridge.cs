using System;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace noWickyXIV;

// Actor-anchored vfx bridge — Create + CallTrigger only. NO HOOKS.
//
// Lifecycle approach:
//   - Create: spawn the avfx anchored to the player. Engine drives the
//     timeline + resource resolution.
//   - End / "Pause": call CallTrigger(handle, endTriggerId). The avfx's
//     own timeline picks up the trigger and runs its end-animation (the
//     natural fade VFXEditor's pause button can't actually do — pause
//     in their UI just calls Remove). Engine then self-cleans the vfx
//     when the end-animation completes. We never call Remove ourselves
//     so there's no UAF risk and no hook collision.
//
// Why not Remove: hooking ActorVfxRemove conflicts with VFXEditor
// (dump 045114) and chaining hooks on top of theirs corrupted the .NET
// runtime trampoline (dump 183736, ExecutionEngineException). Calling
// Remove blind UAFs on stale handles (dump 051841). Triggers don't
// have those issues — they're a one-shot dispatch into the running
// timeline.
public static unsafe class VfxBridge
{
    // Verified ActorVfxCreate sig from VFXEditor's Constants.cs.
    private delegate IntPtr ActorVfxCreateDelegate(
        byte* path, GameObject* caster, GameObject* target,
        float a4, ushort a5, ushort a6, ushort a7);
    private static ActorVfxCreateDelegate _create;

    // VFXEditor `VfxUseTriggerDelete` — dispatch a timeline trigger
    // into a running vfx. Used for graceful end-animation.
    private delegate IntPtr VfxUseTriggerDelegate(IntPtr vfx, uint triggerId);
    private static VfxUseTriggerDelegate _trigger;

    private static bool _initialized;
    private static bool _available;
    public static bool Available => _available;
    public static bool TriggerAvailable => _trigger != null;

    private const string SIG_CREATE =
        "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    // VFXEditor Constants.cs CallTriggerSig — CALL site, decode rel32.
    private const string SIG_TRIGGER = "E8 ?? ?? ?? ?? 0F B7 43 56";

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var addrCreate = DalamudApi.SigScanner.DalamudSigScanner.ScanText(SIG_CREATE);
            if (addrCreate == 0)
            {
                _available = false;
                try { DalamudApi.PluginLog.Warning("[noWickyXIV] VfxBridge sig-scan FAILED for ActorVfxCreate."); } catch { }
                return;
            }
            _create = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(addrCreate);
            _available = true;

            // CallTrigger is a CALL-site sig. Decode the rel32.
            try
            {
                var triggerCallSite = DalamudApi.SigScanner.DalamudSigScanner.ScanText(SIG_TRIGGER);
                if (triggerCallSite != 0)
                {
                    int disp = Marshal.ReadInt32(triggerCallSite + 1);
                    var addrTrigger = triggerCallSite + 5 + disp;
                    _trigger = Marshal.GetDelegateForFunctionPointer<VfxUseTriggerDelegate>(addrTrigger);
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] VfxBridge resolved create=0x{addrCreate:X} trigger=0x{addrTrigger.ToInt64():X}"); } catch { }
                }
                else
                {
                    try { DalamudApi.PluginLog.Warning("[noWickyXIV] VfxBridge sig-scan failed for CallTrigger — Trigger() will no-op."); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { DalamudApi.PluginLog.Warning($"[noWickyXIV] VfxBridge CallTrigger resolve threw: {ex.Message}"); } catch { }
            }
        }
        catch (Exception ex)
        {
            _available = false;
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] VfxBridge init threw: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        _initialized = false;
    }

    // Convenience overload — preserves call sites that don't care about flags.
    public static IntPtr Create(string avfxPath) => Create(avfxPath, 0, 0, 0);

    public static IntPtr Create(string avfxPath, byte flagA5, ushort flagA6, byte flagA7)
    {
        if (!_available || string.IsNullOrEmpty(avfxPath) || _create == null) return IntPtr.Zero;

        var lp = DalamudApi.ObjectTable.LocalPlayer;
        if (lp == null) return IntPtr.Zero;
        var owner = (GameObject*)lp.Address;
        if (owner == null) return IntPtr.Zero;

        IntPtr handle = IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(avfxPath);
            buf = Marshal.AllocHGlobal(byteCount + 1);
            byte[] tmp = new byte[byteCount + 1];
            System.Text.Encoding.UTF8.GetBytes(avfxPath, 0, avfxPath.Length, tmp, 0);
            tmp[byteCount] = 0;
            Marshal.Copy(tmp, 0, buf, byteCount + 1);
            handle = _create((byte*)buf, owner, owner, -1f, flagA5, flagA6, flagA7);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] VfxBridge.Create({avfxPath}) threw: {ex.Message}"); } catch { }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
        return handle;
    }

    // Dispatch a timeline trigger into a running vfx. Used to ask the
    // avfx's own timeline to play its end-animation (graceful fade).
    // Trigger IDs are avfx-specific — common values to try are 0, 1, 2.
    // The avfx's Schedulers in VFXEditor define which IDs map to what.
    public static void Trigger(IntPtr handle, uint triggerId)
    {
        if (handle == IntPtr.Zero || _trigger == null) return;
        try
        {
            _trigger(handle, triggerId);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] VfxBridge.Trigger threw: {ex.Message}"); } catch { }
        }
    }

    // Remove kept as no-op for back-compat with existing call sites.
    // Real removal happens via the engine after a successful Trigger
    // dispatched the avfx's end-animation.
    public static void Remove(IntPtr handle, ushort spawnZone) { _ = handle; _ = spawnZone; }
    public static void Remove(IntPtr handle) { _ = handle; }

    public static void SetScale(IntPtr handle, float uniformScale)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var vfx = (VfxObject*)handle;
            vfx->Scale = new Vector3(uniformScale, uniformScale, uniformScale);
        }
        catch { /* defensive */ }
    }

    public static void SetWorldTransform(IntPtr handle, Vector3 localOffset, Quaternion rotation)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var vfx = (VfxObject*)handle;
            vfx->Position = localOffset;
            vfx->Rotation = rotation;
        }
        catch { /* defensive */ }
    }

    public static void SetWorldTransform(IntPtr handle, Vector3 playerWorldPos, float playerYawRadians, float offX, float offY, float offZ)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var vfx = (VfxObject*)handle;
            vfx->Position = new Vector3(offX, offY, offZ);
            vfx->Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, playerYawRadians);
        }
        catch { /* defensive */ }
    }

    public static bool IsValidForOwner(IntPtr handle) => handle != IntPtr.Zero;

    public static ushort SafeCurrentZone()
    {
        try { return (ushort)DalamudApi.ClientState.TerritoryType; }
        catch { return 0; }
    }
}
