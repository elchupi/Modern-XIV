using System;
using System.Numerics;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Dalamud.Bindings.ImGui;

public static partial class ImGuiEx
{
    // Hover tooltip — custom-rendered north of the hovered item
    // with an exp-lerp fade-in/out. Replaces the stock
    // ImGui.SetTooltip which renders bottom-right of the cursor
    // with no fade.
    //
    // Rendering is deferred to RenderPendingTooltip() so we can
    // fade alpha across frames; this method just captures the
    // hovered item's anchor (top-center of its screen rect) and
    // the text. PluginUI.Draw calls RenderPendingTooltip once at
    // the end of each UI frame so the tooltip lives on the
    // foreground draw list (above all windows).
    private static string  _pendingTooltipText  = "";
    private static Vector2 _pendingTooltipAnchor;
    private static int     _pendingTooltipLastHoverFrame = -2;
    private static int     _pendingTooltipPrevHoverFrame = -2;
    private static float   _tooltipAlpha;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetItemTooltip(string s, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None)
    {
        if (!ImGui.IsItemHovered(flags) || string.IsNullOrEmpty(s)) return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        CaptureTooltip(s, new Vector2((min.X + max.X) * 0.5f, min.Y));
    }

    // Force-capture path — caller has already determined the hover
    // is real (e.g. via IsMouseHoveringRect on a manually-placed
    // rect outside the window content region, where IsItemHovered
    // is unreliable). Used by AddHeaderIcon for title-bar buttons.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CaptureTooltip(string s, Vector2 anchor)
    {
        if (string.IsNullOrEmpty(s)) return;
        _pendingTooltipText = s;
        _pendingTooltipAnchor = anchor;
        _pendingTooltipLastHoverFrame = ImGui.GetFrameCount();
    }

