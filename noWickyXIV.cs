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
        LightSync.Initialize();
        MountAudio.Initialize();
        // MountSoundFilter DISABLED — the PlaySound hook ended up
        // suppressing too much of the game's audio when patterns were
        // even slightly broad. Custom mount audio plays alongside the
        // game's native mount sounds for now. Re-enable here if/when
        // we have a more targeted suppression mechanism.
        // MountSoundFilter.Initialize();
        CharacterRollHook.Initialize();
        CutsceneLetterbox.Initialize();
        GlamourerBridge.Initialize();
        TeleportMenu.Initialize();
        // MountMomentum DISABLED — ViGEm-emitted analog stick was
        // observed pushing the character / camera continuously even
        // with the feature flag off. Re-enable once the stuck-input
        // root cause is resolved (see MountMomentum.cs comment).
        // MountMomentum.Initialize();
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

        // One-shot migration: an earlier auto-save pass wrote
        // `cam->lookAtHeightOffset - PitchTiltLastApplied` into
        // preset.LookAtHeightOffset on every frame. The subtraction
        // didn't fully recover the user's intent because PitchTilt's
        // accumulator drifts under EnablePitchTilt, so corrupted
        // values like -78 ended up persisted. The slider range is
        // -10..10; anything outside that is clearly garbage from the
        // bad auto-save. Clamp into range and reset to 0 (default
        // look-at) for severely-out-of-bounds entries so the camera
        // doesn't focus 78m below the player on plugin load.
        bool sanitized = false;
        foreach (var p in Config.Presets)
        {
            if (float.IsNaN(p.LookAtHeightOffset) || System.MathF.Abs(p.LookAtHeightOffset) > 10f)
            {
                p.LookAtHeightOffset = 0f;
                sanitized = true;
            }
        }
        if (sanitized) Config.Save();

        // One-shot migration: presets saved before per-preset Dynamics
        // existed have Dynamics == null. Populate them from the current
        // global Config NOW (at plugin load) so every preset captures
        // the user's intended global state. Doing this lazily inside
        // ApplyPreset was wrong — by the time a preset first activated,
        // Config already held a DIFFERENT preset's Dynamics values, so
        // the migration copied the outgoing preset's values instead of
        // the user's original globals.
        foreach (var p in Config.Presets)
            p.Dynamics ??= PresetDynamicsState.SnapshotFrom(Config);

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

        // Flush any pending debounced config writes once the user has
        // gone idle (no edits for SAVE_DEBOUNCE_SECONDS). Persists all
        // profiles in one shot since the serializer writes the whole
        // Configuration.
        Config.TickSaveDebounce();

        FreeCam.Update();
        PresetManager.Update();
        InputHandler.Update();
        HotbarSwap.Update();
        CameraDynamics.Update();
        Crosshair.Update();
        Compass.Update();
        JobAura.Update();
        HpRing.Update();
        HotbarFader.Update();
        TargetArrowHider.Update();
        TargetUI.Update();
        ChatFader.Update();
        ChatBubbles.Update();
        ChatTypingEmote.Update();
        LightSync.Update();
        // MountMomentum DISABLED — see Initialize() above.
        // MountMomentum.Update();
        MountAudio.Update();
        MsqTeleport.Update();
        TeleportMenu.Update();
        CutsceneLetterbox.Update();
        AnimationSwap.Update();
        GlamourerBridge.Update();
        EnemySizeClamp.Update();
        CharacterRollHook.Tick();
    }

    protected override void Draw()
    {
        FreeCam.Draw();
        PluginUI.Draw();
        PluginUI.DrawTeleportMenu();
        Crosshair.Draw();
        Compass.Draw();
        JobAura.Draw();
        HpRing.Draw();
        HpVignette.Draw();
        MsqTeleport.Draw();
        QuickMenu.Draw();
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
        // Flush any pending debounced config writes before tearing down
        // the plugin so a fast unload right after a slider drag still
        // persists the user's edits.
        try { Config.FlushSaveDebounce(); } catch { }
        try { HotbarSwap.RestoreOnUnload(); } catch { }
        IPC.Dispose();
        // Restore hotbars to fully opaque so toggling the plugin off
        // doesn't leave the user with faded/invisible bars.
        try { HotbarFader.RestoreOpaque(); } catch { }
        try { TargetArrowHider.Dispose(); } catch { }
        try { TargetUI.Dispose(); } catch { }
        try { ChatFader.Dispose(); } catch { }
        try { ChatBubbles.Dispose(); } catch { }
        try { LightSync.Dispose(); } catch { }
        try { MountAudio.Dispose(); } catch { }
        try { MountSoundFilter.Dispose(); } catch { }
        try { CutsceneLetterbox.Dispose(); } catch { }
        try { AnimationSwap.Dispose(); } catch { }
        try { GlamourerBridge.Dispose(); } catch { }
        try { TeleportMenu.Dispose(); } catch { }
        try { EnemySizeClamp.Dispose(); } catch { }
        try { CharacterRollHook.Dispose(); } catch { }
        try { Compass.Dispose(); } catch { }
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