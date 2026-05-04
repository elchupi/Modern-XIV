using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace noWickyXIV;

// Hooks ActionEffectHandler.Receive to detect combat events the JobAura
// layer engine can fire VFX off:
//   - NormalHit: outgoing damage from us, no crit/dh flag
//   - CritHit:   outgoing damage from us with crit OR direct-hit flag
//   - IncomingDamage: any damage landing on the local player
//
// Edge-style flags: each frame the flags accumulate from the hook, then
// JobAura.Update reads them via EvaluateTrigger and immediately calls
// CombatEvents.ResetEdgeFlags() to clear them. So a layer with
// IsBurst=true on (e.g.) CritHit fires once per crit.
public static unsafe class CombatEvents
{
    public static bool NormalHit       { get; private set; }
    public static bool CritHit         { get; private set; }
    public static bool IncomingDamage  { get; private set; }

    // Auto-attack damage events that land within this many seconds of
    // an outgoing ACTION damage event are suppressed. FFXIV's auto-
    // attack runs continuously on a ~2.7s timer, so when you cast a
    // skill an auto often lands within a few hundred ms — without
    // suppression the NormalHit/CritHit feedback "bleeds into" the
    // action's animation. 1.5s covers a typical GCD action's full
    // animation window while still letting standalone autos (3s+
    // gaps between actions) come through cleanly.
    private const double ACTION_BLEED_SUPPRESS_SECONDS = 1.5;
    private static double _lastActionDamageAt = double.MinValue;
    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private delegate void ReceiveDelegate(
        uint sourceId,
        Character* sourceCharacter,
        System.Numerics.Vector3* position,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds);

    private static Hook<ReceiveDelegate> _hook;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var addr = (nint)ActionEffectHandler.Addresses.Receive.Value;
            if (addr == 0)
            {
                try { DalamudApi.PluginLog.Warning("[noWickyXIV] CombatEvents: ActionEffectHandler.Receive not resolved by ClientStructs."); } catch { }
                return;
            }
            _hook = DalamudApi.GameInteropProvider.HookFromAddress<ReceiveDelegate>(addr, ReceiveDetour);
            _hook.Enable();
            try { DalamudApi.PluginLog.Information($"[noWickyXIV] CombatEvents resolved: receive=0x{addr:X}"); } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] CombatEvents init threw: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        _initialized = false;
    }

    // JobAura calls this after each Update tick has read the flags so the
    // next frame starts from a clean slate.
    public static void ResetEdgeFlags()
    {
        NormalHit = false;
        CritHit = false;
        IncomingDamage = false;
    }

    private static void ReceiveDetour(
        uint sourceId,
        Character* sourceCharacter,
        System.Numerics.Vector3* position,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds)
    {
        // Always invoke the original first — if our parsing throws we
        // don't want to break the game's effect dispatch.
        try { _hook.Original(sourceId, sourceCharacter, position, header, effects, targetIds); }
        catch { return; }

        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null || header == null || effects == null || targetIds == null) return;
            ulong selfId = lp.GameObjectId;
            bool fromMe = sourceId == (uint)selfId;

            // NormalHit / CritHit triggers are AUTO-ATTACK feedback only.
            // Verified via diagnostic logs: ActionId == 7 = melee auto,
            // 8 = ranged Shot, both also mirrored on SpellId. Actions
            // report 4-digit ids (e.g. 7477, 7478, 7481) so the equality
            // filter cleanly excludes them.
            uint actionId = header->ActionId;
            ushort spellId = header->SpellId;
            bool isAutoAttack = actionId == 7 || actionId == 8
                              || spellId  == 7 || spellId  == 8;

            int targetCount = header->NumTargets;
            // Each TargetEffects has 8 effect entries (engine-side fixed array).
            for (int t = 0; t < targetCount && t < 16; t++)
            {
                ulong tidRaw = targetIds[t].ObjectId;
                bool toMe = tidRaw == selfId;
                var teff = effects[t];
                for (int e = 0; e < 8; e++)
                {
                    var entry = teff.Effects[e];
                    // ActionEffectType byte (verified via DamageInfoPlugin enum):
                    //   0 Nothing, 1 Miss, 2 FullResist, 3 Damage,
                    //   4 Heal, 5 BlockedDamage, 6 ParriedDamage, ...
                    // Treat 3/5/6 as "a hit landed" — full / blocked / parried
                    // all count for the purpose of firing a hit-feedback vfx.
                    byte etype = (byte)entry.Type;
                    bool isDamage = etype == 3 || etype == 5 || etype == 6;
                    if (!isDamage) continue;
                    // Param0 carries the crit/DH flags on damage entries.
                    // Verified by per-hit damage diagnostics on auto-attacks:
                    //   p0=0x00 → normal      (~2000 dmg)
                    //   p0=0x20 → crit        (~2700-3000 dmg, ~50% boost)
                    //   p0=0x40 → direct hit  (~2400 dmg, ~25% boost)
                    //   p0=0x60 → crit + DH   (~3900 dmg, both stacked)
                    bool crit = (entry.Param0 & 0x20) != 0;
                    bool dh   = (entry.Param0 & 0x40) != 0;
                    if (fromMe && !toMe)
                    {
                        if (isAutoAttack)
                        {
                            // Suppress when within the action-bleed window
                            // so autos landing during a skill's animation
                            // don't fire visual feedback for the wrong
                            // event. Standalone autos (no recent action)
                            // still come through.
                            double now = NowSec();
                            if (now - _lastActionDamageAt >= ACTION_BLEED_SUPPRESS_SECONDS)
                            {
                                // Only TRUE crits fire CritHit. Direct-hit-
                                // only autos (Param0 & 0x40 alone, no 0x20)
                                // visually look like normal hits in flytext
                                // and the user wants them treated as such.
                                // crit + DH (Param0 = 0x60) still routes to
                                // CritHit because the crit bit is set.
                                _ = dh; // discard — kept for future per-bit splits
                                if (crit) CritHit  = true;
                                else      NormalHit = true;
                            }
                        }
                        else
                        {
                            // Action landed → start the suppress window.
                            _lastActionDamageAt = NowSec();
                        }
                    }
                    if (toMe)
                    {
                        // IncomingDamage stays universal — any damage
                        // landing on the player counts (auto-attacks
                        // and action damage alike).
                        IncomingDamage = true;
                    }
                }
            }
        }
        catch { /* defensive — never propagate from a hook */ }
    }
}
