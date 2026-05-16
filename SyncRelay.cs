using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Plugin.Ipc;
using LiteNetLib;
using LiteNetLib.Utils;
using Open.Nat;

namespace noWickyXIV;

public static class SyncRelay
{
    private const byte MSG_STATE = 0x01;
    private const byte MSG_HELLO = 0x02;
    private const string CONNECTION_KEY = "MXIV_v1";
    private const string TELL_PREFIX = "​‏MXIV:"; // zero-width chars + prefix — invisible in chat
    private const int DISCOVERY_INTERVAL_FRAMES = 300; // ~5 seconds at 60fps

    private static NetManager _net;
    private static EventBasedNetListener _listener;
    private static CancellationTokenSource _cts;
    private static Task _pollTask;
    private static Mapping _upnpMapping;
    private static NatDevice _natDevice;
    private static string _publicIp = "";
    private static int _listenPort;

    // Lightless IPC
    private static ICallGateSubscriber<List<nint>> _lightlessGetHandled;
    private static bool _lightlessAvailable;

    // Discovery state
    private static int _discoveryCounter;
    private static readonly HashSet<string> _handshakeSent = new();
    private static readonly HashSet<string> _connectedNames = new();

    // Outbound throttle
    private static int _sendCounter;
    private const int SEND_INTERVAL = 5;

    // Peer state keyed by LiteNetLib peer ID
    private static readonly ConcurrentDictionary<int, PeerState> _peers = new();

    public static bool Active => _net != null && _net.IsRunning;
    public static string Status { get; private set; } = "Idle";
    public static int PeerCount => _net?.ConnectedPeersCount ?? 0;

    public struct PeerState
    {
        public Vector3 HeadTarget;
        public Vector3 EyeTarget;
        public float FacingCamT;
        public string Name;
        public long LastUpdateTick;
    }

    public static void Initialize()
    {
        // Subscribe to Lightless IPC
        try
        {
            _lightlessGetHandled = DalamudApi.PluginInterface
                .GetIpcSubscriber<List<nint>>("LightlessSync.GetHandledAddresses");
            _lightlessAvailable = true;
        }
        catch
        {
            _lightlessAvailable = false;
        }

        // Hook incoming chat for handshake tells
        try { DalamudApi.ChatGui.ChatMessage += OnChatMessage; } catch { }
    }

    public static void Start(int port)
    {
        if (Active) return;
        _listenPort = port;
        _cts = new CancellationTokenSource();
        _handshakeSent.Clear();
        _connectedNames.Clear();

        _listener = new EventBasedNetListener();
        WireEvents();

        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
        };

        if (!_net.Start(port))
        {
            Status = $"Failed to bind port {port}";
            return;
        }

        Status = $"Listening on {port}";
        _pollTask = Task.Run(() => PollLoop(), _cts.Token);

        // Discover our public IP via UPnP + map the port
        _ = Task.Run(() => TryUpnpAndDiscover(port));
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _net?.Stop();
        _net = null;
        _listener = null;
        _peers.Clear();
        _handshakeSent.Clear();
        _connectedNames.Clear();
        Status = "Idle";

