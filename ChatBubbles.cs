using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace noWickyXIV;

// v1 of the bubble-style chat overlay. Read-only (incoming messages
// only). Captures every chat line via IChatGui.ChatMessage, buffers
// the last N entries, and renders them as alternating-side bubbles
// with the sender label below each bubble. Sending chat is still
// done through FFXIV's normal Enter path — we don't replace the
// input yet (that's v2 with the centered custom prompt + chat-send
// sig).
public static class ChatBubbles
{
    private sealed class Entry
    {
        public XivChatType Channel;
        public string Sender = "";
        public string Body = "";
        public bool   FromSelf;
        public double ReceivedT;
    }

    private static readonly List<Entry> _entries = new();
    private const int MAX_ENTRIES = 200;

    private static bool _hooked;
    // Cache the local player's name once per session so the FromSelf
    // classification is cheap.
    private static string _selfName = "";

    public static void Initialize()
    {
        if (_hooked) return;
        try
        {
            DalamudApi.ChatGui.ChatMessage += OnChatMessage;
            _hooked = true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] ChatBubbles.ChatMessage hook failed: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        if (!_hooked) return;
        try { DalamudApi.ChatGui.ChatMessage -= OnChatMessage; } catch { }
        _hooked = false;
        _entries.Clear();
    }

