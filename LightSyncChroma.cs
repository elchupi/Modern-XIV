using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace noWickyXIV;

// Razer Chroma SDK REST client.
//
// Drives event lighting on the H6603 SyncBox via the local Chroma →
// Govee Desktop → H6603 bridge. This is the same path Apex Legends
// and League of Legends use; Govee deliberately gated direct Video
// Sync engage to their desktop app, but their Chroma compatibility
// makes our session a "Chroma client" that the bridge auto-routes
// to the box.
//
// What's required on the user's PC:
//   1. Razer Synapse 3 + Chroma Connect module (free, no Razer
//      hardware needed). Provides http://localhost:54235.
//   2. Govee Desktop app with "Razer Chroma" / "Chroma Connect"
//      toggle enabled in its settings.
//   3. H6603 set to "Sync from PC" / DreamView source = PC.
//
// Flow:
//   Initialize → POST /razer/chromasdk with our app metadata,
//     receive a session URI.
//   Update     → PUT <uri>/heartbeat every ~3s so Razer doesn't
//     time us out (15s default timeout).
//   FlashColor → PUT <uri>/chromalink with a CHROMA_STATIC effect.
//     Govee Desktop receives via Synapse's Chroma broadcast and
//     drives the H6603. NOTE: Razer's REST API uses BGR color
//     encoding (red = 0x0000FF, blue = 0xFF0000), opposite of HTML.
//   Dispose    → DELETE <uri>. Synapse releases the session,
//     bridge stops receiving Chroma frames, H6603 auto-reverts to
//     whatever default mode Govee Desktop had it on (Video Sync).
//
// No "engage Video Sync" command is sent or needed — that's what
// the bridge does on its own when our session ends.
public static class LightSyncChroma
{
    private const string ChromaBase = "http://localhost:54235/";

    private static HttpClient _http;
    private static string _sessionUri; // e.g. "http://localhost:54235/sid=12345"
    private static double _lastHeartbeatT;
    private static bool _initialized;
    private static bool _initFailedNotified;

    public static bool IsActive => _initialized && !string.IsNullOrEmpty(_sessionUri);

