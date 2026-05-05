using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace noWickyXIV;

// Fades the FFXIV chat log when the user isn't typing. Optional hover-
// to-show + brighten-on-new-message so you don't miss incoming chat.
//
// Typing detection: RaptureAtkModule.AtkModule.IsTextInputActive — the
// engine's "a text input has focus" flag. When chat input is focused,
// this is true and we hold the chat at full alpha.
//
// Addons covered:
//   ChatLog            — main chat window + input field
//   ChatLogPanel_0..3  — split chat panels (when the user has them
//                        detached via "Chat Filters / Tabs" config)
//
// Alpha is written to the addon's RootNode color channel — the engine
// multiplies it through every child node, so a single byte write per
// addon fades the whole panel including text and the input bar.
public static unsafe class ChatFader
{
    // Fade only the panels that hold the chat lines themselves.
    // ChatLog (the parent addon) hosts the input box, tabs, and
    // surrounding chrome — fading its root would multiply down through
    // every child including the input, making it transparent. Panels
    // are independent addons that contain only the scrolling text
    // area, so fading them dims the chat lines while the input stays
    // fully opaque.
    private static readonly string[] Addons =
    {
        "ChatLogPanel_0",
        "ChatLogPanel_1",
        "ChatLogPanel_2",
        "ChatLogPanel_3",
    };

    // Smoothed displayed alpha. Lerps toward target each frame.
    private static float _currentAlpha = 1f;
    private static bool  _initialized;

    // New-message-brighten: stamped from Update via line-count diff
    // on the chat log's text node. Avoids subscribing to
    // IChatGui.ChatMessage (its delegate signature has shifted across
    // Dalamud versions and we don't want to pin to one). Set to
    // double.MinValue when no recent message; current NowSec() while
    // a "new line was just added" event is active.
    private static double _lastNewMessageT = double.MinValue;
    private static int    _lastChatLineCount = -1;

    public static void Initialize()
    {
        // Nothing to wire up — Update polls the chat log addon's
        // textNode line count to detect new messages without needing
        // a Dalamud event subscription.
    }

    public static void Dispose()
    {
        // Restore chat to full opacity on plugin disable.
        foreach (var name in Addons) ApplyAlpha(name, 255);
    }

