using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace noWickyXIV;

// Smooth fade for hotbars and the Duty Actions bar. Every managed
// addon is hover-only by default: it sits at SheathedAlpha (typically
// 0 = invisible) and fades up to DrawnAlpha while the cursor is over
// its rect, then fades back down on cursor-leave. Two opt-in overrides
// can also pin a chosen bar visible:
//
//   - Combo-prompt bar: fades in whenever an active combo action
//     lives on one of its slots.
//   - Availability-flash bar: fades in momentarily whenever any of
//     its action slots transitions from "on cooldown" to "ready".
//
// Per-frame target alpha for every managed addon:
//   target = (hover || combo-here || avail-flash-here) ? DrawnAlpha : SheathedAlpha
// The lerp is exponential (rate-based) so it always settles to the
// target without overshooting.
//
// The legacy weapon-drawn cascade infrastructure is left in place but
// dormant — IsCascadeBar is all-false, so the cascade-target branch
// never wins. Cascade-related config fields stay so old configs load
// cleanly; they have no effect.
//
// FFXIV addon naming:
//   Hotbar 1            = "_ActionBar"     (no suffix on the first one)
//   Hotbar N (2..10)    = "_ActionBarNN"   with NN = (N-1) zero-padded.
//   Duty Actions bar    = "_ActionContents"
//
// Alpha is written to the addon's RootNode color channel — the engine
// multiplies that with each child node at render time, so a single
// byte write fades the whole bar.
public static unsafe class HotbarFader
{
    // Every main hotbar (1..10) plus the Duty Actions bar. All are
    // hover-only: invisible at rest, fade in on cursor-hover, fade out
    // on cursor-leave. isCascade is all-false; the cascade branch in
    // Update() is kept for code symmetry but never wins.
    private static readonly string[] ManagedAddons =
    {
        "_ActionBar",   "_ActionBar01", "_ActionBar02", "_ActionBar03",
        "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07",
        "_ActionBar08", "_ActionBar09",
        "_ActionContents",
    };
    private static readonly bool[]   IsCascadeBar  =
    {
        false, false, false, false,
        false, false, false, false,
        false, false,
        false,
    };

    // Per-managed-bar animation state.
    private static readonly float[]  _barAlpha   = new float[ManagedAddons.Length];
    private static readonly double[] _barStartT  = new double[ManagedAddons.Length];

    private static bool _initialized;
    private static bool _lastDrawn;

    // Animation state for the dynamically-addressed combo and avail
    // bars. Their addon names depend on config and may overlap with
    // the cascade list — when they do, the override target is OR-ed
    // with the cascade target so visibility wins.
    private static float _comboAlpha;
    private static float _availAlpha;

