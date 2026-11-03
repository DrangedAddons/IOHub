using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IOHub.Devices;

/// <summary>
/// Lists every connected display through the Windows CCD API (QueryDisplayConfig), including ones that are
/// plugged in but not part of the desktop, which is the state a TV sits in until you switch to it.
/// </summary>
internal static partial class DisplayDetector
{
    public static List<DisplayDevice> Detect()
    {
        var (paths, modes) = QueryAllPaths();
        var gpuByGdi = GpuNamesByGdiName();
        var seen = new HashSet<(uint, int, uint)>();
        var result = new List<DisplayDevice>();

        // QDC_ALL_PATHS returns every source × target combination. Take active paths first so each target is
        // described by the path it's actually using.
        foreach (var p in paths.OrderByDescending(p => (p.flags & PathActive) != 0))
        {
            var t = p.targetInfo;
            if (t.targetAvailable == 0) continue;
            if (!seen.Add((t.adapterId.LowPart, t.adapterId.HighPart, t.id))) continue;

            bool active = (p.flags & PathActive) != 0;
            var target = GetTargetName(t.adapterId, t.id);
            var gdi = GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
            uint tech = target?.outputTechnology ?? t.outputTechnology;
            bool internalPanel = tech is TechInternal or TechLvds or TechDisplayPortEmbedded or TechUdiEmbedded;

            int x = 0, y = 0, w = 0, h = 0;
            if (active && p.sourceInfo.modeInfoIdx < modes.Length && modes[p.sourceInfo.modeInfoIdx].infoType == ModeTypeSource)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                x = sm.position.x; y = sm.position.y; w = (int)sm.width; h = (int)sm.height;
            }

            var color = GetAdvancedColor(t.adapterId, t.id);
            string name = target?.monitorFriendlyDeviceName is { Length: > 0 } n ? n
                : internalPanel ? "Built-in display" : "Unknown display";

            result.Add(new DisplayDevice
            {
                Id = target?.monitorDevicePath is { Length: > 0 } path ? path : $"{t.adapterId.HighPart:X}{t.adapterId.LowPart:X8}:{t.id}",
                Name = name,
                Connection = ConnectionName(tech),
                Manufacturer = target is { } tn && (tn.flags & EdidIdsValid) != 0 ? ManufacturerName(tn.edidManufactureId) : "",
                Gpu = gdi != null && gpuByGdi.TryGetValue(gdi, out var gpu) ? gpu : "",
                Number = active ? DisplayNumber(gdi) : 0,
                IsActive = active,
                IsPrimary = active && x == 0 && y == 0,
                IsInternal = internalPanel,
                IsVirtual = tech is TechIndirectVirtual or TechMiracast,
                HdrSupported = color is { } c1 && (c1 & 1) != 0,
                HdrEnabled = color is { } c2 && (c2 & 2) != 0,
                X = x, Y = y, Width = w, Height = h,
                RefreshHz = active && t.refreshRate.Denominator != 0 ? (double)t.refreshRate.Numerator / t.refreshRate.Denominator : 0,
                ScalePercent = active ? ScaleFor(x, y, w, h) : 0,
            });
        }