        if (_upnpMapping != null && _natDevice != null)
        {
            try { _natDevice.DeletePortMapAsync(_upnpMapping).Wait(2000); } catch { }
            _upnpMapping = null;
            _natDevice = null;
        }
    }

    // Called per-frame from the main update loop
    public static void Update()
    {
        var cfg = noWickyXIV.Config;
        if (!cfg.SyncEnabled) { if (Active) Stop(); return; }
        if (!Active) Start(cfg.SyncPort);

        // Periodic discovery: find Lightless-paired players and handshake
        _discoveryCounter++;
        if (_discoveryCounter >= DISCOVERY_INTERVAL_FRAMES)
        {
            _discoveryCounter = 0;
            DiscoverPairedPlayers();
        }
    }

    private static void DiscoverPairedPlayers()
    {
        if (!_lightlessAvailable || !Active) return;

        List<nint> handled;
        try { handled = _lightlessGetHandled.InvokeFunc(); }
        catch { return; } // Lightless not loaded

        if (handled == null || handled.Count == 0) return;

        foreach (var addr in handled)
        {
            // Find this address in ObjectTable to get the character name
            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null || obj.Address != addr) continue;
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc) continue;

                var name = obj.Name.TextValue;
                if (string.IsNullOrEmpty(name)) continue;
                if (_connectedNames.Contains(name)) continue;
                if (_handshakeSent.Contains(name)) continue;

                // Send handshake tell with our listen info
                SendHandshakeTell(name);
                _handshakeSent.Add(name);
                break;
            }
        }
    }

    private static void SendHandshakeTell(string playerName)
    {
        string ip = !string.IsNullOrEmpty(_publicIp) ? _publicIp : GetLocalIp();
        if (string.IsNullOrEmpty(ip)) return;

        // Coded tell: invisible prefix + ip:port
        string msg = $"/tell {playerName} {TELL_PREFIX}{ip}:{_listenPort}";
        try { ChatSend.Send(msg); } catch { }
    }

    private static void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        try
        {
            int kind = Convert.ToInt32(message.LogKind);
            if (kind != (int)XivChatType.TellIncoming) return;

            var body = message.Message.TextValue;
            if (body == null || !body.Contains(TELL_PREFIX)) return;

            // Parse ip:port
            int idx = body.IndexOf(TELL_PREFIX) + TELL_PREFIX.Length;
            var endpoint = body.Substring(idx).Trim();
            var parts = endpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port)) return;
            string ip = parts[0];

            // Connect to the peer
            if (_net != null && _net.IsRunning)
            {
                _net.Connect(ip, port, CONNECTION_KEY);
                DalamudApi.LogInfo($"[SyncRelay] Handshake received, connecting to {ip}:{port}");
            }
        }
        catch { }
    }

    private static void WireEvents()
    {
        _listener.ConnectionRequestEvent += request =>
        {
            request.AcceptIfKey(CONNECTION_KEY);
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _peers[peer.Id] = new PeerState { Name = "" };
            Status = $"Syncing ({_net.ConnectedPeersCount} peers)";

            // Send our character name
            var writer = new NetDataWriter();
            writer.Put(MSG_HELLO);
            writer.Put(GetLocalPlayerName());
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        };

        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (_peers.TryRemove(peer.Id, out var ps) && !string.IsNullOrEmpty(ps.Name))
            {
                _connectedNames.Remove(ps.Name);
                _handshakeSent.Remove(ps.Name);
            }
            Status = _net.ConnectedPeersCount > 0
                ? $"Syncing ({_net.ConnectedPeersCount} peers)"
                : $"Listening on {_listenPort}";
        };

        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            if (reader.AvailableBytes < 1) return;
            byte msgType = reader.GetByte();

            if (msgType == MSG_STATE && reader.AvailableBytes >= 28)
            {
                _peers.TryGetValue(peer.Id, out var state);
                state.HeadTarget = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
                state.EyeTarget = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
                state.FacingCamT = reader.GetFloat();
                state.LastUpdateTick = Environment.TickCount64;
                _peers[peer.Id] = state;

                // Relay to other peers (star through whoever receives)
                var others = new List<NetPeer>();
                _net.GetConnectedPeers(others);
                if (others.Count > 1)
                {
                    var fwd = new NetDataWriter();
                    fwd.Put(MSG_STATE);
                    fwd.Put(state.HeadTarget.X); fwd.Put(state.HeadTarget.Y); fwd.Put(state.HeadTarget.Z);
                    fwd.Put(state.EyeTarget.X); fwd.Put(state.EyeTarget.Y); fwd.Put(state.EyeTarget.Z);
                    fwd.Put(state.FacingCamT);
                    foreach (var other in others)
                        if (other.Id != peer.Id)
                            other.Send(fwd, DeliveryMethod.Unreliable);
                }
            }
            else if (msgType == MSG_HELLO)
            {
                var name = reader.GetString();
                if (_peers.TryGetValue(peer.Id, out var ps))
                {
                    ps.Name = name;
                    _peers[peer.Id] = ps;
                    _connectedNames.Add(name);
                }
                DalamudApi.LogInfo($"[SyncRelay] Peer identified: {name}");
            }
        };
    }

    private static void PollLoop()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested && _net != null && _net.IsRunning)
            {
                _net.PollEvents();
                Thread.Sleep(1);
            }
        }
        catch (OperationCanceledException) { }
    }

    public static void SendState(Vector3 headTarget, Vector3 eyeTarget, float facingCamT)
    {
        if (_net == null || !_net.IsRunning || _net.ConnectedPeersCount == 0) return;

        _sendCounter++;
        if (_sendCounter % SEND_INTERVAL != 0) return;

        var writer = new NetDataWriter();
        writer.Put(MSG_STATE);
        writer.Put(headTarget.X); writer.Put(headTarget.Y); writer.Put(headTarget.Z);
        writer.Put(eyeTarget.X); writer.Put(eyeTarget.Y); writer.Put(eyeTarget.Z);
        writer.Put(facingCamT);

        var peers = new List<NetPeer>();
        _net.GetConnectedPeers(peers);
        foreach (var peer in peers)
            peer.Send(writer, DeliveryMethod.Unreliable);
    }

    public static unsafe void ApplyPeerStates()
    {
        if (_net == null || !_net.IsRunning) return;
        if (!noWickyXIV.Config.SyncEnabled) return;

        long now = Environment.TickCount64;

        foreach (var kvp in _peers)
        {
            var state = kvp.Value;
            if (string.IsNullOrEmpty(state.Name)) continue;
            if (now - state.LastUpdateTick > 2000) continue;

            foreach (var obj in DalamudApi.ObjectTable)
            {
                if (obj == null) continue;
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc) continue;
                if (!obj.Name.TextValue.Equals(state.Name, StringComparison.OrdinalIgnoreCase)) continue;

                var chr = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
                if (chr == null) continue;

                HeadTracker.WritePeerLookAt(chr, state.HeadTarget, state.EyeTarget);
                break;
            }
        }
    }

    private static async Task TryUpnpAndDiscover(int port)
    {
        try
        {
            var disc = new NatDiscoverer();
            var cts = new CancellationTokenSource(5000);
            _natDevice = await disc.DiscoverDeviceAsync(PortMapper.Upnp, cts);

            _upnpMapping = new Mapping(Protocol.Udp, port, port, "Modern XIV Sync");
            await _natDevice.CreatePortMapAsync(_upnpMapping);

            var externalIp = await _natDevice.GetExternalIPAsync();
            _publicIp = externalIp.ToString();
            DalamudApi.LogInfo($"[SyncRelay] UPnP mapped {port}, public IP: {_publicIp}");
        }
        catch (Exception ex)
        {
            _publicIp = "";
            DalamudApi.LogInfo($"[SyncRelay] UPnP failed, using local IP: {ex.Message}");
        }
    }

    private static string GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
        }
        catch { return ""; }
    }

    private static string GetLocalPlayerName()
    {
        try { return DalamudApi.ObjectTable.LocalPlayer?.Name?.TextValue ?? ""; }
        catch { return ""; }
    }

    public static void Dispose()
    {
        Stop();
        try { DalamudApi.ChatGui.ChatMessage -= OnChatMessage; } catch { }
    }
}
