using System;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace noWickyXIV;

// Minimal wrapper around `UIModule::ProcessChatBox` so plugin code
// can issue game chat commands (e.g. `/p ...`, `/g ...`). The function
// is what the chat window itself invokes when you press Enter, so it
// accepts any text the user could type — slash commands included.
// Sig-scanned once on first use; if the scan fails we silently no-op
// rather than crashing the plugin.
public static unsafe class ChatSend
{
    // Stable signature across recent Dawntrail patches. Resolves an
    // E8-relative call to UIModule::ProcessChatBox.
    private const string ProcessChatBoxSig =
        "48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9";

    private static delegate* unmanaged<IntPtr, Utf8String*, IntPtr, byte, void> _processChatBox;
    private static bool _scanned;

    private static bool EnsureScanned()
    {
        if (_scanned) return _processChatBox != null;
        _scanned = true;
        try
        {
            if (DalamudApi.SigScanner.TryScanText(ProcessChatBoxSig, out var addr) && addr != 0)
            {
                _processChatBox = (delegate* unmanaged<IntPtr, Utf8String*, IntPtr, byte, void>)addr;
                return true;
            }
            DalamudApi.LogInfo("[ChatSend] ProcessChatBox sig not found — chat send disabled.");
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[ChatSend] Sig scan error: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Send <paramref name="text"/> exactly as if the user had typed it
    /// into the chat box and pressed Enter. Includes slash commands.
    /// Silently fails if the game function isn't resolvable or the
    /// message is too long (>500 bytes — the game's hard limit).
    /// </summary>
    public static void Send(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!EnsureScanned()) return;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 500) return;

            var module = Framework.Instance()->GetUIModule();
            if (module == null) return;

            Utf8String msg = new();
            msg.Ctor();
            msg.SetString(text);
            _processChatBox((IntPtr)module, &msg, IntPtr.Zero, 0);
            msg.Dtor();
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[ChatSend] Send failed: {ex.Message}");
        }
    }
}