        return result
            .OrderByDescending(d => d.IsActive)
            .ThenByDescending(d => d.IsPrimary)
            .ThenBy(d => d.X)
            .ToList();
    }

    static (Path[] paths, Mode[] modes) QueryAllPaths() => Query(QdcAllPaths);

    static (Path[] paths, Mode[] modes) Query(uint queryFlags)
    {
        while (true)
        {
            int err = GetDisplayConfigBufferSizes(queryFlags, out uint pathCount, out uint modeCount);
            if (err != 0) throw new Win32Exception(err);
            var paths = new Path[pathCount];
            var modes = new Mode[modeCount];
            err = QueryDisplayConfig(queryFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err == ErrorInsufficientBuffer) continue; // a display changed between the two calls
            if (err != 0) throw new Win32Exception(err);
            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }
    }

    static TargetDeviceName? GetTargetName(Luid adapter, uint id)
    {
        var req = new TargetDeviceName
        {
            header = new() { type = InfoGetTargetName, size = (uint)Marshal.SizeOf<TargetDeviceName>(), adapterId = adapter, id = id },
        };
        return DisplayConfigGetDeviceInfo(ref req) == 0 ? req : null;
    }

    static string? GetSourceGdiName(Luid adapter, uint id)
    {
        var req = new SourceDeviceName
        {
            header = new() { type = InfoGetSourceName, size = (uint)Marshal.SizeOf<SourceDeviceName>(), adapterId = adapter, id = id },
        };
        return DisplayConfigGetDeviceInfo(ref req) == 0 ? req.viewGdiDeviceName : null;
    }

    static uint? GetAdvancedColor(Luid adapter, uint id)
    {
        var req = new AdvancedColorInfo
        {
            header = new() { type = InfoGetAdvancedColor, size = (uint)Marshal.SizeOf<AdvancedColorInfo>(), adapterId = adapter, id = id },
        };
        return DisplayConfigGetDeviceInfo(ref req) == 0 ? req.value : null;
    }

    /// <summary>\\.\DISPLAYn → graphics card name. Covers inactive sources too, so unused outputs still get a GPU.</summary>
    static Dictionary<string, string> GpuNamesByGdiName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dd = new DisplayDeviceW { cb = Marshal.SizeOf<DisplayDeviceW>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            map[dd.DeviceName] = dd.DeviceString;
            dd.cb = Marshal.SizeOf<DisplayDeviceW>();
        }
        return map;
    }

    static int DisplayNumber(string? gdi) =>
        gdi != null && int.TryParse(gdi.AsSpan(gdi.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase) + 7), out var n) ? n : 0;

    static int ScaleFor(int x, int y, int w, int h)
    {
        var monitor = MonitorFromPoint(new Point { x = x + w / 2, y = y + h / 2 }, 0);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0) return 0;
        return (int)Math.Round(dpi * 100.0 / 96);
    }

    static string ConnectionName(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        18 => "USB-C",
        15 => "Miracast",
        16 => "Wired (indirect)",
        TechIndirectVirtual => "Virtual",
        TechInternal or TechLvds or TechDisplayPortEmbedded or TechUdiEmbedded => "Internal",
        1 or 2 or 3 or 14 => "Analog",
        _ => "Other",
    };

    // EDID stores the vendor as three 5-bit letters, big-endian; CCD hands back the raw bytes.
    static string ManufacturerName(ushort raw)
    {
        int v = (ushort)((raw >> 8) | (raw << 8));
        var code = new string([(char)('A' - 1 + ((v >> 10) & 31)), (char)('A' - 1 + ((v >> 5) & 31)), (char)('A' - 1 + (v & 31))]);
        return KnownVendors.TryGetValue(code, out var name) ? name : code;
    }

    static readonly Dictionary<string, string> KnownVendors = new()
    {
        ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["AUO"] = "AU Optronics", ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["DEL"] = "Dell", ["GBT"] = "Gigabyte",
        ["GSM"] = "LG", ["HEC"] = "Hisense", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "iiyama", ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display", ["MEI"] = "Panasonic", ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips",
        ["SAM"] = "Samsung", ["SDC"] = "Samsung Display", ["SHP"] = "Sharp", ["SNY"] = "Sony", ["TCL"] = "TCL",
        ["VIZ"] = "Vizio", ["VSC"] = "ViewSonic", ["XMI"] = "Xiaomi",
    };

    // ---- interop ----

    const uint QdcAllPaths = 0x1;
    const uint PathActive = 0x1;
    const int ErrorInsufficientBuffer = 122;
    const uint ModeTypeSource = 1;
    const uint InfoGetSourceName = 1, InfoGetTargetName = 2, InfoGetAdvancedColor = 9;
    const uint EdidIdsValid = 0x4;
    const uint TechLvds = 6, TechDisplayPortEmbedded = 11, TechUdiEmbedded = 13, TechMiracast = 15,
        TechIndirectVirtual = 17, TechInternal = 0x80000000;

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] struct Region { public uint cx; public uint cy; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathTargetInfo
    {
        public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public Rational refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Path { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct VideoSignalInfo
    {
        public ulong pixelRate; public Rational hSyncFreq; public Rational vSyncFreq; public Region activeSize;
        public Region totalSize; public uint videoStandard; public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SourceMode { public uint width; public uint height; public uint pixelFormat; public Point position; }

    [StructLayout(LayoutKind.Explicit)]
    struct Mode
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public Luid adapterId;
        [FieldOffset(16)] public VideoSignalInfo targetMode;
        [FieldOffset(16)] public SourceMode sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInfoHeader { public uint type; public uint size; public Luid adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetDeviceName
    {
        public DeviceInfoHeader header; public uint flags; public uint outputTechnology;
        public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AdvancedColorInfo { public DeviceInfoHeader header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDeviceW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] Path[] pathArray,
        ref uint numModeInfoArrayElements, [Out] Mode[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DisplayDeviceW displayDevice, uint flags);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
