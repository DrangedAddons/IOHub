using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IOHub.Devices;

/// <summary>How IO Hub reaches one LG TV. Remembered per display, because a TV that's off isn't detected at all.</summary>
public sealed class LgTvLink
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>
    /// The TV's network cards for Wake-on-LAN, comma separated (wired and Wi-Fi have different ones). Learned from the
    /// TV itself and from the network while it's on.
    /// </summary>
    public string Mac { get; set; } = "";
    /// <summary>What the TV hands back when its "Allow this device?" prompt is accepted. Lets IO Hub in without asking again.</summary>
    public string ClientKey { get; set; } = "";
    /// <summary>An LG display someone said isn't a TV, so it isn't offered again.</summary>
    public bool NotATv { get; set; }
    /// <summary>
    /// For Auto return: how the TV was before IO Hub switched to it. "off", or the app that was showing
    /// ("com.webos.app.hdmi1" for HDMI 1). "off|com.webos.app.hdmi1": it was off, and woke up on HDMI 1. Empty when
    /// there's nothing to return to. Saved, in case IO Hub restarts.
    /// </summary>
    public string ReturnTo { get; set; } = "";

    [JsonIgnore] public bool IsPaired => Ip != "" && ClientKey != "";
    [JsonIgnore] public IEnumerable<string> Macs => Mac.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Adds MAC addresses not already known. True if any were new.</summary>
    public bool AddMacs(IEnumerable<string> macs)
    {
        var known = Macs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = macs.Select(m => m.Trim().ToUpperInvariant().Replace('-', ':'))
            .Where(m => Regex.IsMatch(m, "^([0-9A-F]{2}:){5}[0-9A-F]{2}$") && m != "00:00:00:00:00:00" && known.Add(m)).ToList();
        if (added.Count > 0) Mac = string.Join(",", known);
        return added.Count > 0;
    }
}

public sealed record LgTvFound(string Ip, string Name);

public enum LgPower
{
    /// <summary>Not answering on the network at all: fully off, or unreachable.</summary>
    Unreachable,
    /// <summary>Off, but still on the network (Quick Start+ / "Active Standby"). Answers, but the screen is off.</summary>
    Standby,
    On,
    /// <summary>On with the picture turned off (the TV's "screen off").</summary>
    ScreenOff,
}

