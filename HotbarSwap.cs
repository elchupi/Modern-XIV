using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace noWickyXIV;

// Hotbar 7 swap. Hotbar 7 Slot 3 is the "active" slot the user
// normally has Ikishoten on. Hotbar 7 Slot 9 holds the swap target
// (Ogi Namikiri). While slot 9's action is currently usable —
// according to the engine's own check, NOT a hardcoded status id —
// slot 3 mirrors slot 9. The moment slot 9's action becomes unusable
// (Ogi Namikiri Ready buff consumed AND any follow-up Kaeshi:
// Namikiri Ready also gone), slot 3 reverts to whatever was there
// when the user toggled the feature on.
//
// Asking ActionManager.GetActionStatus avoids the entire problem of
// "which buff id gates this action" — the engine already knows.
// FFXIV's built-in slot replacement also auto-upgrades Ogi Namikiri
// to Kaeshi: Namikiri while the second-stage buff is up, so reading
// slot 9 fresh each frame picks up whatever the engine put there.
public static unsafe class HotbarSwap
{
    private const int HOTBAR_INDEX        = 6;   // Hotbar 7 (0-indexed)
    private const int ACTIVE_SLOT_INDEX   = 2;   // Slot 3
    private const int SWAP_REF_SLOT_INDEX = 8;   // Slot 9

    // Snapshot of the active slot at toggle-on time. Becomes the
    // restore target when slot 9 stops being usable.
    private static bool   _hasSnapshot;
    private static RaptureHotbarModule.HotbarSlotType _snapType;
    private static uint   _snapId;

    // Last (type, id) we wrote into the active slot.
    private static RaptureHotbarModule.HotbarSlotType _lastWrittenType;
    private static uint   _lastWrittenId;
    private static bool   _hasWritten;

    private static bool _wasEnabled;

    public static void Update()
    {
        bool enabled = noWickyXIV.Config.EnableIkishotenOgiSwap;

        // Keep retrying CaptureSnapshot until it succeeds. The first
        // few Update ticks after plugin load can run before the hotbar
        // is populated (login/zone load not finished), and capturing
        // a zero slot would mean the eventual swap-back writes
        // emptiness over Ikishoten — wiping the action from the bar.
        // Polling on _hasSnapshot instead of the _wasEnabled rising
        // edge gives us as many tries as needed.
        if (enabled && !_hasSnapshot) CaptureSnapshot();
        if (!enabled && _wasEnabled)
        {
            RestoreFromSnapshot();
            _hasSnapshot = false;
            _hasWritten  = false;
        }
        _wasEnabled = enabled;
        if (!enabled || !_hasSnapshot) return;

        if (!TryReadSlot(SWAP_REF_SLOT_INDEX, out var refType, out var refId))
            return;

        // The swap target qualifies as "live" only while the engine
        // says you can actually press it. ActionManager covers buff
        // prereqs (Ogi Namikiri Ready / Kaeshi: Namikiri Ready), level
        // gating, and recast in one call. If it's a non-Action slot
        // type (item, macro, etc.) we treat it as never-live so we
        // don't accidentally overwrite slot 3 with garbage.
        if (refType == RaptureHotbarModule.HotbarSlotType.Action
            && IsActionUsable(refId))
        {
            WriteActiveSlot(refType, refId);
        }
        else
        {
            WriteActiveSlot(_snapType, _snapId);
        }
    }

    public static void RestoreOnUnload()
    {
        try
        {
            if (_hasSnapshot) RestoreFromSnapshot();
            _hasSnapshot = false;
            _hasWritten  = false;
            _wasEnabled  = false;
        }
        catch { }
    }

    private static void CaptureSnapshot()
    {
        if (!TryReadSlot(ACTIVE_SLOT_INDEX, out var t, out var id)) return;
        // Reject zero/empty reads — those happen during the brief
        // window where the hotbar struct exists but slots haven't
        // been filled in yet (eg. very early in login). Capturing
        // here would persist Empty/0 and the eventual swap-back
        // would erase Ikishoten from the bar.
        if (id == 0) return;
        _snapType = t;
        _snapId   = id;
        _hasSnapshot = true;
    }

    private static void RestoreFromSnapshot()
    {
        if (!_hasSnapshot) return;
        WriteActiveSlot(_snapType, _snapId);
    }

    // The slot's stored action id never changes mid-combo — what
    // changes is FFXIV's "adjusted" action id, which the engine
    // resolves at press time. Ogi Namikiri (25804) gets adjusted to
    // Kaeshi: Namikiri (25805) once the Kaeshi Ready buff is up, so
    // we have to ask GetAdjustedActionId first before checking
    // usability. Otherwise the Kaeshi stage looks "unusable" because
    // the raw Ogi id requires a buff that was consumed by the first
    // cast.
    private static bool IsActionUsable(uint actionId)
    {
        if (actionId == 0) return false;
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return false;
            uint adjusted = am->GetAdjustedActionId(actionId);
            return am->GetActionStatus(ActionType.Action, adjusted) == 0;
        }
        catch { return false; }
    }

    private static bool TryReadSlot(int slotIndex,
        out RaptureHotbarModule.HotbarSlotType type, out uint id)
    {
        type = default;
        id = 0;
        try
        {
            var hot = RaptureHotbarModule.Instance();
            if (hot == null) return false;
            if (HOTBAR_INDEX < 0 || HOTBAR_INDEX >= hot->Hotbars.Length) return false;
            ref var bar = ref hot->Hotbars[HOTBAR_INDEX];
            if (slotIndex < 0 || slotIndex >= bar.Slots.Length) return false;
            ref var slot = ref bar.Slots[slotIndex];
            type = slot.CommandType;
            id   = slot.CommandId;
            return true;
        }
        catch { return false; }
    }

    private static void WriteActiveSlot(RaptureHotbarModule.HotbarSlotType type, uint id)
    {
        // Defense-in-depth — never write a zero/empty action over the
        // active slot. The capture path already filters this out, but
        // if anything ever leaves _snapId at 0 (eg. an unexpected
        // restore path) we'd silently erase the user's Ikishoten.
        if (id == 0) return;
        if (_hasWritten && type == _lastWrittenType && id == _lastWrittenId) return;
        try
        {
            var hot = RaptureHotbarModule.Instance();
            if (hot == null) return;
            if (HOTBAR_INDEX < 0 || HOTBAR_INDEX >= hot->Hotbars.Length) return;
            ref var bar = ref hot->Hotbars[HOTBAR_INDEX];
            if (ACTIVE_SLOT_INDEX < 0 || ACTIVE_SLOT_INDEX >= bar.Slots.Length) return;
            ref var slot = ref bar.Slots[ACTIVE_SLOT_INDEX];
            slot.Set(type, id);
            _lastWrittenType = type;
            _lastWrittenId   = id;
            _hasWritten      = true;
        }
        catch { }
    }
}