    // Called from Update — polls the chat log for new lines arriving.
    // Compares the addon's "TextNode line count" to last frame; on
    // increase, stamp _lastNewMessageT. Cheap (single addon read per
    // frame).
    private static void DetectNewMessages()
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            // Use UldManager.NodeListCount as a coarse proxy for
            // "something changed". On a new chat line the engine
            // mutates the node tree's child count. Not perfect
            // (every addon refresh moves the count) but good enough
            // for a "saw activity, hold visible" signal.
            int count = addon->UldManager.NodeListCount;
            if (_lastChatLineCount < 0)
            {
                _lastChatLineCount = count;
                return;
            }
            if (count != _lastChatLineCount)
            {
                _lastNewMessageT = NowSec();
                _lastChatLineCount = count;
            }
        }
        catch { /* defensive */ }
    }

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableChatFader)
        {
            // Toggling off: snap to fully visible once so the chat
            // doesn't stay faded after disable.
            if (_initialized)
            {
                foreach (var name in Addons) ApplyAlpha(name, 255);
                _initialized = false;
            }
            return;
        }

        if (!_initialized)
        {
            _currentAlpha = 1f;
            _initialized = true;
        }

        // ---- Detect new messages by polling addon node count ----
        DetectNewMessages();

        // ---- Decide target alpha ----
        bool typing  = IsChatInputActive();
        bool hovered = noWickyXIV.Config.ChatFaderHoverActivates && IsCursorOverAnyAddon();

        double now = NowSec();
        bool recentMessage =
            noWickyXIV.Config.ChatFaderHoldOnNewMessageSeconds > 0f
            && (now - _lastNewMessageT) < noWickyXIV.Config.ChatFaderHoldOnNewMessageSeconds;

        bool keepUp = typing || hovered || recentMessage;
        float target = keepUp
            ? MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.ChatFaderActiveAlpha))
            : MathF.Max(0f, MathF.Min(1f, noWickyXIV.Config.ChatFaderIdleAlpha));

        // ---- Lerp ----
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        float rate = MathF.Max(0.5f, noWickyXIV.Config.ChatFaderRate);
        float k = 1f - MathF.Exp(-rate * dt);
        _currentAlpha += (target - _currentAlpha) * k;
        if (MathF.Abs(target - _currentAlpha) < 0.002f) _currentAlpha = target;

        byte alphaByte = (byte)Math.Clamp((int)MathF.Round(_currentAlpha * 255f), 0, 255);
        foreach (var name in Addons)
            ApplyAlpha(name, alphaByte);

        // Minimal mode: hide the chat tabs + the three icon buttons
        // next to them. Each frame we either clear or restore the
        // visibility bit on the known top-bar child nodes; this lets
        // the user toggle freely without needing to /chatconfig
        // reload to restore.
        ApplyMinimalMode();

        // Optional: hide the entire native chat (used in conjunction
        // with the bubble overlay). Clears the visibility bit on
        // ChatLog's RootNode each frame; toggle off restores it.
        ApplyHideNativeChat();
    }

    private static void ApplyHideNativeChat()
    {
        bool hide = noWickyXIV.Config.ChatHideNative;
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            ushort flags = (ushort)addon->RootNode->NodeFlags;
            if (hide && (flags & NODEFLAG_VISIBLE) != 0)
                addon->RootNode->NodeFlags = (NodeFlags)(unchecked((ushort)(flags & ~NODEFLAG_VISIBLE)));
            else if (!hide && (flags & NODEFLAG_VISIBLE) == 0)
                addon->RootNode->NodeFlags = (NodeFlags)(unchecked((ushort)(flags | NODEFLAG_VISIBLE)));
        }
        catch { }
    }

    // True when the engine has any text input focused (chat input,
    // search bar, anywhere). For chat-fade purposes that's good
    // enough — if the user is typing into a text field, holding chat
    // visible is the safer choice anyway.
    private static bool IsChatInputActive()
    {
        try
        {
            var ratk = RaptureAtkModule.Instance();
            if (ratk == null) return false;
            return ratk->AtkModule.IsTextInputActive();
        }
        catch { return false; }
    }

    private static bool IsCursorOverAnyAddon()
    {
        Vector2 cursor;
        try { cursor = ImGui.GetIO().MousePos; }
        catch { return false; }

        foreach (var name in Addons)
        {
            try
            {
                var wrapper = DalamudApi.GameGui.GetAddonByName(name, 1);
                var addr = wrapper.Address;
                if (addr == IntPtr.Zero) continue;
                var addon = (AtkUnitBase*)addr;
                if (addon->RootNode == null) continue;
                if (!addon->IsVisible) continue;

                float x = addon->X;
                float y = addon->Y;
                float s = MathF.Max(0.01f, addon->Scale);
                float w = addon->RootNode->Width  * s;
                float h = addon->RootNode->Height * s;
                if (cursor.X >= x && cursor.X < x + w
                 && cursor.Y >= y && cursor.Y < y + h)
                    return true;
            }
            catch { /* defensive */ }
        }
        return false;
    }

    private static void ApplyAlpha(string addonName, byte alpha)
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName(addonName, 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;
            if (addon->RootNode == null) return;
            addon->RootNode->Color.A = alpha;
        }
        catch { /* defensive — addon may not be loaded yet on early frames */ }
    }

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    // ---- Minimal mode ----
    // Top-bar child node IDs in ChatLog covering the chat tabs + the
    // three icon buttons (chat config gear, log filter, scroll-to-
    // bottom). These IDs are stable within a Dalamud release and have
    // historically been:
    //   1   = root container (do not touch)
    //   2   = main background frame  (do not touch)
    //   3   = chat scroll content    (do not touch — that's the lines)
    //   4   = chat input field       (do not touch)
    //   5..9 = top bar (tabs + icons)
    // If a future game patch shifts these, the user will need to
    // tell us which IDs map to the visible top bar so we can update.
    // Confirmed via runtime dump 2026-05-04:
    //   id=2     top-right icon (settings cog)
    //   id=3     chat-mode dropdown beneath the tabs
    //   id=4     active chat-mode label text
    //   id=7     hidden tab placeholder (kept for safety)
    //   id=10,11 icon button container + child
    //   id=15    bottom-bar collision strip (clickable backdrop)
    //   70001..70004 the four tab slots (battle/general/extras)
    private static readonly uint[] MinimalHideNodeIds =
        { 2, 3, 4, 7, 10, 11, 15, 70001, 70002, 70003, 70004 };
    private const ushort NODEFLAG_VISIBLE = 0x10;

    private static void ApplyMinimalMode()
    {
        bool minimal = noWickyXIV.Config.ChatMinimalMode;
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (AtkUnitBase*)addr;

            foreach (uint id in MinimalHideNodeIds)
            {
                var node = addon->UldManager.SearchNodeById(id);
                if (node == null) continue;
                ushort flags = (ushort)node->NodeFlags;
                if (minimal)
                {
                    // Clear the Visible bit if currently set.
                    if ((flags & NODEFLAG_VISIBLE) != 0)
                        node->NodeFlags = (NodeFlags)(unchecked((ushort)(flags & ~NODEFLAG_VISIBLE)));
                }
                else
                {
                    // Restore the Visible bit if currently cleared.
                    if ((flags & NODEFLAG_VISIBLE) == 0)
                        node->NodeFlags = (NodeFlags)(unchecked((ushort)(flags | NODEFLAG_VISIBLE)));
                }
            }
        }
        catch { /* defensive */ }
    }

    // Dumps every node in the ChatLog addon to PluginLog with its ID
    // and screen rect. Used to identify which node IDs to add to
    // MinimalHideNodeIds when the defaults miss elements.
    public static void DumpChatLogNodeTree()
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero)
            {
                DalamudApi.PluginLog.Information("[noWickyXIV] ChatLog addon not loaded");
                return;
            }
            var addon = (AtkUnitBase*)addr;
            DalamudApi.PluginLog.Information(
                $"[noWickyXIV] ChatLog dump: addon X={addon->X} Y={addon->Y} Scale={addon->Scale}");

            // Walk top-level children via UldManager.NodeList.
            int count = addon->UldManager.NodeListCount;
            DalamudApi.PluginLog.Information($"[noWickyXIV] ChatLog node list count = {count}");
            for (int i = 0; i < count; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n == null) continue;
                bool visible = ((ushort)n->NodeFlags & NODEFLAG_VISIBLE) != 0;
                DalamudApi.PluginLog.Information(
                    $"[noWickyXIV] node[{i}] id={n->NodeId} type={n->Type} " +
                    $"x={n->X:F0} y={n->Y:F0} w={n->Width} h={n->Height} visible={visible}");
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] DumpChatLogNodeTree threw: {ex.Message}"); } catch { }
        }
    }
}
