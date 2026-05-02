using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace noWickyXIV;

// Scroll-wheel target cycling:
//   plain scroll  → cycle nearby HOSTILE NPCs (nearest-first)
//   Shift + scroll → cycle PARTY members (slot order)
//
// Direction is the wheel sign: +1 = scroll-up = next, -1 = scroll-down = prev.
// Wrap-around at list ends. Calling with an empty list is a no-op.
//
// Called from InputHandler.UpdateScrollHeight when the matching modifier
// matrix slot is hit. Reads/writes via Dalamud services — no game-side hook
// installation needed (TargetManager.Target setter does the work).
public static class TargetCycle
{
    public static void CycleEnemy(int direction)
    {
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null) return;
            var lpPos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);

            // Hostile NPCs in object table, sorted by distance to player
            var enemies = new List<IGameObject>();
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null || !obj.IsValid()) continue;
                // BattleNpcSubKind enum members vary across Dalamud versions.
                // Underlying value 5 = "BattleNpcEnemy" — compare numerically
                // to avoid enum-name drift breaking builds.
                if (obj is IBattleNpc bn
                    && (byte)bn.BattleNpcKind == 5
                    && bn.IsTargetable
                    && bn.CurrentHp > 0)
                {
                    enemies.Add(obj);
                }
            }
            if (enemies.Count == 0) return;
            enemies.Sort((a, b) =>
            {
                float da = Vector3.DistanceSquared(new Vector3(a.Position.X, a.Position.Y, a.Position.Z), lpPos);
                float db = Vector3.DistanceSquared(new Vector3(b.Position.X, b.Position.Y, b.Position.Z), lpPos);
                return da.CompareTo(db);
            });

            CycleAndSet(enemies, direction);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] CycleEnemy threw: {ex.Message}"); } catch { }
        }
    }

    public static void CyclePartyMember(int direction)
    {
        try
        {
            var party = DalamudApi.PartyList;
            if (party == null || party.Length == 0)
            {
                // Solo: target self when Shift+scroll fires (so the user gets feedback).
                var lp = DalamudApi.ObjectTable.LocalPlayer;
                if (lp != null) DalamudApi.TargetManager.Target = lp;
                return;
            }

            var members = new List<IGameObject>();
            foreach (var pm in party)
            {
                if (pm?.GameObject != null && pm.GameObject.IsValid())
                    members.Add(pm.GameObject);
            }
            if (members.Count == 0) return;

            CycleAndSet(members, direction);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] CyclePartyMember threw: {ex.Message}"); } catch { }
        }
    }

    private static void CycleAndSet(List<IGameObject> list, int direction)
    {
        if (list.Count == 0) return;

        // Locate current target's index in the list (if any)
        var current = DalamudApi.TargetManager.Target;
        int idx = -1;
        if (current != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Address == current.Address) { idx = i; break; }
            }
        }

        // direction: scroll-up = +1 (next), scroll-down = -1 (prev). C# % can be
        // negative — use (((idx + dir) % n) + n) % n for proper wrap.
        int next = idx < 0 ? (direction > 0 ? 0 : list.Count - 1)
                           : (((idx + direction) % list.Count) + list.Count) % list.Count;

        DalamudApi.TargetManager.Target = list[next];
    }
}