    // Call this once at the end of the plugin's UI frame so the
    // tooltip renders on top of all windows, fades smoothly, and
    // hovers above the last hovered item.
    public static void RenderPendingTooltip()
    {
        // Hover is "active this frame" if a SetItemTooltip captured
        // it on the current OR previous frame (one-frame slack so
        // moving between adjacent items doesn't make the tooltip
        // start fading instantly).
        int curFrame = ImGui.GetFrameCount();
        bool active = _pendingTooltipLastHoverFrame >= curFrame - 1
                   && !string.IsNullOrEmpty(_pendingTooltipText);

        // Fresh-session reset: if we're activating now but the last
        // capture was more than 3 frames ago, treat it as a brand-
        // new hover and reset alpha to 0 so the user actually sees
        // the fade-in. Without this, alpha stays high while the
        // cursor sweeps between adjacent items and every "new"
        // tooltip pops in instantly. 3 frames @ 60Hz ≈ 50 ms — long
        // enough that a sub-pixel cursor flick between items
        // doesn't trigger a re-fade, short enough that any actual
        // "leave then come back" gesture does.
        if (active && _pendingTooltipLastHoverFrame >= curFrame - 1
                   && _pendingTooltipPrevHoverFrame < curFrame - 3)
        {
            _tooltipAlpha = 0f;
        }
        _pendingTooltipPrevHoverFrame = _pendingTooltipLastHoverFrame;

        // Exp-lerp alpha. ~9/s rate (~77 ms halflife) — visibly
        // fades in (reaches ~0.5 in 75 ms, ~0.9 in 250 ms) without
        // being so slow the user thinks the tooltip never showed up.
        float dt = 0.016f;
        try { dt = ImGui.GetIO().DeltaTime; } catch { }
        float k = 1f - MathF.Exp(-9f * dt);
        float target = active ? 1f : 0f;
        _tooltipAlpha += (target - _tooltipAlpha) * k;
        if (MathF.Abs(target - _tooltipAlpha) < 0.002f) _tooltipAlpha = target;

        if (_tooltipAlpha < 0.01f || string.IsNullOrEmpty(_pendingTooltipText)) return;

        var dl = ImGui.GetForegroundDrawList();
        var ts = ImGui.CalcTextSize(_pendingTooltipText, false, 320f);
        const float padX = 8f, padY = 5f, gap = 8f;
        float w = ts.X + padX * 2f;
        float h = ts.Y + padY * 2f;

        // Position above the anchor; clamp to viewport so anchors
        // near the screen edge don't push the tooltip off-screen.
        // If there's no room above, fall back to below.
        var vp = ImGui.GetMainViewport();
        float left = _pendingTooltipAnchor.X - w * 0.5f;
        float top  = _pendingTooltipAnchor.Y - h - gap;
        if (left < vp.Pos.X + 4f) left = vp.Pos.X + 4f;
        if (left + w > vp.Pos.X + vp.Size.X - 4f) left = vp.Pos.X + vp.Size.X - 4f - w;
        if (top < vp.Pos.Y + 4f) top = _pendingTooltipAnchor.Y + gap;

        var tl = new Vector2(left, top);
        var br = new Vector2(left + w, top + h);

        var bg = new Vector4(0.05f, 0.05f, 0.07f, 0.92f * _tooltipAlpha);
        dl.AddRectFilled(tl, br, ImGui.GetColorU32(bg), 6f);
        var border = new Vector4(0.4f, 0.4f, 0.45f, 0.8f * _tooltipAlpha);
        dl.AddRect(tl, br, ImGui.GetColorU32(border), 6f);

        var fg = new Vector4(1f, 1f, 1f, _tooltipAlpha);
        dl.AddText(new Vector2(left + padX, top + padY),
            ImGui.GetColorU32(fg), _pendingTooltipText);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsItemDoubleClicked(ImGuiMouseButton button = ImGuiMouseButton.Left, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None) =>
        ImGui.IsMouseDoubleClicked(button) && ImGui.IsItemHovered(flags);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsItemReleased(ImGuiMouseButton button = ImGuiMouseButton.Left, ImGuiHoveredFlags flags = ImGuiHoveredFlags.None) =>
        ImGui.IsMouseReleased(button) && ImGui.IsItemHovered(flags);

    // Why is this not a basic feature of ImGui...
    private static readonly Stack<float> fontScaleStack = new();
    private static float curScale = 1;
    public static void PushFontScale(float scale)
    {
        ImGui.SetWindowFontScale(scale);
        fontScaleStack.Push(curScale);
        curScale = scale;
    }

    public static void PopFontScale()
    {
        curScale = fontScaleStack.Pop();
        ImGui.SetWindowFontScale(curScale);
    }

    public static void PushFontSize(float size) => PushFontScale(size / ImGui.GetFont().FontSize);

    public static void PopFontSize() => PopFontScale();

    public static float GetFontScale() => curScale;

    public static float GetFontSize() => curScale * ImGui.GetFont().FontSize;

    private static readonly Stack<float> indentStack = new();
    public static void PushIndent(float indent = 0f)
    {
        ImGui.Indent(indent);
        indentStack.Push(indent);
    }

    public static void PopIndent() => ImGui.Unindent(indentStack.Pop());

    public static void ClampWindowPosToViewport()
    {
        var viewport = ImGui.GetWindowViewport();
        if (ImGui.IsWindowAppearing() || viewport.ID != ImGuiHelpers.MainViewport.ID) return;

        var pos = viewport.Pos;
        ClampWindowPos(pos, pos + viewport.Size);
    }

    public static void ClampWindowPos(Vector2 max) => ClampWindowPos(Vector2.Zero, max);

    public static void ClampWindowPos(Vector2 min, Vector2 max)
    {
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var x = Math.Min(Math.Max(pos.X, min.X), max.X - size.X);
        var y = Math.Min(Math.Max(pos.Y, min.Y), max.Y - size.Y);
        ImGui.SetWindowPos(new Vector2(x, y));
    }

    public static bool IsWindowInMainViewport() => ImGui.GetWindowViewport().ID == ImGuiHelpers.MainViewport.ID;

    public static bool ShouldDrawInViewport() => IsWindowInMainViewport() || Util.IsWindowFocused;

    public static void ShouldDrawInViewport(out bool b) => b = ShouldDrawInViewport();

    // Helper function for displaying / hiding windows outside of the main viewport when the game isn't focused, returns the bool to allow using it in if statements to reduce code
    public static bool SetBoolOnGameFocus(ref bool b)
    {
        if (!b)
            b = Util.IsWindowFocused;
        return b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetClipboardTextOrDefault(string def = "")
    {
        try { return ImGui.GetClipboardText(); }
        catch { return def; }
    }

    // ?????????
    public static void PushClipRectFullScreen() => ImGui.GetWindowDrawList().PushClipRectFullScreen();

    public static void TextCopyable(string text)
    {
        ImGui.TextUnformatted(text);

        if (!ImGui.IsItemHovered()) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(text);
    }

    public static void TextCopyable(Vector4 color, string text)
    {
        ImGui.TextColored(color, text);

        if (!ImGui.IsItemHovered()) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(text);
    }

    public static void TextMarquee(string text, float speed = 0.1f)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        var scrollWidth = ImGui.GetContentRegionMax().X + textWidth;
        var indent = (float)DalamudApi.PluginInterface.LoadTimeDelta.TotalSeconds * (scrollWidth * speed) % scrollWidth - textWidth;
        ImGui.Indent(indent);
        ImGui.TextUnformatted(text);
        ImGui.Unindent(indent);
    }

    public static void TextMarquee(Vector4 color, string text, float speed = 0.1f)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        var scrollWidth = ImGui.GetContentRegionMax().X + textWidth;
        var indent = (float)DalamudApi.PluginInterface.LoadTimeDelta.TotalSeconds * (scrollWidth * speed) % scrollWidth - textWidth;
        ImGui.Indent(indent);
        ImGui.TextColored(color, text);
        ImGui.Unindent(indent);
    }

