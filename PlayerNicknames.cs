using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace noWickyXIV;

// Player nickname system.
//
// - Right-click a player → "Set Nickname" context menu item opens an
//   ImGui popup to assign a nickname.
// - Incoming chat: sender SeString payloads are rewritten so the
//   displayed name shows the nickname instead of the real name.
// - Outgoing /tell: "/t Nickname msg" is intercepted before
//   ProcessChatBox and rewritten to "/tell RealName@World msg".
// - ChatBubbles integration: the nickname lookup is public so bubbles
//   can display the nickname too.
public static class PlayerNicknames
{
    // Pending nickname popup state — set by the context menu callback,
    // consumed by Draw() which renders the ImGui popup.
    private static bool   _popupPending;
    private static string _popupPlayerName = "";
    private static string _popupWorldName  = "";
    private static string _popupNickname   = "";
    private static bool   _popupJustOpened;

    private static bool _menuRegistered;

    private static bool _commandRegistered;

    // Throttle: only rescan addon text nodes every SCAN_INTERVAL_MS
    // instead of every frame. The game only refreshes list contents on
    // discrete events (open, scroll, tab switch), not 60× per second.
    private const double SCAN_INTERVAL_MS = 250;
    private static double _lastScanTime;
    private static bool   _dirty = true;   // force immediate first scan

    public static void Initialize()
    {
        if (_menuRegistered) return;
        try
        {
            DalamudApi.ContextMenu.OnMenuOpened += OnMenuOpened;
            DalamudApi.ChatGui.ChatMessage += OnChatMessage;
            _menuRegistered = true;
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[PlayerNicknames] Init failed: {ex.Message}");
        }

        // Register /w separately so a failure here doesn't block the
        // rest of the nickname system.
        try
        {
            _commandRegistered = DalamudApi.CommandManager.AddHandler("/w", new(OnWhisperCommand)
            {
                HelpMessage = "Whisper (nickname tell): /w Nickname message",
                ShowInHelp = true,
            });
            if (!_commandRegistered)
                DalamudApi.LogInfo("[PlayerNicknames] /w command registration returned false — command may already exist.");
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[PlayerNicknames] /w registration failed: {ex.Message}");
        }
    }

    public static void Dispose()
    {
        if (!_menuRegistered) return;
        try { DalamudApi.ContextMenu.OnMenuOpened -= OnMenuOpened; } catch { }
        try { DalamudApi.ChatGui.ChatMessage -= OnChatMessage; } catch { }
        if (_commandRegistered)
        {
            try { DalamudApi.CommandManager.RemoveHandler("/w"); } catch { }
            _commandRegistered = false;
        }
        _menuRegistered = false;
    }

    // Addons whose text nodes are scanned every frame for nickname
    // replacement. List-style addons (FriendList, FreeCompany) have
    // player names scattered across component list rows — the generic
    // walker handles them all the same way.
    private static readonly string[] _rewriteAddons =
    {
        "CharacterInspect",        // Examine plate
        "FriendList",              // Friends list
        "Social",                  // Social window (FC tab, party, etc.)
        "FreeCompany",             // FC member list (standalone)
        "FreeCompanyMember",       // FC member list (alternate name)
        "SocialList",              // Social → player search results
        "PartyMemberList",         // Party list overlay
        "_PartyList",              // Party HUD overlay
        "LinkShell",               // Linkshell member list
        "CrossWorldLinkShell",     // CWLS member list
        "LookingForGroupDetail",   // Party Finder detail
        "ChatLog",                 // Chat panel (sender names in log lines)
        "ContentMemberList",       // Duty/instance member list
        "BlackList",               // Blacklist (show nicknames there too)
        "ContactList",             // Contacts
    };

    /// <summary>
    /// Mark the addon scan as dirty so the next Update() rescans
    /// immediately regardless of the throttle timer. Call this when
    /// nicknames are added, edited, or removed.
    /// </summary>
    public static void MarkDirty() => _dirty = true;

    public static unsafe void Update()
    {
        if (!noWickyXIV.Config.EnablePlayerNicknames) return;

        // Throttle: skip the expensive addon walk unless enough time
        // has elapsed or something dirtied the state (nickname change,
        // first run, etc.).
        double now = Environment.TickCount64;
        if (!_dirty && (now - _lastScanTime) < SCAN_INTERVAL_MS) return;
        _dirty = false;
        _lastScanTime = now;

        for (int a = 0; a < _rewriteAddons.Length; a++)
        {
            try { RewriteAddonTextNodes(_rewriteAddons[a]); }
            catch { }
        }
    }

