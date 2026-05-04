using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace noWickyXIV;

// Hides the chevron/arrow indicator that floats above the current
// target. FFXIV draws this via the _TargetCursor / _TargetCursorParent
// addons.
//
// We hook PreUpdate, PreDraw, and PostSetup on each addon via
// Dalamud's IAddonLifecycle. Each handler does TWO things:
//   1. Clears the Visible bit on the RootNode's NodeFlags. The
//      renderer skips any node with this bit off — and crucially this
//      *bypasses* the click-flash intro animation that briefly sets
//      alpha back to 255 inside the engine.
//   2. Belt-and-braces sets alpha=0 in case some other plugin or game
//      code re-flips the visibility bit.
//
// We don't call AtkUnitBase->Hide() because that triggers the addon's
// own onHide path and the engine re-shows it whenever target state
// changes — flipping visibility on the root node is durable and
// invisible to the engine's visibility state machine.
public static unsafe class TargetArrowHider
{
    private static readonly string[] HiddenAddons =
    {
        "_TargetCursor",
        "_TargetCursorParent",
    };

    private static bool _hooked;

    // Initialize wires the lifecycle hooks once. Toggling
    // HideTargetArrow on/off doesn't add/remove the hook — the handler
    // checks the flag itself, so the user can flip it freely.
    public static void Initialize()
    {
        if (_hooked) return;
        try
        {
            foreach (var name in HiddenAddons)
            {
                DalamudApi.AddonLifecycle.RegisterListener(AddonEvent.PreUpdate, name, OnHide);
                DalamudApi.AddonLifecycle.RegisterListener(AddonEvent.PreDraw,   name, OnHide);
                DalamudApi.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, name, OnHide);
            }
            _hooked = true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] TargetArrowHider lifecycle hook failed: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        if (!_hooked) return;
        try
        {
            foreach (var name in HiddenAddons)
            {
                DalamudApi.AddonLifecycle.UnregisterListener(AddonEvent.PreUpdate, OnHide);
                DalamudApi.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw,   OnHide);
                DalamudApi.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, OnHide);
            }
        }
        catch { }
        _hooked = false;
        RestoreOpaque();
    }

    // NodeFlags bit for "renderer should draw this node". Clearing it
    // is the most reliable hide on FFXIV's UI tree — the engine's
    // intro/click animations animate Color.A but they don't touch
    // NodeFlags, so this survives the click-flash.
    private const ushort NODEFLAG_VISIBLE = 0x10;

    private static void OnHide(AddonEvent type, AddonArgs args)
    {
        if (!noWickyXIV.Config.HideTargetArrow) return;
        try
        {
            var addr = args.Addon.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            // Primary mechanism: clear visibility bit. Survives engine
            // alpha animations.
            addon->RootNode->NodeFlags = (NodeFlags)(unchecked((ushort)((ushort)addon->RootNode->NodeFlags & ~NODEFLAG_VISIBLE)));
            // Belt: zero alpha too in case some other plugin or future
            // code re-flips the visibility bit.
            addon->RootNode->Color.A = 0;
        }
        catch { }
    }

    public static void RestoreOpaque()
    {
        foreach (var name in HiddenAddons)
            RestoreNode(name);
    }

    private static void RestoreNode(string addonName)
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName(addonName, 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            // Re-set the visibility bit + restore alpha so the engine
            // resumes drawing the addon normally.
            addon->RootNode->NodeFlags = (NodeFlags)((ushort)addon->RootNode->NodeFlags | (ushort)NODEFLAG_VISIBLE);
            addon->RootNode->Color.A = 255;
        }
        catch { }
    }

    // Update is now a no-op — the lifecycle hook does all the work.
    // Kept so the call site in noWickyXIV.cs doesn't need to change,
    // and so that toggling HideTargetArrow off triggers a one-shot
    // alpha restore via the same path the previous implementation used.
    private static bool _wasHidden;
    public static void Update()
    {
        bool hide = noWickyXIV.Config.HideTargetArrow;
        if (hide && !_wasHidden) _wasHidden = true;
        else if (!hide && _wasHidden)
        {
            RestoreOpaque();
            _wasHidden = false;
        }
    }
}
