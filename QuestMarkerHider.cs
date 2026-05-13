using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace noWickyXIV;

public static unsafe class QuestMarkerHider
{
    private const uint QUEST_ICON_MIN = 71001;
    private const uint QUEST_ICON_MAX = 71999;

    // MSQ (Main Scenario Quest) icons live in 71201–71299.
    // These are never hidden — only side quests and other markers.
    private const uint MSQ_ICON_MIN = 71201;
    private const uint MSQ_ICON_MAX = 71299;

    // Object-ID → original NamePlateIconId, populated each frame the
    // hider is active. Compass.CollectNpcMarkers reads this when the
    // live field is 0.
    public static readonly Dictionary<ulong, uint> HiddenIcons = new();

    public static void Update()
    {
        HiddenIcons.Clear();

        if (!noWickyXIV.Config.EnableHideQuestMarkers) return;

        try
        {
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null) continue;
                var gop = (GameObject*)obj.Address;
                if (gop == null) continue;
                uint id = gop->NamePlateIconId;
                if (id >= QUEST_ICON_MIN && id <= QUEST_ICON_MAX
                    && !(id >= MSQ_ICON_MIN && id <= MSQ_ICON_MAX))
                {
                    HiddenIcons[obj.GameObjectId] = id;
                    gop->NamePlateIconId = 0;
                }
            }
        }
        catch { }
    }
}
