using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace noWickyXIV;

// Govee event-driven light sync. Models the Apex/LoL "interrupt"
// pattern: the SyncBox normally runs HDMI capture (Video mode);
// when a game event fires, we override with a static color or named
// scene for a duration, then restore the previously-active mode.
//
// Cloud REST only. The H6603 Gaming Sync Box Kit doesn't expose the
// LAN API — Govee's own docs list LAN support as a per-SKU opt-in
// (typical H6xxx WiFi strips/bulbs ~2022+, not the SyncBox).
//
// This is the SCAFFOLD pass:
//   - Configuration fields live (LightSyncApiKey + sku + mac).
//   - /lightsync slash command for manual testing:
//       /lightsync devices              → dump the user/devices response
//       /lightsync test red             → fire a test override
//       /lightsync test #AABBCC         → arbitrary hex
//       /lightsync restore              → force-restore to Video mode
//   - No event hooks yet. Once we confirm the override + restore round
//     trip works on the box, we wire HpRing/death/duty-pop/tells.
public static class LightSync
{
    private const string CloudBase = "https://openapi.api.govee.com/router/api/v1";

    private static HttpClient _http;
    private static bool _initialized;
    // Restore-after-N-ms timer; replaced if a new override fires
    // before the previous restore finishes.
    private static CancellationTokenSource _restoreCts;

    // ---- Event-tick state ----
    // Edge-detection caches so each event fires once per real
    // transition, not every Update tick while the condition is true.
    private static bool   _prevDead;
    private static bool   _lowHpArmed = true; // re-armed when HP recovers
    // Low-HP brightness pulse state. Active = HP currently below
    // threshold and we're cycling brightness; idx walks the pattern;
    // lastTickT throttles to one step per LightSyncEventLowHpPulseStepMs.
    private static bool   _lowHpPulseActive;
    private static int    _lowHpPulseIdx;
    private static double _lowHpPulseLastTickT;

    // Idle-dim state. _eventBrightUntilT is when the current non-
    // pulse event expires; after that, if no other event is firing,
    // we drop brightness to 0. _eventBrightLastSent caches the last
    // brightness we sent so we don't spam the same value every
    // frame. _idleEstablished gates the initial "send 0 once" so
    // the lights drop to dim the moment LightSync becomes active
    // (not only after the first event ends).
    private static double _eventBrightUntilT;
    private static int    _eventBrightLastSent = -1;
    private static bool   _idleEstablished;

    // Riding state: continuous color+brightness while mounted and
    // moving. Position tracked frame-to-frame for speed estimation.
    // _ridingLastBright caches the last brightness sent so we only
    // POST when speed changes meaningfully (avoid spamming the same
    // value 60×/sec).
    private static bool             _ridingActive;
    private static int              _ridingLastBright = -1;
    private static System.Numerics.Vector3 _moveLastPos;
    private static float            _moveSmoothedSpeed;
    // Running-on-foot state: pulses brightness in a step cadence
    // while moving without a mount. Color = neutral warm white.
    // Phase flips between peak/low at StepMs; lights read as
    // footsteps.
    private static bool             _runningActive;
    private static int              _runningLastBright = -1;
    private static double           _runningLastStepT;
    private static bool             _runningPhaseHigh;
    // In-combat state. Continuous yellow while InCombat. Overrides
    // riding+running, overridden by Low HP. Heartbeat re-sends
    // brightness every 2s against UDP packet drops.
    private static bool   _combatActive;
    private static double _combatHeartbeatT;
    // Sprint state. Continuous light green while Sprint status is
    // active. Higher priority than walk/run; overridden by combat
    // and low HP.
    private static bool   _sprintingActive;
    private static double _sprintingHeartbeatT;
    // Tracks whether we last ticked footstep as walking or running
    // so we can re-establish color when the regime changes mid-
    // movement (e.g. user toggles /walk off while moving).
    private static bool   _runningWasWalking;
    // Footstep alternation: which foot's light is currently being
    // peaked. Toggled on each high-phase entry so we get a
    // right-left-right-left cadence instead of both-on-at-once.
    // First step of a fresh run starts on the right foot.
    private static bool   _runningRightFoot = true;
    // While _eventBlockUntilT > now, the riding/running tickers skip
    // their continuous brightness writes so a tell/duty pulse can
    // play out cleanly without being overwritten mid-pulse.
    private static double _eventBlockUntilT;
    private static bool   _prevDutyQueueWaiting;
    private static bool   _chatHooked;

