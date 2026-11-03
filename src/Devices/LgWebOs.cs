using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IOHub.Devices;

/// <summary>How IO Hub reaches one LG TV. Remembered per display, because a TV that's off isn't detected at all.</summary>
public sealed class LgTvLink
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>The TV's network card, for Wake-on-LAN. Learned from the network while the TV is on.</summary>
    public string Mac { get; set; } = "";
    /// <summary>What the TV hands back when its "Allow this device?" prompt is accepted. Lets IO Hub in without asking again.</summary>
    public string ClientKey { get; set; } = "";
    /// <summary>An LG display someone said isn't a TV, so it isn't offered again.</summary>
    public bool NotATv { get; set; }

    [JsonIgnore] public bool IsPaired => Ip != "" && ClientKey != "";
}

public sealed record LgTvFound(string Ip, string Name);

/// <summary>
/// Talks to LG webOS TVs directly: finds them on the network (SSDP), pairs (the TV shows "Allow this device?" once),
/// wakes them (Wake-on-LAN) and sends commands over the TV's WebSocket API (ssap://…). The same protocol LGTV Companion
/// and Home Assistant's aiowebostv use; written from aiowebostv's handshake (Apache 2.0) without either installed.
/// Turning a TV on needs "Turn on via Wi-Fi" (or Quick Start+) enabled on the TV.
/// </summary>
public static class LgWebOs
{
    const int WsPort = 3000, WssPort = 3001;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);

    // ---------------- finding TVs ----------------

    /// <summary>Asks the local network for LG TVs (SSDP, the way LG's own phone app finds them).</summary>
    public static async Task<List<LgTvFound>> DiscoverAsync(TimeSpan listenFor)
    {
        var found = new Dictionary<string, LgTvFound>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:lge-com:service:webos-second-screen:1\r\n\r\n");
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        for (int i = 0; i < 2; i++) await udp.SendAsync(search, target);

        using var stop = new CancellationTokenSource(listenFor);
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(stop.Token);
                var text = Encoding.UTF8.GetString(reply.Buffer);
                var ip = reply.RemoteEndPoint.Address.ToString();
                if (found.ContainsKey(ip)) continue;
                // Some devices (Philips Hue bridges, for one) answer every search; only keep ones that say they're webOS.
                if (!text.Contains("webos-second-screen", StringComparison.OrdinalIgnoreCase)) continue;
                // LG puts the TV's name in its own header, URL-encoded.
                var name = text.Split("\r\n")
                    .Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2 && p[0].Trim().Equals("DLNADeviceName.lge.com", StringComparison.OrdinalIgnoreCase))
                    .Select(p => Uri.UnescapeDataString(p[1].Trim()))
                    .FirstOrDefault();
                found[ip] = new LgTvFound(ip, string.IsNullOrWhiteSpace(name) ? "LG TV" : name);
            }
        }
        catch (OperationCanceledException) { }
        return found.Values.OrderBy(t => t.Name).ToList();
    }

    /// <summary>Whether the TV is answering on its API ports, i.e. on (or in a networked standby).</summary>
    public static async Task<bool> IsAwakeAsync(string ip)
    {
        foreach (var port in new[] { WsPort, WssPort })
        {
            try
            {
                using var tcp = new TcpClient();
                using var cut = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                await tcp.ConnectAsync(ip, port, cut.Token);
                return true;
            }
            catch { }
        }
        return false;
    }

    // ---------------- pairing ----------------

    /// <summary>
    /// Pairs with the TV at <paramref name="ip"/>. It shows "Allow this device?" and this waits up to a minute for
    /// someone to accept with the remote. Returns the client key, or an error message.
    /// </summary>
    public static async Task<(string? Key, string? Error)> PairAsync(string ip, CancellationToken cancel = default)
    {
        try
        {
            using var ws = await ConnectAsync(ip, cancel);
            var key = await RegisterAsync(ws, existingKey: null, allowPrompt: true, cancel);
            await CloseAsync(ws);
            return (key, null);
        }
        catch (Exception ex) { return (null, Describe(ex)); }
    }

    // ---------------- commands ----------------

    /// <summary>Turns the TV on: Wake-on-LAN, repeated until its API answers (up to 20 seconds).</summary>
    public static async Task<string?> PowerOnAsync(LgTvLink tv)
    {
        if (await IsAwakeAsync(tv.Ip)) return null;
        if (tv.Mac == "") return $"{Name(tv)} can't be turned on: its network address (MAC) isn't known yet. Pair it again while it's on.";
        var deadline = DateTime.Now + TimeSpan.FromSeconds(20);
        while (DateTime.Now < deadline)
        {
            await WakeAsync(tv.Mac, tv.Ip);
            if (await IsAwakeAsync(tv.Ip)) return null;
            await Task.Delay(1000);
        }
        return $"{Name(tv)} didn't wake up. Check \"Turn on via Wi-Fi\" (or Quick Start+) is on in the TV's settings.";
    }

    public static Task<string?> PowerOffAsync(LgTvLink tv) =>
        // A TV switching itself off often doesn't answer, so don't wait for a reply.
        SendAsync(tv, "ssap://system/turnOff", null, waitForReply: false);

    public static Task<string?> SetHdmiAsync(LgTvLink tv, int input) =>
        SendAsync(tv, "ssap://tv/switchInput", new JsonObject { ["inputId"] = $"HDMI_{input}" }, waitForReply: true);

    /// <summary>Connects, logs in with the saved key, sends one request. Returns an error message, or null.</summary>
    static async Task<string?> SendAsync(LgTvLink tv, string uri, JsonObject? payload, bool waitForReply)
    {
        if (!tv.IsPaired) return $"{Name(tv)} isn't paired yet.";
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var ws = await ConnectAsync(tv.Ip, cancel.Token);
            await RegisterAsync(ws, tv.ClientKey, allowPrompt: false, cancel.Token);
            var message = new JsonObject { ["id"] = "cmd_1", ["type"] = "request", ["uri"] = uri, ["payload"] = payload ?? new JsonObject() };
            await SendJsonAsync(ws, message, cancel.Token);
            if (waitForReply)
            {
                var reply = await ReceiveUntilAsync(ws, m => (string?)m["id"] == "cmd_1", cancel.Token);
                if ((string?)reply["type"] == "error") return $"{Name(tv)} refused it: {(string?)reply["error"]}";
            }
            await CloseAsync(ws);
            return null;
        }
        catch (Exception ex) { return $"{Name(tv)}: {Describe(ex)}"; }
    }

    // ---------------- protocol ----------------

    /// <summary>ws:// on port 3000, or wss:// on 3001 for firmware that insists. The TV's certificate is self-signed.</summary>
    static async Task<ClientWebSocket> ConnectAsync(string ip, CancellationToken cancel)
    {
        var ws = new ClientWebSocket();
        try
        {
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cut.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri($"ws://{ip}:{WsPort}"), cut.Token);
            return ws;
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            ws.Dispose();
            var secure = new ClientWebSocket();
            secure.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var cut = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cut.CancelAfter(TimeSpan.FromSeconds(5));
            await secure.ConnectAsync(new Uri($"wss://{ip}:{WssPort}"), cut.Token);
            return secure;
        }
    }

    /// <summary>
    /// Registers with the TV. With a saved key it lets IO Hub straight in; without one it shows the pairing prompt,
    /// which only <paramref name="allowPrompt"/> waits for. Returns the client key.
    /// </summary>
    static async Task<string> RegisterAsync(ClientWebSocket ws, string? existingKey, bool allowPrompt, CancellationToken cancel)
    {
        // Newer webOS versions want this asked before registering.
        await SendJsonAsync(ws, new JsonObject { ["id"] = "sysinfo", ["type"] = "request", ["uri"] = "ssap://system/getSystemInfo", ["payload"] = new JsonObject() }, cancel);
        await ReceiveUntilAsync(ws, m => (string?)m["id"] == "sysinfo", cancel);

        var payload = JsonNode.Parse(RegistrationPayload)!.AsObject();
        if (existingKey != null) payload["client-key"] = existingKey;
        await SendJsonAsync(ws, new JsonObject { ["id"] = "register_0", ["type"] = "register", ["payload"] = payload }, cancel);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(allowPrompt ? TimeSpan.FromSeconds(60) : ReplyTimeout);
        while (true)
        {
            var reply = await ReceiveUntilAsync(ws, m => (string?)m["id"] == "register_0", wait.Token);
            var type = (string?)reply["type"];
            if (type == "registered")
                return (string?)reply["payload"]?["client-key"] ?? throw new InvalidDataException("The TV didn't send a key.");
            if (type == "error")
                throw new InvalidOperationException((string?)reply["error"] ?? "The TV said no.");
            // "response" with pairingType PROMPT: the TV is showing its prompt.
            if (!allowPrompt) throw new InvalidOperationException("The TV wants to be paired again.");
        }
    }

    static async Task SendJsonAsync(ClientWebSocket ws, JsonNode message, CancellationToken cancel) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancel);

    /// <summary>Reads messages until one matches (the TV may send others first). Times out after <see cref="ReplyTimeout"/> unless the token says otherwise.</summary>
    static async Task<JsonNode> ReceiveUntilAsync(ClientWebSocket ws, Func<JsonNode, bool> match, CancellationToken cancel)
    {
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (!cancel.CanBeCanceled) cut.CancelAfter(ReplyTimeout);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var text = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cut.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("The TV closed the connection.");
                text.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var node = JsonNode.Parse(text.ToArray());
            if (node != null && match(node)) return node;
        }
    }

    static async Task CloseAsync(ClientWebSocket ws)
    {
        try
        {
            using var cut = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cut.Token);
        }
        catch { }
    }

    static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "the TV didn't answer in time",
        WebSocketException or SocketException => "couldn't reach the TV (is it on and on this network?)",
        _ => ex.Message,
    };

    static string Name(LgTvLink tv) => tv.Name != "" ? tv.Name : "The TV";

    // ---------------- Wake-on-LAN ----------------

    /// <summary>Sends the magic packet (6 × FF, then the MAC 16 times) to the whole network and to the TV's subnet.</summary>
    static async Task WakeAsync(string mac, string ip)
    {
        var bytes = Convert.FromHexString(mac.Replace(":", "").Replace("-", ""));
        if (bytes.Length != 6) return;
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);

        using var udp = new UdpClient { EnableBroadcast = true };
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        if (IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = addr.GetAddressBytes();
            parts[3] = 255; // most home networks are /24
            targets.Add(new IPAddress(parts));
        }
        foreach (var target in targets)
            foreach (var port in new[] { 9, 7 })
                try { await udp.SendAsync(packet, new IPEndPoint(target, port)); } catch { }
    }

    /// <summary>The MAC address for an IP on the local network (an ARP lookup), or "" if it can't be found.</summary>
    public static string MacOf(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return "";
        var mac = new byte[6];
        int len = mac.Length;
        int dest = BitConverter.ToInt32(addr.GetAddressBytes(), 0);
        return SendARP(dest, 0, mac, ref len) == 0 && len == 6 ? string.Join(":", mac.Select(b => b.ToString("X2"))) : "";
    }

    [DllImport("iphlpapi.dll")] static extern int SendARP(int destIp, int srcIp, byte[] mac, ref int length);

    // What IO Hub asks the TV to allow. Same list as aiowebostv's (Apache 2.0); only power, inputs and
    // settings are used today.
    const string RegistrationPayload = """
        {
          "forcePairing": false,
          "pairingType": "PROMPT",
          "manifest": {
            "appVersion": "1.1",
            "manifestVersion": 1,
            "permissions": [
              "APP_TO_APP", "CLOSE", "CONTROL_AUDIO", "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
              "CONTROL_INPUT_MEDIA_PLAYBACK", "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_TEXT", "CONTROL_INPUT_TV",
              "CONTROL_MOUSE_AND_KEYBOARD", "CONTROL_POWER", "CONTROL_TV_SCREEN", "LAUNCH", "LAUNCH_WEBAPP",
              "READ_APP_STATUS", "READ_COUNTRY_INFO", "READ_CURRENT_CHANNEL", "READ_INPUT_DEVICE_LIST",
              "READ_INSTALLED_APPS", "READ_LGE_SDX", "READ_LGE_TV_INPUT_EVENTS", "READ_NETWORK_STATE",
              "READ_NOTIFICATIONS", "READ_POWER_STATE", "READ_RUNNING_APPS", "READ_SETTINGS", "READ_TV_CHANNEL_LIST",
              "READ_TV_CURRENT_TIME", "READ_UPDATE_INFO", "SEARCH", "TEST_OPEN", "TEST_PROTECTED", "TEST_SECURE",
              "UPDATE_FROM_REMOTE_APP", "WRITE_NOTIFICATION_ALERT", "WRITE_NOTIFICATION_TOAST", "WRITE_SETTINGS"
            ]
          }
        }
        """;
}
