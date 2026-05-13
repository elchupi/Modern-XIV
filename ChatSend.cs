using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace noWickyXIV;

// Sends chat text (including slash commands like /tell) via the game's
// shell command pipeline: RaptureShellModule → ExecuteCommandInner.
// This is the same path the engine takes when the player presses
// Enter on the chat input — accepts any text the user could type.
//
// Previous implementation used a sig-scanned ProcessChatBox pointer
// that went stale across patches. This version uses the managed
// FFXIVClientStructs wrappers so it tracks game updates automatically.
public static unsafe class ChatSend
{
    /// <summary>
    /// Send <paramref name="text"/> exactly as if the user had typed it
    /// into the chat box and pressed Enter. Includes slash commands.
    /// Silently fails if the game modules aren't available or the
    /// message is too long (>500 bytes — the game's hard limit).
    /// </summary>
    public static void Send(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 500) return;

        try
        {
            var ui = UIModule.Instance();
            if (ui == null) return;
            var rsm = ui->GetRaptureShellModule();
            if (rsm == null) return;

            // ShellCommandModule is an EMBEDDED struct on
            // RaptureShellModule — not a pointer. Taking it by value
            // copies the struct to the stack and the vtable dispatch
            // crashes. Use the field's ADDRESS so `this` points to the
            // real heap-resident module.
            var shellPtr = &rsm->ShellCommandModule;

            var utf8 = Utf8String.FromSequence(bytes);
            try
            {
                shellPtr->ExecuteCommandInner(utf8, ui);
            }
            finally
            {
                if (utf8 != null) utf8->Dtor(true);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.LogInfo($"[ChatSend] Send failed: {ex.Message}");
        }
    }
}