    // Availability-flash bookkeeping. When any slot on the avail bar
    // transitions from "on cooldown" to "ready", we set
    // _availFlashUntil = now + flashSeconds. The bar is force-shown
    // while now < _availFlashUntil.
    private static double _availFlashUntil = double.MinValue;
    private static readonly Dictionary<int, bool> _availPrevUsable = new();

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableHotbarFader) return;

        bool drawn = false;
        try
        {
            var p = DalamudApi.ObjectTable.LocalPlayer;
            if (p != null)
                drawn = (p.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.WeaponOut) != 0;
        }
        catch { return; }

        double now = NowSec();
        double cascadeDelay = Math.Max(0.0, noWickyXIV.Config.HotbarFaderCascadeDelay);

        if (!_initialized)
        {
            // Cascade bars start at the steady weapon-drawn alpha;
            // hover-only bars start sheathed (their default).
            float drawnA0  = noWickyXIV.Config.HotbarFaderDrawnAlpha;
            float sheathA0 = noWickyXIV.Config.HotbarFaderSheathedAlpha;
            for (int i = 0; i < _barAlpha.Length; i++)
            {
                _barAlpha[i]  = IsCascadeBar[i] ? (drawn ? drawnA0 : sheathA0) : sheathA0;
                _barStartT[i] = double.MinValue;
            }
            _comboAlpha = sheathA0;
            _availAlpha = sheathA0;
            _initialized = true;
            _lastDrawn   = drawn;
        }

        if (drawn != _lastDrawn)
        {
            // Cascade only schedules cascade bars. Hover-only bars
            // ignore weapon-drawn changes entirely.
            int cascadeIdx = 0;
            for (int i = 0; i < _barStartT.Length; i++)
            {
                if (IsCascadeBar[i])
                {
                    _barStartT[i] = now + cascadeIdx * cascadeDelay;
                    cascadeIdx++;
                }
                else
                {
                    _barStartT[i] = double.MinValue;
                }
            }
            _lastDrawn = drawn;
        }

        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }

        float rate     = MathF.Max(0.5f, noWickyXIV.Config.HotbarFaderRate);
        float k        = 1f - MathF.Exp(-rate * dt);
        float drawnA   = noWickyXIV.Config.HotbarFaderDrawnAlpha;
        float sheathA  = noWickyXIV.Config.HotbarFaderSheathedAlpha;
        bool hoverEnabled = noWickyXIV.Config.HotbarFaderHoverActivates;

        Vector2 cursor;
        try { cursor = ImGui.GetIO().MousePos; }
        catch { cursor = new Vector2(float.MinValue, float.MinValue); }

        // ---- Combo / availability state for this frame ----
        int comboBarN = noWickyXIV.Config.HotbarFaderComboPromptBar;   // 1..10, 0=off
        int availBarN = noWickyXIV.Config.HotbarFaderAvailabilityBar;  // 1..10, 0=off

        bool comboActiveOnBar = comboBarN > 0 && IsComboActiveOnBar(comboBarN);

        if (availBarN > 0)
            UpdateAvailabilityFlash(availBarN, now);

        bool availFlashActive = availBarN > 0 && now < _availFlashUntil;

        string comboAddon = AddonForBar(comboBarN);
        string availAddon = AddonForBar(availBarN);

        // ---- Managed bars (cascade + hover-only) ----
        // Per bar, target alpha is:
        //   - if override active (hover, comboBar match, availFlash bar
        //     match): DrawnAlpha
        //   - else if cascade bar AND weapon drawn: DrawnAlpha
        //     (still gated by the per-bar cascade start time)
        //   - else: SheathedAlpha
        for (int i = 0; i < ManagedAddons.Length; i++)
        {
            string addon  = ManagedAddons[i];
            bool isCascade = IsCascadeBar[i];

            bool hovered  = hoverEnabled && IsCursorOverAddon(addon, cursor);
            bool comboHere = comboActiveOnBar && comboAddon == addon;
            bool availHere = availFlashActive  && availAddon == addon;
            bool overrideActive = hovered || comboHere || availHere;

            // Cascade hold-gate: cascade bars wait their turn before
            // following the weapon-drawn target. Hover-only bars and
            // any bar with an active override skip the gate.
            if (!overrideActive && isCascade && now < _barStartT[i])
            {
                ApplyAlpha(addon, _barAlpha[i]);
                continue;
            }

            float target;
            if (overrideActive)             target = drawnA;
            else if (isCascade && drawn)    target = drawnA;
            else                            target = sheathA;

            _barAlpha[i] += (target - _barAlpha[i]) * k;
            if (MathF.Abs(target - _barAlpha[i]) < 0.002f) _barAlpha[i] = target;
            ApplyAlpha(addon, _barAlpha[i]);
        }

        // ---- Combo-prompt bar (only when not already managed above) ----
        if (comboBarN > 0 && comboAddon != null && Array.IndexOf(ManagedAddons, comboAddon) < 0)
        {
            bool hovered = hoverEnabled && IsCursorOverAddon(comboAddon, cursor);
            bool overrideActive = hovered || comboActiveOnBar;
            float target = overrideActive ? drawnA : sheathA;
            _comboAlpha += (target - _comboAlpha) * k;
            if (MathF.Abs(target - _comboAlpha) < 0.002f) _comboAlpha = target;
            ApplyAlpha(comboAddon, _comboAlpha);
        }

        // ---- Availability-flash bar (only when not already managed
        //      above AND not the same as the combo bar). ----
        if (availBarN > 0 && availAddon != null
            && Array.IndexOf(ManagedAddons, availAddon) < 0
            && availAddon != comboAddon)
        {
            bool hovered = hoverEnabled && IsCursorOverAddon(availAddon, cursor);
            bool overrideActive = hovered || availFlashActive;
            float target = overrideActive ? drawnA : sheathA;
            _availAlpha += (target - _availAlpha) * k;
            if (MathF.Abs(target - _availAlpha) < 0.002f) _availAlpha = target;
            ApplyAlpha(availAddon, _availAlpha);
        }
    }

    // Returns the addon name for a 1-based hotbar number, or null
    // when the number is out of range (incl. 0 = disabled sentinel).
    private static string AddonForBar(int hotbarNumber) => hotbarNumber switch
    {
        1                  => "_ActionBar",
        >= 2 and <= 10     => $"_ActionBar{(hotbarNumber - 1):D2}",
        _                  => null,
    };

    // True when ActionManager.Combo.Action is non-zero AND that action
    // ID lives on one of the slots of the configured combo bar.
    private static bool IsComboActiveOnBar(int hotbarNumber)
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return false;
            uint comboAction = am->Combo.Action;
            if (comboAction == 0) return false;

            var hot = RaptureHotbarModule.Instance();
            if (hot == null) return false;

            int idx = hotbarNumber - 1;
            if (idx < 0 || idx >= hot->Hotbars.Length) return false;
            ref var bar = ref hot->Hotbars[idx];

            int slotCount = bar.Slots.Length;
            for (int i = 0; i < slotCount; i++)
            {
                var slot = bar.Slots[i];
                if (slot.CommandType == RaptureHotbarModule.HotbarSlotType.Action
                    && slot.CommandId == comboAction)
                    return true;
            }
        }
        catch { }
        return false;
    }

    // Walks the configured availability bar each frame. For every
    // Action slot, recomputes "usable" (= recast elapsed >= recast)
    // and compares against the previous-frame state. On any rising
    // edge (became usable this frame), set _availFlashUntil = now +
    // flashSeconds.
    private static void UpdateAvailabilityFlash(int hotbarNumber, double now)
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return;
            var hot = RaptureHotbarModule.Instance();
            if (hot == null) return;

            int idx = hotbarNumber - 1;
            if (idx < 0 || idx >= hot->Hotbars.Length) return;
            ref var bar = ref hot->Hotbars[idx];

            float flashSec = MathF.Max(0.05f, noWickyXIV.Config.HotbarFaderAvailabilityFlashSeconds);

            int slotCount = bar.Slots.Length;
            for (int i = 0; i < slotCount; i++)
            {
                var slot = bar.Slots[i];
                if (slot.CommandType != RaptureHotbarModule.HotbarSlotType.Action) continue;
                uint actionId = slot.CommandId;
                if (actionId == 0) continue;

                // GetRecastTime returns the action's full recast (cooldown);
                // GetRecastTimeElapsed returns time elapsed since use.
                // recast == 0 means no cooldown → always "usable".
                float recast  = am->GetRecastTime(ActionType.Action, actionId);
                float elapsed = am->GetRecastTimeElapsed(ActionType.Action, actionId);
                bool usable   = recast <= 0f || elapsed >= recast;

                int key = (idx << 8) | (i & 0xFF);
                bool prev = _availPrevUsable.TryGetValue(key, out var pv) && pv;
                if (usable && !prev)
                    _availFlashUntil = now + flashSec;
                _availPrevUsable[key] = usable;
            }
        }
        catch { }
    }

    private static bool IsCursorOverAddon(string addonName, Vector2 cursor)
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName(addonName, 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return false;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return false;
            if (!addon->IsVisible) return false;

            float x = addon->X;
            float y = addon->Y;
            float s = MathF.Max(0.01f, addon->Scale);
            float w = addon->RootNode->Width  * s;
            float h = addon->RootNode->Height * s;
            return cursor.X >= x && cursor.X < x + w
                && cursor.Y >= y && cursor.Y < y + h;
        }
        catch { return false; }
    }

    private static void ApplyAlpha(string addonName, float alpha01)
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName(addonName, 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            byte a = (byte)Math.Clamp((int)MathF.Round(alpha01 * 255f), 0, 255);
            addon->RootNode->Color.A = a;
        }
        catch { /* defensive — addon may not be loaded yet on early frames */ }
    }

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    public static void RestoreOpaque()
    {
        for (int i = 0; i < ManagedAddons.Length; i++)
            ApplyAlpha(ManagedAddons[i], 1f);
        var combo = AddonForBar(noWickyXIV.Config.HotbarFaderComboPromptBar);
        var avail = AddonForBar(noWickyXIV.Config.HotbarFaderAvailabilityBar);
        if (combo != null) ApplyAlpha(combo, 1f);
        if (avail != null) ApplyAlpha(avail, 1f);
        _initialized = false;
        _availPrevUsable.Clear();
        _availFlashUntil = double.MinValue;
    }
}
