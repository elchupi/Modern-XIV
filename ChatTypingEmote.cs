using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace noWickyXIV;

// Fires a slash command (default /tomescroll) when the chat input
// gains focus, so the player's character plays a "reading a tome"
// loop while typing. /tomescroll is a self-looping pose; a single
// rising-edge fire is enough — no retrigger loop needed.
//
// On the falling edge (chat input loses focus) we optionally fire a
// configured cancel command. Default is empty so the user can just
// move to break the pose; set to e.g. "/sit off" if you want
// automatic cancel.
//
// Programmatic slash-command execution goes through
// ShellCommandModule.ExecuteCommandInner — the same path the engine
// uses when the player presses Enter on the native chat input.
// Anti-cheat-wise this is the safe equivalent of the player typing
// the command themselves.
public static unsafe class ChatTypingEmote
{
    private static bool   _wasTyping;
    // Last time we fired the emote (rising edge OR retrigger). Used
    // to gate the periodic re-fire so any engine-side interruption
    // is recovered without spamming the chat shell.
    private static double _lastFireT;

    public static void Update()
    {
        if (!noWickyXIV.Config.EnableTypingEmote)
        {
            _wasTyping = false;
            return;
        }

        bool typing = false;
        try
        {
            var ratk = RaptureAtkModule.Instance();
            if (ratk != null) typing = ratk->AtkModule.IsTextInputActive();
        }
        catch { return; }

        double now = NowSec();

        if (typing && !_wasTyping)
        {
            // Rising edge — fire immediately.
            var cmd = noWickyXIV.Config.ChatTypingEmoteCommand;
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                ExecuteShellCommand(cmd);
                _lastFireT = now;
            }
        }
        else if (typing && _wasTyping)
        {
            // While still typing — re-fire at the configured cadence
            // so any interruption (chat-prompt close+reopen, brief
            // movement, engine cancel) is restored. /tomescroll
            // doesn't always loop reliably across these events.
            float interval = MathF.Max(0.5f, noWickyXIV.Config.ChatTypingEmoteRetriggerSeconds);
            if (now - _lastFireT >= interval)
            {
                var cmd = noWickyXIV.Config.ChatTypingEmoteCommand;
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    ExecuteShellCommand(cmd);
                    _lastFireT = now;
                }
            }
        }
        else if (!typing && _wasTyping)
        {
            // Falling edge — optional cancel command.
            var cancel = noWickyXIV.Config.ChatTypingEmoteCancelCommand;
            if (!string.IsNullOrWhiteSpace(cancel))
                ExecuteShellCommand(cancel);
        }

        _wasTyping = typing;
    }

    private static double NowSec() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    // Fire a slash command via the engine's normal shell-command
    // path. Allocates a Utf8String, hands it to ExecuteCommandInner,
    // then frees the allocation. This is the exact path the engine
    // takes when the player presses Enter on the chat input.
    private static void ExecuteShellCommand(string command)
    {
        try
        {
            var ui = UIModule.Instance();
            if (ui == null) return;
            var rsm = ui->GetRaptureShellModule();
            if (rsm == null) return;
            // ShellCommandModule is an EMBEDDED struct on RaptureShellModule
            // — not a pointer. Taking it by value
            // (`var shell = rsm->ShellCommandModule`) copies the
            // struct to the stack; calling shell.ExecuteCommandInner
            // then runs the C++ member function with a stack-copy
            // `this` pointer, and the function's first vtable
            // dispatch (vf2) reads off that bogus this → C0000005
            // crash. Use the field's ADDRESS instead so `this` is
            // the real heap-resident module.
            var shellPtr = &rsm->ShellCommandModule;

            var bytes = System.Text.Encoding.UTF8.GetBytes(command);
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
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] ChatTypingEmote shell exec failed: {ex.Message}"); } catch { }
        }
    }
}
