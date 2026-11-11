using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IOHub.Profiles;
using IOHub.Triggers;

namespace IOHub.Devices;

/// <summary>
/// Turns wireless controllers off: the Xbox ones through XInput's own power-off call (the one Game Bar uses), with a
/// Bluetooth disconnect as the fallback; Switch pads with their sleep command; PlayStation pads by dropping their
/// Bluetooth link, which is how a PlayStation turns them off too. A wired controller can't be turned off, and nothing
/// can turn a wireless one on: its radio is off until its button is pressed.
/// Every attempt is written to switch.log.
/// </summary>
public static partial class ControllerPower
{
    /// <summary>Whether the controller in this slot runs on battery (a wired one is powered by its cable).</summary>
    public static bool IsWireless(int slot) => slot >= Pads.HidBase ? HidPads.IsBluetooth(slot) : XInputWireless(slot);

    /// <summary>
    /// The same test the Devices page uses: XInput's wireless flag, or running on battery. Xbox pads on Bluetooth set
    /// the flag but report no battery type; pads on the Xbox Wireless Adapter can be the other way round.
    /// </summary>
    static bool XInputWireless(int slot)
    {
        try
        {
            if (XInputGetCapabilities((uint)slot, 0, out var caps) != 0) return false;
            if ((caps.Flags & 0x0002) != 0) return true; // XINPUT_CAPS_WIRELESS
            return XInputGetBatteryInformation((uint)slot, 0, out var b) == 0 && b.BatteryType is 2 or 3;
        }
        catch { return false; }
    }

    public const string WiredMessage = "It's connected by cable, and a wired controller stays on while it's plugged in.";

    /// <summary>Turns one controller off. Returns an error message, or null once it's gone.</summary>
    public static string? TurnOff(int slot)
    {
        if (!IsWireless(slot)) return WiredMessage;
        return slot >= Pads.HidBase ? HidPads.TurnOff(slot) : TurnOffXbox(slot);
    }

    /// <summary>Turns off every wireless controller that's on. Returns an error message, or null.</summary>
    public static string? TurnOffAll()
    {
        var wireless = Pads.Connected().Where(IsWireless).ToList();
        if (wireless.Count == 0) return "No wireless controller is on.";
        var errors = wireless.Select(TurnOff).Where(e => e != null).ToList();
        return errors.Count == 0 ? null : string.Join("\n", errors.Distinct());
    }

    static string? TurnOffXbox(int slot)
    {
        uint result;
        try { result = XInputPowerOffController((uint)slot); }
        catch (EntryPointNotFoundException) { result = uint.MaxValue; }
        SwitchLog.Write($"Controller in XInput slot {slot + 1}: power off answered {result}");
        if (result == 0 && GoneSoon(() => XInputGetCapabilities((uint)slot, 0, out _) != 0)) return null;

        // Xbox pads on Bluetooth: drop the link, if it's clear which pad is in this slot.
        var bluetooth = InputDetector.GamepadPaths(includeXbox: true).Where(p => p.Xbox && p.Bluetooth).ToList();
        int wirelessSlots = Enumerable.Range(0, XInputPad.Slots).Count(XInputWireless);
        SwitchLog.Write($"Xbox pads on Bluetooth: {bluetooth.Count} ({string.Join(", ", bluetooth.Select(b => b.Path))}); wireless XInput slots: {wirelessSlots}");
        if (bluetooth.Count == 1 && wirelessSlots == 1)
        {
            var error = Bluetooth.Disconnect(bluetooth[0].Path);
            if (error == null && GoneSoon(() => XInputGetCapabilities((uint)slot, 0, out _) != 0)) return null;
            return error ?? "Its Bluetooth link was dropped, but it still answers.";
        }
        if (bluetooth.Count > 1) return "Windows didn't turn it off, and with several Bluetooth controllers it can't tell which one this is.";
        return result == 0
            ? "Windows said it turned it off, but it still answers. switch.log has the details."
            : $"Windows didn't turn it off (XInput answered {result}). switch.log has the details.";
    }

