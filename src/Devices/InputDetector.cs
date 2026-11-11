using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace IOHub.Devices;

/// <summary>
/// Controllers come from XInput (Xbox-style pads, with battery) plus any other HID gamepad or joystick; PlayStation
/// and Switch pads among those are opened by <see cref="HidPads"/>.
/// Keyboards and mice come from the Raw Input device list, grouped so one receiver with three interfaces
/// shows as one device.
/// </summary>
internal static partial class InputDetector
{
    public static InputSnapshot Detect()
    {
        var raw = EnumerateRaw();

        var pads = raw.Where(r => r.Type == RimTypeHid && r.UsagePage == 1 && r.Usage is 4 or 5 or 8).ToList();
        // XInput devices also appear as HID with "IG_" in the path. Use those for names, not as extra entries.
        var xinputHid = pads.Where(p => p.Path.Contains("IG_", StringComparison.OrdinalIgnoreCase)).ToList();

        var controllers = new List<InputDevice>();
        var slots = XInputSlots();
        foreach (var slot in slots)
        {
            // XInput doesn't say which HID device is in which slot; a name is only safe when there's one of each.
            var hid = slots.Count == 1 && xinputHid.Count >= 1 && xinputHid.Select(h => h.Name).Distinct().Count() == 1
                ? xinputHid[0] : null;
            controllers.Add(new InputDevice
            {
                Kind = InputKind.Controller,
                Name = hid?.Name ?? $"Xbox-compatible {slot.TypeName.ToLowerInvariant()}",
                Vendor = hid?.Vendor ?? "",
                VidPid = hid?.VidPid ?? "",
                Source = $"XInput · Slot {slot.Index + 1}",
                Connection = slot.Wireless ? "Wireless" : "Wired",
                IsWireless = slot.Wireless,
                PadSlot = slot.Index,
                BatteryLevel = slot.Battery,
            });
        }
        // PlayStation and Switch pads IO Hub reads itself: open them now, so they're ready for triggers and rumble.
        var others = pads.Except(xinputHid).ToList();
        HidPads.Sync(others.Select(p => p.Gamepad));
        foreach (var device in Group(others, InputKind.Controller))
            controllers.Add(Direct(device, others.Where(p => p.VidPid == device.VidPid && p.Name == device.Name)));

        return new InputSnapshot(
            controllers,
            Group(raw.Where(r => r.Type == RimTypeKeyboard), InputKind.Keyboard),
            Group(raw.Where(r => r.Type == RimTypeMouse), InputKind.Mouse));
    }

    /// <summary>A pad IO Hub reads over HID gets its model, its own source text and (on Bluetooth) its battery.</summary>
    static InputDevice Direct(InputDevice device, IEnumerable<RawDevice> interfaces)
    {
        var info = interfaces.Select(i => HidPads.About(i.Path)).FirstOrDefault(i => i != null);
        if (info is not { } pad) return device;
        return new InputDevice
        {
            Kind = device.Kind, Name = device.Name, Vendor = device.Vendor, VidPid = device.VidPid,
            Connection = device.Connection, Interfaces = device.Interfaces, IsWireless = device.IsWireless,
            Source = "HID · read by IO Hub",
            Model = pad.Model,
            PadSlot = pad.Slot,
            BatteryLevel = pad.Alive ? pad.Battery : -1,
        };
    }

    public sealed record GamepadPath(string Path, ushort Vid, ushort Pid, bool Bluetooth, bool Xbox = false);

