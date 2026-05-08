using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace noWickyXIV;

// Govee LAN API client.
//
// Per https://app-h5.govee.com/user-manual/wlan-guide:
//   - Discovery: UDP multicast send to 239.255.255.250:4001;
//                listen for responses on UDP 4002.
//   - Control:   UDP send to <deviceIp>:4003.
//   - Payloads:  JSON over UDP, e.g.
//       Scan       {"msg":{"cmd":"scan","data":{"account_topic":"reserve"}}}
//       Color      {"msg":{"cmd":"colorwc","data":{"color":{"r":N,"g":N,"b":N},"colorTemInKelvin":0}}}
//       Brightness {"msg":{"cmd":"brightness","data":{"value":0..100}}}
//       Power      {"msg":{"cmd":"turn","data":{"value":0|1}}}
//
// Latency: typically 5-30ms (vs Cloud REST's 300-500ms).
//
// Constraints:
//   - LAN Control must be enabled per-device in Govee Home app.
//   - Device must be on the same LAN segment as the PC.
//   - SKU support varies; many H6xxx WiFi strips/bulbs ~2022+
//     support it, older or cheap models do not.
//   - Windows Firewall may need to allow inbound UDP 4002 the
//     first time discovery runs.
public static class LightSyncLan
{
    private const int LanScanPort = 4001;     // device listens here for scan
    private const int LanRespPort = 4002;     // we listen here for scan responses
    private const int LanCmdPort  = 4003;     // we send commands here
    private const string MulticastIp = "239.255.255.250";

    // Persistent send-side socket. Opening/closing per send adds
    // measurable latency (5-10ms socket setup) and we do up to 4
    // sends per event (one per device). Keep it open for the
    // lifetime of the plugin.
    private static UdpClient _sendUdp;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        try
        {
            _sendUdp = new UdpClient();
            _sendUdp.EnableBroadcast = true;
            _initialized = true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan init failed: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        try { _sendUdp?.Dispose(); } catch { }
        _sendUdp = null;
        _initialized = false;
    }

    // Sends a colorRgb command to a specific device IP. Fire-and-forget
    // — UDP has no ack. Returns true if the send went through (no
    // exception); the caller can use that as a hint to fall back to
    // Cloud on failure.
    public static async Task<bool> SendColorRgb(string ip, int rgb)
    {
        if (string.IsNullOrEmpty(ip) || _sendUdp == null) return false;
        int r = (rgb >> 16) & 0xFF;
        int g = (rgb >>  8) & 0xFF;
        int b =  rgb        & 0xFF;
        // colorwc accepts both color (RGB) and colorTemInKelvin (set
        // 0 to use RGB). The "wc" suffix = "with color temp" combined
        // command — the plain "color" cmd works too on some SKUs but
        // colorwc is the documented one in Govee's WLAN guide.
        var payload = $"{{\"msg\":{{\"cmd\":\"colorwc\",\"data\":{{\"color\":{{\"r\":{r},\"g\":{g},\"b\":{b}}},\"colorTemInKelvin\":0}}}}}}";
        return await SendUtf8(ip, LanCmdPort, payload).ConfigureAwait(false);
    }

    public static async Task<bool> SendBrightness(string ip, int pct)
    {
        if (string.IsNullOrEmpty(ip) || _sendUdp == null) return false;
        if (pct < 1) pct = 1; if (pct > 100) pct = 100;
        var payload = $"{{\"msg\":{{\"cmd\":\"brightness\",\"data\":{{\"value\":{pct}}}}}}}";
        return await SendUtf8(ip, LanCmdPort, payload).ConfigureAwait(false);
    }

    public static async Task<bool> SendPower(string ip, bool on)
    {
        if (string.IsNullOrEmpty(ip) || _sendUdp == null) return false;
        var payload = $"{{\"msg\":{{\"cmd\":\"turn\",\"data\":{{\"value\":{(on ? 1 : 0)}}}}}}}";
        return await SendUtf8(ip, LanCmdPort, payload).ConfigureAwait(false);
    }