    public static void Initialize()
    {
        if (_initialized) return;
        try
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(ChromaBase),
                Timeout = TimeSpan.FromSeconds(5),
            };
            _ = StartSessionAsync();
            _initialized = true;
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncChroma init failed: {ex.Message}"); } catch { }
        }
    }

    public static void Dispose()
    {
        // Best-effort session release. Synapse's Chroma server reaps
        // dead sessions on its own (15s heartbeat timeout) so even if
        // this DELETE fails the bridge cleans up after us shortly.
        try
        {
            if (!string.IsNullOrEmpty(_sessionUri))
                _ = _http?.DeleteAsync(_sessionUri);
        }
        catch { }
        try { _http?.Dispose(); } catch { }
        _http = null;
        _sessionUri = null;
        _initialized = false;
        _initFailedNotified = false;
    }

    // Heartbeat keeper — call every frame. Cheap when no session
    // is active (returns immediately) and self-paced (only PUTs
    // when ~3s have passed).
    public static void Update()
    {
        if (!IsActive) return;
        var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        if (now - _lastHeartbeatT < 3.0) return;
        _lastHeartbeatT = now;
        _ = HeartbeatAsync();
    }

    // RGB → BGR conversion. Razer's REST takes 0xBBGGRR while we
    // store everything as 0xRRGGBB internally (HTML/CSS / Govee
    // Cloud API convention).
    private static int RgbToBgr(int rgb)
    {
        int r = (rgb >> 16) & 0xFF;
        int g = (rgb >>  8) & 0xFF;
        int b =  rgb        & 0xFF;
        return (b << 16) | (g << 8) | r;
    }

    public static async Task FlashColor(int rgb)
    {
        if (!IsActive) return;
        try
        {
            var bgr = RgbToBgr(rgb);
            // Send the static color to multiple device types so it
            // covers any Chroma-compatible registered device. The
            // chromalink endpoint is the catch-all for accessories
            // like the Govee bridge; sending to keyboard too means
            // the effect also drives any actual Razer keyboard the
            // user might have.
            var body = new JsonObject
            {
                ["effect"] = "CHROMA_STATIC",
                ["param"] = new JsonObject { ["color"] = bgr },
            };
            await PutDevice("chromalink", body).ConfigureAwait(false);
            await PutDevice("keyboard",  body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncChroma FlashColor failed: {ex.Message}"); } catch { }
        }
    }

    // Releases the SyncBox back to its default mode by clearing our
    // effects. Razer Chroma's CHROMA_NONE effect tells Synapse "no
    // longer driving this device," so the bridge stops asserting
    // override and the H6603 falls through to Video Sync. Used
    // explicitly when an event's flash duration ends.
    public static async Task ClearEffects()
    {
        if (!IsActive) return;
        try
        {
            var body = new JsonObject { ["effect"] = "CHROMA_NONE" };
            await PutDevice("chromalink", body).ConfigureAwait(false);
            await PutDevice("keyboard",  body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncChroma ClearEffects failed: {ex.Message}"); } catch { }
        }
    }

    private static async Task PutDevice(string device, JsonObject body)
    {
        if (string.IsNullOrEmpty(_sessionUri) || _http == null) return;
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_sessionUri}/{device}")
        { Content = new StringContent(body.ToJsonString(),
            System.Text.Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncChroma {device} {(int)resp.StatusCode}: {b}"); } catch { }
        }
    }

    private static async Task StartSessionAsync()
    {
        try
        {
            // Razer Chroma init payload — the REST docs require
            // title/description/author/device_supported/category
            // for the session to be accepted.
            var init = new JsonObject
            {
                ["title"] = "noWickyXIV",
                ["description"] = "FFXIV game-event lighting via Govee Sync Box",
                ["author"] = new JsonObject
                {
                    ["name"] = "noWickyXIV",
                    ["contact"] = "https://github.com/local",
                },
                ["device_supported"] = new JsonArray
                    { "keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink" },
                ["category"] = "application",
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "razer/chromasdk")
            { Content = new StringContent(init.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                NotifyInitFailed(
                    $"Chroma init failed: status={(int)resp.StatusCode} body={body}");
                return;
            }
            var json = JsonNode.Parse(body) as JsonObject;
            var uri = json?["uri"]?.GetValue<string>();
            if (string.IsNullOrEmpty(uri))
            {
                NotifyInitFailed($"Chroma init returned no uri: {body}");
                return;
            }
            _sessionUri = uri.TrimEnd('/');
            _lastHeartbeatT = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        }
        catch (Exception ex)
        {
            NotifyInitFailed($"Chroma init threw: {ex.Message}");
        }
    }

    private static async Task HeartbeatAsync()
    {
        if (string.IsNullOrEmpty(_sessionUri) || _http == null) return;
        try
        {
            using var resp = await _http.PutAsync(
                $"{_sessionUri}/heartbeat",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Session likely expired. Tear down and reconnect on
                // next Initialize call.
                try { DalamudApi.PluginLog.Warning(
                    $"[noWickyXIV] LightSyncChroma heartbeat lost ({(int)resp.StatusCode}); reconnecting."); } catch { }
                _sessionUri = null;
                _ = StartSessionAsync();
            }
        }
        catch (Exception ex)
        {
            try { DalamudApi.PluginLog.Warning(
                $"[noWickyXIV] LightSyncChroma heartbeat threw: {ex.Message}"); } catch { }
            _sessionUri = null;
        }
    }

    private static void NotifyInitFailed(string msg)
    {
        if (_initFailedNotified) return;
        _initFailedNotified = true;
        try { DalamudApi.PluginLog.Warning($"[noWickyXIV] {msg}"); } catch { }
        try { DalamudApi.ChatGui.Print(
            "[LightSync] Razer Chroma server unreachable on localhost:54235. Make sure Razer Synapse 3 + Chroma Connect is running, and Govee Desktop has its Chroma toggle enabled."); } catch { }
    }
}