    /// <summary>
    /// HID game controllers with their USB IDs, without opening them; Xbox ones (XInput's) only when asked for. Cheap
    /// enough to call every second.
    /// </summary>
    public static List<GamepadPath> GamepadPaths(bool includeXbox = false)
    {
        var result = new List<GamepadPath>();
        uint count = 0, size = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0) return result;
        var list = new RawInputDeviceList[count];
        uint got = GetRawInputDeviceList(list, ref count, size);
        if (got == uint.MaxValue) return result;
        foreach (var d in list.Take((int)got))
        {
            if (d.dwType != RimTypeHid) continue;
            var info = new RidDeviceInfo { cbSize = (uint)Marshal.SizeOf<RidDeviceInfo>() };
            uint infoSize = info.cbSize;
            if (GetRawInputDeviceInfoW(d.hDevice, RidiDeviceInfo, ref info, ref infoSize) == uint.MaxValue) continue;
            if (!(info.hidUsagePage == 1 && info.hidUsage is 4 or 5 or 8)) continue;
            var path = DeviceName(d.hDevice);
            if (path == null || IsVirtual(path)) continue;
            bool xbox = path.Contains("IG_", StringComparison.OrdinalIgnoreCase);
            if (xbox && !includeXbox) continue;
            bool bluetooth = path.Contains(BluetoothHid, StringComparison.OrdinalIgnoreCase) || path.Contains(BluetoothLeHid, StringComparison.OrdinalIgnoreCase);
            result.Add(new GamepadPath(path, (ushort)info.hidVendorId, (ushort)info.hidProductId, bluetooth, xbox));
        }
        return result;
    }

    /// <summary>
    /// A cheap fingerprint of XInput state (connected slots and battery levels) for change polling. The battery is only
    /// asked for when <paramref name="readBattery"/> is set (once a minute); otherwise the last level read counts.
    /// </summary>
    public static int XInputFingerprint(bool readBattery)
    {
        int fp = 0;
        foreach (var s in XInputSlots(readBattery))
            fp |= (1 | ((s.Battery + 1) << 1) | (s.Wireless ? 1 << 4 : 0)) << (s.Index * 5);
        return fp;
    }

    static List<InputDevice> Group(IEnumerable<RawDevice> devices, InputKind kind) =>
        devices
            .GroupBy(d => (d.VidPid, d.Name))
            .Select(g => new InputDevice
            {
                Kind = kind,
                Name = g.Key.Name,
                Vendor = g.First().Vendor,
                VidPid = g.Key.VidPid,
                Connection = g.First().Connection,
                Source = "HID",
                Interfaces = g.Count(),
                IsWireless = g.First().Connection.StartsWith("Bluetooth"),
            })
            .OrderBy(d => d.VidPid == "" ? 0 : 1) // built-in first
            .ThenBy(d => d.Name)
            .ToList();

    sealed record RawDevice(uint Type, string Path, ushort UsagePage, ushort Usage, string Name, string Vendor, string VidPid, string Connection,
        ushort VendorId = 0, ushort ProductId = 0)
    {
        public GamepadPath Gamepad => new(Path, VendorId, ProductId, Connection == "Bluetooth");
    }

    static List<RawDevice> EnumerateRaw()
    {
        uint count = 0, size = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0) return [];
        var list = new RawInputDeviceList[count];
        uint got = GetRawInputDeviceList(list, ref count, size);
        if (got == uint.MaxValue) return [];

        var result = new List<RawDevice>();
        foreach (var d in list.Take((int)got))
        {
            var path = DeviceName(d.hDevice);
            if (path == null || IsVirtual(path)) continue;

            ushort usagePage = 0, usage = 0, vendorId = 0, productId = 0;
            if (d.dwType == RimTypeHid)
            {
                var info = new RidDeviceInfo { cbSize = (uint)Marshal.SizeOf<RidDeviceInfo>() };
                uint infoSize = info.cbSize;
                if (GetRawInputDeviceInfoW(d.hDevice, RidiDeviceInfo, ref info, ref infoSize) == uint.MaxValue) continue;
                usagePage = info.hidUsagePage;
                usage = info.hidUsage;
                vendorId = (ushort)info.hidVendorId;
                productId = (ushort)info.hidProductId;
                // Only game controllers matter from the generic HID list; the rest are headset buttons, RGB, etc.
                if (!(usagePage == 1 && usage is 4 or 5 or 8)) continue;
            }

            var vid = HexAfter(path, VidRegex());
            var pid = HexAfter(path, PidRegex());
            var strings = HidStrings(path);
            var vendor = strings.Manufacturer is { Length: > 0 } m ? m : KnownVendor(vid);
            var name = strings.Product is { Length: > 0 } p ? p : FallbackName(d.dwType, usage, vid, pid);

            result.Add(new RawDevice(d.dwType, path, usagePage, usage, name, vendor,
                vid != "" && pid != "" ? $"{vid}:{pid}" : "", ConnectionFor(path, vid), vendorId, productId));
        }
        return result;
    }

    static bool IsVirtual(string path) =>
        path.Contains(@"\ROOT#", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("RDP_", StringComparison.OrdinalIgnoreCase);

    const string BluetoothHid = "{00001124-0000-1000-8000-00805f9b34fb}", BluetoothLeHid = "{00001812-0000-1000-8000-00805f9b34fb}";

    static string ConnectionFor(string path, string vid)
    {
        if (path.Contains(BluetoothHid, StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase)) return "Bluetooth LE";
        return vid == "" ? "Built-in" : "USB";
    }

    static string FallbackName(uint type, ushort usage, string vid, string pid)
    {
        string kind = type switch
        {
            RimTypeKeyboard => "Keyboard",
            RimTypeMouse => "Mouse",
            _ => usage == 4 ? "Joystick" : "Gamepad",
        };
        if (vid == "") return type == RimTypeMouse ? "Built-in pointing device" : $"Built-in {kind.ToLowerInvariant()}";
        var vendor = KnownVendor(vid);
        return vendor != "" ? $"{vendor} {kind.ToLowerInvariant()}" : $"{kind} ({vid}:{pid})";
    }

    // USB paths say VID_046D; Bluetooth adds a source prefix (VID&0002046d, VID&02045e). The last four hex digits are the ID.
    static string HexAfter(string path, Regex regex)
    {
        var m = regex.Match(path);
        return m.Success && m.Groups[1].Value.Length >= 4 ? m.Groups[1].Value[^4..].ToUpperInvariant() : "";
    }

    [GeneratedRegex("VID[_&]([0-9A-Fa-f]{4,8})")] private static partial Regex VidRegex();
    [GeneratedRegex("PID[_&]([0-9A-Fa-f]{4,8})")] private static partial Regex PidRegex();

    static readonly ConcurrentDictionary<string, (string? Product, string? Manufacturer)> StringCache = new();

    static (string? Product, string? Manufacturer) HidStrings(string path) => StringCache.GetOrAdd(path, p =>
    {
        // Zero access rights: enough to read descriptor strings, and works on keyboards and mice Windows holds open.
        using var handle = CreateFileW(p, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (null, null);
        var buffer = new char[127];
        string? Read(Func<SafeFileHandle, char[], uint, bool> get)
        {
            Array.Clear(buffer);
            return get(handle, buffer, (uint)buffer.Length * 2) ? new string(buffer).TrimEnd('\0').Trim() : null;
        }
        return (Read(HidD_GetProductString), Read(HidD_GetManufacturerString));
    });

    static string? DeviceName(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(device, RidiDeviceName, null, ref size);
        if (size == 0) return null;
        var buffer = new char[size];
        if (GetRawInputDeviceInfoW(device, RidiDeviceName, buffer, ref size) == uint.MaxValue) return null;
        var name = new string(buffer).TrimEnd('\0');
        return name.StartsWith(@"\??\") ? @"\\?\" + name[4..] : name;
    }

    static string KnownVendor(string vid) => vid switch
    {
        "045E" => "Microsoft", "054C" => "Sony", "057E" => "Nintendo", "046D" => "Logitech", "28DE" => "Valve",
        "2DC8" => "8BitDo", "0E6F" => "PDP", "0F0D" => "HORI", "24C6" => "PowerA", "1532" => "Razer",
        "1038" => "SteelSeries", "1B1C" => "Corsair", "3537" => "GameSir", "0B05" => "ASUS", "3434" => "Keychron",
        "05AC" => "Apple", "17EF" => "Lenovo", "413C" => "Dell", "03F0" => "HP", "0951" => "HyperX",
        "1E7D" => "ROCCAT", "1050" => "Yubico", "0738" => "Mad Catz", "044F" => "Thrustmaster", "06A3" => "Saitek",
        "231D" => "VKB", "3344" => "Virpil", "258A" => "Sinowealth", "373B" => "Glorious", "2516" => "Cooler Master",
        _ => "",
    };

    // ---- XInput ----

    sealed record XInputSlot(int Index, string TypeName, bool Wireless, int Battery);

    // Last battery reading per slot: (wireless by battery, level). Kept so the change poll needn't ask every time.
    static readonly (bool OnBattery, int Level, bool Read)[] LastBattery = new (bool, int, bool)[4];

    /// <summary>
    /// The connected XInput slots. Battery levels are asked for only when <paramref name="readBattery"/> is set: the
    /// rest of what IO Hub asks an Xbox controller comes from the driver's copy, but whether a battery query reaches
    /// the controller itself is up to the driver, and nothing IO Hub does should keep a controller from going to sleep.
    /// </summary>
    static List<XInputSlot> XInputSlots(bool readBattery = true)
    {
        var slots = new List<XInputSlot>();
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetCapabilities(i, 0, out var caps) != 0) { LastBattery[i] = default; continue; }
                // Once when it connects, then only while it's being used (see ControllerIdle.InUse).
                if (!LastBattery[i].Read || (readBattery && ControllerIdle.InUse((int)i)))
                {
                    bool read = XInputGetBatteryInformation(i, 0, out var b) == 0 && b.BatteryType is >= 2 and <= 3;
                    LastBattery[i] = (read, read ? b.BatteryLevel : -1, true);
                }
                var (onBattery, battery, _) = LastBattery[i];
                // Pads on the Xbox Wireless Adapter don't always set the wireless flag; running on battery says it too.
                slots.Add(new XInputSlot((int)i, SubTypeName(caps.SubType), (caps.Flags & 0x0002) != 0 || onBattery, battery));
            }
            catch (DllNotFoundException) { break; }
        }
        return slots;
    }

    static string SubTypeName(byte subType) => subType switch
    {
        2 => "Wheel",
        3 => "Arcade stick",
        4 => "Flight stick",
        5 => "Dance pad",
        6 or 7 => "Guitar",
        8 => "Drum kit",
        0x13 => "Arcade pad",
        _ => "Controller",
    };

    // ---- interop ----

    const uint RimTypeMouse = 0, RimTypeKeyboard = 1, RimTypeHid = 2;
    const uint RidiDeviceName = 0x20000007, RidiDeviceInfo = 0x2000000b;
    const uint FileShareReadWrite = 3, OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDeviceList { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RidDeviceInfo
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint hidVendorId;
        [FieldOffset(12)] public uint hidProductId;
        [FieldOffset(16)] public uint hidVersion;
        [FieldOffset(20)] public ushort hidUsagePage;
        [FieldOffset(22)] public ushort hidUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputCapabilities
    {
        public byte Type; public byte SubType; public ushort Flags;
        public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY;
        public ushort LeftMotor; public ushort RightMotor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputBatteryInformation { public byte BatteryType; public byte BatteryLevel; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, ref RidDeviceInfo data, ref uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, [Out] char[]? data, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    static extern bool HidD_GetProductString(SafeFileHandle device, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    static extern bool HidD_GetManufacturerString(SafeFileHandle device, [Out] char[] buffer, uint bufferLength);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities capabilities);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out XInputBatteryInformation information);
}