    private static async Task<bool> SendUtf8(string ip, int port, string payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            await _sendUdp.SendAsync(bytes, bytes.Length, ep).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan send to {ip}:{port} failed: {ex.Message}"); } catch { }
            return false;
        }
    }

    // Blocking 3-second LAN scan. Multicasts the scan payload to
    // 239.255.255.250:4001, listens on UDP 4002 for scan responses,
    // and merges discovered devices into Config.LightSyncDevices.
    //
    // Match strategy: response includes {ip, device, sku}. We try
    // exact device-id match first; if no match, we fall back to
    // unique-SKU match (only if exactly one device with that SKU
    // exists in our Cloud-discovered list and its LanIp is empty).
    // Anything that doesn't match is logged for the user to handle
    // manually via UI.
    public static async Task ScanAsync(int timeoutMs = 3000)
    {
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan.ScanAsync entered (timeout={timeoutMs}ms)"); } catch { }

        UdpClient listener = null;
        try
        {
            listener = new UdpClient();
            // Allow reuse so multiple scan runs don't conflict, and
            // explicitly mark non-exclusive so any other Govee-aware
            // app that DID set the same flags can share the port.
            // Most apps (Govee Home Desktop included) bind
            // exclusively though, so this typically doesn't help —
            // user has to close the conflicting app to scan.
            listener.ExclusiveAddressUse = false;
            listener.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, LanRespPort));
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSyncLan: listener bound to UDP {LanRespPort}"); } catch { }
        }
        catch (SocketException sx) when (sx.SocketErrorCode == SocketError.AccessDenied)
        {
            // Windows error 10013 = WSAEACCES = another process owns
            // the port exclusively. Most common cause: Govee Home
            // Desktop. We kill any "Govee*" named process and retry
            // once. Substring filter is intentional — we only kill
            // known Govee software, never arbitrary holders.
            try { listener?.Dispose(); } catch { }
            int killed = KillGoveeProcesses();
            if (killed == 0)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] LightSyncLan scan: UDP {LanRespPort} held by an unknown (non-Govee) process. Run `netstat -ano | findstr :4002` to identify, then close it manually."); } catch { }
                try { DalamudApi.ChatGui.Print(
                    "[LightSync] LAN scan blocked: UDP 4002 held by a non-Govee process. Check netstat to identify."); } catch { }
                return;
            }
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSyncLan: killed {killed} Govee process(es); waiting 1s for socket release before retry."); } catch { }
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                listener = new UdpClient();
                listener.ExclusiveAddressUse = false;
                listener.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, LanRespPort));
                try { DalamudApi.PluginLog.Information(
                    $"[noWickyXIV] LightSyncLan: post-kill bind to UDP {LanRespPort} succeeded"); } catch { }
            }
            catch (Exception ex2)
            {
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] LightSyncLan: post-kill rebind still failed — {ex2.Message}"); } catch { }
                try { listener?.Dispose(); } catch { }
                return;
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan scan: bind UDP {LanRespPort} failed — {ex.Message}"); } catch { }
            try { DalamudApi.ChatGui.Print(
                $"[LightSync] LAN scan: bind UDP {LanRespPort} failed ({ex.GetType().Name}). Check /xllog."); } catch { }
            try { listener?.Dispose(); } catch { }
            return;
        }

        var responses = new List<JsonObject>();
        using var cts = new CancellationTokenSource(timeoutMs);

        // Spin up the listen task FIRST so any fast-responding device
        // doesn't get its packet dropped before we're ready.
        var listenTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await listener.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        var text = Encoding.UTF8.GetString(result.Buffer);
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] LightSyncLan scan recv from {result.RemoteEndPoint}: {text}"); } catch { }
                        try
                        {
                            var json = JsonNode.Parse(text) as JsonObject;
                            if (json != null) responses.Add(json);
                        }
                        catch { /* not JSON — skip */ }
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch { }
        });

        // Send scan on EVERY up interface. Multi-NIC setups (WiFi +
        // ethernet + VPN tap + virtual switches from Hyper-V/Docker)
        // route a single multicast send out one interface — which on
        // a typical gaming PC is rarely the WiFi where Govee lights
        // actually live. By binding a per-interface socket and
        // sending on each, we guarantee the packet leaves on the
        // adapter the lights are on.
        var scan = "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}";
        var scanBytes = Encoding.UTF8.GetBytes(scan);
        var multicastEp = new IPEndPoint(IPAddress.Parse(MulticastIp), LanScanPort);
        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, LanScanPort);

        int interfacesTried = 0;
        int interfacesSent  = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    // Skip APIPA / link-local — devices won't be there.
                    var bytes0 = addr.Address.GetAddressBytes();
                    if (bytes0[0] == 169 && bytes0[1] == 254) continue;
                    interfacesTried++;
                    UdpClient sender = null;
                    try
                    {
                        sender = new UdpClient(new IPEndPoint(addr.Address, 0));
                        sender.EnableBroadcast = true;
                        sender.MulticastLoopback = true;
                        sender.Ttl = 4;
                        await sender.SendAsync(scanBytes, scanBytes.Length, multicastEp).ConfigureAwait(false);
                        await sender.SendAsync(scanBytes, scanBytes.Length, broadcastEp).ConfigureAwait(false);
                        interfacesSent++;
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] LightSyncLan: scan sent via {ni.Name} ({addr.Address}) → multicast {MulticastIp}:{LanScanPort} + broadcast"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { DalamudApi.PluginLog.Warning(
                            $"[noWickyXIV] LightSyncLan: scan send via {ni.Name} ({addr.Address}) failed — {ex.Message}"); } catch { }
                    }
                    finally
                    {
                        try { sender?.Dispose(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan: interface enumeration failed: {ex.Message}"); } catch { }
        }

        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan: scan dispatched on {interfacesSent}/{interfacesTried} interfaces; waiting up to {timeoutMs}ms for replies."); } catch { }

        try { await listenTask.ConfigureAwait(false); } catch { }
        try { listener.Dispose(); } catch { }

        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan: scan window closed; received {responses.Count} response(s)."); } catch { }

        MergeScanResponses(responses);
    }

    // Unicast sweep — sends a scan request to every IP in the local
    // /24 subnet (e.g. 192.168.86.1 .. 192.168.86.254). Used as a
    // fallback when multicast discovery doesn't propagate (Google
    // WiFi / Nest WiFi blocks multicast cross-segment, isolated
    // IoT VLANs, etc.). Govee devices accept the same scan packet
    // via unicast as via multicast, so this discovers everything
    // multicast does and works in environments where multicast
    // doesn't.
    //
    // Cost: ~254 small UDP packets sent in parallel — trivial load.
    // Listens on UDP 4002 same as multicast scan. Same matcher.
    public static async Task SweepAsync(int timeoutMs = 4000)
    {
        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan.SweepAsync entered (timeout={timeoutMs}ms)"); } catch { }

        UdpClient listener = null;
        try
        {
            listener = new UdpClient();
            listener.ExclusiveAddressUse = false;
            listener.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, LanRespPort));
        }
        catch (SocketException sx) when (sx.SocketErrorCode == SocketError.AccessDenied)
        {
            try { listener?.Dispose(); } catch { }
            int killed = KillGoveeProcesses();
            if (killed == 0)
            {
                try { DalamudApi.ChatGui.Print(
                    "[LightSync] sweep blocked: UDP 4002 held by a non-Govee process. Check netstat."); } catch { }
                return;
            }
            await Task.Delay(1000).ConfigureAwait(false);
            try
            {
                listener = new UdpClient();
                listener.ExclusiveAddressUse = false;
                listener.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, LanRespPort));
            }
            catch
            {
                try { listener?.Dispose(); } catch { }
                return;
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan sweep: bind failed — {ex.Message}"); } catch { }
            try { listener?.Dispose(); } catch { }
            return;
        }

        var responses = new List<JsonObject>();
        using var cts = new CancellationTokenSource(timeoutMs);

        var listenTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await listener.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        var text = Encoding.UTF8.GetString(result.Buffer);
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] LightSyncLan sweep recv from {result.RemoteEndPoint}: {text}"); } catch { }
                        try
                        {
                            var json = JsonNode.Parse(text) as JsonObject;
                            if (json != null) responses.Add(json);
                        }
                        catch { }
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch { }
        });

        // Compose scan payload + collect every up interface's /24
        // subnet. For each interface, send unicast to every host
        // address in its subnet.
        var scan = "{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}";
        var bytes = Encoding.UTF8.GetBytes(scan);

        int totalSent = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ipBytes = addr.Address.GetAddressBytes();
                    if (ipBytes[0] == 169 && ipBytes[1] == 254) continue; // skip APIPA
                    if (ipBytes[0] == 127) continue;                       // skip loopback
                    // Use the first three octets as the subnet prefix.
                    // Assumes /24 — typical home network. /16 would
                    // mean 65k packets which is rude.
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] LightSyncLan sweep: scanning {ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.1-254 via {ni.Name} ({addr.Address})"); } catch { }
                    UdpClient sender = null;
                    try
                    {
                        sender = new UdpClient(new IPEndPoint(addr.Address, 0));
                        for (int host = 1; host <= 254; host++)
                        {
                            var target = new IPEndPoint(
                                new IPAddress(new byte[] { ipBytes[0], ipBytes[1], ipBytes[2], (byte)host }),
                                LanScanPort);
                            try { await sender.SendAsync(bytes, bytes.Length, target).ConfigureAwait(false); }
                            catch { /* skip this IP, continue sweep */ }
                            totalSent++;
                        }
                    }
                    finally
                    {
                        try { sender?.Dispose(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan sweep: enumeration failed — {ex.Message}"); } catch { }
        }

        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan sweep: dispatched {totalSent} unicast probes; waiting up to {timeoutMs}ms for replies."); } catch { }

        try { await listenTask.ConfigureAwait(false); } catch { }
        try { listener.Dispose(); } catch { }

        try { DalamudApi.PluginLog.Information(
            $"[noWickyXIV] LightSyncLan sweep: window closed; received {responses.Count} response(s)."); } catch { }

        MergeScanResponses(responses);
    }

    // ARP cache dump. Runs `arp -a` and dumps the result to /xllog
    // so the user can identify which IP belongs to which Govee
    // device (by matching MAC prefixes to known Govee OUIs, or by
    // elimination). Useful when UDP discovery (multicast OR unicast
    // sweep) fails — every device the OS has recently talked to
    // shows up in this table even if it doesn't reply to our probes.
    public static async Task ArpDumpAsync()
    {
        try { DalamudApi.PluginLog.Information(
            "[noWickyXIV] LightSyncLan.ArpDumpAsync entered"); } catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "arp",
                Arguments = "-a",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                try { DalamudApi.PluginLog.Warning(
                    "[noWickyXIV] LightSyncLan: failed to start `arp -a`"); } catch { }
                return;
            }
            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSyncLan ARP table:\n{output}"); } catch { }

            // Highlight Govee-ish entries (any MAC that we suspect is
            // smart-light hardware) — Govee uses several OUIs over the
            // years; substring match on a few common Govee prefixes
            // catches most. Anything not flagged here can still be
            // identified by the user via name/MAC in the dumped table.
            string[] goveeOuiPrefixes = new[]
            {
                "00-da-e2", "b0-60-88", "dc-f5-1b", "84-7a-30", "5c-89-1c",
                "98-d3-31", "8c-f6-81", "00-1d-c9", "a4-c1-38", "e4-c0-15"
            };

            int found = 0;
            foreach (var line in output.Split('\n'))
            {
                var lower = line.ToLowerInvariant();
                foreach (var oui in goveeOuiPrefixes)
                {
                    if (lower.Contains(oui))
                    {
                        try { DalamudApi.PluginLog.Information(
                            $"[noWickyXIV] LightSyncLan ARP candidate Govee device: {line.Trim()}"); } catch { }
                        found++;
                        break;
                    }
                }
            }

            try { DalamudApi.ChatGui.Print(
                $"[LightSync] ARP dump logged ({found} likely-Govee entries flagged). See /xllog. Paste the suspected IP into the LAN IP field for the matching device."); } catch { }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan ARP dump failed: {ex.Message}"); } catch { }
        }
    }

    // Enumerates running processes, kills any whose name contains
    // "Govee" (case-insensitive). Substring match is deliberate so
    // we catch the various Govee Desktop/Home executable names
    // without needing to keep an exact-match list. Returns the
    // number killed for logging.
    //
    // Safety: we ONLY filter on the "Govee" substring. Any other
    // process holding UDP 4002 is left alone — the user gets a log
    // line directing them to netstat instead.
    private static int KillGoveeProcesses()
    {
        int killed = 0;
        try
        {
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                Process held = p;
                try
                {
                    if (held.ProcessName.IndexOf("Govee", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        held.Dispose();
                        continue;
                    }
                    try { DalamudApi.PluginLog.Information(
                        $"[noWickyXIV] LightSyncLan: killing Govee process '{held.ProcessName}' (pid {held.Id})"); } catch { }
                    held.Kill();
                    held.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    try { DalamudApi.PluginLog.Warning(
                        $"[noWickyXIV] LightSyncLan: failed to kill '{held.ProcessName}': {ex.Message}"); } catch { }
                }
                finally
                {
                    try { held.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncLan: process enumeration failed: {ex.Message}"); } catch { }
        }
        return killed;
    }

    private static void MergeScanResponses(List<JsonObject> responses)
    {
        if (responses.Count == 0)
        {
            try { DalamudApi.ChatGui.Print(
                "[LightSync] LAN scan returned 0 devices. Verify 'LAN Control' is enabled per-device in Govee Home, your PC + lights are on the same LAN segment, and Windows Firewall isn't blocking UDP 4002."); } catch { }
            return;
        }

        var devices = noWickyXIV.Config.LightSyncDevices;
        int matched = 0;
        int orphaned = 0;

        foreach (var resp in responses)
        {
            var data = resp["msg"]?["data"] as JsonObject;
            if (data == null) continue;
            var ip = data["ip"]?.GetValue<string>() ?? "";
            var devId = data["device"]?.GetValue<string>() ?? "";
            var sku = data["sku"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(devId)) continue;

            // 1. Exact device id match — preferred.
            var match = devices.Find(d =>
                string.Equals(d.DeviceId, devId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                match.LanIp = ip;
                if (string.IsNullOrEmpty(match.Sku) && !string.IsNullOrEmpty(sku))
                    match.Sku = sku;
                matched++;
                continue;
            }

            // 2. Suffix match — LAN device id may be a substring of the
            // Cloud device id (Govee's MAC formatting varies).
            match = devices.Find(d =>
                !string.IsNullOrEmpty(d.DeviceId)
                && (d.DeviceId.EndsWith(devId, StringComparison.OrdinalIgnoreCase)
                 || devId.EndsWith(d.DeviceId, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
            {
                match.LanIp = ip;
                matched++;
                continue;
            }

            // 3. Unique-SKU fallback — only one device with that SKU
            // and no LanIp yet → assume it's the same.
            if (!string.IsNullOrEmpty(sku))
            {
                var skuMatches = devices.FindAll(d =>
                    string.Equals(d.Sku, sku, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(d.LanIp));
                if (skuMatches.Count == 1)
                {
                    skuMatches[0].LanIp = ip;
                    matched++;
                    continue;
                }
            }

            orphaned++;
            try { DalamudApi.PluginLog.Information(
                $"[noWickyXIV] LightSyncLan unmatched LAN device: ip={ip} device={devId} sku={sku} (will need manual IP entry in UI)"); } catch { }
        }

        try { noWickyXIV.Config.Save(); } catch { }
        try { DalamudApi.ChatGui.Print(
            $"[LightSync] LAN scan: {matched} matched, {orphaned} orphaned (see /xllog)."); } catch { }
    }
}