    /// <summary>Waits up to three seconds for a controller to drop off.</summary>
    internal static bool GoneSoon(Func<bool> gone)
    {
        for (int i = 0; i < 30; i++)
        {
            if (gone()) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Drops a Bluetooth controller's link, which makes it turn itself off (Xbox, PlayStation). The controller's
    /// Bluetooth address comes from its HID device's parents in the device tree.
    /// </summary>
    internal static partial class Bluetooth
    {
        public static string? Disconnect(string hidPath)
        {
            var address = AddressOf(hidPath);
            if (address == null)
            {
                SwitchLog.Write($"Bluetooth disconnect: no address found for {hidPath}");
                return "Couldn't find its Bluetooth address.";
            }
            var p = new FindRadioParams { Size = (uint)Marshal.SizeOf<FindRadioParams>() };
            var find = BluetoothFindFirstRadio(ref p, out var radio);
            if (find == IntPtr.Zero) return "No Bluetooth radio found.";
            int lastError = 0;
            bool done = false;
            try
            {
                do
                {
                    ulong a = address.Value;
                    if (DeviceIoControl(radio, IoctlBthDisconnectDevice, ref a, 8, IntPtr.Zero, 0, out _, IntPtr.Zero)) done = true;
                    else lastError = Marshal.GetLastWin32Error();
                    CloseHandle(radio);
                } while (!done && BluetoothFindNextRadio(find, out radio));
            }
            finally { BluetoothFindRadioClose(find); }
            SwitchLog.Write($"Bluetooth disconnect of {address.Value:X12}: " + (done ? "done" : $"failed, error {lastError}"));
            return done ? null : lastError == 5
                ? "Windows needs administrator rights to drop this controller's Bluetooth link."
                : $"Windows didn't drop its Bluetooth link (error {lastError}).";
        }

        /// <summary>
        /// \\?\HID#{00001124-…}_VID&amp;…#9&amp;…&amp;0&amp;0000#{…} is device HID\{00001124-…}_VID&amp;…\9&amp;…; its parent
        /// (BTHENUM\…\8&amp;…&amp;0&amp;98B6E9123456_C00000000, or BTHLEDEVICE\…_98B6E9123456\…) holds the address.
        /// </summary>
        static ulong? AddressOf(string hidPath)
        {
            var id = hidPath.StartsWith(@"\\?\") ? hidPath[4..] : hidPath;
            int guid = id.LastIndexOf("#{", StringComparison.Ordinal);
            if (guid > 0) id = id[..guid];
            id = id.Replace('#', '\\');
            if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) return null;
            for (int up = 0; up < 3; up++)
            {
                if (CM_Get_Parent(out uint parent, node, 0) != 0) return null;
                node = parent;
                var buffer = new char[512];
                if (CM_Get_Device_IDW(node, buffer, buffer.Length, 0) != 0) return null;
                var m = AddressRegex().Match(new string(buffer).TrimEnd('\0'));
                if (m.Success) return Convert.ToUInt64(m.Groups[1].Value, 16);
            }
            return null;
        }

        [GeneratedRegex(@"(?:&0&|_)([0-9A-Fa-f]{12})(?=_C|\\|$)")] private static partial Regex AddressRegex();

        const uint IoctlBthDisconnectDevice = 0x41000C;

        [StructLayout(LayoutKind.Sequential)] struct FindRadioParams { public uint Size; }

        [DllImport("BluetoothApis.dll", SetLastError = true)] static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr radio);
        [DllImport("BluetoothApis.dll", SetLastError = true)] static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);
        [DllImport("BluetoothApis.dll")] static extern bool BluetoothFindRadioClose(IntPtr find);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr device, uint code, ref ulong input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("CfgMgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);
    }

    // ---- XInput ----

    [StructLayout(LayoutKind.Sequential)] struct Battery { public byte BatteryType, BatteryLevel; }
    [StructLayout(LayoutKind.Sequential)]
    struct Capabilities
    {
        public byte Type, SubType; public ushort Flags, Buttons; public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; public ushort LeftMotor, RightMotor;
    }

    /// <summary>Undocumented, ordinal 103 of every Windows 10/11 xinput1_4: what Game Bar's "turn off controller" uses.</summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#103")] static extern uint XInputPowerOffController(uint slot);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetCapabilities(uint slot, uint flags, out Capabilities caps);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint slot, byte type, out Battery battery);
}

/// <summary>
/// Turns a wireless controller off after it's been left alone for a while (AppSettings.ControllerIdleOffMinutes), the
/// way a console does. Left alone means no button held or changed and the sticks near the middle, so a drifting stick
/// doesn't keep it awake. Reads the controllers about 7 times a second on its own thread, which costs next to nothing.
/// </summary>
public sealed class ControllerIdle : IDisposable
{
    readonly AppSettings _settings;
    readonly Timer _timer;
    readonly Dictionary<int, (int Buttons, long LastUsed, bool Wireless, bool GaveUp)> _pads = [];
    List<int> _slots = [];
    long _nextScan;
    int _busy;
    const int StickRest = 8000; // about a quarter of the way out

    public ControllerIdle(AppSettings settings)
    {
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, 2000, 150);
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            int minutes = _settings.ControllerIdleOffMinutes;
            if (minutes <= 0) { _pads.Clear(); return; }
            long now = Environment.TickCount64;
            if (now >= _nextScan)
            {
                _slots = Pads.Connected();
                _nextScan = now + 5000;
                foreach (var gone in _pads.Keys.Except(_slots).ToList()) _pads.Remove(gone);
                foreach (var slot in _slots)
                    if (!_pads.ContainsKey(slot)) _pads[slot] = (0, now, ControllerPower.IsWireless(slot), false);
                    else _pads[slot] = _pads[slot] with { Wireless = ControllerPower.IsWireless(slot) };
            }
            foreach (var slot in _slots)
            {
                if (!_pads.TryGetValue(slot, out var p) || !Pads.TryReadPad(slot, out var pad)) continue;
                bool used = pad.Buttons != 0 || pad.Buttons != p.Buttons
                    || Math.Abs((int)pad.LeftX) > StickRest || Math.Abs((int)pad.LeftY) > StickRest
                    || Math.Abs((int)pad.RightX) > StickRest || Math.Abs((int)pad.RightY) > StickRest;
                p = p with { Buttons = pad.Buttons, LastUsed = used ? now : p.LastUsed };
                if (!used && p.Wireless && !p.GaveUp && now - p.LastUsed >= minutes * 60_000L)
                {
                    SwitchLog.Write($"Controller in slot {slot + 1} idle for {minutes} min: turning it off");
                    var error = ControllerPower.TurnOff(slot);
                    if (error != null)
                    {
                        SwitchLog.Write($"Couldn't turn it off: {error}");
                        p = p with { GaveUp = true }; // don't keep trying; it starts over when it reconnects
                    }
                    else _nextScan = 0;
                }
                _pads[slot] = p;
            }
        }
        catch { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