    // ---- Generic addon text-node rewriter ----
    // Walks every text node in the addon's node list AND recursively
    // descends into AtkComponentNode children. List-based addons
    // (FriendList, FreeCompany, etc.) nest player names inside
    // component list rows — a flat NodeList scan misses them.
    //
    // No pointer-based dedup — the game can rewrite node text at any
    // time (list refresh, scroll, tab switch, new nickname applied),
    // so we must re-check every frame. SetText on a node that already
    // holds the nickname is essentially free (short strcmp + bail).

    private static unsafe void RewriteAddonTextNodes(string addonName)
    {
        var wrapper = DalamudApi.GameGui.GetAddonByName(addonName, 1);
        var addr = wrapper.Address;
        if (addr == IntPtr.Zero) return;

        var addon = (AtkUnitBase*)addr;
        if (!addon->IsVisible) return;

        // Walk the flat NodeList first (covers simple addons).
        int count = addon->UldManager.NodeListCount;
        for (int i = 0; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null) continue;
            WalkNode(node);
        }

        // Also walk from the root node's child chain (covers nested
        // component structures the flat list may not include).
        if (addon->RootNode != null)
            WalkNodeRecursive(addon->RootNode, 0);
    }

    private static unsafe void WalkNode(AtkResNode* node)
    {
        if (node == null) return;

        if (node->Type == NodeType.Text)
        {
            TryRewriteTextNode((AtkTextNode*)node);
            return;
        }

        // Component nodes (type >= 1000) wrap an AtkComponentBase
        // with its own UldManager containing child nodes.
        if ((int)node->Type >= 1000)
        {
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component != null)
            {
                var innerCount = compNode->Component->UldManager.NodeListCount;
                for (int j = 0; j < innerCount; j++)
                {
                    var inner = compNode->Component->UldManager.NodeList[j];
                    if (inner == null) continue;
                    WalkNode(inner);
                }
            }
        }
    }

    // Recursive walk via the ChildNode→PrevSiblingNode linked list.
    // Depth-limited to avoid runaway in case of cycles.
    private const int MAX_DEPTH = 12;

    private static unsafe void WalkNodeRecursive(AtkResNode* node, int depth)
    {
        if (node == null || depth > MAX_DEPTH) return;

        if (node->Type == NodeType.Text)
            TryRewriteTextNode((AtkTextNode*)node);

        // Descend into component children.
        if ((int)node->Type >= 1000)
        {
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component != null)
            {
                var innerCount = compNode->Component->UldManager.NodeListCount;
                for (int j = 0; j < innerCount; j++)
                {
                    var inner = compNode->Component->UldManager.NodeList[j];
                    if (inner != null)
                        WalkNodeRecursive(inner, depth + 1);
                }
            }
        }

        // Walk children via linked list.
        var child = node->ChildNode;
        while (child != null)
        {
            WalkNodeRecursive(child, depth + 1);
            child = child->PrevSiblingNode;
        }
    }

    private static unsafe void TryRewriteTextNode(AtkTextNode* textNode)
    {
        string current;
        try { current = textNode->NodeText.ToString(); }
        catch { return; }
        if (string.IsNullOrEmpty(current)) return;

        var nick = GetNickname(current);
        if (nick == null) return;

        // Already showing the nickname — skip the SetText call.
        if (string.Equals(current, nick, StringComparison.Ordinal)) return;

        try { textNode->SetText(nick); }
        catch { }
    }

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnablePlayerNicknames) return;
        DrawNicknamePopup();
    }

    // ---- Public API for other modules (ChatBubbles, etc.) ----

    /// <summary>
    /// Look up a nickname for the given player name. Matches full name
    /// ("First Last") and FFXIV shorthand ("First L.") forms.
    /// Returns null if no nickname is assigned.
    /// </summary>
    public static string GetNickname(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return null;
        var entries = noWickyXIV.Config.PlayerNicknames;
        if (entries == null || entries.Count == 0) return null;

        string cleaned = StripPrivateUseChars(playerName).Trim();

        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.PlayerName) || string.IsNullOrEmpty(e.Nickname))
                continue;
            if (string.Equals(e.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                return e.Nickname;
            if (string.Equals(e.PlayerName, cleaned, StringComparison.OrdinalIgnoreCase))
                return e.Nickname;
            if (cleaned.StartsWith(e.PlayerName, StringComparison.OrdinalIgnoreCase)
                && cleaned.Length > e.PlayerName.Length
                && !char.IsLetterOrDigit(cleaned[e.PlayerName.Length]))
                return e.Nickname;
            if (ChatBubbles.IsShorthandOf(playerName, e.PlayerName))
                return e.Nickname;
            if (ChatBubbles.IsShorthandOf(cleaned, e.PlayerName))
                return e.Nickname;
        }
        return null;
    }

    private static string StripPrivateUseChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '' && c <= '') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Look up the real player name + world for a given nickname.
    /// Used by the /tell rewriter.
    /// </summary>
    public static PlayerNicknameEntry FindByNickname(string nickname)
    {
        if (string.IsNullOrEmpty(nickname)) return null;
        var entries = noWickyXIV.Config.PlayerNicknames;
        if (entries == null) return null;
        return entries.FirstOrDefault(e =>
            !string.IsNullOrEmpty(e.Nickname) &&
            string.Equals(e.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
    }

    // ---- /w (whisper) command ----
    // /w Nickname message → /tell RealName@World message
    // Custom Dalamud command since native /t and /tell can't be
    // intercepted by plugins.

    private static void OnWhisperCommand(string command, string argument)
    {
        if (!noWickyXIV.Config.EnablePlayerNicknames)
        {
            DalamudApi.PrintError("Player nicknames are disabled.");
            return;
        }

        if (string.IsNullOrEmpty(argument))
        {
            DalamudApi.PrintError("Usage: /w Nickname message");
            return;
        }

        var entries = noWickyXIV.Config.PlayerNicknames;
        if (entries == null || entries.Count == 0)
        {
            DalamudApi.PrintError("No nicknames set. Right-click a player to assign one.");
            return;
        }

        // Try longest-match first against known nicknames.
        foreach (var e in entries.OrderByDescending(e => e.Nickname?.Length ?? 0))
        {
            if (string.IsNullOrEmpty(e.Nickname) || string.IsNullOrEmpty(e.PlayerName))
                continue;

            // Exact match (no message body) — open tell targeting that player.
            if (argument.Length == e.Nickname.Length &&
                argument.Equals(e.Nickname, StringComparison.OrdinalIgnoreCase))
            {
                ChatSend.Send($"/tell {e.PlayerName}@{e.WorldName}");
                return;
            }

            // Nickname followed by space + message.
            if (argument.Length > e.Nickname.Length &&
                argument.StartsWith(e.Nickname, StringComparison.OrdinalIgnoreCase) &&
                argument[e.Nickname.Length] == ' ')
            {
                string msg = argument.Substring(e.Nickname.Length + 1);
                ChatSend.Send($"/tell {e.PlayerName}@{e.WorldName} {msg}");
                return;
            }
        }

        DalamudApi.PrintError($"No nickname matching \"{argument.Split(' ')[0]}\" found.");
    }

    // ---- Context menu ----

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!noWickyXIV.Config.EnablePlayerNicknames) return;

        // Only inject on player-targeted context menus. The target
        // must be a MenuTargetDefault with a valid player name.
        if (args.Target is not MenuTargetDefault target) return;
        var name = target.TargetName;
        if (string.IsNullOrEmpty(name)) return;

        // Resolve home world name from the world ID.
        string worldName = "";
        try
        {
            var worldSheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
            var row = worldSheet?.GetRow(target.TargetHomeWorld.RowId);
            if (row.HasValue)
                worldName = row.Value.Name.ExtractText() ?? "";
        }
        catch { }

        // Check if this player already has a nickname.
        var existing = noWickyXIV.Config.PlayerNicknames.FirstOrDefault(e =>
            string.Equals(e.PlayerName, name, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(worldName) || string.Equals(e.WorldName, worldName, StringComparison.OrdinalIgnoreCase)));

        string label = existing != null
            ? $"Edit Nickname ({existing.Nickname})"
            : "Set Nickname";

        args.AddMenuItem(new MenuItem
        {
            Name = label,
            PrefixChar = 'N',
            OnClicked = _ =>
            {
                _popupPlayerName = name;
                _popupWorldName  = worldName;
                _popupNickname   = existing?.Nickname ?? "";
                _popupPending    = true;
                _popupJustOpened = true;
            },
        });
    }

    // ---- Chat display rewriter ----

    private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (!noWickyXIV.Config.EnablePlayerNicknames) return;

        try
        {
            // Rewrite both sender and message body. Incoming tells put
            // the name in Sender; outgoing tells (">> Name: msg") may
            // place the recipient name in Sender and/or Message.
            RewritePayloads(message.Sender);
            RewritePayloads(message.Message);
        }
        catch { }
    }

    private static void RewritePayloads(SeString seStr)
    {
        if (seStr == null) return;
        var payloads = seStr.Payloads;
        if (payloads == null || payloads.Count == 0) return;

        for (int i = 0; i < payloads.Count; i++)
        {
            // Pattern 1: PlayerPayload followed by a TextPayload.
            // Standard FFXIV sender format:
            //   [PlayerPayload "First Last"] [TextPayload "First L."]
            if (payloads[i] is PlayerPayload pp)
            {
                string fullName = pp.PlayerName;
                if (string.IsNullOrEmpty(fullName)) continue;

                string nick = GetNickname(fullName);
                if (nick == null) continue;

                if (i + 1 < payloads.Count && payloads[i + 1] is TextPayload tp)
                {
                    if (ChatBubbles.IsShorthandOf(tp.Text, fullName)
                        || string.Equals(tp.Text?.Trim(), fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        tp.Text = nick;
                    }
                }
                continue;
            }

            // Pattern 2: Standalone TextPayload containing a known
            // player name (no preceding PlayerPayload). Some chat
            // formats — especially outgoing tells — may embed the
            // target name as plain text without a PlayerPayload.
            if (payloads[i] is TextPayload standaloneTP)
            {
                var text = standaloneTP.Text;
                if (string.IsNullOrEmpty(text)) continue;

                // Skip if previous payload was already a PlayerPayload
                // (handled by Pattern 1 above).
                if (i > 0 && payloads[i - 1] is PlayerPayload) continue;

                var entries = noWickyXIV.Config.PlayerNicknames;
                if (entries == null) continue;

                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e.PlayerName) || string.IsNullOrEmpty(e.Nickname))
                        continue;
                    if (text.Contains(e.PlayerName, StringComparison.OrdinalIgnoreCase))
                    {
                        standaloneTP.Text = text.Replace(e.PlayerName, e.Nickname, StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                    if (ChatBubbles.IsShorthandOf(text.Trim(), e.PlayerName))
                    {
                        standaloneTP.Text = e.Nickname;
                        break;
                    }
                }
            }
        }
    }

    // ---- ImGui popup ----

    private static void DrawNicknamePopup()
    {
        if (_popupPending)
        {
            ImGui.OpenPopup("##nwx-nickname-popup");
            _popupPending = false;
        }

        bool open = true;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(320, 0));
        if (ImGui.BeginPopupModal("##nwx-nickname-popup", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextDisabled($"Set nickname for: {_popupPlayerName}");
            if (!string.IsNullOrEmpty(_popupWorldName))
                ImGui.TextDisabled($"World: {_popupWorldName}");
            ImGui.Separator();

            if (_popupJustOpened)
            {
                ImGui.SetKeyboardFocusHere();
                _popupJustOpened = false;
            }

            bool enter = ImGui.InputText("##nickname-input", ref _popupNickname, 64,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.Button("OK") || enter)
            {
                ApplyNickname(_popupPlayerName, _popupWorldName, _popupNickname);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                RemoveNickname(_popupPlayerName, _popupWorldName);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private static void ApplyNickname(string playerName, string worldName, string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            RemoveNickname(playerName, worldName);
            return;
        }

        var entries = noWickyXIV.Config.PlayerNicknames;
        var existing = entries.FirstOrDefault(e =>
            string.Equals(e.PlayerName, playerName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(worldName) || string.Equals(e.WorldName, worldName, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            existing.Nickname = nickname;
        }
        else
        {
            entries.Add(new PlayerNicknameEntry
            {
                PlayerName = playerName,
                WorldName  = worldName,
                Nickname   = nickname,
            });
        }

        noWickyXIV.Config.Save();
        MarkDirty();
        DalamudApi.PrintEcho($"Nickname for {playerName} set to \"{nickname}\".");
    }

    private static void RemoveNickname(string playerName, string worldName)
    {
        var entries = noWickyXIV.Config.PlayerNicknames;
        var idx = entries.FindIndex(e =>
            string.Equals(e.PlayerName, playerName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(worldName) || string.Equals(e.WorldName, worldName, StringComparison.OrdinalIgnoreCase)));

        if (idx >= 0)
        {
            entries.RemoveAt(idx);
            noWickyXIV.Config.Save();
            MarkDirty();
            DalamudApi.PrintEcho($"Nickname for {playerName} removed.");
        }
    }

    // ---- Management UI (drawn by PluginUI in the Misc tab) ----

    public static void DrawManagementUI()
    {
        var entries = noWickyXIV.Config.PlayerNicknames;
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("No nicknames set. Right-click a player to assign one.");
            return;
        }

        int removeIdx = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            ImGui.PushID(2000 + i);
            var e = entries[i];

            // Row: "Nickname" → RealName@World  [X]
            ImGui.AlignTextToFramePadding();
            string display = string.IsNullOrEmpty(e.WorldName)
                ? e.PlayerName
                : $"{e.PlayerName}@{e.WorldName}";
            ImGui.Text($"\"{e.Nickname}\"");
            ImGui.SameLine();
            ImGui.TextDisabled($"  ->  {display}");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20);
            if (ImGui.SmallButton("X")) removeIdx = i;

            ImGui.PopID();
        }

        if (removeIdx >= 0)
        {
            entries.RemoveAt(removeIdx);
            noWickyXIV.Config.Save();
            MarkDirty();
        }
    }
}
