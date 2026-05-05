using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace noWickyXIV;

public class noWickyXIV(IDalamudPluginInterface pluginInterface) : DalamudPlugin<Configuration>(pluginInterface), IDalamudPlugin
{
    protected override void Initialize()
    {
        Game.Initialize();
        IPC.Initialize();
        JobAura.Initialize();
        CombatEvents.Initialize();
        TargetArrowHider.Initialize();
        ChatFader.Initialize();
        ChatBubbles.Initialize();
        DalamudApi.ClientState.Login += Login;

        // One-shot migration: MouseSensitivityMul values < 0.56 produce
        // jitter/recenter behavior because the delta-replay code fights the
        // game's own per-frame writes. Old saved configs (mine had 0.1) get
        // bumped to 0.56 silently on load.
        if (Config.MouseSensitivityMul < 0.56f)
        {
            Config.MouseSensitivityMul = 0.56f;
            Config.Save();
        }
        // Hypostasis base wires Draw + OpenConfigUi only. OpenMainUi was
        // added later by Dalamud as a distinct "open the plugin's primary
        // window" entrypoint (the click-to-open button in the installer);
        // without it Dalamud emits a validation warning. Reuse ToggleConfig.
        DalamudApi.PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
    }

    protected override void ToggleConfig() => PluginUI.IsVisible ^= true;

    private const string nowickyxivSubcommands = "/nowickyxiv [ help | preset | zoom | fov | spectate | nocollide | freecam ]";

    [PluginCommand("/nowickyxiv", HelpMessage = "Opens / closes the config. Additional usage: " + nowickyxivSubcommands)]
    private unsafe void ToggleConfig(string command, string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            ToggleConfig();
            return;
        }

        var regex = Regex.Match(argument, "^(\\w+) ?(.*)");
        var subcommand = regex.Success && regex.Groups.Count > 1 ? regex.Groups[1].Value : string.Empty;

        switch (subcommand.ToLower())
        {
            case "preset":
                {
                    if (regex.Groups.Count < 2 || string.IsNullOrEmpty(regex.Groups[2].Value))
                    {
                        PresetManager.CurrentPreset = null;
                        DalamudApi.PrintEcho("Removed preset override.");
                        return;
                    }

                    var arg = regex.Groups[2].Value;
                    var preset = Config.Presets.FirstOrDefault(preset => preset.Name == arg);

                    if (preset == null)
                    {
                        DalamudApi.PrintError($"Failed to find preset \"{arg}\"");
                        return;
                    }

                    PresetManager.CurrentPreset = preset;
                    DalamudApi.PrintEcho($"Preset set to \"{arg}\"");
                    break;
                }
            case "zoom":
                {
                    if (regex.Groups.Count < 2 || !float.TryParse(regex.Groups[2].Value, out var amount))
                    {
                        DalamudApi.PrintError("Invalid amount.");
                        return;
                    }

                    Common.CameraManager->worldCamera->currentZoom = amount;
                    break;
                }
            case "fov":
                {
                    if (regex.Groups.Count < 2 || !float.TryParse(regex.Groups[2].Value, out var amount))
                    {
                        DalamudApi.PrintError("Invalid amount.");
                        return;
                    }

                    Common.CameraManager->worldCamera->currentFoV = amount;
                    break;
                }
            case "spectate":
                {
                    Game.EnableSpectating ^= true;
                    DalamudApi.PrintEcho($"Spectating is now {(Game.EnableSpectating ? "enabled" : "disabled")}!");
                    break;
                }
            case "nocollide":
                {
                    Config.EnableCameraNoClippy ^= true;
                    if (!FreeCam.Enabled)
                        Game.cameraNoClippyReplacer.Toggle();
                    Config.Save();
                    DalamudApi.PrintEcho($"Camera collision is now {(Config.EnableCameraNoClippy ? "disabled" : "enabled")}!");
                    break;
                }
            case "freecam":
                {
                    FreeCam.Toggle();
                    break;
                }
            case "help":
                {
                    DalamudApi.PrintEcho("Subcommands:" +
                        "\npreset <name> - Applies a preset to override automatic presets, specified by name. Use without a name to disable." +
                        "\nzoom <amount> - Sets the current zoom level." +
                        "\nfov <amount> - Sets the current FoV level." +
                        "\nspectate - Toggles the \"Spectate Focus / Soft Target\" option." +
                        "\nnocollide - Toggles the \"Disable Camera Collision\" option." +
                        "\nfreecam - Toggles the \"Free Cam\" option.");
                    break;
                }
            default:
                {
                    DalamudApi.PrintError("Invalid usage: " + nowickyxivSubcommands);
                    break;
                }
        }
    }

    protected override void Update()
    {
        // Workaround for disconnects
        var loggedIn = DalamudApi.ClientState.IsLoggedIn;
        if (loggedIn != didLogin)
        {
            if (!didLogin)
                Login();
            else
                didLogin = false;
        }

        FreeCam.Update();
        PresetManager.Update();
        InputHandler.Update();
        CameraDynamics.Update();
        JobAura.Update();
        HpRing.Update();
        HotbarFader.Update();
        TargetArrowHider.Update();
        TargetUI.Update();
        ChatFader.Update();
    }

    protected override void Draw()
    {
        FreeCam.Draw();
        PluginUI.Draw();
        Crosshair.Draw();
        JobAura.Draw();
        HpRing.Draw();
        TargetUI.Draw();
        ChatBubbles.Draw();
    }

    private static bool didLogin = false; // Workaround
    private static void Login()
    {
        if (didLogin) return;
        didLogin = true;
        DalamudApi.Framework.Update += UpdateDefaultPreset;
        PresetManager.DisableCameraPresets();
        PresetManager.CheckCameraConditionSets(true);
        // Restore the user's last-active preset over any auto-applied
        // QoL Bar preset. The override carries across sessions so the
        // user doesn't have to re-pick their preset every login.
        PresetManager.RestoreLastActivePreset();
    }

    private static void UpdateDefaultPreset(IFramework framework)
    {
        if (DalamudApi.Condition[ConditionFlag.BetweenAreas]) return;
        PresetManager.DefaultPreset = new();
        DalamudApi.Framework.Update -= UpdateDefaultPreset;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        IPC.Dispose();
        // Restore hotbars to fully opaque so toggling the plugin off
        // doesn't leave the user with faded/invisible bars.
        try { HotbarFader.RestoreOpaque(); } catch { }
        try { TargetArrowHider.Dispose(); } catch { }
        try { TargetUI.Dispose(); } catch { }
        try { ChatFader.Dispose(); } catch { }
        try { ChatBubbles.Dispose(); } catch { }
        PresetManager.DefaultPreset.Apply();
        DalamudApi.ClientState.Login -= Login;

        if (FreeCam.Enabled)
            FreeCam.Toggle();

        CombatEvents.Dispose();
        JobAura.Dispose();
        VfxBridge.Dispose();
        Game.Dispose();
    }
}