    public static void Initialize()
    {
        if (_initialized) return;
        try
        {
            // Govee's hdmiSource control holds the HTTP connection open
            // while the SyncBox actually switches input + re-handshakes
            // HDMI capture, which can take 10-15s. 30s gives plenty of
            // headroom; non-mode commands (colorRgb, brightness) still
            // return in <1s so this only matters on the slow path.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            DalamudApi.CommandManager.AddHandler("/lightsync", new(OnCommand)
            {
                HelpMessage =
                    "Govee light sync: /lightsync devices | lanscan | lansweep | arpdump | probe <ip> | test <color> | restore",
                ShowInHelp = true,
            });

            // Subscribe to chat for the Tell event. Done here once so
            // the per-frame Update doesn't have to hook/unhook.
            try { DalamudApi.ChatGui.ChatMessage += OnChatMessage; _chatHooked = true; } catch { }

            // If user has chosen Chroma mode, fire up the Chroma
            // session right away so events have a live session to
            // POST into. Cloud mode needs no init — it just makes
            // requests on demand.
            if (noWickyXIV.Config.LightSyncMode == "Chroma")
                LightSyncChroma.Initialize();

            // LAN socket is always opened — per-device LAN routing
            // is decided per-event based on whether the device has
            // a LanIp set. Cheap to keep open even if no devices
            // currently use LAN.
            LightSyncLan.Initialize();

            _initialized = true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync init failed: {ex.Message}"); } catch { }
        }
    }

    // Per-frame tick. Polls game state for the rising-edge events
    // (death / low-HP / duty-pop). Tell is event-driven via the chat
    // hook subscribed in Initialize. Cheap when nothing's enabled or
    // no API key is set.
    public static void Update()
    {
        var cfg = noWickyXIV.Config;
        if (!cfg.EnableLightSync) return;

        // Chroma mode keeps a heartbeat session alive on the local
        // Razer Chroma server; tick it every frame (cheap — only PUTs
        // every ~3s).
        if (cfg.LightSyncMode == "Chroma")
            LightSyncChroma.Update();

        // Credential / target gate. Require either an API key (for
        // Cloud fallback) OR at least one enabled device with a LAN
        // IP (LAN-only operation). The legacy single-target
        // LightSyncDeviceMac field is also accepted but no longer
        // required — most users now have devices in the multi-list.
        if (cfg.LightSyncMode != "Chroma")
        {
            bool hasApiKey = !string.IsNullOrEmpty(cfg.LightSyncApiKey);
            bool hasLegacyTarget = !string.IsNullOrEmpty(cfg.LightSyncDeviceMac)
                                && !string.IsNullOrEmpty(cfg.LightSyncDeviceSku);
            bool hasMultiTarget = false;
            try
            {
                hasMultiTarget = cfg.LightSyncDevices != null
                    && cfg.LightSyncDevices.Exists(d =>
                        d.Enabled && !string.IsNullOrEmpty(d.DeviceId)
                                  && (!string.IsNullOrEmpty(d.LanIp) || hasApiKey));
            }
            catch { }
            // Need at least one viable path to a device. If neither
            // a legacy target nor a multi-list entry is present, the
            // per-frame state machine has nothing to drive.
            if (!hasApiKey && !hasLegacyTarget && !hasMultiTarget) return;
            if (!hasLegacyTarget && !hasMultiTarget) return;
        }
        try
        {
            var lp = DalamudApi.ObjectTable.LocalPlayer;
            if (lp == null)
            {
                _prevDead = false;
                _lowHpArmed = true;
                return;
            }

            // ---- Death: rising edge of IsDead ----
            bool dead = false;
            try { dead = lp.IsDead; } catch { }
            if (cfg.LightSyncEventDeath && dead && !_prevDead)
                _ = FlashColor(cfg.LightSyncEventDeathColor, cfg.LightSyncEventDeathDurationMs);
            _prevDead = dead;

            // ---- Low HP: continuous color + brightness pulse while
            // below threshold. Sets color (red) once on entry, then
            // cycles brightness through the configured pattern at
            // the configured step rate. Recovery (HP > threshold + 5%)
            // restores brightness to 100% and stops the pulse —
            // color stays as the last event override.
            try
            {
                if (cfg.LightSyncEventLowHp && lp.MaxHp > 0)
                {
                    float pct = MathF.Max(0f, MathF.Min(1f, lp.CurrentHp / (float)lp.MaxHp));
                    float threshold = MathF.Max(0.05f, MathF.Min(0.95f, cfg.LightSyncEventLowHpThreshold));

                    if (_lowHpArmed && pct < threshold && pct > 0f)
                    {
                        // Rising-edge into low HP: set color, kick
                        // off the pulse engine.
                        _ = SendColorRgb(cfg.LightSyncEventLowHpColor);
                        _lowHpArmed = false;
                        _lowHpPulseActive = true;
                        _lowHpPulseIdx = 0;
                        _lowHpPulseLastTickT = 0;
                    }
                    else if (!_lowHpArmed && pct > threshold + 0.05f)
                    {
                        // Recovery edge: stop the pulse. In idle-dim
                        // mode brightness goes to 0 (no event = lights
                        // off via dim); otherwise restore to 100%.
                        if (_lowHpPulseActive)
                        {
                            int restoreBright = cfg.LightSyncIdleDim ? 0 : 100;
                            _ = SendBrightness(restoreBright);
                            _eventBrightLastSent = restoreBright;
                            _eventBrightUntilT = 0; // no pending event
                            _lowHpPulseActive = false;
                        }
                        _lowHpArmed = true;
                    }

                    // While the pulse is active, walk the brightness
                    // pattern at the configured cadence.
                    if (_lowHpPulseActive)
                    {
                        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                        double stepSec = MathF.Max(50, cfg.LightSyncEventLowHpPulseStepMs) / 1000.0;
                        if (now - _lowHpPulseLastTickT >= stepSec)
                        {
                            _lowHpPulseLastTickT = now;
                            var pattern = cfg.LightSyncEventLowHpPulse;
                            if (pattern != null && pattern.Count > 0)
                            {
                                int b = pattern[_lowHpPulseIdx % pattern.Count];
                                _lowHpPulseIdx = (_lowHpPulseIdx + 1) % pattern.Count;
                                _ = SendBrightness(b);
                            }
                        }
                    }
                }
            }
            catch { }

            // ---- Duty pop: ConditionFlag.WaitingForDutyFinder
            // transitions from TRUE→FALSE = queue popped (the user is
            // either accepting or the queue ended). Pattern: split
            // enabled devices into two groups by index, alternate
            // them twice for an attention-grabbing notification.
            try
            {
                bool waiting = DalamudApi.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WaitingForDutyFinder];
                if (cfg.LightSyncEventDutyPop && _prevDutyQueueWaiting && !waiting)
                {
                    _ = AlternateColor(
                        cfg.LightSyncEventDutyPopColor,
                        cfg.LightSyncEventDutyPopAltCount,
                        cfg.LightSyncEventDutyPopAltStepMs);
                }
                _prevDutyQueueWaiting = waiting;
            }
            catch { }

            // ---- Riding & Running continuous events ----
            // Compute the player's flat-plane (XZ) speed from frame
            // delta and dispatch to whichever continuous mode is
            // appropriate. Skipped while low-HP pulse is active
            // (pulse owns the lights) or while a one-shot pulse
            // (tell/duty) is in its blocking window.
            try
            {
                if (!_lowHpPulseActive)
                {
                    var nowMv = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    bool blocked = nowMv < _eventBlockUntilT;
                    var pos = lp.Position;
                    float dt = 0.016f;
                    try { dt = (float)DalamudApi.Framework.UpdateDelta.TotalSeconds; } catch { }
                    if (dt <= 0f) dt = 0.016f;
                    float dx = pos.X - _moveLastPos.X;
                    float dz = pos.Z - _moveLastPos.Z;
                    float rawSpd = MathF.Sqrt(dx * dx + dz * dz) / dt;
                    // Suppress huge spikes from teleports / first frame.
                    if (rawSpd > 100f) rawSpd = 0f;
                    float k = 1f - MathF.Exp(-8f * dt);
                    _moveSmoothedSpeed += (rawSpd - _moveSmoothedSpeed) * k;
                    _moveLastPos = pos;

                    bool mounted = false;
                    try
                    {
                        unsafe
                        {
                            var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
                            if (ch != null) mounted = ch->Mount.MountId != 0;
                        }
                    }
                    catch { }
                    bool moving = _moveSmoothedSpeed > 0.5f;
                    bool inCombat = false;
                    try { inCombat = DalamudApi.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]; } catch { }

                    // Combat takes priority over riding+running. While
                    // engaged, the lights stay on the combat color +
                    // brightness regardless of mount/movement state.
                    if (cfg.LightSyncEventCombat && inCombat)
                    {
                        if (_ridingActive) ExitRiding();
                        if (_runningActive) ExitRunning();
                        TickCombat(cfg, blocked, nowMv);
                    }
                    else
                    {
                        if (_combatActive) ExitCombat();
                        // Riding: mounted + moving → cyan with brightness scaled to speed
                        if (cfg.LightSyncEventRiding && mounted && moving)
                        {
                            if (_runningActive) ExitRunning();
                            TickRiding(cfg, blocked);
                        }
                        else if (_ridingActive)
                        {
                            ExitRiding();
                        }

                        // On-foot movement dispatch: pick walking /
                        // running / sprinting based on speed + the
                        // Sprint buff. Sprint always wins; otherwise
                        // walk-vs-run is decided by smoothed speed
                        // against the configured threshold.
                        if (!mounted && moving)
                        {
                            bool sprinting = CameraConfigPreset.HasSprintStatus();
                            if (sprinting && cfg.LightSyncEventSprinting)
                            {
                                if (_runningActive) ExitRunning();
                                TickSprinting(cfg, blocked, nowMv);
                            }
                            else if (_moveSmoothedSpeed < cfg.LightSyncWalkSpeedThreshold
                                  && cfg.LightSyncEventWalking)
                            {
                                if (_sprintingActive) ExitSprinting();
                                // Walking uses the same step-pulse
                                // engine as running; the configurable
                                // params just shrink the swing and
                                // slow the cadence. _runningActive
                                // is reused as the foot-movement
                                // active flag.
                                TickFootStep(cfg, blocked, walking: true);
                            }
                            else if (cfg.LightSyncEventRunning)
                            {
                                if (_sprintingActive) ExitSprinting();
                                TickFootStep(cfg, blocked, walking: false);
                            }
                            else if (_runningActive) ExitRunning();
                        }
                        else
                        {
                            if (_sprintingActive) ExitSprinting();
                            if (_runningActive) ExitRunning();
                        }
                    }
                }
            }
            catch { }

            // ---- Idle-dim ticker ----
            // Drops brightness to 0 once any active event's duration
            // has expired. Skips while the low-HP pulse is active
            // (pulse owns brightness). On first tick after enabling,
            // establishes the dim baseline by sending 0 once.
            if (cfg.LightSyncIdleDim && !_lowHpPulseActive)
            {
                if (!_idleEstablished)
                {
                    _ = SendBrightness(0);
                    _eventBrightLastSent = 0;
                    _idleEstablished = true;
                }
                else if (_eventBrightUntilT > 0)
                {
                    var nowT = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    if (nowT > _eventBrightUntilT && _eventBrightLastSent != 0)
                    {
                        _ = SendBrightness(0);
                        _eventBrightLastSent = 0;
                        _eventBrightUntilT = 0;
                    }
                }
            }
            else if (!cfg.LightSyncIdleDim && _idleEstablished)
            {
                // User toggled idle-dim off mid-session — let
                // brightness stay where the last event left it.
                _idleEstablished = false;
            }
        }
        catch { /* defensive — never let the per-frame tick throw */ }
    }

    // Chat-driven Tell event. The check is on the LogKind type — for
    // self-sent tells (TellOutgoing) we deliberately don't fire; only
    // incoming tells from someone else flash the lights.
    private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        try
        {
            var cfg = noWickyXIV.Config;
            if (!cfg.EnableLightSync || !cfg.LightSyncEventTell) return;
            int kind;
            try { kind = Convert.ToInt32(message.LogKind); } catch { return; }
            if (kind != (int)Dalamud.Game.Text.XivChatType.TellIncoming) return;
            _ = PulseColor(
                cfg.LightSyncEventTellColor,
                cfg.LightSyncEventTellPulseCount,
                cfg.LightSyncEventTellPulseStepMs);
        }
        catch { }
    }

    public static void Dispose()
    {
        try { DalamudApi.CommandManager.RemoveHandler("/lightsync"); } catch { }
        if (_chatHooked)
        {
            try { DalamudApi.ChatGui.ChatMessage -= OnChatMessage; } catch { }
            _chatHooked = false;
        }
        // Best-effort restore so we don't leave the user stuck on a
        // death-red override after a /xldev disable.
        try { _restoreCts?.Cancel(); } catch { }
        try { _ = RestoreToVideoMode(); } catch { }
        try { LightSyncChroma.Dispose(); } catch { }
        try { LightSyncLan.Dispose(); } catch { }
        try { _http?.Dispose(); } catch { }
        _http = null;
        _initialized = false;
    }

    // ---------- Slash command ----------
    private static void OnCommand(string command, string args)
    {
        var trimmed = (args ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Print("usage: /lightsync devices | lanscan | lansweep | test <color> | restore");
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        switch (verb)
        {
            case "devices":
                _ = DumpDevicesAsync();
                break;
            case "test":
                if (string.IsNullOrEmpty(rest))
                {
                    Print("usage: /lightsync test <red|blue|green|yellow|cyan|pink|white|#RRGGBB>");
                    return;
                }
                if (TryParseColor(rest, out var rgb))
                    _ = FlashAsync(rgb, noWickyXIV.Config.LightSyncDefaultFlashMs);
                else
                    Print($"could not parse color: '{rest}'");
                break;
            case "restore":
                _ = RestoreToVideoMode();
                break;
            case "dream":
                // Experimental: retry dreamViewToggle = 1 even though
                // it was returning 400 before. If the firmware update
                // now accepts it, this is the cleanest restore path
                // and we should switch the default Restore method
                // back to using it.
                _ = TestDreamViewAsync();
                break;
            case "raw":
                // /lightsync raw <type> <instance> <int-value>
                // Bare-int experimentation only; useful for probing
                // newly-added or undocumented capabilities. Type and
                // instance match Govee's `devices.capabilities.X`
                // and the schema's instance name respectively.
                _ = SendRawAsync(rest);
                break;
            case "lanscan":
                // Multicast UDP scan to find LAN-capable Govee
                // devices. Discovered IPs are merged into
                // Config.LightSyncDevices automatically. After
                // scanning, /lightsync test red goes UDP-direct on
                // matched devices for sub-30ms latency.
                try { DalamudApi.PluginLog.Information(
                    "[noWickyXIV] /lightsync lanscan invoked"); } catch { }
                Print("starting LAN scan (3s window)…");
                _ = LightSyncLan.ScanAsync();
                break;
            case "lansweep":
                // Unicast sweep — scans every IP in the local /24
                // by sending the scan packet directly to each. Use
                // when multicast lanscan returns 0 devices on
                // networks that block multicast (Google Nest WiFi,
                // IoT-VLAN setups, etc.).
                try { DalamudApi.PluginLog.Information(
                    "[noWickyXIV] /lightsync lansweep invoked"); } catch { }
                Print("starting LAN sweep (~254 unicast probes, 4s window)…");
                _ = LightSyncLan.SweepAsync();
                break;
            case "arpdump":
                // Dump the OS ARP cache to /xllog. Lists every
                // IP+MAC the OS has talked to recently — useful
                // when UDP discovery fails. User can identify the
                // Govee device by its IP and paste it into the
                // LAN IP field manually.
                try { DalamudApi.PluginLog.Information(
                    "[noWickyXIV] /lightsync arpdump invoked"); } catch { }
                _ = LightSyncLan.ArpDumpAsync();
                break;
            case "state":
                // Dump current per-event state to chat + xllog so we
                // can see what mode the plugin thinks it's in.
                {
                    var lp = DalamudApi.ObjectTable.LocalPlayer;
                    bool mounted = false;
                    try
                    {
                        unsafe
                        {
                            if (lp != null)
                            {
                                var ch = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
                                if (ch != null) mounted = ch->Mount.MountId != 0;
                            }
                        }
                    }
                    catch { }
                    var msg =
                        $"riding={_ridingActive} (lastBright={_ridingLastBright})  " +
                        $"running={_runningActive} (lastBright={_runningLastBright})  " +
                        $"lowHpPulse={_lowHpPulseActive} (lowHpArmed={_lowHpArmed})  " +
                        $"mounted={mounted} smoothedSpeed={_moveSmoothedSpeed:F2}m/s  " +
                        $"idleEstablished={_idleEstablished} eventBrightLast={_eventBrightLastSent}  " +
                        $"eventBlockUntil={_eventBlockUntilT - ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond):F2}s";
                    Print(msg);
                }
                break;
            case "probe":
                // Send a red color command directly to the given IP.
                // If the device at that IP is a Govee light with LAN
                // Control enabled, it'll turn red — confirms the IP
                // matches. Used for hunt-and-peck identification when
                // discovery doesn't work.
                if (string.IsNullOrEmpty(rest))
                {
                    Print("usage: /lightsync probe <ip>  e.g. /lightsync probe 192.168.86.52");
                    break;
                }
                try { DalamudApi.PluginLog.Information(
                    $"[noWickyXIV] /lightsync probe invoked for {rest}"); } catch { }
                _ = ProbeAsync(rest.Trim());
                break;
            default:
                Print($"unknown subcommand '{verb}'");
                break;
        }
    }

    // Single-IP probe — sends a sequence of commands designed to
    // produce a visible reaction on a Govee LAN-capable light, plus
    // a devStatus query that the device should reply to even if it's
    // currently off. If the visual probe lands but the user sees no
    // reaction, devStatus reply (logged) tells us whether the device
    // is reachable at all.
    //
    // Sequence:
    //   1. devStatus query (asks for current state; device replies on 4002)
    //   2. turn = 1 (in case the light is off — Govee ignores color
    //      commands when powered off)
    //   3. brightness = 100
    //   4. colorwc red
    //   5. wait 2s, then dim to 5% so the visual signature is
    //      unambiguous (red flash → near-off)
    private static async Task ProbeAsync(string ip)
    {
        if (string.IsNullOrEmpty(ip))
        {
            Print("probe: no IP given");
            return;
        }
        Print($"probing {ip} → turn-on + brightness 100 + red");

        // 1. turn on (some devices ignore color while powered off)
        bool ok1 = await LightSyncLan.SendPower(ip, true).ConfigureAwait(false);
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] probe: turn=1 → {(ok1 ? "sent" : "send failed")}"); } catch { }

        // Brief gap so the device processes the power state change
        // before color commands land.
        await Task.Delay(150).ConfigureAwait(false);

        // 3. brightness 100
        bool ok2 = await LightSyncLan.SendBrightness(ip, 100).ConfigureAwait(false);
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] probe: brightness=100 → {(ok2 ? "sent" : "send failed")}"); } catch { }

        // 4. red color
        bool ok3 = await LightSyncLan.SendColorRgb(ip, 0xFF0000).ConfigureAwait(false);
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] probe: colorwc red → {(ok3 ? "sent" : "send failed")}. Watch the lights."); } catch { }

        await Task.Delay(2000).ConfigureAwait(false);

        // 5. dim to make visual signature unambiguous
        await LightSyncLan.SendBrightness(ip, 5).ConfigureAwait(false);
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] probe: brightness=5 sent (dim confirmation)"); } catch { }
    }

    private static async Task TestDreamViewAsync()
    {
        if (!HasDeviceTarget()) return;
        try
        {
            var payload = BuildControlPayload(
                "devices.capabilities.toggle", "dreamViewToggle", 1);
            await PutControl(payload).ConfigureAwait(false);
            Print("dreamViewToggle=1 sent. Check /xllog body for code=200 (works) or 400 (still rejected).");
        }
        catch (Exception ex)
        {
            Print($"dreamview test failed: {ex.Message}");
        }
    }

    private static async Task SendRawAsync(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            Print("usage: /lightsync raw <type> <instance> <int-value>");
            return;
        }
        if (!int.TryParse(parts[2], out var value))
        {
            Print($"value must be an integer, got '{parts[2]}'");
            return;
        }
        var capType = parts[0];
        var instance = parts[1];
        // Allow shorthand without the leading 'devices.capabilities.'.
        if (!capType.StartsWith("devices.capabilities."))
            capType = "devices.capabilities." + capType;
        try
        {
            var payload = BuildControlPayload(capType, instance, value);
            await PutControl(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Print($"raw send failed: {ex.Message}");
        }
    }

    // ---------- Public API for future event hooks ----------
    // FlashColor: short override that auto-restores. Use for one-shots
    // (death flash, tell ping, duty pop). Higher-priority calls
    // preempt in-flight restores.
    public static Task FlashColor(int rgb, int durationMs)
        => FlashAsync(rgb, durationMs);

    public static Task SetSolidColor(int rgb)
        => SendColorRgb(rgb);

    // ---------- Cloud REST primitives ----------
    private static async Task DumpDevicesAsync()
    {
        if (!HasCreds()) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{CloudBase}/user/devices");
            req.Headers.Add("Govee-API-Key", noWickyXIV.Config.LightSyncApiKey);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSync /user/devices status={(int)resp.StatusCode} body={body}");
            if (resp.IsSuccessStatusCode)
                MergeDevicesFromResponse(body);
            Print($"devices response logged (/xllog) — status {(int)resp.StatusCode}, "
                + $"{noWickyXIV.Config.LightSyncDevices.Count} known devices");
        }
        catch (Exception ex)
        {
            Print($"devices request failed: {ex.Message}");
        }
    }

    // Parses the /user/devices response and merges discovered devices
    // into Config.LightSyncDevices. Keeps existing user-set Enabled
    // flags; new devices land disabled (user opts in). Devices no
    // longer in the account get pruned.
    private static void MergeDevicesFromResponse(string body)
    {
        try
        {
            var json = JsonNode.Parse(body) as JsonObject;
            var data = json?["data"] as JsonArray;
            if (data == null) return;

            var existing = noWickyXIV.Config.LightSyncDevices;
            var seenIds = new HashSet<string>();

            foreach (var item in data)
            {
                var sku = item?["sku"]?.GetValue<string>() ?? "";
                var deviceId = item?["device"]?.GetValue<string>() ?? "";
                var name = item?["deviceName"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(deviceId)) continue;
                seenIds.Add(deviceId);

                var match = existing.Find(d => d.DeviceId == deviceId);
                if (match != null)
                {
                    match.Sku  = sku;
                    match.Name = name;
                }
                else
                {
                    existing.Add(new LightSyncDevice
                    {
                        Sku = sku,
                        DeviceId = deviceId,
                        Name = name,
                        Enabled = false,
                    });
                }
            }

            existing.RemoveAll(d => !seenIds.Contains(d.DeviceId));
            noWickyXIV.Config.Save();
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync devices parse failed: {ex.Message}"); } catch { }
        }
    }

    // Sends an RGB override. Two paths:
    //   Multi-device list (preferred) — fan out a colorRgb POST to
    //     every enabled device in Config.LightSyncDevices. These are
    //     non-SyncBox lights (strips, ambient, light bars) that
    //     don't have a Video-Sync mode to revert to; events just set
    //     the color and stay until the next event overrides.
    //   Legacy single-target — used as fallback when the multi-list
    //     is empty / no enabled devices. Targets the single SKU+ID
    //     from Config.LightSyncDeviceSku/Mac.
    private static async Task SendColorRgb(int rgb)
    {
        var enabled = noWickyXIV.Config.LightSyncDevices?
            .FindAll(d => d.Enabled
                       && !string.IsNullOrEmpty(d.DeviceId)
                       && !string.IsNullOrEmpty(d.Sku));

        if (enabled != null && enabled.Count > 0)
        {
            if (!HasCreds()) return;
            // Fan-out POSTs in parallel — Govee Cloud accepts
            // concurrent /device/control calls so events fire
            // simultaneously across all targets.
            var tasks = new Task[enabled.Count];
            for (int i = 0; i < enabled.Count; i++)
            {
                var dev = enabled[i];
                tasks[i] = SendColorRgbToDevice(dev, rgb);
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] LightSync fan-out partial failure: {ex.Message}"); } catch { }
            }
            return;
        }

        // Legacy single-target fallback.
        if (!HasDeviceTarget()) return;
        try
        {
            var payload = BuildControlPayload(
                "devices.capabilities.color_setting", "colorRgb", rgb);
            await PutControl(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Print($"color send failed: {ex.Message}");
        }
    }

    // Fan-out brightness command to every enabled device. Same
    // routing as SendColorRgb — LAN-first per device, falls back
    // to Cloud REST. Used by the Low-HP pulse to cycle brightness
    // while color stays.
    private static async Task SendBrightness(int pct)
    {
        if (pct < 1) pct = 1; if (pct > 100) pct = 100;
        var enabled = noWickyXIV.Config.LightSyncDevices?
            .FindAll(d => d.Enabled
                       && !string.IsNullOrEmpty(d.DeviceId)
                       && !string.IsNullOrEmpty(d.Sku));
        if (enabled == null || enabled.Count == 0) return;
        if (!HasCreds()) return;

        var tasks = new Task[enabled.Count];
        for (int i = 0; i < enabled.Count; i++)
        {
            var dev = enabled[i];
            tasks[i] = SendBrightnessToDevice(dev, pct);
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync brightness fan-out partial failure: {ex.Message}"); } catch { }
        }
    }

    private static async Task SendBrightnessToDevice(LightSyncDevice dev, int pct)
    {
        // LAN first when available — same path as color sends.
        if (dev.UseLan && !string.IsNullOrEmpty(dev.LanIp))
        {
            bool ok = await LightSyncLan.SendBrightness(dev.LanIp, pct).ConfigureAwait(false);
            if (ok) return;
        }
        try
        {
            var capability = new JsonObject
            {
                ["type"]     = "devices.capabilities.range",
                ["instance"] = "brightness",
                ["value"]    = JsonValue.Create(pct),
            };
            var body = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString(),
                ["payload"] = new JsonObject
                {
                    ["sku"]        = dev.Sku,
                    ["device"]     = dev.DeviceId,
                    ["capability"] = capability,
                },
            };
            await PutControl(body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync brightness Cloud send to {dev.Name} failed: {ex.Message}"); } catch { }
        }
    }

    // ---- Pulse / Alternate event primitives ----
    // PulseColor: set color, then quickly toggle brightness on/off
    // count times at stepMs each. Used by tells.
    public static async Task PulseColor(int rgb, int count, int stepMs)
    {
        try
        {
            var totalMs = count * stepMs * 2 + 50;
            _eventBlockUntilT = ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond)
                              + (totalMs / 1000.0);
            await SendColorRgb(rgb).ConfigureAwait(false);
            for (int i = 0; i < count; i++)
            {
                await SendBrightness(100).ConfigureAwait(false);
                await Task.Delay(stepMs).ConfigureAwait(false);
                await SendBrightness(0).ConfigureAwait(false);
                await Task.Delay(stepMs).ConfigureAwait(false);
            }
            _eventBrightLastSent = 0;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync.PulseColor failed: {ex.Message}"); } catch { }
        }
    }

    // AlternateColor: set color, then alternate two device groups
    // on/off count×2 times. Falls back to PulseColor when only one
    // device is enabled.
    public static async Task AlternateColor(int rgb, int count, int stepMs)
    {
        try
        {
            var enabled = noWickyXIV.Config.LightSyncDevices?
                .FindAll(d => d.Enabled
                           && !string.IsNullOrEmpty(d.DeviceId)
                           && !string.IsNullOrEmpty(d.Sku));
            if (enabled == null || enabled.Count == 0) return;
            if (enabled.Count == 1)
            {
                await PulseColor(rgb, count, stepMs).ConfigureAwait(false);
                return;
            }

            var groupA = new List<LightSyncDevice>();
            var groupB = new List<LightSyncDevice>();
            for (int i = 0; i < enabled.Count; i++)
                (i % 2 == 0 ? groupA : groupB).Add(enabled[i]);

            var totalMs = count * 2 * stepMs + 50;
            _eventBlockUntilT = ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond)
                              + (totalMs / 1000.0);
            await SendColorRgb(rgb).ConfigureAwait(false);
            for (int i = 0; i < count * 2; i++)
            {
                bool aOn = (i % 2 == 0);
                var t1 = SetGroupBrightness(groupA, aOn ? 100 : 0);
                var t2 = SetGroupBrightness(groupB, aOn ? 0 : 100);
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
                await Task.Delay(stepMs).ConfigureAwait(false);
            }
            // End by sending all to 0 explicitly so idle is clean.
            await SendBrightness(0).ConfigureAwait(false);
            _eventBrightLastSent = 0;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync.AlternateColor failed: {ex.Message}"); } catch { }
        }
    }

    private static async Task SetGroupBrightness(List<LightSyncDevice> devs, int pct)
    {
        if (devs == null || devs.Count == 0) return;
        var tasks = new Task[devs.Count];
        for (int i = 0; i < devs.Count; i++)
            tasks[i] = SendBrightnessToDevice(devs[i], pct);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
    }

    // ---- Crit hit one-shot ----
    // External entry point: call OnCritHit() when player lands a
    // critical hit. Flashes start color, fades to end color over
    // FadeMs, then drops brightness to 0. Quick visual sting.
    public static async Task OnCritHit()
    {
        var cfg = noWickyXIV.Config;
        if (!cfg.EnableLightSync || !cfg.LightSyncEventCrit) return;
        try
        {
            int fadeMs = MathF.Max(50, cfg.LightSyncEventCritFadeMs) is var f && f > 50 ? (int)f : 50;
            _eventBlockUntilT = ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond)
                              + ((fadeMs + 50) / 1000.0);
            await SendColorRgb(cfg.LightSyncEventCritStartColor).ConfigureAwait(false);
            await SendBrightness(100).ConfigureAwait(false);
            await Task.Delay(fadeMs / 2).ConfigureAwait(false);
            await SendColorRgb(cfg.LightSyncEventCritEndColor).ConfigureAwait(false);
            await Task.Delay(fadeMs / 2).ConfigureAwait(false);
            await SendBrightness(0).ConfigureAwait(false);
            _eventBrightLastSent = 0;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync.OnCritHit failed: {ex.Message}"); } catch { }
        }
    }

    // ---- Riding / Running continuous ticks ----
    // Riding: called per-frame while mounted+moving. Establishes
    // cyan on entry, then continuously updates brightness based on
    // smoothed speed, debounced so we only POST when the brightness
    // value would change by ≥3%.
    private static async void TickRiding(Configuration cfg, bool blocked)
    {
        if (!_ridingActive)
        {
            _ridingActive = true;
            await SendColorRgb(cfg.LightSyncEventRidingColor).ConfigureAwait(false);
        }
        if (blocked) return;
        float frac = MathF.Min(1f, _moveSmoothedSpeed / MathF.Max(1f, cfg.LightSyncEventRidingMaxSpeed));
        int min = cfg.LightSyncEventRidingMinBright;
        int max = cfg.LightSyncEventRidingMaxBright;
        int target = (int)MathF.Round(min + frac * (max - min));
        if (Math.Abs(target - _ridingLastBright) < 3) return;
        _ridingLastBright = target;
        _ = SendBrightness(target);
    }

    // Foot-step pulse engine. Drives both walking and running modes;
    // the `walking` flag picks which set of color/peak/low/cadence
    // params to use. Re-establishes color on transitions between
    // walking↔running so the visual changes color too if the user
    // configured them differently.
    //
    // Asymmetric phases: peak takes 25% of the step, low takes 75% —
    // reads as actual footsteps (brief impact + dwell) rather than
    // a 50/50 square wave.
    //
    // 2s heartbeat re-send guards against silent UDP packet loss.
    private static double _runningHeartbeatT;
    private static async void TickFootStep(Configuration cfg, bool blocked, bool walking)
    {
        bool regimeChanged = _runningActive && (_runningWasWalking != walking);
        if (!_runningActive || regimeChanged)
        {
            _runningActive = true;
            _runningPhaseHigh = false;
            _runningLastStepT = 0;
            _runningHeartbeatT = 0;
            _runningWasWalking = walking;
            // First high-phase entry will toggle this to true →
            // right group peaks first, matching the "right-left as
            // it starts" cadence the user asked for.
            _runningRightFoot = false;
            int color = walking ? cfg.LightSyncEventWalkingColor
                                : cfg.LightSyncEventRunningColor;
            await SendColorRgb(color).ConfigureAwait(false);
        }
        if (blocked) return;
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        int stepMs = walking ? cfg.LightSyncEventWalkingPulseStepMs
                             : cfg.LightSyncEventRunningPulseStepMs;
        int peak = walking ? cfg.LightSyncEventWalkingPulsePeak
                           : cfg.LightSyncEventRunningPulsePeak;
        int low  = walking ? cfg.LightSyncEventWalkingPulseLow
                           : cfg.LightSyncEventRunningPulseLow;
        double stepSec = Math.Max(0.05, stepMs / 1000.0);
        double currentPhaseSec = _runningPhaseHigh ? stepSec * 0.25 : stepSec * 0.75;

        // Original 25/75 high/low pulse cadence preserved. The ONLY
        // change vs the pre-alternation version is which devices
        // receive `peak` during the high phase: instead of all
        // devices going peak, only the active foot's group does;
        // the other group stays at `low`. Active foot toggles each
        // time we ENTER the high phase, giving right-left-right-left.
        bool needPhaseFlip = (now - _runningLastStepT) >= currentPhaseSec;
        if (needPhaseFlip)
        {
            _runningLastStepT = now;
            _runningPhaseHigh = !_runningPhaseHigh;
            if (_runningPhaseHigh) _runningRightFoot = !_runningRightFoot;
            int target = _runningPhaseHigh ? peak : low;
            _runningLastBright = target;
            _runningHeartbeatT = now;
            _ = ApplyFootStepBrightness(_runningPhaseHigh, peak, low, _runningRightFoot);
            return;
        }

        if (now - _runningHeartbeatT > 2.0)
        {
            _runningHeartbeatT = now;
            _ = ApplyFootStepBrightness(_runningPhaseHigh, peak, low, _runningRightFoot);
        }
    }

    // Footstep brightness fan-out with right/left alternation.
    // Splits enabled devices into two groups by index (even=right,
    // odd=left, same convention as AlternateColor's group split).
    // High phase: active foot's group goes peak, other group stays
    // at low. Low phase: both groups go low (the dwell between
    // impacts). Falls back to all-devices-same-brightness when
    // only one device is configured.
    private static async Task ApplyFootStepBrightness(bool high, int peak, int low, bool rightFoot)
    {
        try
        {
            var enabled = noWickyXIV.Config.LightSyncDevices?
                .FindAll(d => d.Enabled
                           && !string.IsNullOrEmpty(d.DeviceId)
                           && !string.IsNullOrEmpty(d.Sku));
            if (enabled == null || enabled.Count == 0) return;
            if (enabled.Count == 1)
            {
                var dev = enabled[0];
                // Single-device with multi-segment controller (e.g.
                // H6056 bars on a multi-segment controller) — split
                // segments into right/left halves and drive them via
                // Cloud's segmentedBrightness capability for true
                // per-bar alternation. SwapSegmentSides flips the
                // halves if the physical bars are reversed.
                if (dev.SegmentCount > 1)
                {
                    int half = dev.SegmentCount / 2;
                    var firstHalf = new List<int>(half);
                    var secondHalf = new List<int>(dev.SegmentCount - half);
                    for (int s = 0; s < half; s++) firstHalf.Add(s);
                    for (int s = half; s < dev.SegmentCount; s++) secondHalf.Add(s);
                    var rightSegs = dev.SwapSegmentSides ? secondHalf : firstHalf;
                    var leftSegs  = dev.SwapSegmentSides ? firstHalf  : secondHalf;
                    var activeSegs = rightFoot ? rightSegs : leftSegs;
                    var otherSegs  = rightFoot ? leftSegs  : rightSegs;
                    int activeSegBright = high ? peak : low;
                    int otherSegBright  = low;

                    var ts1 = SendSegmentedBrightnessToDevice(dev, activeSegs, activeSegBright);
                    var ts2 = SendSegmentedBrightnessToDevice(dev, otherSegs,  otherSegBright);
                    await Task.WhenAll(ts1, ts2).ConfigureAwait(false);
                    return;
                }

                int target = high ? peak : low;
                await SendBrightness(target).ConfigureAwait(false);
                return;
            }
            var groupRight = new List<LightSyncDevice>();
            var groupLeft = new List<LightSyncDevice>();
            for (int i = 0; i < enabled.Count; i++)
                (i % 2 == 0 ? groupRight : groupLeft).Add(enabled[i]);
            var active = rightFoot ? groupRight : groupLeft;
            var other  = rightFoot ? groupLeft  : groupRight;
            int activeBright, otherBright;
            if (high)
            {
                activeBright = peak;
                otherBright  = low;
            }
            else
            {
                activeBright = low;
                otherBright  = low;
            }

            var t1 = SetGroupBrightness(active, activeBright);
            var t2 = SetGroupBrightness(other,  otherBright);
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync.ApplyFootStepBrightness failed: {ex.Message}"); } catch { }
        }
    }

    // Sprinting: continuous light green while Sprint status active.
    // No pulse — just a steady glow at configured brightness.
    // Heartbeat against UDP loss every 2s.
    private static async void TickSprinting(Configuration cfg, bool blocked, double nowMv)
    {
        if (!_sprintingActive)
        {
            _sprintingActive = true;
            _sprintingHeartbeatT = 0;
            await SendColorRgb(cfg.LightSyncEventSprintingColor).ConfigureAwait(false);
        }
        if (blocked) return;
        int target = Math.Clamp(cfg.LightSyncEventSprintingBrightness, 1, 100);
        if (_eventBrightLastSent != target || nowMv - _sprintingHeartbeatT > 2.0)
        {
            _eventBrightLastSent = target;
            _sprintingHeartbeatT = nowMv;
            _ = SendBrightness(target);
        }
    }

    private static void ExitSprinting()
    {
        _sprintingActive = false;
        _ = SendBrightness(0);
        _eventBrightLastSent = 0;
    }

    // Combat: continuous yellow at configured brightness while
    // ConditionFlag.InCombat is true. Heartbeat-resends every 2s
    // against UDP drops.
    private static async void TickCombat(Configuration cfg, bool blocked, double nowMv)
    {
        if (!_combatActive)
        {
            _combatActive = true;
            _combatHeartbeatT = 0;
            await SendColorRgb(cfg.LightSyncEventCombatColor).ConfigureAwait(false);
        }
        if (blocked) return;
        int target = Math.Clamp(cfg.LightSyncEventCombatBrightness, 1, 100);
        if (_eventBrightLastSent != target || nowMv - _combatHeartbeatT > 2.0)
        {
            _eventBrightLastSent = target;
            _combatHeartbeatT = nowMv;
            _ = SendBrightness(target);
        }
    }

    private static void ExitCombat()
    {
        _combatActive = false;
        _ = SendBrightness(0);
        _eventBrightLastSent = 0;
    }

    private static void ExitRiding()
    {
        _ridingActive = false;
        _ridingLastBright = -1;
        _ = SendBrightness(0);
        _eventBrightLastSent = 0;
    }

    private static void ExitRunning()
    {
        _runningActive = false;
        _runningLastBright = -1;
        _ = SendBrightness(0);
        _eventBrightLastSent = 0;
    }

    private static async Task SendColorRgbToDevice(LightSyncDevice dev, int rgb)
    {
        // Per-device routing: prefer LAN when the device has a
        // discovered IP and UseLan is on. UDP latency is ~5-30ms,
        // vs Cloud REST's ~300-500ms. If the LAN send fails (UDP
        // returned an exception, e.g. host unreachable / IP
        // changed), fall through to Cloud as a backup so the user
        // still gets the flash.
        if (dev.UseLan && !string.IsNullOrEmpty(dev.LanIp))
        {
            bool ok = await LightSyncLan.SendColorRgb(dev.LanIp, rgb).ConfigureAwait(false);
            if (ok) return;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSync LAN send to {dev.Name} ({dev.LanIp}) failed; falling back to Cloud."); } catch { }
        }

        try
        {
            var capability = new JsonObject
            {
                ["type"]     = "devices.capabilities.color_setting",
                ["instance"] = "colorRgb",
                ["value"]    = JsonValue.Create(rgb),
            };
            var body = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString(),
                ["payload"] = new JsonObject
                {
                    ["sku"]        = dev.Sku,
                    ["device"]     = dev.DeviceId,
                    ["capability"] = capability,
                },
            };
            await PutControl(body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync Cloud send to {dev.Name ?? dev.DeviceId} failed: {ex.Message}"); } catch { }
        }
    }

    // True when at least one device target is configured (multi-list
    // OR legacy single-target). Used by Update's gate so the per-tick
    // event poll bails cheaply when there's nothing to drive.
    private static bool HasAnyDeviceTarget()
    {
        var devs = noWickyXIV.Config.LightSyncDevices;
        if (devs != null && devs.Exists(d => d.Enabled
                                          && !string.IsNullOrEmpty(d.DeviceId)
                                          && !string.IsNullOrEmpty(d.Sku)))
            return true;
        return !string.IsNullOrEmpty(noWickyXIV.Config.LightSyncDeviceMac)
            && !string.IsNullOrEmpty(noWickyXIV.Config.LightSyncDeviceSku);
    }

    // Override + auto-restore. Cancels a previous pending restore so
    // back-to-back events don't trigger an early "go back to video
    // mode" before the most recent event finishes its window.
    //
    // Backend selection:
    //   Cloud  → POST colorRgb to Govee Cloud, restore via the
    //            Restore method picker (mostly Manual on H6603).
    //   Chroma → PUT CHROMA_STATIC to localhost:54235 chromalink.
    //            Govee Desktop's Chroma bridge picks it up and
    //            drives the H6603. Restore = ClearEffects() →
    //            Synapse stops broadcasting our color, bridge
    //            falls through to Video Sync. No Cloud API hit.
    private static async Task FlashAsync(int rgb, int durationMs)
    {
        try { _restoreCts?.Cancel(); } catch { }
        _restoreCts = new CancellationTokenSource();
        var token = _restoreCts.Token;

        bool chroma = noWickyXIV.Config.LightSyncMode == "Chroma";
        // Multi-device list = "set color and stay" — no restore call,
        // no per-event duration enforced. Each event overrides whatever
        // color the previous one set. This fits the use case for
        // standalone WiFi strips / light bars that aren't part of a
        // SyncBox HDMI scene and don't have a Video Sync state to
        // return to.
        bool multiDevice = (noWickyXIV.Config.LightSyncDevices?
            .Exists(d => d.Enabled && !string.IsNullOrEmpty(d.DeviceId))) == true;

        if (chroma)
        {
            if (!LightSyncChroma.IsActive) LightSyncChroma.Initialize();
            await LightSyncChroma.FlashColor(rgb).ConfigureAwait(false);
        }
        else
        {
            await SendColorRgb(rgb).ConfigureAwait(false);
        }

        // Multi-device path: idle-dim mode raises brightness to the
        // event level for the duration, then per-frame logic in
        // Update drops it back to 0 once the timer expires. We
        // don't await the dim-back here — Update owns it.
        if (multiDevice && !chroma)
        {
            var cfg2 = noWickyXIV.Config;
            if (cfg2.LightSyncIdleDim)
            {
                int bright = Math.Clamp(cfg2.LightSyncEventBright, 1, 100);
                _ = SendBrightness(bright);
                _eventBrightLastSent = bright;
                _eventBrightUntilT = ((double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond)
                                    + (durationMs / 1000.0);
            }
            return;
        }

        try { await Task.Delay(durationMs, token).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; } // newer event preempted us

        if (token.IsCancellationRequested) return;

        if (chroma)
            await LightSyncChroma.ClearEffects().ConfigureAwait(false);
        else
            await RestoreToVideoMode().ConfigureAwait(false);
    }

    private static async Task RestoreToVideoMode()
    {
        if (!HasDeviceTarget()) return;
        try
        {
            switch (noWickyXIV.Config.LightSyncRestoreMethod)
            {
                case "Manual":
                    return;
                case "HdmiSource":
                {
                    var hdmi = noWickyXIV.Config.LightSyncHdmiSource;
                    if (hdmi < 1 || hdmi > 4) hdmi = 1;
                    var payload = BuildControlPayload(
                        "devices.capabilities.mode", "hdmiSource", hdmi);
                    await PutControl(payload).ConfigureAwait(false);
                    return;
                }
                case "Snapshot":
                default:
                {
                    var id = noWickyXIV.Config.LightSyncSnapshotId;
                    if (id <= 0)
                    {
                        Print("snapshot id not set — save Video Sync as a Snapshot in the Govee Home app, run /lightsync devices, then paste the id into the Light Sync tab.");
                        return;
                    }
                    // Bare-integer value: matches the format Govee
                    // returns in the options[] of dynamic_scene/snapshot
                    // (e.g. {"name":"Video Sync","value":3847229}). The
                    // control endpoint expects the value in the same
                    // shape, not wrapped in {"id":N}.
                    var payload = BuildControlPayload(
                        "devices.capabilities.dynamic_scene", "snapshot", id);
                    await PutControl(payload).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Print($"restore failed: {ex.Message}");
        }
    }

    // ---------- Payload helpers ----------
    private static JsonObject BuildControlPayload(string capType, string instance, object value)
    {
        var capability = new JsonObject
        {
            ["type"] = capType,
            ["instance"] = instance,
            ["value"] = JsonValue.Create(value),
        };
        return new JsonObject
        {
            ["requestId"] = Guid.NewGuid().ToString(),
            ["payload"] = new JsonObject
            {
                ["sku"] = noWickyXIV.Config.LightSyncDeviceSku,
                ["device"] = noWickyXIV.Config.LightSyncDeviceMac,
                ["capability"] = capability,
            },
        };
    }

    // Segmented brightness via Govee Cloud's segment_color_setting
    // capability. Used for one-device, multi-segment setups (e.g.
    // H6056 bars + a multi-segment controller) where the user wants
    // right/left footstep alternation but only has a single LAN
    // endpoint. Cloud-only — there's no documented LAN equivalent
    // for per-segment writes.
    private static async Task SendSegmentedBrightnessToDevice(
        LightSyncDevice dev, IList<int> segmentIndices, int pct)
    {
        if (dev == null || segmentIndices == null || segmentIndices.Count == 0) return;
        if (string.IsNullOrEmpty(noWickyXIV.Config.LightSyncApiKey)) return;
        if (_http == null) return;
        try
        {
            var segArr = new JsonArray();
            foreach (var s in segmentIndices) segArr.Add(JsonValue.Create(s));
            var capability = new JsonObject
            {
                ["type"]     = "devices.capabilities.segment_color_setting",
                ["instance"] = "segmentedBrightness",
                ["value"]    = new JsonObject
                {
                    ["segment"]    = segArr,
                    ["brightness"] = JsonValue.Create(pct),
                },
            };
            var body = new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString(),
                ["payload"] = new JsonObject
                {
                    ["sku"]        = dev.Sku,
                    ["device"]     = dev.DeviceId,
                    ["capability"] = capability,
                },
            };
            await PutControl(body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSync segmentedBrightness send to {dev.Name} failed: {ex.Message}"); } catch { }
        }
    }

    private static async Task PutControl(JsonObject body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{CloudBase}/device/control");
        req.Headers.Add("Govee-API-Key", noWickyXIV.Config.LightSyncApiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        // body intentionally not awaited — fire-and-forget for the
        // event-driven control calls. If you need response details for
        // diagnosis, run /lightsync raw <body> from chat.
        _ = resp.StatusCode;
    }

    // ---------- Misc ----------
    private static bool HasCreds()
    {
        if (string.IsNullOrEmpty(noWickyXIV.Config.LightSyncApiKey))
        {
            Print("no API key set — paste it in the Light Sync tab first");
            return false;
        }
        return _http != null;
    }

    private static bool HasDeviceTarget()
    {
        if (!HasCreds()) return false;
        if (string.IsNullOrEmpty(noWickyXIV.Config.LightSyncDeviceMac))
        {
            Print("no device MAC set — run /lightsync devices and set one");
            return false;
        }
        return true;
    }

    private static void Print(string s)
    {
        try { DalamudApi.ChatGui.Print($"[LightSync] {s}"); } catch { }
        try { DalamudApi.PluginLog.Information($"[noWickyXIV] LightSync: {s}"); } catch { }
    }

    private static readonly Dictionary<string, int> _namedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"]    = 0xFF0000,
        ["green"]  = 0x00FF00,
        ["blue"]   = 0x0000FF,
        ["yellow"] = 0xFFFF00,
        ["cyan"]   = 0x00FFFF,
        ["pink"]   = 0xFF66CC,
        ["white"]  = 0xFFFFFF,
        ["orange"] = 0xFF8000,
    };

    private static bool TryParseColor(string s, out int rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (_namedColors.TryGetValue(s, out rgb)) return true;
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out rgb))
            return true;
        return false;
    }
}