    public static bool FontButton(string label, ImFontPtr font)
    {
        ImGui.PushFont(font);
        var ret = ImGui.Button(label);
        ImGui.PopFont();
        return ret;
    }

    public static bool FontButton(string label, ImFontPtr font, Vector2 size)
    {
        ImGui.PushFont(font);
        var ret = ImGui.Button(label, size);
        ImGui.PopFont();
        return ret;
    }

    public static bool DeleteConfirmationButton(Vector2 size = default)
    {
        using var _ = FontBlock.Begin(UiBuilder.IconFont);
        ImGui.Button(FontAwesomeIcon.Times.ToIconString(), size);
        if (IsItemReleased(ImGuiMouseButton.Right)) return true;

        using var __ = StyleVarBlock.Begin(ImGuiStyleVar.PopupBorderSize, 1);
        if (!ImGui.BeginPopupContextItem(ImU8String.Empty, ImGuiPopupFlags.MouseButtonLeft)) return false;
        var ret = ImGui.Selectable(FontAwesomeIcon.TrashAlt.ToIconString());
        ImGui.EndPopup();
        return ret;
    }

    // No way to block the title bar
    public static void BlockWindowDrag()
    {
        var io = ImGui.GetIO();
        var prev = io.ConfigWindowsMoveFromTitleBarOnly;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        DalamudApi.Framework.RunOnTick(() => io.ConfigWindowsMoveFromTitleBarOnly = prev);
    }

    private static void AddTextCentered(Vector2 pos, string text, uint color)
    {
        var textSize = ImGui.CalcTextSize(text);
        ImGui.GetWindowDrawList().AddText(pos - textSize / 2, color, text);
    }

    public static void Prefix(string prefix = "◇")
    {
        var dummySize = new Vector2(ImGui.GetFrameHeight());
        ImGui.Dummy(dummySize);
        AddTextCentered(ImGui.GetItemRectMin() + dummySize / 2, prefix, ImGui.GetColorU32(ImGuiCol.Text));
        ImGui.SameLine();
    }

    public static void Prefix(bool isLast) => Prefix(isLast ? "└" : "├");

