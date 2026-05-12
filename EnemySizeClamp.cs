using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace noWickyXIV;

// Clamps oversized enemy models during Duty Finder content.
// Each frame while inside a duty, sweeps the ObjectTable for hostile
// BattleNpcs whose DrawObject.Scale exceeds the configured cap and
// writes a proportionally reduced scale so they don't fill the entire
// screen. Restores originals on duty exit or feature disable.
//
// Write-every-frame pattern matches CharacterRollHook: the engine
// may re-set DrawObject.Scale on animation/state changes, so we
// must persist our clamp continuously.
public static unsafe class EnemySizeClamp
{
    // EntityId → original DrawObject.Scale captured before our first
    // clamp write for that entity. Used to restore on disable / duty exit.
    private static readonly Dictionary<uint, Vector3> _originalScales = new();
    // EntityId → the exact Vector3 we wrote last frame. Lets us tell
    // "engine touched the scale" from "engine left our write alone" so
    // we don't mistake our own clamp output for a fresh engine value
    // and overwrite the snapshot with it.
    private static readonly Dictionary<uint, Vector3> _lastWritten = new();
    private static bool _wasInDuty;
    private static bool _wasEnabled;

    public static void Update()
    {
        bool enabled = noWickyXIV.Config.EnableEnemySizeClamp;
        bool inDuty  = enabled && IsInDuty();

        // Feature toggled off while active → restore originals.
        if (_wasEnabled && !enabled)
        {
            RestoreAll();
            _wasEnabled = false;
            _wasInDuty  = false;
            return;
        }
        _wasEnabled = enabled;

        if (!enabled) return;

        // Left the duty → restore any clamped actors that are still
        // in the ObjectTable (edge case: cutscene after final boss).
        if (_wasInDuty && !inDuty)
        {
            RestoreAll();
            _wasInDuty = false;
            return;
        }
        _wasInDuty = inDuty;

        if (!inDuty) return;

        float maxScale = noWickyXIV.Config.EnemySizeClampMax;

        try
        {
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null) continue;
                if (obj is not IBattleNpc bn) continue;
                // Only hostile combatants. BattleNpcSubKind 5 = Enemy/Combatant;
                // numeric compare avoids enum-name drift across Dalamud versions.
                if ((byte)bn.BattleNpcKind != 5) continue;

                var go = (GameObject*)obj.Address;
                if (go == null || go->DrawObject == null) continue;

                uint eid = obj.EntityId;
                ref var scale = ref go->DrawObject->Scale;

                // Resolve the engine's intended scale for this actor.
                // If we've clamped before AND the live value still matches
                // our last write, the engine hasn't touched it — trust the
                // snapshot. Otherwise the engine has set it (first sight
                // or phase change) — refresh the snapshot from live.
                Vector3 intended;
                if (_originalScales.TryGetValue(eid, out var snap)
                    && _lastWritten.TryGetValue(eid, out var lw)
                    && scale.X == lw.X && scale.Y == lw.Y && scale.Z == lw.Z)
                {
                    intended = snap;
                }
                else
                {
                    intended = scale;
                    _originalScales[eid] = intended;
                }

                float largest = MathF.Max(intended.X, MathF.Max(intended.Y, intended.Z));

                if (largest <= maxScale)
                {
                    // Cap no longer binds (slider widened, or actor was
                    // already small). Restore the engine's intended scale
                    // if we'd previously written a smaller clamp, then
                    // drop tracking so we stop touching this actor.
                    if (scale.X != intended.X || scale.Y != intended.Y || scale.Z != intended.Z)
                        scale = intended;
                    _originalScales.Remove(eid);
                    _lastWritten.Remove(eid);
                    continue;
                }

                // Proportional reduction: preserves the model's aspect
                // ratios (wide enemies stay wide, just smaller overall).
                float ratio = maxScale / largest;
                var clamped = new Vector3(intended.X * ratio, intended.Y * ratio, intended.Z * ratio);
                scale = clamped;
                _lastWritten[eid] = clamped;
            }
        }
        catch { }
    }

    // Walk ObjectTable and write back every saved original.
    private static void RestoreAll()
    {
        if (_originalScales.Count == 0) return;
        try
        {
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null) continue;
                if (!_originalScales.TryGetValue(obj.EntityId, out var orig))
                    continue;
                var go = (GameObject*)obj.Address;
                if (go == null || go->DrawObject == null) continue;
                go->DrawObject->Scale = orig;
            }
        }
        catch { }
        _originalScales.Clear();
        _lastWritten.Clear();
    }

    // Dispose from plugin unload — same restore pass.
    public static void Dispose()
    {
        try { RestoreAll(); } catch { }
    }

    private static bool IsInDuty()
    {
        try
        {
            var cond = DalamudApi.Condition;
            // Skip cutscenes — let the cinematic play at intended scale.
            if (cond[ConditionFlag.OccupiedInCutSceneEvent]
                || cond[ConditionFlag.WatchingCutscene]
                || cond[ConditionFlag.WatchingCutscene78])
                return false;

            return cond[ConditionFlag.BoundByDuty]
                || cond[ConditionFlag.BoundByDuty56]
                || cond[ConditionFlag.BoundByDuty95];
        }
        catch { return false; }
    }
}