/// <param name="State">What the TV called it ("Active", "Active Standby"…), for the log.</param>
public sealed record LgTvState(LgPower Power, string? AppId, string? State);

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
    static readonly TimeSpan WakeFor = TimeSpan.FromSeconds(40);

    // ---------------- finding TVs ----------------

    /// <summary>Asks the local network for LG TVs (SSDP, the way LG's own phone app finds them).</summary>
    public static async Task<List<LgTvFound>> DiscoverAsync(TimeSpan listenFor)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, LgTvFound>();
        var search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:lge-com:service:webos-second-screen:1\r\n\r\n");
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        using var stop = new CancellationTokenSource(listenFor);

        // Asked from every network adapter: a single socket's search leaves by one adapter only, which on a PC with
        // a VPN or virtual adapters can be the wrong one.
        async Task SearchFrom(IPAddress local)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                for (int i = 0; i < 2; i++) await udp.SendAsync(search, target);
                while (true)
                {
                    var reply = await udp.ReceiveAsync(stop.Token);
                    var text = Encoding.UTF8.GetString(reply.Buffer);
                    // Some devices (Philips Hue bridges, for one) answer every search; only keep ones that say they're webOS.
                    if (!text.Contains("webos-second-screen", StringComparison.OrdinalIgnoreCase)) continue;
                    // LG puts the TV's name in its own header, URL-encoded.
                    var name = text.Split("\r\n")
                        .Select(l => l.Split(':', 2))
                        .Where(p => p.Length == 2 && p[0].Trim().Equals("DLNADeviceName.lge.com", StringComparison.OrdinalIgnoreCase))
                        .Select(p => Uri.UnescapeDataString(p[1].Trim()))
                        .FirstOrDefault();
                    var ip = reply.RemoteEndPoint.Address.ToString();
                    found.TryAdd(ip, new LgTvFound(ip, string.IsNullOrWhiteSpace(name) ? "LG TV" : name));
                }
            }
            catch { } // timed out, or this adapter can't send
        }
        await Task.WhenAll(LocalNetworks().Select(n => n.Local).Distinct().Select(SearchFrom));
        return found.Values.OrderBy(t => t.Name).ToList();
    }

    /// <summary>
    /// Whether the TV answers on its API ports. Not the same as on: with Quick Start+ it answers while it's off.
    /// Use <see cref="GetStateAsync"/> to know which.
    /// </summary>
    public static async Task<bool> IsReachableAsync(string ip)
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
    /// someone to accept with the remote. Returns the client key and the TV's MAC addresses, or an error message.
    /// </summary>
    public static async Task<(string? Key, List<string> Macs, string? Error)> PairAsync(string ip, CancellationToken cancel = default)
    {
        try
        {
            using var session = await Session.OpenAsync(ip, existingKey: null, allowPrompt: true, cancel);
            var macs = await session.MacsAsync();
            await session.CloseAsync();
            macs.AddRange(new[] { MacOf(ip) }.Where(m => m != ""));
            return (session.ClientKey, macs, null);
        }
        catch (Exception ex) { return (null, [], Describe(ex)); }
    }

    // ---------------- state ----------------

    /// <summary>Whether the TV is on and what it's showing. Also learns its MAC addresses while it's reachable.</summary>
    public static async Task<LgTvState> GetStateAsync(LgTvLink tv)
    {
        if (!tv.IsPaired || !await IsReachableAsync(tv.Ip)) return new(LgPower.Unreachable, null, null);
        try
        {
            using var session = await Session.OpenAsync(tv.Ip, tv.ClientKey, allowPrompt: false);
            tv.AddMacs(await session.MacsAsync());
            var power = await session.RequestAsync("ssap://com.webos.service.tvpower/power/getPowerState");
            var state = (string?)power?["payload"]?["state"];
            var app = (string?)(await session.RequestAsync("ssap://com.webos.applicationManager/getForegroundAppInfo"))?["payload"]?["appId"];
            await session.CloseAsync();
            LgPower result = state switch
            {
                "Power Off" or "Suspend" or "Active Standby" => LgPower.Standby,
                "Screen Off" => LgPower.ScreenOff,
                null => string.IsNullOrEmpty(app) ? LgPower.Standby : LgPower.On, // older webOS: no power state, but an app means on
                _ => LgPower.On,
            };
            return new(result, string.IsNullOrEmpty(app) ? null : app, state);
        }
        catch { return new(LgPower.Standby, null, "unknown (answers, but wouldn't say)"); }
    }

    // ---------------- commands ----------------

    /// <summary>
    /// Turns the TV on. Decides by the TV's power state, not by whether it answers (a Quick Start+ TV answers while
    /// off). Off or in standby: Wake-on-LAN from every network adapter, plus the TV's own "turn on" while it answers,
    /// repeated until it reports itself on (up to 40 seconds). Screen off: just turns the picture back on.
    /// </summary>
    public static async Task<string?> PowerOnAsync(LgTvLink tv, Action<string>? log = null)
    {
        var state = await GetStateAsync(tv);
        log?.Invoke($"state before: {state.Power} ({state.State ?? "no answer"})");
        if (state.Power == LgPower.On) return null;
        if (state.Power == LgPower.ScreenOff) return await SendAsync(tv, "ssap://com.webos.service.tvpower/power/turnOnScreen", null, waitForReply: true);

        if (!tv.Macs.Any()) tv.AddMacs([MacOf(tv.Ip)]);
        if (!tv.Macs.Any() && state.Power == LgPower.Unreachable && CompanionCli() == null)
            return $"{Name(tv)} can't be woken: its network address (MAC) isn't known yet. Turn it on once by hand with IO Hub running, or pair it again while it's on.";

        // LGTV Companion, if it's installed, gets asked too: it's another route to the same TV, and whichever works wins.
        if (CompanionCli() is { } cli)
        {
            log?.Invoke($"also asking LGTV Companion ({cli} -poweron)");
            _ = RunCompanionAsync(cli, "-poweron", log);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int round = 0;
        while (clock.Elapsed < WakeFor)
        {
            round++;
            var sent = await WakeAsync(tv.Macs, tv.Ip);
            if (round == 1) log?.Invoke($"wake packets to {(tv.Macs.Any() ? string.Join(", ", tv.Macs) : "no MAC")} via {sent}");
            // A TV in networked standby can also take a "turn on" over the API.
            if (state.Power == LgPower.Standby) await SendAsync(tv, "ssap://system/turnOn", null, waitForReply: false);
            await Task.Delay(2000);
            state = await GetStateAsync(tv);
            if (state.Power == LgPower.On) { log?.Invoke($"on after {clock.ElapsedMilliseconds} ms ({round} rounds)"); return null; }
            if (state.Power == LgPower.ScreenOff)
                return await SendAsync(tv, "ssap://com.webos.service.tvpower/power/turnOnScreen", null, waitForReply: true);
        }
        log?.Invoke($"still {state.Power} ({state.State ?? "no answer"}) after {WakeFor.TotalSeconds:0} s");
        return $"{Name(tv)} didn't turn on. Check \"Turn on via Wi-Fi\" (or Quick Start+) is on in the TV's settings.";
    }

    /// <summary>LGTV Companion's command-line tool, if it's installed. IO Hub doesn't need it; it's an extra route for turning on.</summary>
    public static string? CompanionCli()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), "LGTV Companion", "LGTVcli.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static async Task RunCompanionAsync(string cli, string args, Action<string>? log)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cli, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            if (process == null) return;
            using var cut = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cut.Token);
            log?.Invoke($"LGTV Companion finished (exit {process.ExitCode})");
        }
        catch (Exception ex) { log?.Invoke($"LGTV Companion: {ex.Message}"); }
    }

    public static Task<string?> PowerOffAsync(LgTvLink tv) =>
        // A TV switching itself off often doesn't answer, so don't wait for a reply.
        SendAsync(tv, "ssap://system/turnOff", null, waitForReply: false);

    public static Task<string?> SetHdmiAsync(LgTvLink tv, int input) =>
        SendAsync(tv, "ssap://tv/switchInput", new JsonObject { ["inputId"] = $"HDMI_{input}" }, waitForReply: true);

    /// <summary>
    /// Puts the TV back the way <see cref="LgTvLink.ReturnTo"/> says: off, an HDMI input, or the app that was showing.
    /// "off|app" means it was off on that input: switch back to it first, so the remote turns it on there next time.
    /// </summary>
    public static async Task<string?> ReturnAsync(LgTvLink tv, string returnTo)
    {
        if (returnTo == "off") return await PowerOffAsync(tv);
        if (returnTo.StartsWith("off|"))
        {
            var input = await ShowAsync(tv, returnTo[4..]);
            await Task.Delay(1500); // let the TV settle on that input before it's turned off, or it may not keep it
            return await PowerOffAsync(tv) ?? input;
        }
        return await ShowAsync(tv, returnTo);
    }

    /// <summary>Switches to an HDMI input, or launches the app, by its webOS app id.</summary>
    static Task<string?> ShowAsync(LgTvLink tv, string appId)
    {
        var hdmi = Regex.Match(appId, @"^com\.webos\.app\.hdmi(\d)$", RegexOptions.IgnoreCase);
        if (hdmi.Success) return SetHdmiAsync(tv, int.Parse(hdmi.Groups[1].Value));
        return SendAsync(tv, "ssap://system.launcher/launch", new JsonObject { ["id"] = appId }, waitForReply: true);
    }

    /// <summary>Connects, logs in with the saved key, sends one request. Returns an error message, or null.</summary>
    static async Task<string?> SendAsync(LgTvLink tv, string uri, JsonObject? payload, bool waitForReply)
    {
        if (!tv.IsPaired) return $"{Name(tv)} isn't paired yet.";
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var session = await Session.OpenAsync(tv.Ip, tv.ClientKey, allowPrompt: false, cancel.Token);
            var reply = await session.RequestAsync(uri, payload, waitForReply, cancel.Token);
            await session.CloseAsync();
            if (reply != null && (string?)reply["type"] == "error") return $"{Name(tv)} refused it: {(string?)reply["error"]}";
            return null;
        }
        catch (Exception ex) { return $"{Name(tv)}: {Describe(ex)}"; }
    }

    // ---------------- protocol ----------------

    /// <summary>A registered connection to one TV.</summary>
    sealed class Session : IDisposable
    {
        readonly ClientWebSocket _ws;
        int _next;
        public string ClientKey { get; private set; } = "";

        Session(ClientWebSocket ws) => _ws = ws;

        public static async Task<Session> OpenAsync(string ip, string? existingKey, bool allowPrompt, CancellationToken cancel = default)
        {
            var session = new Session(await ConnectAsync(ip, cancel));
            try
            {
                session.ClientKey = await RegisterAsync(session._ws, existingKey, allowPrompt, cancel);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>Sends a request; returns the reply (or null when not waiting for one, or when it errors out).</summary>
        public async Task<JsonNode?> RequestAsync(string uri, JsonObject? payload = null, bool waitForReply = true, CancellationToken cancel = default)
        {
            var id = $"req_{++_next}";
            await SendJsonAsync(_ws, new JsonObject { ["id"] = id, ["type"] = "request", ["uri"] = uri, ["payload"] = payload ?? new JsonObject() }, cancel);
            if (!waitForReply) return null;
            try { return await ReceiveUntilAsync(_ws, m => (string?)m["id"] == id, cancel); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
        }

        /// <summary>The TV's own idea of its MAC addresses (wired and Wi-Fi).</summary>
        public async Task<List<string>> MacsAsync()
        {
            var info = (await RequestAsync("ssap://com.webos.service.connectionmanager/getinfo"))?["payload"];
            return new[] { info?["wiredInfo"]?["macAddress"], info?["wifiInfo"]?["macAddress"] }
                .Select(n => (string?)n).OfType<string>().Where(m => m != "").ToList();
        }

        public Task CloseAsync() => LgWebOs.CloseAsync(_ws);
        public void Dispose() => _ws.Dispose();
    }

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

    /// <summary>
    /// Sends the magic packet (6 × FF, then the MAC 16 times) for every MAC, from every network adapter that's up, to
    /// that adapter's own subnet broadcast and to 255.255.255.255, on ports 9 and 7, plus straight to the TV's IP.
    /// A plain broadcast from one socket can leave by the wrong adapter on a PC with VPN or virtual adapters.
    /// Returns which networks it went out on, for the log.
    /// </summary>
    static async Task<string> WakeAsync(IEnumerable<string> macs, string tvIp)
    {
        var packets = macs.Select(MagicPacket).OfType<byte[]>().ToList();
        if (packets.Count == 0) return "nothing (no MAC)";

        var used = new List<string>();
        foreach (var (local, broadcast) in LocalNetworks())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                foreach (var packet in packets)
                    foreach (var target in new[] { broadcast, IPAddress.Broadcast })
                        foreach (var port in new[] { 9, 7 })
                            await udp.SendAsync(packet, new IPEndPoint(target, port));
                used.Add($"{local}→{broadcast}");
            }
            catch { }
        }
        // Unicast as well: works while the network still remembers where the TV is.
        if (IPAddress.TryParse(tvIp, out var tv))
            try
            {
                using var udp = new UdpClient();
                foreach (var packet in packets) await udp.SendAsync(packet, new IPEndPoint(tv, 9));
            }
            catch { }
        return used.Count == 0 ? "no network adapter" : string.Join(", ", used);
    }

    static byte[]? MagicPacket(string mac)
    {
        byte[] bytes;
        try { bytes = Convert.FromHexString(mac.Replace(":", "").Replace("-", "")); }
        catch (FormatException) { return null; }
        if (bytes.Length != 6) return null;
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>Each IPv4 address on an adapter that's up (not loopback), with its subnet's broadcast address.</summary>
    static IEnumerable<(IPAddress Local, IPAddress Broadcast)> LocalNetworks()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null) continue;
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return (u.Address, new IPAddress(broadcast));
            }
        }
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

    // What IO Hub asks the TV to allow. Same list as aiowebostv's (Apache 2.0); power, inputs, apps and network info
    // are used.
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