    public static bool RadioBox(string label, ref int v, string[] optionsArray, bool vertical)
    {
        if (!BeginGroupBox(label, 0)) return false;

        var ret = false;
        var numOptions = optionsArray.Length;
        var maxWidth = 0f;

        ImGui.PushID(label);
        for (int i = 0; i < numOptions; i++)
        {
            var option = optionsArray[i];
            var selected = v == i;
            ret |= ImGui.RadioButton(vertical ? option : $"##{i}", ref v, i) && !selected;

            var width = ImGui.GetItemRectSize().X;
            maxWidth = Math.Max(width, maxWidth);
            if (i == numOptions - 1)
                maxWidth -= width;

            if (vertical) continue;

            SetItemTooltip(option);
            if (i != numOptions - 1)
                ImGui.SameLine();
        }
        ImGui.PopID();

        if (vertical)
        {
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(maxWidth, 0));
        }
        else if (v >= 0 && v < numOptions)
        {
            ImGui.SameLine();
            var text = optionsArray[v];
            ImGui.TextUnformatted(text);
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(optionsArray.Select(s => ImGui.CalcTextSize(s).X).Max() - ImGui.CalcTextSize(text).X, 0));
        }

        EndGroupBox();
        return ret;
    }

    public static bool RadioBox(string label, ref int v, string options, bool vertical) => RadioBox(label, ref v, options.Split('\0'), vertical);

    public static bool RadioBox<T>(string label, ref T e, bool vertical) where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        var i = Array.IndexOf(names, Enum.GetName(e));
        var ret = RadioBox(label, ref i, names.Select(name => typeof(T).GetField(name)?.GetCustomAttribute<DisplayAttribute>()?.Name ?? name).ToArray(), vertical);
        if (ret)
            e = Enum.Parse<T>(names[i]);
        return ret;
    }

    public static bool RadioBox<T>(string label, ref T e, T[] optionsArray, bool vertical) where T : struct, Enum
    {
        var i = Array.IndexOf(optionsArray, e);
        var ret = RadioBox(label, ref i, optionsArray.Select(Util.GetDisplayName).ToArray(), vertical);
        if (ret)
            e = optionsArray[i];
        return ret;
    }

    public static bool EnumCombo<T>(string label, ref T e, ImGuiComboFlags flags = ImGuiComboFlags.None) where T : struct, Enum
    {
        if (!ImGui.BeginCombo(label, e.GetDisplayName(), flags)) return false;

        var names = Enum.GetNames<T>();
        var selected = Enum.GetName(e);
        foreach (var name in names)
        {
            if (!ImGui.Selectable($"{typeof(T).GetField(name)?.GetCustomAttribute<DisplayAttribute>()?.Name ?? name}##{name}", name == selected)) continue;
            e = Enum.Parse<T>(name);
            ImGui.EndCombo();
            return true;
        }
        ImGui.EndCombo();

        return false;
    }

    public static bool CheckboxTristate(string label, ref bool? v)
    {
        bool ret;

        var unset = !v.HasValue;
        if (unset)
        {
            var _ = false;
            ret = ImGui.Checkbox(label, ref _);
            if (ret)
                v = true;

            var size = ImGui.GetFrameHeight();
            var padSize = Math.Max(MathF.Floor(size / 4), 1);
            var padding = new Vector2(padSize);
            var min = ImGui.GetItemRectMin();
            var max = min + new Vector2(size);
            ImGui.GetWindowDrawList().AddRect(min + padding, max - padding, ImGui.GetColorU32(ImGuiCol.CheckMark), ImGui.GetStyle().FrameRounding, ImDrawFlags.None, 3 * ImGuiHelpers.GlobalScale);
        }
        else
        {
            var value = v.Value;
            var isFalse = !value;

            if (isFalse)
                ImGui.PushStyleColor(ImGuiCol.CheckMark, Vector4.Zero);

            ret = ImGui.Checkbox(label, ref value);
            if (ret)
                v = value ? null : false;

            if (isFalse)
                ImGui.PopStyleColor();
        }

        return ret;
    }

    public static void FloatingDrawable(Action<ImDrawListPtr, float, Vector2> draw, uint timerMS = 1000)
    {
        var viewport = ImGui.GetWindowViewport() is { IsNull: false } v ? v : ImGui.GetMainViewport();
        var pos = ImGui.GetMousePos();
        var timer = Stopwatch.StartNew();

        void f()
        {
            var percentElapsed = Math.Min(timer.ElapsedMilliseconds / (float)timerMS, 1);

            // Moving a window to the main viewport and then back off while one of these is drawing can sometimes cause a crash if done quickly enough, this flag seems to be set on those viewports though
            if (percentElapsed < 1 && !viewport.Flags.HasFlag(ImGuiViewportFlags.NoTaskBarIcon))
                draw(ImGui.GetForegroundDrawList(viewport), percentElapsed, pos);
            else
                DalamudApi.PluginInterface.UiBuilder.Draw -= f;
        }

        DalamudApi.PluginInterface.UiBuilder.Draw += f;
    }

    public static void FloatingText(string text, uint color = 0xFFFFFFFF, uint timerMS = 1000)
    {
        var textSize = ImGui.CalcTextSize(text);
        var startingAlpha = color >> 24;

        FloatingDrawable((drawList, percentElapsed, pos) =>
        {
            var alphaReduction = percentElapsed > 0.75f ? (uint)(startingAlpha * (percentElapsed - 0.75f) * 4) << 24 : 0;
            pos = new Vector2(pos.X - textSize.X / 2, pos.Y - textSize.Y - 20 * percentElapsed * ImGuiHelpers.GlobalScale);
            drawList.AddText(pos + Vector2.One * ImGuiHelpers.GlobalScale, (startingAlpha << 24) - alphaReduction, text);
            drawList.AddText(pos, color - alphaReduction, text);
        }, timerMS);
    }
}