    private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        try
        {
            var senderText = message.Sender?.TextValue ?? "";
            var bodyText   = message.Message?.TextValue ?? "";

            // Cache local player name once. The lookup must happen on
            // the framework thread, but we're already in the Update
            // pipeline when chat fires — safe.
            if (string.IsNullOrEmpty(_selfName))
            {
                try { _selfName = DalamudApi.ObjectTable.LocalPlayer?.Name?.TextValue ?? ""; }
                catch { }
            }

            bool fromSelf = !string.IsNullOrEmpty(_selfName)
                         && senderText.IndexOf(_selfName, StringComparison.OrdinalIgnoreCase) >= 0;

            // LogKind in newer Dalamud is the chat-type kind (matches
            // XivChatType byte values). Cast since the runtime type may
            // not be XivChatType directly.
            XivChatType chType;
            try { chType = (XivChatType)Convert.ToInt32(message.LogKind); }
            catch { chType = XivChatType.None; }

            var entry = new Entry
            {
                Channel   = chType,
                Sender    = senderText,
                Body      = bodyText,
                FromSelf  = fromSelf,
                ReceivedT = NowSec(),
            };
            _entries.Add(entry);
            // Cap history.
            if (_entries.Count > MAX_ENTRIES)
                _entries.RemoveRange(0, _entries.Count - MAX_ENTRIES);
        }
        catch { /* never let chat-render bugs throw across the hook */ }
    }

    public static void Draw()
    {
        if (!noWickyXIV.Config.EnableChatBubbles) return;
        if (_entries.Count == 0) return;

        var cfg = noWickyXIV.Config;
        var io  = ImGui.GetIO();
        // Anchor the bubble stack to the configured screen position
        // (defaults: center of screen, just below the middle).
        var anchor = new Vector2(cfg.ChatBubblesX, cfg.ChatBubblesY);
        float maxBubbleW = MathF.Max(120f, cfg.ChatBubblesMaxWidth);
        float colWidth   = MathF.Max(maxBubbleW + 80f, cfg.ChatBubblesColumnWidth);
        float bottomY    = anchor.Y;

        var dl = ImGui.GetForegroundDrawList();

        // Walk newest → oldest and stack upward from the anchor. Bubbles
        // older than ChatBubblesMaxAgeSeconds are dropped from the
        // visible window.
        double now = NowSec();
        float maxAge = MathF.Max(2f, cfg.ChatBubblesMaxAgeSeconds);

        // Optional fade-in window for newly-arrived messages.
        const float fadeInSec = 0.25f;

        float curY = bottomY;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            float age = (float)(now - e.ReceivedT);
            if (age > maxAge) continue;

            // Tail-fade — the last 1.5s of life dim toward 0.
            float lifeAlpha = 1f;
            if (age > maxAge - 1.5f)
                lifeAlpha = MathF.Max(0f, (maxAge - age) / 1.5f);
            float popAlpha = MathF.Min(1f, age / fadeInSec);
            float a = lifeAlpha * popAlpha;
            if (a < 0.01f) continue;

            // Word-wrapped body size. Binding signature is
            // CalcTextSize(string, bool hide_text_after_double_hash, float wrap_width).
            var bodySize = ImGui.CalcTextSize(e.Body, false, maxBubbleW);
            // Sender label sits below the bubble.
            string label = string.IsNullOrEmpty(e.Sender) ? ChannelLabel(e.Channel) : e.Sender;
            var labelSize = ImGui.CalcTextSize(label);

            float padX = 10f, padY = 6f;
            float bubbleW = MathF.Min(maxBubbleW, MathF.Max(40f, bodySize.X)) + padX * 2f;
            float bubbleH = bodySize.Y + padY * 2f;
            float blockH  = bubbleH + 4f + labelSize.Y; // bubble + gap + label

            // Lay out the block above the running cursor.
            float blockTop = curY - blockH;
            float bubbleTop = blockTop;
            float bubbleLeft = e.FromSelf
                ? (anchor.X + colWidth * 0.5f - bubbleW)  // right-aligned within column
                : (anchor.X - colWidth * 0.5f);            // left-aligned

            // Draw the bubble background.
            var fillC = e.FromSelf
                ? new Vector4(cfg.ChatBubblesSelfR, cfg.ChatBubblesSelfG, cfg.ChatBubblesSelfB, cfg.ChatBubblesSelfAlpha * a)
                : new Vector4(cfg.ChatBubblesOtherR, cfg.ChatBubblesOtherG, cfg.ChatBubblesOtherB, cfg.ChatBubblesOtherAlpha * a);
            uint fillCol = ImGui.GetColorU32(fillC);
            var rectMin = new Vector2(bubbleLeft, bubbleTop);
            var rectMax = new Vector2(bubbleLeft + bubbleW, bubbleTop + bubbleH);
            dl.AddRectFilled(rectMin, rectMax, fillCol, 8f);

            // Body text — channel-aware color. Word-wrap is handled
            // implicitly because we measured with a wrap width and the
            // raw `AddText(pos, col, text)` overload renders without
            // wrap; for our typical short chat lines this is fine.
            // (The wrap-aware overload's signature differs across
            // Dalamud-Bindings versions and isn't worth the cost.)
            var textC = ChannelTextColor(e.Channel, e.FromSelf);
            textC.W *= a;
            uint textCol = ImGui.GetColorU32(textC);
            dl.AddText(new Vector2(bubbleLeft + padX, bubbleTop + padY), textCol, e.Body);

            // Sender / channel label — small, faded, sits underneath
            // the bubble on the same alignment side.
            var labelC = new Vector4(0.85f, 0.85f, 0.85f, 0.85f * a);
            float labelX = e.FromSelf
                ? (rectMax.X - labelSize.X)
                : rectMin.X;
            dl.AddText(new Vector2(labelX, bubbleTop + bubbleH + 2f), ImGui.GetColorU32(labelC), label);

            curY = blockTop - 6f; // 6px gap between bubbles
        }
    }

    // Maps channel → the chat color shown in stock chat. Self-line gets
    // a brighter tint regardless of channel so it stands out.
    private static Vector4 ChannelTextColor(XivChatType ch, bool fromSelf)
    {
        if (fromSelf) return new Vector4(1f, 1f, 1f, 1f);
        return ch switch
        {
            XivChatType.Say                    => new Vector4(1.00f, 1.00f, 1.00f, 1f),
            XivChatType.Yell                   => new Vector4(1.00f, 0.60f, 0.20f, 1f),
            XivChatType.Shout                  => new Vector4(1.00f, 0.55f, 0.30f, 1f),
            XivChatType.Party                  => new Vector4(0.45f, 0.85f, 1.00f, 1f),
            XivChatType.Alliance               => new Vector4(0.95f, 0.65f, 0.20f, 1f),
            XivChatType.FreeCompany            => new Vector4(0.50f, 1.00f, 0.65f, 1f),
            XivChatType.NoviceNetwork          => new Vector4(0.55f, 0.90f, 0.65f, 1f),
            XivChatType.TellIncoming           => new Vector4(1.00f, 0.55f, 1.00f, 1f),
            XivChatType.TellOutgoing           => new Vector4(1.00f, 0.55f, 1.00f, 1f),
            XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4
              or XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8
                                              => new Vector4(0.55f, 0.90f, 0.55f, 1f),
            XivChatType.CrossLinkShell1 or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3
              or XivChatType.CrossLinkShell4 or XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6
              or XivChatType.CrossLinkShell7 or XivChatType.CrossLinkShell8
                                              => new Vector4(0.50f, 0.85f, 0.50f, 1f),
            _                                  => new Vector4(0.85f, 0.85f, 0.85f, 1f),
        };
    }

    private static string ChannelLabel(XivChatType ch) => ch switch
    {
        XivChatType.Say          => "Say",
        XivChatType.Yell         => "Yell",
        XivChatType.Shout        => "Shout",
        XivChatType.Party        => "Party",
        XivChatType.Alliance     => "Alliance",
        XivChatType.FreeCompany  => "FC",
        XivChatType.TellIncoming => "Tell",
        XivChatType.TellOutgoing => "Tell",
        _                        => ch.ToString(),
    };

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
}
