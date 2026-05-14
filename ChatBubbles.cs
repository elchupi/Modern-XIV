using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ManagedFontAtlas;

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
        // Stable identity used for same-sender merge. Built from the first
        // PlayerPayload (just the player name, no world/icon noise) so
        // two messages from the same player merge even when the surrounding
        // payloads vary (cross-world tags, FC icons, shorthand display
        // forms — all of which can come and go between consecutive lines).
        public string SenderKey = "";
        public string Body = "";
        public bool   FromSelf;
        // ReceivedT — when the bubble first appeared. Drives the
        // 0.25s fade-in animation; we never reset this on merge so
        // an active bubble doesn't blink in and out as new lines are
        // appended.
        public double ReceivedT;
        // LastMessageT — when the most recent line was added. Drives
        // the merge-window check (60s) and the tail-fade-out timer
        // (so the bubble stays alive until X seconds after the LAST
        // line, not the first).
        public double LastMessageT;
        // Wall-clock time the LAST line landed. Used for the "HH:mm"
        // sender label — we want the conversation's most recent
        // activity reflected in the timestamp, not when it started.
        public DateTime LastMessageAt;

        // Latest map / item link payload captured from any message that
        // got merged into this bubble. v1 scope: the bubble surfaces ONE
        // map link and ONE item link — the most recent of each. Click
        // anywhere on the bubble dispatches them. Multi-link-per-bubble
        // would require segment-level word-wrap, which we can layer on
        // later if needed.
        public Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload MapLink;
        public Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload    ItemLink;
    }

    // Same-sender merge window. Consecutive messages within this many
    // seconds from the same (sender, channel, fromSelf) merge into the
    // most recent bubble instead of pushing a new one.
    private const double MERGE_WINDOW_SEC = 60.0;

    // Hover-reveal — when the cursor enters the bubble column rect we
    // refresh this timestamp. While now < _hoverHoldUntilT the entire
    // buffered history renders at full alpha, no max-age cutoff.
    // Messages were never actually deleted (the FIFO buffer holds 200
    // entries) — they just stopped drawing after ChatBubblesMaxAge.
    // Hover bypasses that draw filter so chat history is always one
    // mouse-over away.
    private static double _hoverHoldUntilT = double.MinValue;
    // Smoothed reveal alpha — exp-lerps to 1 while reveal is active,
    // back to 0 otherwise. Multiplies into the per-entry rendered
    // alpha so previously-hidden messages FADE in on hover instead
    // of popping. The same alpha is used for the top-fade mask.
    private static float _revealAlpha;

    // Scroll state. _scrollOffsetTarget is the wheel's raw target
    // (jumps in 120 px steps each tick); _scrollOffsetPx is what the
    // renderer actually uses, exp-lerping toward target each frame
    // so wheel ticks feel smooth instead of stepped.
    private static float _scrollOffsetPx;
    private static float _scrollOffsetTarget;
    private const float SCROLL_SPEED_PX = 120f;
    // Exp-lerp rate (1/s). Higher = snappier; ~14 gives ~70 ms
    // halflife so wheel motion feels responsive but smooth.
    private const float SCROLL_LERP_RATE = 14f;

    // Throttle for the per-frame scroll diagnostic so we don't spam.
    private static double _diagLastLogT;

    // Wheel input is routed in via OnWheel from InputHandler. ImGui's
    // io.MouseWheel only populates while ImGui is capturing the mouse,
    // and our overlay uses a foreground draw list (no capture) — so
    // the wheel never reached us via that path. InputHandler reads
    // the engine's actual wheel state via InputData.GetMouseWheelStatus
    // and forwards it here when the cursor is over the column.
    public static void OnWheel(int direction)
    {
        if (!noWickyXIV.Config.EnableChatBubbles) return;
        // direction is +1 (forward) / -1 (back). Forward = scroll up
        // = see older. Mutate the TARGET, not the displayed offset —
        // the per-frame lerp eases the displayed offset toward target
        // so each wheel tick reads as a smooth glide instead of a
        // discrete jump.
        _scrollOffsetTarget += direction * SCROLL_SPEED_PX;
    }

    // Public hover check used by InputHandler to suppress its wheel
    // handlers (target-cycle, height-offset, etc.) while the cursor
    // is over the bubble column. Without this, scrolling inside the
    // chat would ALSO cycle targets / zoom / change height. The rect
    // is identical to the hover-reveal rect computed in Draw, but
    // exposed here so InputHandler can ask before reading the wheel.
    public static bool IsCursorOverColumn()
    {
        try
        {
            if (!noWickyXIV.Config.EnableChatBubbles) return false;
            var io = ImGui.GetIO();
            var cursor = io.MousePos;

            var cfg = noWickyXIV.Config;
            float maxBubbleW = MathF.Max(120f, cfg.ChatBubblesMaxWidth);
            float colWidth   = MathF.Max(maxBubbleW + 80f, cfg.ChatBubblesColumnWidth);
            float maxColH    = MathF.Max(80f, cfg.ChatBubblesMaxColumnHeight);
            float revealH    = MathF.Max(maxColH, cfg.ChatBubblesHoverRevealHeight);
            float ax = cfg.ChatBubblesX, ay = cfg.ChatBubblesY;
            return cursor.X >= ax - colWidth * 0.5f
                && cursor.X <= ax + colWidth * 0.5f
                && cursor.Y >= ay - revealH
                && cursor.Y <= ay;
        }
        catch { return false; }
    }

    private static readonly List<Entry> _entries = new();
    private const int MAX_ENTRIES = 200;

    private static bool _hooked;
    // Cache the local player's name once per session so the FromSelf
    // classification is cheap.
    private static string _selfName = "";

    // Two cached font handles: one for the bubble body, one for the
    // smaller sender label underneath. Rebuilt only when (path, size)
    // changes so per-frame Draw is cheap. Mirrors the TargetUI font
    // pattern.
    private static Dalamud.Interface.ManagedFontAtlas.IFontHandle _bodyFont;
    private static string _bodyFontPath = "";
    private static float  _bodyFontSize = -1f;
    private static Dalamud.Interface.ManagedFontAtlas.IFontHandle _senderFont;
    private static string _senderFontPath = "";
    private static float  _senderFontSize = -1f;

    // Deferred backfill — schedule a one-shot run ~1.5s after
    // Initialize so the addon and engine modules are ready. Set true
    // once we've actually attempted backfill so we don't repeat.
    private static bool   _backfillAttempted;
    private static double _backfillRunAt;

    public static void Initialize()
    {
        if (_hooked) return;
        try
        {
            DalamudApi.ChatGui.ChatMessage += OnChatMessage;
            _hooked = true;
            _backfillAttempted = false;
            _backfillRunAt = NowSec() + 1.5;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] ChatBubbles.ChatMessage hook failed: {ex.Message}"); } catch { }
        }
    }

    // Tick — called from the main Update pipeline so we can run the
    // deferred backfill once. Cheap when nothing's pending.
    public static void Update()
    {
        if (!_backfillAttempted
            && noWickyXIV.Config.ChatBubblesBackfillOnLoad
            && NowSec() >= _backfillRunAt)
        {
            BackfillFromLogModule();
            _backfillAttempted = true;
        }
    }

    public static void Dispose()
    {
        if (!_hooked) return;
        try { DalamudApi.ChatGui.ChatMessage -= OnChatMessage; } catch { }
        _hooked = false;
        _entries.Clear();
        try { _bodyFont?.Dispose(); }   catch { } _bodyFont = null;
        try { _senderFont?.Dispose(); } catch { } _senderFont = null;
    }

    // Rebuilds the two font handles when their (path, size) changes.
    // Same shape as TargetUI.EnsureFont — atlas builds asynchronously,
    // .Available flips true once the build resolves.
    private static void EnsureFonts()
    {
        var cfg = noWickyXIV.Config;
        EnsureOne(ref _bodyFont,   ref _bodyFontPath,   ref _bodyFontSize,
                  cfg.ChatBubblesFontPath,        cfg.ChatBubblesFontSize);
        EnsureOne(ref _senderFont, ref _senderFontPath, ref _senderFontSize,
                  cfg.ChatBubblesFontPath,        cfg.ChatBubblesSenderFontSize);
    }

    private static void EnsureOne(
        ref Dalamud.Interface.ManagedFontAtlas.IFontHandle handle,
        ref string loadedPath, ref float loadedSize,
        string requestedPath, float requestedSizePx)
    {
        // Quantize size to whole pixels so a slider drag through
        // sub-pixel float values doesn't queue a rebuild every frame.
        // The font atlas can't render fractional pixel sizes anyway.
        float size = MathF.Max(6f, MathF.Round(requestedSizePx));
        string path = requestedPath ?? "";
        if (path == loadedPath && size == loadedSize) return;

        try { handle?.Dispose(); } catch { }
        handle = null;
        loadedPath = path;
        loadedSize = size;

        bool useFile = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);

        try
        {
            var atlas = DalamudApi.PluginInterface.UiBuilder.FontAtlas;
            handle = atlas.NewDelegateFontHandle(e =>
            {
                e.OnPreBuild(tk =>
                {
                    try
                    {
                        if (useFile)
                        {
                            tk.AddFontFromFile(path,
                                new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig
                                {
                                    SizePx      = size,
                                    OversampleH = 2,
                                    OversampleV = 1,
                                });
                        }
                        else
                        {
                            tk.AddDalamudDefaultFont(size);
                        }
                    }
                    catch (Exception inner)
                    {
                        try { DalamudApi.PluginLog.Warning(
                            $"[noWickyXIV] ChatBubbles font OnPreBuild threw: {inner.Message}"); } catch { }
                    }
                });
            });

            // Fire-and-forget log so we know whether the build
            // actually completed (and therefore Push will swap the
            // font on subsequent draws). If the build never resolves
            // we'll see a "FAILED" line.
            WaitForBuildAndLog(handle, path, size);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] ChatBubbles font load failed: path={path} size={size}px — {ex.Message}"); } catch { }
            handle = null;
        }
    }

    // ContinueWith instead of async/await because parent class isn't
    // unsafe — keeping the await-free path is fine here.
    private static void WaitForBuildAndLog(
        Dalamud.Interface.ManagedFontAtlas.IFontHandle h, string path, float size)
    {
        try
        {
            h.WaitAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    try { DalamudApi.PluginLog.Warning(
                        $"[noWickyXIV] ChatBubbles font build FAILED for path='{path}' size={size}px: {t.Exception?.GetBaseException().Message}"); } catch { }
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch { }
    }

    private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        try
        {
            // LogKind in newer Dalamud is the chat-type kind (matches
            // XivChatType byte values).
            XivChatType chType;
            try { chType = (XivChatType)Convert.ToInt32(message.LogKind); }
            catch { chType = XivChatType.None; }

            // Filter out non-conversational channels — system
            // notifications, login/logout banners, NPC announcements,
            // sysmessages, kill feeds, etc. Render only player-to-
            // player chat in the bubble overlay.
            if (!IsConversationalChannel(chType)) return;

            var senderText = ExtractSeStringText(message.Sender);
            var bodyText   = ExtractSeStringText(message.Message);
            var senderKey  = ExtractPlayerKey(message.Sender, senderText);
            var (msgMap, msgItem) = ExtractFirstLinks(message.Message);

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

            double now = NowSec();

            // Same-sender, same-channel merge: if the most recent
            // entry matches AND its last line landed within the merge
            // window, append this line to its body instead of pushing
            // a new bubble. Avoids spamming individual lines for
            // someone typing a multi-line message rapidly.
            if (_entries.Count > 0)
            {
                var last = _entries[_entries.Count - 1];
                // Compare by SenderKey (player name only) so payload
                // variation between consecutive messages doesn't break
                // the merge. Fall back to the full sender text only when
                // a sender has no PlayerPayload (e.g. NPC or system —
                // unlikely here since IsConversationalChannel filtered).
                bool senderMatch = !string.IsNullOrEmpty(senderKey)
                    ? string.Equals(last.SenderKey, senderKey, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(last.Sender,    senderText, StringComparison.OrdinalIgnoreCase);
                if (last.Channel == chType
                    && last.FromSelf == fromSelf
                    && senderMatch
                    && (now - last.LastMessageT) <= MERGE_WINDOW_SEC)
                {
                    last.Body = string.IsNullOrEmpty(last.Body)
                        ? bodyText
                        : last.Body + "\n" + bodyText;
                    last.LastMessageT  = now;
                    last.LastMessageAt = DateTime.Now;
                    // Newest message's link payloads win on merge so the
                    // bubble's click action reflects the most recent
                    // content (typical chat behavior).
                    if (msgMap  != null) last.MapLink  = msgMap;
                    if (msgItem != null) last.ItemLink = msgItem;
                    return;
                }
            }

            var entry = new Entry
            {
                Channel       = chType,
                Sender        = senderText,
                SenderKey     = senderKey,
                Body          = bodyText,
                FromSelf      = fromSelf,
                ReceivedT     = now,
                LastMessageT  = now,
                LastMessageAt = DateTime.Now,
                MapLink       = msgMap,
                ItemLink      = msgItem,
            };
            _entries.Add(entry);
            // Cap history.
            if (_entries.Count > MAX_ENTRIES)
                _entries.RemoveRange(0, _entries.Count - MAX_ENTRIES);
        }
        catch { /* never let chat-render bugs throw across the hook */ }
    }

    public static unsafe void Draw()
    {
        var cfg = noWickyXIV.Config;
        EnsureFonts();

        // Always-on draw: the chat prompt overlay can render even when
        // the bubble feed is empty, since it surfaces what the user is
        // currently typing.
        if (cfg.EnableChatPrompt)
            DrawTypingPrompt(cfg);

        if (!cfg.EnableChatBubbles) return;
        if (_entries.Count == 0) return;

        var io = ImGui.GetIO();
        // Anchor the bubble stack to the configured screen position
        // (defaults: center of screen, just below the middle).
        var anchor = new Vector2(cfg.ChatBubblesX, cfg.ChatBubblesY);
        float maxBubbleW = MathF.Max(120f, cfg.ChatBubblesMaxWidth);
        float colWidth   = MathF.Max(maxBubbleW + 80f, cfg.ChatBubblesColumnWidth);
        float bottomY    = anchor.Y;

        var dl = ImGui.GetForegroundDrawList();

        double now = NowSec();
        float maxAge = MathF.Max(2f, cfg.ChatBubblesMaxAgeSeconds);

        // Container clip: the visible column is bounded by
        // MaxColumnHeight. Anything above colTop gets fully masked.
        float maxColH = MathF.Max(80f, cfg.ChatBubblesMaxColumnHeight);
        float colTopY = anchor.Y - maxColH;

        // Hover detection — cursor inside the column rect (column
        // width × max column height). While hovered (or within the
        // hold window after cursor leaves) every buffered entry can
        // draw, ignoring max-age. Reveal alpha is smoothed so the
        // transition fades rather than pops.
        Vector2 cursor;
        try { cursor = io.MousePos; } catch { cursor = new Vector2(float.MinValue, float.MinValue); }
        float revealRectH = MathF.Max(maxColH, cfg.ChatBubblesHoverRevealHeight);
        var colHoverTL  = new Vector2(anchor.X - colWidth * 0.5f, anchor.Y - revealRectH);
        var colHoverBR  = new Vector2(anchor.X + colWidth * 0.5f, anchor.Y);
        bool hovered = cursor.X >= colHoverTL.X && cursor.X <= colHoverBR.X
                    && cursor.Y >= colHoverTL.Y && cursor.Y <= colHoverBR.Y;
        if (hovered)
            _hoverHoldUntilT = now + MathF.Max(0f, cfg.ChatBubblesHoverHoldSeconds);
        bool revealActive = now < _hoverHoldUntilT;

        // Wheel input is forwarded via ChatBubbles.OnWheel (called
        // from InputHandler when the cursor is over the column).
        // We don't read io.MouseWheel here — Dalamud's ImGui only
        // populates that while it's capturing the mouse, and our
        // overlay uses a foreground draw list (no capture).

        // Reset target + displayed offset once reveal has fully
        // faded out so coming back to chat lands at the newest
        // message.
        if (!revealActive && _revealAlpha < 0.05f)
        {
            _scrollOffsetTarget = 0f;
            _scrollOffsetPx     = 0f;
        }

        // Smooth-lerp the displayed offset toward the wheel's target
        // so each tick reads as a glide instead of a discrete jump.
        {
            float dtS = 0.016f;
            try { dtS = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
            float kS = 1f - MathF.Exp(-SCROLL_LERP_RATE * dtS);
            _scrollOffsetPx += (_scrollOffsetTarget - _scrollOffsetPx) * kS;
            if (MathF.Abs(_scrollOffsetTarget - _scrollOffsetPx) < 0.5f)
                _scrollOffsetPx = _scrollOffsetTarget;
        }

        // Smoothed reveal lerp — eases hidden bubbles in/out instead
        // of popping. Same rate as the chat fader for consistency.
        float dtR = 0.016f;
        try { dtR = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        float revealRate = MathF.Max(1f, noWickyXIV.Config.ChatFaderRate);
        float kReveal = 1f - MathF.Exp(-revealRate * dtR);
        float revealTarget = revealActive ? 1f : 0f;
        _revealAlpha += (revealTarget - _revealAlpha) * kReveal;
        if (MathF.Abs(revealTarget - _revealAlpha) < 0.002f) _revealAlpha = revealTarget;

        // Top-fade gradient: bubbles whose TOP edge sits inside the
        // top fadeHeight band of the container fade toward 0. Beyond
        // colTopY (above the container) → fully masked.
        float fadeBand = MathF.Min(maxColH, MathF.Max(0f, cfg.ChatBubblesTopFadeHeight));

        // Optional fade-in window for newly-arrived messages.
        const float fadeInSec = 0.25f;

        // Clamp the TARGET to a reasonable range so the lerp never
        // chases an out-of-range value. Floor at 0 (can't scroll
        // past newest); upper bound is loose since the per-bubble
        // top-fade mask handles "too far up" cleanly anyway.
        if (_scrollOffsetTarget < 0f) _scrollOffsetTarget = 0f;
        if (_scrollOffsetTarget > 4000f) _scrollOffsetTarget = 4000f;

        // ---- Typing indicators (rtyping IPC) ----
        // Always-reserved bottom band so real bubbles don't shift
        // when typing comes and goes. Width = ChatBubblesTypingReserveHeight.
        // Players are rendered into that band with a smoothed alpha
        // (fade in / fade out via UpdateTypingPresence) — self is
        // never included; the user already sees their own input in
        // the typing-prompt overlay.
        var typingPresence = UpdateTypingPresence();
        float typingReserveH = MathF.Max(0f, cfg.ChatBubblesTypingReserveHeight);

        if (typingPresence.Count > 0)
        {
            int phase = (int)(NowSec() * 3.0) % 3 + 1;
            string dots = new string('•', phase);
            // Stack the indicators upward from anchor.Y so they live
            // INSIDE the reserved band. If they overflow the band,
            // older entries get clipped — typically only one or two
            // players are typing at once so this is rare.
            float typingY = anchor.Y;
            bool typingFontPushed = false;
            try
            {
                if (_bodyFont != null && _bodyFont.Available)
                {
                    _bodyFont.Push();
                    typingFontPushed = true;
                }
                foreach (var (name, alpha) in typingPresence)
                {
                    string label = $"{name} is typing {dots}";
                    var labelSize = ImGui.CalcTextSize(label);
                    float ghostPadX = 10f, ghostPadY = 5f;
                    float ghostW = labelSize.X + ghostPadX * 2f;
                    float ghostH = labelSize.Y + ghostPadY * 2f;
                    typingY -= (ghostH + 4f);
                    if (typingY < anchor.Y - typingReserveH - ghostH) break;

                    float ghostLeft = anchor.X - colWidth * 0.5f; // others always on left
                    var gMin = new Vector2(ghostLeft, typingY);
                    var gMax = new Vector2(ghostLeft + ghostW, typingY + ghostH);

                    var ghostBg = new Vector4(
                        cfg.ChatBubblesOtherR, cfg.ChatBubblesOtherG, cfg.ChatBubblesOtherB,
                        cfg.ChatBubblesOtherAlpha * 0.45f * alpha);
                    if (ghostBg.W >= 0.01f)
                        dl.AddRectFilled(gMin, gMax, ImGui.GetColorU32(ghostBg), 8f);

                    var ghostFg = new Vector4(0.85f, 0.85f, 0.85f, 0.85f * alpha);
                    if (ghostFg.W >= 0.01f)
                        dl.AddText(new Vector2(ghostLeft + ghostPadX, typingY + ghostPadY),
                            ImGui.GetColorU32(ghostFg), label);
                }
            }
            finally
            {
                if (typingFontPushed) _bodyFont?.Pop();
            }
        }

        // Apply scroll: positive offset shifts the entire stack DOWN
        // so older messages move into the visible band from above.
        // Real bubbles start above the RESERVED typing band so the
        // column geometry is stable whether or not anyone is typing.
        float curY = bottomY + _scrollOffsetPx - typingReserveH;

        // Pre-walk the entry list to compute total stack height so
        // the scrollbar thumb size + position is proportional. Push
        // the body font around the measurement so lineH reflects the
        // actual rendered size (without the push, ImGui's default
        // font's lineH was used and the totalStack was underestimated,
        // which clamped maxScroll → 0 and prevented scrolling).
        float totalStackH = 0f;
        bool prewalkFontPushed = false;
        try
        {
            if (_bodyFont != null && _bodyFont.Available)
            {
                _bodyFont.Push();
                prewalkFontPushed = true;
            }
            float lineH = ImGui.GetTextLineHeight();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                int lineCount = 1;
                foreach (var ch in e.Body) if (ch == '\n') lineCount++;
                totalStackH += lineCount * lineH + 12f + 4f + lineH + 6f;
            }
        }
        catch { totalStackH = 0f; }
        finally
        {
            if (prewalkFontPushed) _bodyFont?.Pop();
        }
        float maxScroll = MathF.Max(0f, totalStackH - maxColH);
        // Clamp the TARGET (not the displayed value) so the lerp
        // smoothly eases into the upper bound even when the user
        // wheels well past it. Allow ~200 px overscroll so a fast
        // burst doesn't immediately snap back.
        if (_scrollOffsetTarget > maxScroll + 200f)
            _scrollOffsetTarget = maxScroll + 200f;

        // Click-targets for this frame — bubbles that carry a map or item
        // payload get their rect tracked here. After the draw loop we
        // hit-test the mouse against them and dispatch on left-click.
        var clickTargets = new List<(Vector2 min, Vector2 max, Entry entry)>();

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];

            // Pop-in is timed from when the bubble FIRST appeared —
            // appending more lines via merge must not retrigger the
            // fade-in (would visibly blink).
            float ageFromFirst = (float)(now - e.ReceivedT);
            // Tail fade-out is timed from when the LAST line landed,
            // so a still-active conversation keeps the bubble alive.
            float ageFromLast  = (float)(now - e.LastMessageT);

            // Compute the natural life alpha (within max-age window).
            float naturalLife = 1f;
            bool inLifeWindow = ageFromLast <= maxAge;
            if (inLifeWindow && ageFromLast > maxAge - 1.5f)
                naturalLife = MathF.Max(0f, (maxAge - ageFromLast) / 1.5f);

            // Reveal-alpha (smoothed) extends visibility beyond the
            // life window. While reveal is up, hidden entries fade IN
            // at the reveal rate, not pop.
            float lifeAlpha;
            if (inLifeWindow)
            {
                // Take whichever is HIGHER so a tail-fading bubble
                // smoothly recovers to full while the user hovers.
                lifeAlpha = MathF.Max(naturalLife, _revealAlpha);
            }
            else
            {
                // Already past max-age — only visible via reveal.
                if (_revealAlpha < 0.01f) continue;
                lifeAlpha = _revealAlpha;
            }

            float popAlpha = MathF.Min(1f, ageFromFirst / fadeInSec);
            float a = lifeAlpha * popAlpha;
            if (a < 0.01f) continue;

            // Word-wrap the body to maxBubbleW. The same wrap pass is
            // used by both the size measurement and the per-line
            // render below so layout and rendering can never disagree.
            List<string> bodyLines;
            float bodyW;
            float bodyH;
            float bodyLineH;
            bool bodyFontPushed = false;
            try
            {
                if (_bodyFont != null && _bodyFont.Available)
                {
                    _bodyFont.Push();
                    bodyFontPushed = true;
                }
                bodyLines = WrapText(e.Body, maxBubbleW);
                bodyLineH = ImGui.GetTextLineHeight();
                bodyH     = MathF.Max(1, bodyLines.Count) * bodyLineH;
                bodyW = 0f;
                foreach (var ln in bodyLines)
                {
                    var sz = ImGui.CalcTextSize(ln);
                    if (sz.X > bodyW) bodyW = sz.X;
                }
            }
            finally
            {
                if (bodyFontPushed) _bodyFont?.Pop();
            }
            var bodySize = new Vector2(bodyW, bodyH);

            // Label format:
            //   self  → timestamp only ("12:34")
            //   other → "Sender 12:34"
            // Falls back to channel name if Sender is empty (system
            // notifications shouldn't pass the conversational filter
            // anyway, but defensive).
            string timeStr = e.LastMessageAt != default
                ? e.LastMessageAt.ToString("HH:mm")
                : "";
            string rawName = string.IsNullOrEmpty(e.Sender) ? ChannelLabel(e.Channel) : e.Sender;
            string nameStr = PlayerNicknames.GetNickname(rawName) ?? rawName;
            string label = e.FromSelf
                ? timeStr
                : (string.IsNullOrEmpty(timeStr) ? nameStr : $"{nameStr}  {timeStr}");

            // Channel label: shown ABOVE each bubble so the user can
            // tell at a glance which chat mode the message came in on.
            // Suppressed for Say (default conversational channel — the
            // unmarked baseline) to keep the column from feeling
            // labelled-noisy when most messages are /say. Wrap [Tell]
            // and [FC] in brackets to read as a tag rather than a
            // sender prefix. Gated by config toggle.
            string channelTag = cfg.ChatBubblesShowChannelTag
                ? ChannelTagAbove(e.Channel)
                : "";

            Vector2 labelSize;
            Vector2 channelTagSize = Vector2.Zero;
            bool senderFontPushed = false;
            try
            {
                if (_senderFont != null && _senderFont.Available)
                {
                    _senderFont.Push();
                    senderFontPushed = true;
                }
                labelSize = ImGui.CalcTextSize(label);
                if (!string.IsNullOrEmpty(channelTag))
                    channelTagSize = ImGui.CalcTextSize(channelTag);
            }
            finally
            {
                if (senderFontPushed) _senderFont?.Pop();
            }

            float padX = 10f, padY = 6f;
            float bubbleW = MathF.Min(maxBubbleW, MathF.Max(40f, bodySize.X)) + padX * 2f;
            float bubbleH = bodySize.Y + padY * 2f;
            // Block layout (top-to-bottom):
            //   [channel tag] (only if channelTag is non-empty)
            //   [2px gap]
            //   [bubble body]
            //   [4px gap]
            //   [sender/time label]
            float channelTagH    = channelTagSize.Y;
            float channelTagGap  = string.IsNullOrEmpty(channelTag) ? 0f : 2f;
            float blockH = channelTagH + channelTagGap + bubbleH + 4f + labelSize.Y;

            // Lay out the block above the running cursor. The bubble
            // sits below the channel tag (if any), so its top is
            // offset by the tag's height + gap.
            float blockTop = curY - blockH;
            float bubbleTop = blockTop + channelTagH + channelTagGap;
            float bubbleLeft = e.FromSelf
                ? (anchor.X + colWidth * 0.5f - bubbleW)  // right-aligned within column
                : (anchor.X - colWidth * 0.5f);            // left-aligned

            // Container clip + soft fade gradients on BOTH edges.
            //
            // Top (older messages scrolling up): bubbles whose top
            // edge enters the fadeBand at colTopY are alpha-graded
            // toward 0 so old messages dissolve into the column's
            // top edge instead of hard-clipping. Once a bubble sits
            // entirely above colTopY we stop walking — every older
            // entry is also outside the container.
            //
            // Bottom (newer messages drifting below anchor.Y when
            // the user has scrolled up, and re-entering as they
            // scroll back down): mirror the same fadeBand at the
            // anchor.Y line. Bubbles whose top is past anchor.Y are
            // fully masked (off-screen below). Bubbles straddling
            // the bottom edge fade based on how much of their body
            // sits inside the band. Without this the bottom was a
            // hard cut — chat would pop in/out as the user scrolled
            // back toward the newest message.
            float topMask;
            if (bubbleTop >= colTopY + fadeBand)
            {
                topMask = 1f;
            }
            else if (bubbleTop + bubbleH <= colTopY)
            {
                break;
            }
            else
            {
                topMask = MathF.Max(0f,
                    (bubbleTop - colTopY) / MathF.Max(1f, fadeBand));
            }

            float bottomMask;
            if (bubbleTop >= anchor.Y)
            {
                // Entirely below the column — render with 0 alpha and
                // continue walking. We can't `break` here because the
                // iteration walks NEWEST first (bottom) → older (top),
                // and older bubbles will sit ABOVE this one once the
                // running curY climbs back up.
                bottomMask = 0f;
            }
            else if (bubbleTop + bubbleH <= anchor.Y - fadeBand)
            {
                bottomMask = 1f;
            }
            else
            {
                // Fade based on how far the bubble's TOP is above
                // anchor.Y. When the top is at the band's upper edge
                // (anchor.Y - fadeBand) → fully visible; at anchor.Y
                // → fully masked. Using the top edge keeps the fade
                // monotonic as the bubble slides downward through
                // the band on scroll-up.
                bottomMask = MathF.Max(0f,
                    (anchor.Y - bubbleTop) / MathF.Max(1f, fadeBand));
            }

            a *= MathF.Min(topMask, bottomMask);
            if (a < 0.01f) { curY = blockTop - 6f; continue; }

            // Channel tag — rendered ABOVE the bubble using the
            // channel's text color so the visual link between tag and
            // bubble is immediate. Same alignment as the bubble (right
            // for self, left for other) so it sits flush with the
            // bubble's leading edge.
            if (!string.IsNullOrEmpty(channelTag))
            {
                var tagC = ChannelTextColor(e.Channel, false);
                tagC.W *= 0.7f * a;  // a touch dimmer than body text
                uint tagCol = PackRgba(tagC);
                float tagX = e.FromSelf
                    ? (bubbleLeft + bubbleW - channelTagSize.X)
                    : bubbleLeft;
                bool tagFontPushed = false;
                try
                {
                    if (_senderFont != null && _senderFont.Available)
                    {
                        _senderFont.Push();
                        tagFontPushed = true;
                    }
                    dl.AddText(new Vector2(tagX, blockTop), tagCol, channelTag);
                }
                finally
                {
                    if (tagFontPushed) _senderFont?.Pop();
                }
            }

            // Draw the bubble background. Self / other use their own
            // color + alpha sliders so the user can style them
            // independently. Earlier all bubbles used Other*, which
            // ignored the Self alpha slider entirely (looked stuck at
            // a fixed value regardless of where the user dragged it).
            float fillR, fillG, fillB, fillA;
            if (e.FromSelf)
            {
                fillR = cfg.ChatBubblesSelfR;
                fillG = cfg.ChatBubblesSelfG;
                fillB = cfg.ChatBubblesSelfB;
                fillA = cfg.ChatBubblesSelfAlpha * a;
            }
            else
            {
                fillR = cfg.ChatBubblesOtherR;
                fillG = cfg.ChatBubblesOtherG;
                fillB = cfg.ChatBubblesOtherB;
                fillA = cfg.ChatBubblesOtherAlpha * a;
            }
            var fillC = new Vector4(fillR, fillG, fillB, fillA);
            // Raw RGBA pack — bypasses ImGui.Style.Alpha which Dalamud
            // applies via GetColorU32 and which capped the visible
            // alpha around the user's reported "stuck at 85" value.
            uint fillCol = PackRgba(fillC);
            var rectMin = new Vector2(bubbleLeft, bubbleTop);
            var rectMax = new Vector2(bubbleLeft + bubbleW, bubbleTop + bubbleH);
            dl.AddRectFilled(rectMin, rectMax, fillCol, 8f);

            // Register this bubble as a click target if it carries a link.
            if (e.MapLink != null || e.ItemLink != null)
                clickTargets.Add((rectMin, rectMax, e));

            // Body text — channel-aware color, body font, line by line.
            var textC = ChannelTextColor(e.Channel, e.FromSelf);
            textC.W *= a;
            uint textCol = PackRgba(textC);
            bool bodyPushedDraw = false;
            try
            {
                if (_bodyFont != null && _bodyFont.Available)
                {
                    _bodyFont.Push();
                    bodyPushedDraw = true;
                }
                float lineY = bubbleTop + padY;
                foreach (var line in bodyLines)
                {
                    dl.AddText(new Vector2(bubbleLeft + padX, lineY), textCol, line);
                    lineY += bodyLineH;
                }
            }
            finally
            {
                if (bodyPushedDraw) _bodyFont?.Pop();
            }

            // Sender / channel label — sender font, faded, sits
            // underneath the bubble on the same alignment side.
            // Alpha follows the user's bubble alpha slider (was
            // hard-coded to 0.85 which made the label appear stuck
            // around 50% regardless of slider position).
            float labelAlpha = (e.FromSelf
                ? cfg.ChatBubblesSelfAlpha
                : cfg.ChatBubblesOtherAlpha) * a;
            var labelC = new Vector4(0.85f, 0.85f, 0.85f, labelAlpha);

            // Suppress the sender label when the previous (older) entry
            // in the list is from the same sender — those messages are
            // part of a continuous run and the name would just duplicate
            // immediately above. Always show on entry[0] (no older
            // entry exists) and on the first message of any new sender.
            bool isFirstOfSenderRun = i == 0
                || _entries[i - 1].Sender != e.Sender
                || _entries[i - 1].FromSelf != e.FromSelf;
            if (!isFirstOfSenderRun) labelC.W = 0f;
            float labelX = e.FromSelf
                ? (rectMax.X - labelSize.X)
                : rectMin.X;
            bool senderPushedDraw = false;
            try
            {
                if (_senderFont != null && _senderFont.Available)
                {
                    _senderFont.Push();
                    senderPushedDraw = true;
                }
                if (labelC.W > 0.01f)
                    dl.AddText(new Vector2(labelX, bubbleTop + bubbleH + 2f), PackRgba(labelC), label);
            }
            finally
            {
                if (senderPushedDraw) _senderFont?.Pop();
            }

            curY = blockTop - 6f; // 6px gap between bubbles
        }

        // ---- Bubble click dispatch ----
        // Hit-test the cursor against each registered click target and
        // open the link if the user left-clicked. Skipped while FPS
        // mouselook is active — the OS cursor is hidden and would dispatch
        // accidentally as it drifts around the screen.
        if (clickTargets.Count > 0 && !CameraDynamics.IsMouseLookActive)
        {
            try
            {
                var mp = ImGui.GetIO().MousePos;
                foreach (var (min, max, entry) in clickTargets)
                {
                    if (mp.X < min.X || mp.X >= max.X || mp.Y < min.Y || mp.Y >= max.Y)
                        continue;

                    // Subtle hover indicator — thin border in the channel
                    // text color so it's obvious which bubble's link will
                    // fire. Drawn on the foreground draw list at the same
                    // rounding as the bubble fill.
                    var hoverC = ChannelTextColor(entry.Channel, entry.FromSelf);
                    hoverC.W = 0.85f;
                    dl.AddRect(min, max, PackRgba(hoverC), 8f, 0, 1.5f);
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        try
                        {
                            if (entry.MapLink != null)
                                DalamudApi.GameGui.OpenMapWithMapLink(entry.MapLink);
                            else if (entry.ItemLink != null)
                                PrintItemLinkToChat(entry.ItemLink);
                        }
                        catch (Exception ex)
                        {
                            try { DalamudApi.PluginLog.Warning(
                                $"[noWickyXIV] ChatBubbles link dispatch failed: {ex.Message}"); } catch { }
                        }
                    }
                    break; // only one bubble at a time
                }
            }
            catch { /* defensive */ }
        }

        // No visible scrollbar — wheel scroll is functional via
        // ChatBubbles.OnWheel (InputHandler-routed) but no thumb or
        // track is drawn. The user gets feedback via the bubbles
        // themselves moving as they scroll.
        _ = maxScroll; // keep variable referenced for future use
    }

    // True when `candidate` looks like the shorthand display form of
    // `fullName` that FFXIV's chat sender SeString emits as a TextPayload
    // immediately after the PlayerPayload. Examples:
    //   "Zykov R."  ↔  "Zykov Romanov"      → true
    //   "First L."  ↔  "First Last"         → true
    //   anything else                       → false
    internal static bool IsShorthandOf(string candidate, string fullName)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(fullName)) return false;
        // Whole-name match (some encodings repeat the full name).
        if (string.Equals(candidate.Trim(), fullName.Trim(),
            StringComparison.OrdinalIgnoreCase))
            return true;
        var fullParts = fullName.Trim().Split(' ');
        if (fullParts.Length < 2) return false;
        // Build the expected shorthand: "First L." (first part + space
        // + first letter of last part + period).
        string expected = fullParts[0] + " " + fullParts[fullParts.Length - 1][0] + ".";
        return string.Equals(candidate.Trim(), expected,
            StringComparison.OrdinalIgnoreCase);
    }

    // Pack a Vector4 (0..1 RGBA) into a 0xAABBGGRR uint without going
    // through ImGui.GetColorU32 — that path multiplies by ImGui.Style
    // .Alpha, which Dalamud's theme sets at ~0.85 by default and which
    // capped the visible bubble alpha at the user's reported "stuck
    // at 85" value regardless of the slider position.
    private static uint PackRgba(Vector4 c)
    {
        byte r = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, c.X)) * 255f);
        byte g = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, c.Y)) * 255f);
        byte b = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, c.Z)) * 255f);
        byte a = (byte)MathF.Round(MathF.Max(0f, MathF.Min(1f, c.W)) * 255f);
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    // Maps channel → the chat color shown in stock chat. Same color
    // for self and other so the channel tag/body color is the single
    // source of truth for "what channel was this on".
    private static Vector4 ChannelTextColor(XivChatType ch, bool fromSelf)
    {
        _ = fromSelf;
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

    // Allowlist of player-to-player conversational channels. Anything
    // not in this list (FreeCompanyLoginLogout, NoviceNetworkSystem,
    // Echo, CustomEmote, StandardEmote, all error/system/urgent
    // categories) is silently dropped from the bubble overlay.
    private static bool IsConversationalChannel(XivChatType ch) => ch switch
    {
        XivChatType.Say              => true,
        XivChatType.Yell             => true,
        XivChatType.Shout            => true,
        XivChatType.Party            => true,
        XivChatType.CrossParty       => true,
        XivChatType.Alliance         => true,
        XivChatType.FreeCompany      => true,
        XivChatType.NoviceNetwork    => true,
        XivChatType.TellIncoming     => true,
        XivChatType.TellOutgoing     => true,
        XivChatType.Ls1 or XivChatType.Ls2 or XivChatType.Ls3 or XivChatType.Ls4
          or XivChatType.Ls5 or XivChatType.Ls6 or XivChatType.Ls7 or XivChatType.Ls8 => true,
        XivChatType.CrossLinkShell1 or XivChatType.CrossLinkShell2 or XivChatType.CrossLinkShell3
          or XivChatType.CrossLinkShell4 or XivChatType.CrossLinkShell5 or XivChatType.CrossLinkShell6
          or XivChatType.CrossLinkShell7 or XivChatType.CrossLinkShell8 => true,
        _ => false,
    };

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

    // Short tag rendered above each bubble. Uppercased + bracketed so
    // it reads as a chat-mode marker rather than a sender name.
    // Numbered for linkshells/cwls so the user can tell LS1 from LS3.
    // Returns "" for Say to suppress the tag (Say is the unmarked
    // baseline — labelling every /say bubble adds visual noise).
    private static string ChannelTagAbove(XivChatType ch) => ch switch
    {
        XivChatType.Say              => "",
        XivChatType.Yell             => "[YELL]",
        XivChatType.Shout            => "[SHOUT]",
        XivChatType.Party            => "[PARTY]",
        XivChatType.CrossParty       => "[CWP]",
        XivChatType.Alliance         => "[ALLIANCE]",
        XivChatType.FreeCompany      => "[FC]",
        XivChatType.NoviceNetwork    => "[NN]",
        XivChatType.TellIncoming     => "[TELL]",
        XivChatType.TellOutgoing     => "[TELL]",
        XivChatType.Ls1 => "[LS1]",
        XivChatType.Ls2 => "[LS2]",
        XivChatType.Ls3 => "[LS3]",
        XivChatType.Ls4 => "[LS4]",
        XivChatType.Ls5 => "[LS5]",
        XivChatType.Ls6 => "[LS6]",
        XivChatType.Ls7 => "[LS7]",
        XivChatType.Ls8 => "[LS8]",
        XivChatType.CrossLinkShell1 => "[CWLS1]",
        XivChatType.CrossLinkShell2 => "[CWLS2]",
        XivChatType.CrossLinkShell3 => "[CWLS3]",
        XivChatType.CrossLinkShell4 => "[CWLS4]",
        XivChatType.CrossLinkShell5 => "[CWLS5]",
        XivChatType.CrossLinkShell6 => "[CWLS6]",
        XivChatType.CrossLinkShell7 => "[CWLS7]",
        XivChatType.CrossLinkShell8 => "[CWLS8]",
        _                            => "",
    };

    // Re-emit the clicked item link to the user's chat as a clickable
    // native link. Dalamud doesn't expose a direct "show item tooltip"
    // entry point, so the cleanest "make the bubble's item link useful"
    // path is to echo the link into the standard chat log — from there
    // the player gets the full native interaction (hover for tooltip,
    // click for context menu, send to market board, etc.).
    private static unsafe void PrintItemLinkToChat(
        Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload payload)
    {
        try
        {
            // Look up the item name so the link has visible text. Use the
            // Lumina sheet directly via ItemId (avoids RowRef churn).
            string name = "Item";
            try
            {
                var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                var row = sheet?.GetRow(payload.ItemId);
                if (row.HasValue)
                {
                    var n = row.Value.Name.ExtractText();
                    if (!string.IsNullOrEmpty(n)) name = n;
                }
            }
            catch { }

            // Build the SeString: ItemPayload  TextPayload(name)  end-of-link.
            var se = new Dalamud.Game.Text.SeStringHandling.SeString(
                payload,
                new Dalamud.Game.Text.SeStringHandling.Payloads.UIForegroundPayload(0x0225),
                new Dalamud.Game.Text.SeStringHandling.Payloads.UIGlowPayload(0x0226),
                new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload("" + name + ""),
                Dalamud.Game.Text.SeStringHandling.Payloads.UIGlowPayload.UIGlowOff,
                Dalamud.Game.Text.SeStringHandling.Payloads.UIForegroundPayload.UIForegroundOff,
                Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload.LinkTerminator);
            DalamudApi.ChatGui.Print(new Dalamud.Game.Text.XivChatEntry { Message = se });
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] ChatBubbles PrintItemLinkToChat failed: {ex.Message}"); } catch { }
        }
    }

    // Extract the FIRST map and item link from a message SeString. Used
    // to make bubbles clickable — map link click opens the map at the
    // coordinates; item link click prints the item back to chat via the
    // game's own SeString so the native item-tooltip handler triggers.
    private static (Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload map,
                    Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload   item)
        ExtractFirstLinks(Dalamud.Game.Text.SeStringHandling.SeString seString)
    {
        Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload firstMap = null;
        Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload    firstItem = null;
        try
        {
            if (seString == null) return (null, null);
            foreach (var p in seString.Payloads)
            {
                if (firstMap == null && p is Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload ml)
                    firstMap = ml;
                else if (firstItem == null && p is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload ip)
                    firstItem = ip;
                if (firstMap != null && firstItem != null) break;
            }
        }
        catch { }
        return (firstMap, firstItem);
    }

    // Stable per-sender identity for the same-sender merge. Walks the
    // sender SeString and returns the FIRST PlayerPayload's PlayerName
    // (no world tag, no FC icon, no shorthand TextPayload). This is the
    // one piece of the sender that's consistent across every message a
    // given player sends — the surrounding payloads (cross-world world
    // text, FC channel icons, shorthand display forms) can come and go
    // line-to-line and break a full-string sender comparison.
    //
    // Falls back to the display text when there's no PlayerPayload at
    // all (non-player senders still merge by name).
    private static string ExtractPlayerKey(
        Dalamud.Game.Text.SeStringHandling.SeString seString, string fallbackDisplay)
    {
        try
        {
            if (seString != null)
            {
                foreach (var p in seString.Payloads)
                {
                    if (p is Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload pp)
                    {
                        var nm = pp.PlayerName;
                        if (!string.IsNullOrEmpty(nm)) return nm;
                    }
                }
            }
        }
        catch { }
        return fallbackDisplay ?? "";
    }

    // Convert a SeString to display-friendly text. Direct .TextValue
    // works for plain TextPayloads but produces garbage for messages
    // with attached map links / item links / party-finder links —
    // those payloads include FFXIV private-use icon glyphs (e.g. the
    // map-pin ) that render as tofu boxes in normal fonts. We
    // iterate payloads ourselves and substitute renderable prefixes
    // so map links + co show up legibly in the bubble.
    private static string ExtractSeStringText(Dalamud.Game.Text.SeStringHandling.SeString seString)
    {
        if (seString == null) return "";

        // Walk payloads ourselves and emit the appropriate text for
        // each type. SeString.TextValue is unreliable for link
        // payloads on some Dalamud versions — MapLinkPayload's
        // .Text returns just the placeholder string and the actual
        // region/coords are emitted by surrounding text payloads
        // (which TextValue may or may not include depending on
        // serialization path: live ChatMessage vs LogModule
        // backfill).
        bool hasMapLink = false;
        bool hasItemLink = false;
        var sb = new System.Text.StringBuilder();
        // Sender SeString from chat events typically encodes the
        // player as a PlayerPayload (full name "First Last") followed
        // by a TextPayload with the shortened display form
        // (e.g. "First L."). Both got appended → "First LastFirst L.".
        // Track when we just emitted a PlayerPayload's name and skip
        // the next text payload if it's a prefix-match of that name.
        string lastPlayerName = null;
        try
        {
            foreach (var p in seString.Payloads)
            {
                switch (p)
                {
                    case Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload ml:
                        hasMapLink = true;
                        // MapLinkPayload exposes structured fields —
                        // use them directly so we get the place name
                        // + coords regardless of which surrounding
                        // payloads exist.
                        try
                        {
                            var pn = ml.PlaceName ?? "";
                            var coord = ml.CoordinateString ?? "";
                            if (!string.IsNullOrEmpty(pn) || !string.IsNullOrEmpty(coord))
                                sb.Append($"{pn} {coord}".Trim()).Append(' ');
                        }
                        catch { }
                        break;
                    case Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload ip:
                        hasItemLink = true;
                        try
                        {
                            // Look up the item name from the Lumina
                            // Item sheet directly via ItemId — avoids
                            // RowRef API churn between Dalamud
                            // versions and works with both regular
                            // and HQ item IDs.
                            var sheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                            var row = sheet?.GetRow(ip.ItemId);
                            if (row.HasValue)
                            {
                                var name = row.Value.Name.ExtractText() ?? "";
                                if (!string.IsNullOrEmpty(name))
                                    sb.Append(name).Append(' ');
                            }
                        }
                        catch { }
                        break;
                    case Dalamud.Game.Text.SeStringHandling.Payloads.IconPayload _:
                        // Skip — game-icon glyph in private-use area.
                        break;
                    case Dalamud.Game.Text.SeStringHandling.Payloads.AutoTranslatePayload atp:
                        try { sb.Append(atp.Text ?? ""); } catch { }
                        break;
                    case Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload pp:
                        try
                        {
                            var nm = pp.PlayerName ?? "";
                            sb.Append(nm);
                            lastPlayerName = nm;
                        }
                        catch { lastPlayerName = null; }
                        break;
                    case Dalamud.Game.Text.SeStringHandling.ITextProvider tp:
                        try
                        {
                            var t = tp.Text ?? "";
                            // Skip the duplicate shorthand display name
                            // FFXIV emits right after a PlayerPayload
                            // (e.g. "Zykov R." after "Zykov Romanov").
                            // Heuristic: starts with the same first
                            // word as the PlayerPayload AND is short.
                            if (lastPlayerName != null
                                && IsShorthandOf(t, lastPlayerName))
                            {
                                lastPlayerName = null;
                                break;
                            }
                            sb.Append(t);
                            lastPlayerName = null;
                        }
                        catch { }
                        break;
                }
            }
        }
        catch
        {
            // Payload walk failed entirely — fall back to TextValue
            // so we at least show *something*.
            try { sb.Append(seString.TextValue ?? ""); } catch { }
        }
        string raw = sb.ToString();

        // Strip FFXIV's private-use icon glyphs (U+E000..U+F8FF).
        // They aren't in any standard text font, so emitting them
        // gives tofu boxes. Whitespace and surrounding text is kept.
        var stripped = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch >= '' && ch <= '') continue;
            stripped.Append(ch);
        }
        string clean = stripped.ToString();
        // Collapse runs of whitespace introduced by the icon strip.
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        clean = clean.Trim();

        if (hasMapLink) clean = "[map] " + clean;
        else if (hasItemLink) clean = "[item] " + clean;

        return clean;
    }

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    // Custom typing prompt. While a text input is focused (engine
    // flag), render a centered overlay box showing the current input
    // buffer text. Reads the chat input addon's TextNode (id=5 child)
    // each frame to mirror what the user is typing.
    // Tracks whether Ctrl+A was already handled this keypress so
    // holding Ctrl+A doesn't fire repeatedly on every frame; we
    // re-arm once both keys are released.
    private static bool _ctrlAHandled;

    private static unsafe void DrawTypingPrompt(Configuration cfg)
    {
        bool active = false;
        try
        {
            var ratk = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
            if (ratk != null) active = ratk->AtkModule.IsTextInputActive();
        }
        catch { return; }

        // Ctrl+A: select-all in the chat input. FFXIV's native chat
        // input doesn't do this on its own — pressing Ctrl+A while
        // typing has no engine-side effect. We detect the chord via
        // ImGui IO and write directly to the AtkComponentInputBase
        // selection fields so the entire current text becomes
        // highlighted (next type/cut replaces it, expected behaviour).
        if (active)
        {
            try
            {
                var io = ImGui.GetIO();
                bool ctrl = io.KeyCtrl;
                bool aDown = io.KeysDown[(int)ImGuiKey.A];
                if (ctrl && aDown && !_ctrlAHandled)
                {
                    SelectAllChatInput();
                    _ctrlAHandled = true;
                }
                else if (!ctrl || !aDown)
                {
                    _ctrlAHandled = false;
                }
            }
            catch { }
        }
        else
        {
            _ctrlAHandled = false;
        }

        // Exp-lerp visibility toward target. Reuse the chat-fader rate
        // so prompt fade and panel fade feel the same.
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        float rate = MathF.Max(0.5f, noWickyXIV.Config.ChatFaderRate);
        float k = 1f - MathF.Exp(-rate * dt);
        float target = active ? 1f : 0f;
        _promptAlpha += (target - _promptAlpha) * k;
        if (MathF.Abs(target - _promptAlpha) < 0.002f) _promptAlpha = target;

        // Cheap bail when fully faded out.
        if (_promptAlpha < 0.01f) return;

        // Pull the current input text from the chat-input AtkComponent.
        string typed = "";
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr != IntPtr.Zero)
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addr;
                var inputNode = addon->UldManager.SearchNodeById(5);
                if (inputNode != null && (int)inputNode->Type >= 1000)
                {
                    // Component-typed nodes wrap a Component pointer.
                    var compNode = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode*)inputNode;
                    if (compNode->Component != null)
                    {
                        var ti = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentTextInput*)compNode->Component;
                        if (ti != null)
                            typed = ti->AtkComponentInputBase.RawString.ToString() ?? "";
                    }
                }
            }
        }
        catch { /* best-effort — fall through with empty text */ }

        // Render a centered box.
        var dl = ImGui.GetForegroundDrawList();
        float w = MathF.Max(120f, cfg.ChatPromptWidth);
        var center = new Vector2(cfg.ChatPromptX, cfg.ChatPromptY);

        // Use the bubble body font for consistency, sized to
        // ChatPromptFontSize via a separate handle.
        EnsurePromptFont(cfg.ChatBubblesFontPath, cfg.ChatPromptFontSize);
        bool pushed = false;
        try
        {
            if (_promptFont != null && _promptFont.Available)
            {
                _promptFont.Push();
                pushed = true;
            }
            string display = string.IsNullOrEmpty(typed) ? "Type a message…" : typed;
            // Word-wrap to (width − 24px padding).
            float innerW = MathF.Max(40f, w - 24f);
            var lines = WrapText(display, innerW);
            float lineH = ImGui.GetTextLineHeight();
            float blockH = MathF.Max(1, lines.Count) * lineH;

            float h = blockH + 16f; // top/bottom padding
            var topLeft  = new Vector2(center.X - w * 0.5f, center.Y - h * 0.5f);
            var botRight = new Vector2(topLeft.X + w, topLeft.Y + h);

            // _promptAlpha (0..1) modulates everything so the box
            // fades in and out smoothly.
            var bg = new Vector4(cfg.ChatPromptBgR, cfg.ChatPromptBgG, cfg.ChatPromptBgB,
                                 cfg.ChatPromptBgAlpha * _promptAlpha);
            dl.AddRectFilled(topLeft, botRight, ImGui.GetColorU32(bg), 8f);
            dl.AddRect(topLeft, botRight,
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, cfg.ChatPromptBgAlpha * _promptAlpha)), 8f);

            float baseTextA = string.IsNullOrEmpty(typed) ? 0.55f : 1f;
            var textC = new Vector4(
                cfg.ChatPromptTextR, cfg.ChatPromptTextG, cfg.ChatPromptTextB,
                cfg.ChatPromptTextAlpha * baseTextA * _promptAlpha);
            uint textCol = ImGui.GetColorU32(textC);

            float cursorY = topLeft.Y + 8f;
            float lineX   = topLeft.X + 12f;
            foreach (var line in lines)
            {
                dl.AddText(new Vector2(lineX, cursorY), textCol, line);
                cursorY += lineH;
            }
        }
        finally
        {
            if (pushed) _promptFont?.Pop();
        }
    }

    // Forces a "select all" on the chat input by writing directly to
    // the AtkComponentInputBase fields: SelectionStart=0, SelectionEnd
    // and CursorPos to the end of the current text. Engine renders
    // the selected range highlighted on the next paint pass.
    private static unsafe void SelectAllChatInput()
    {
        try
        {
            var wrapper = DalamudApi.GameGui.GetAddonByName("ChatLog", 1);
            var addr = wrapper.Address;
            if (addr == IntPtr.Zero) return;
            var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addr;
            var inputNode = addon->UldManager.SearchNodeById(5);
            if (inputNode == null || (int)inputNode->Type < 1000) return;
            var compNode = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentNode*)inputNode;
            if (compNode->Component == null) return;
            var ti = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentTextInput*)compNode->Component;
            if (ti == null) return;

            // Length of the current input in chars. RawString may
            // contain SeString payload bytes, but for cursor/selection
            // the engine works in character indices — RawString.Length
            // is the closest cheap approximation we can write.
            int len = 0;
            try { len = ti->AtkComponentInputBase.RawString.ToString()?.Length ?? 0; }
            catch { }
            ti->AtkComponentInputBase.SelectionStart = 0;
            ti->AtkComponentInputBase.SelectionEnd   = len;
            ti->AtkComponentInputBase.CursorPos      = len;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] SelectAllChatInput threw: {ex.Message}"); } catch { }
        }
    }

    // ---- Backfill from RaptureLogModule.LogMessageData ----
    // Reads the engine's running chat history into our buffer so the
    // overlay isn't blank on plugin load. Format is column-packed,
    // separated by 0x1F (LogMessageDataTerminator). Per-message
    // columns we use:
    //   0: chat type byte (XivChatType)
    //   1: timestamp (ignored — we synthesize ordering instead)
    //   2: sender (SeString bytes)
    //   3: body   (SeString bytes)
    // Anything past column 3 (filter id, world, item-link metadata)
    // is read as bytes for column-walking but not interpreted.
    //
    // SeString payloads are decoded via Dalamud's SeString.Parse so
    // item-link / glamour-link / autotrans tags resolve to text.
    private static unsafe void BackfillFromLogModule()
    {
        try
        {
            var rlm = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureLogModule.Instance();
            if (rlm == null) return;
            int count = rlm->LogMessageCount;
            if (count <= 0) return;

            int start = Math.Max(0, count - MAX_ENTRIES);
            string selfName = "";
            try { selfName = DalamudApi.ObjectTable.LocalPlayer?.Name?.TextValue ?? ""; } catch { }

            double now = NowSec();
            float maxAge = MathF.Max(2f, noWickyXIV.Config.ChatBubblesMaxAgeSeconds);
            // Synthesize timestamps PAST the max-age cutoff so backfill
            // is hidden by default (only revealed on hover) — matches
            // the user's mental model of "this is history, not new chat."
            // Spread across (now - maxAge - 1) downward by 0.1s/entry
            // so ordering is preserved.
            double baseT = now - maxAge - 1.0;

            int loaded = 0;
            int skipped = 0;
            // LogMessageData / LogMessageIndex are StdVector<>s in
            // current FFXIVClientStructs. Read them as pointer-to-
            // first + element count.
            byte* dataBase = rlm->LogMessageData.First;
            long dataLen = (long)rlm->LogMessageData.LongCount;
            long indexLen = (long)rlm->LogMessageIndex.LongCount;
            _ = dataLen;

            // Walk newest-first so merge logic works exactly like the
            // live path — but we still need to insert oldest-first
            // into _entries so display order is correct. Build to a
            // local list and prepend at the end.
            var fresh = new List<Entry>();

            for (int i = start; i < count && i < indexLen; i++)
            {
                long offset = rlm->LogMessageIndex.First[i];
                long endOff = (i + 1 < count && i + 1 < indexLen)
                    ? rlm->LogMessageIndex.First[i + 1] : -1;
                if (offset < 0) { skipped++; continue; }
                byte* ptr = dataBase + offset;
                byte* end = endOff > 0 ? dataBase + endOff
                                       : ptr + 4096; // safety cap per message

                // Walk columns separated by 0x1F.
                var cols = new List<byte[]>(8);
                byte* colStart = ptr;
                while (ptr < end && cols.Count < 8)
                {
                    byte b = *ptr;
                    if (b == 0x1F)
                    {
                        int len = (int)(ptr - colStart);
                        var arr = new byte[len];
                        if (len > 0)
                            System.Runtime.InteropServices.Marshal.Copy((IntPtr)colStart, arr, 0, len);
                        cols.Add(arr);
                        ptr++;
                        colStart = ptr;
                    }
                    else
                    {
                        ptr++;
                    }
                }
                if (cols.Count < 4) { skipped++; continue; }

                // Column 0: type byte.
                XivChatType chType = XivChatType.None;
                if (cols[0].Length > 0) chType = (XivChatType)cols[0][0];
                if (!IsConversationalChannel(chType)) { skipped++; continue; }

                // Columns 2/3 — sender and body. Decode SeString to
                // get the visible text value (resolves item links etc.)
                string senderText, bodyText, senderKey;
                Dalamud.Game.Text.SeStringHandling.SeString senderSe = null;
                Dalamud.Game.Text.SeStringHandling.SeString bodySe   = null;
                try
                {
                    senderSe = Dalamud.Game.Text.SeStringHandling.SeString.Parse(cols[2]);
                    senderText = ExtractSeStringText(senderSe);
                }
                catch { senderText = ""; }
                try
                {
                    bodySe = Dalamud.Game.Text.SeStringHandling.SeString.Parse(cols[3]);
                    bodyText = ExtractSeStringText(bodySe);
                }
                catch { bodyText = ""; }
                senderKey = ExtractPlayerKey(senderSe, senderText);
                var (bfMap, bfItem) = ExtractFirstLinks(bodySe);
                if (string.IsNullOrEmpty(bodyText)) { skipped++; continue; }

                bool fromSelf = !string.IsNullOrEmpty(selfName)
                             && senderText.IndexOf(selfName, StringComparison.OrdinalIgnoreCase) >= 0;

                double t = baseT - (count - i) * 0.1; // older = lower t

                // Same-sender merge inside backfill list (matches the
                // live OnChatMessage merge so consecutive lines from
                // one person collapse). Uses SenderKey (player name only)
                // so payload variation can't split the merge.
                // Best-effort wall-clock for the label. The engine's
                // log entries don't expose a parseable timestamp via
                // a public API we trust, so we approximate by walking
                // back from "now" using the same per-entry stride
                // we use for the synthetic ReceivedT. Older entries
                // get earlier-looking times.
                var wallClock = DateTime.Now.AddSeconds(-((count - i) * 0.1) - maxAge - 1.0);

                if (fresh.Count > 0)
                {
                    var last = fresh[fresh.Count - 1];
                    bool senderMatch = !string.IsNullOrEmpty(senderKey)
                        ? string.Equals(last.SenderKey, senderKey, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(last.Sender,    senderText, StringComparison.OrdinalIgnoreCase);
                    if (last.Channel == chType && last.FromSelf == fromSelf
                        && senderMatch
                        && (t - last.LastMessageT) <= MERGE_WINDOW_SEC)
                    {
                        last.Body = string.IsNullOrEmpty(last.Body) ? bodyText : last.Body + "\n" + bodyText;
                        last.LastMessageT  = t;
                        last.LastMessageAt = wallClock;
                        if (bfMap  != null) last.MapLink  = bfMap;
                        if (bfItem != null) last.ItemLink = bfItem;
                        loaded++;
                        continue;
                    }
                }

                fresh.Add(new Entry
                {
                    Channel       = chType,
                    Sender        = senderText,
                    SenderKey     = senderKey,
                    Body          = bodyText,
                    FromSelf      = fromSelf,
                    ReceivedT     = t,
                    LastMessageT  = t,
                    LastMessageAt = wallClock,
                    MapLink       = bfMap,
                    ItemLink      = bfItem,
                });
                loaded++;
            }

            // Splice backfilled entries in front of any live entries
            // that may have arrived during the deferred wait.
            if (fresh.Count > 0)
                _entries.InsertRange(0, fresh);
            if (_entries.Count > MAX_ENTRIES)
                _entries.RemoveRange(0, _entries.Count - MAX_ENTRIES);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning($"[noWickyXIV] ChatBubbles backfill failed: {ex.Message}"); } catch { }
        }
    }

    // ---- rtyping IPC integration ----
    // The rtyping plugin broadcasts typing presence over Dalamud IPC.
    // We poll its channels each frame to find who's currently typing
    // (self + each party member) and render ghost bubbles below the
    // real-message stack. Channels and signatures cribbed directly
    // from rtyping/IpcController.cs.
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<bool>           _rtConnected;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<bool>           _rtSelfTyping;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<int, bool>      _rtPartyByIndex;

    private static void EnsureRTypingIpc()
    {
        if (_rtConnected != null) return;
        try
        {
            var pi = DalamudApi.PluginInterface;
            _rtConnected    = pi.GetIpcSubscriber<bool>("RTyping.Connected");
            _rtSelfTyping   = pi.GetIpcSubscriber<bool>("RTyping.Status.GetSelf");
            _rtPartyByIndex = pi.GetIpcSubscriber<int, bool>("RTyping.Status.PartyMember.Index");
        }
        catch { /* rtyping not installed or IPC contract changed — fall back to no-op */ }
    }

    // Per-player typing presence — smoothed alpha lerps toward 1 when
    // the player is actively typing, back to 0 when they stop. The
    // bubble draw uses the smoothed value so transitions fade rather
    // than pop. Entries linger until alpha hits 0 then are dropped.
    private sealed class TypingState
    {
        public bool  IsTyping;
        public float Alpha;
    }
    private static readonly Dictionary<string, TypingState> _typingByName = new();

    // Pulls "is typing" presence from rtyping's IPC for every party
    // member except self, then lerps each known player's smoothed
    // alpha toward their current state. Returns the entries with
    // non-trivial alpha so the renderer knows what to draw.
    private static List<(string name, float alpha)> UpdateTypingPresence()
    {
        if (!noWickyXIV.Config.EnableTypingIndicators)
        {
            _typingByName.Clear();
            return new List<(string, float)>();
        }
        EnsureRTypingIpc();

        bool connected = false;
        if (_rtConnected != null)
        {
            try { connected = _rtConnected.InvokeFunc(); } catch { connected = false; }
        }

        // Build the "currently typing" set this frame. Self is
        // intentionally excluded — the user already sees their own
        // text in the typing-prompt overlay; a duplicate "You are
        // typing" indicator is noise.
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string selfName = "";
        try { selfName = DalamudApi.ObjectTable.LocalPlayer?.Name?.TextValue ?? ""; } catch { }

        if (connected)
        {
            try
            {
                var party = DalamudApi.PartyList;
                int n = party?.Length ?? 0;
                for (int i = 0; i < n; i++)
                {
                    bool typing = false;
                    try { typing = _rtPartyByIndex != null && _rtPartyByIndex.InvokeFunc(i); } catch { }
                    if (!typing) continue;
                    var pm = party[i];
                    var nm = pm?.Name?.TextValue ?? "";
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase)) continue; // skip self
                    current.Add(nm);
                }
            }
            catch { }
        }

        // Update presence for everyone in `current` (typing) AND
        // anyone we've seen recently who is no longer typing (so we
        // can fade their indicator out).
        foreach (var nm in current)
        {
            if (!_typingByName.TryGetValue(nm, out var st))
            {
                st = new TypingState();
                _typingByName[nm] = st;
            }
            st.IsTyping = true;
        }
        // Mark known-but-no-longer-typing as not typing.
        foreach (var kv in _typingByName)
        {
            if (!current.Contains(kv.Key)) kv.Value.IsTyping = false;
        }

        // Lerp each entry's alpha toward target.
        float dt = 0.016f;
        try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
        float rate = MathF.Max(2f, noWickyXIV.Config.ChatFaderRate);
        float k = 1f - MathF.Exp(-rate * dt);

        var result = new List<(string, float)>();
        var toRemove = new List<string>();
        foreach (var kv in _typingByName)
        {
            float target = kv.Value.IsTyping ? 1f : 0f;
            kv.Value.Alpha += (target - kv.Value.Alpha) * k;
            if (MathF.Abs(target - kv.Value.Alpha) < 0.002f) kv.Value.Alpha = target;

            if (kv.Value.Alpha >= 0.01f)
                result.Add((kv.Key, kv.Value.Alpha));
            else if (!kv.Value.IsTyping && kv.Value.Alpha < 0.001f)
                toRemove.Add(kv.Key);
        }
        foreach (var k2 in toRemove) _typingByName.Remove(k2);
        return result;
    }

    private static IFontHandle _promptFont;
    private static string _promptFontPath = "";
    private static float  _promptFontSize = -1f;
    private static void EnsurePromptFont(string path, float size)
    {
        EnsureOne(ref _promptFont, ref _promptFontPath, ref _promptFontSize, path, size);
    }

    // Manual word-wrap. The Dalamud-Bindings overload of AddText that
    // accepts a wrap width has a signature mismatch in our build, so
    // we wrap by walking words and measuring with the currently-pushed
    // font (caller is responsible for Push/Pop around the call).
    // Splits long words that don't fit on a single line by character.
    private static List<string> WrapText(string text, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
        {
            if (!string.IsNullOrEmpty(text)) lines.Add(text);
            return lines;
        }

        // Preserve explicit \n breaks first, then word-wrap each piece.
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }

            var words = paragraph.Split(' ');
            string current = "";
            foreach (var word in words)
            {
                string trial = current.Length == 0 ? word : current + " " + word;
                if (ImGui.CalcTextSize(trial).X <= maxWidth)
                {
                    current = trial;
                }
                else
                {
                    if (current.Length > 0) lines.Add(current);

                    // Word itself doesn't fit — break by character.
                    if (ImGui.CalcTextSize(word).X > maxWidth)
                    {
                        string buf = "";
                        foreach (var ch in word)
                        {
                            string trial2 = buf + ch;
                            if (ImGui.CalcTextSize(trial2).X <= maxWidth) buf = trial2;
                            else
                            {
                                if (buf.Length > 0) lines.Add(buf);
                                buf = ch.ToString();
                            }
                        }
                        current = buf;
                    }
                    else
                    {
                        current = word;
                    }
                }
            }
            if (current.Length > 0) lines.Add(current);
        }
        return lines;
    }

    // Smoothed prompt visibility — lerps to 1 while chat input is
    // focused, back to 0 when it's not. Same exp-lerp pattern used
    // elsewhere; rate matches noWickyXIV.Config.ChatFaderRate so the
    // prompt and the panel-fade share a feel.
    private static float _promptAlpha;
}
