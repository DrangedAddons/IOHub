using System.Runtime.InteropServices;
using System.Text;

namespace IOHub.Devices;

/// <summary>
/// Webcams and capture cards: everything registered as a video camera (KSCATEGORY_VIDEO_CAMERA), which is what
/// Windows' own Camera app and browsers list. Includes disabled ones; skips ones that aren't plugged in.
/// </summary>
internal static class CameraDetector
{
    static Guid VideoCamera = new("E5323777-F976-4F5B-9B55-B94699C46E44");

    public static List<CameraDevice> Detect()
    {
        var result = new List<CameraDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var iface in Interfaces())
        {
            var instanceId = InterfaceString(iface, InstanceIdKey);
            if (instanceId == null || !seen.Add(instanceId)) continue;

            // Not present at all (unplugged): CM_Locate_DevNode only finds it as a phantom.
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) continue;
            bool disabled = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0
                && (status & DnHasProblem) != 0 && problem == ProblemDisabled;

            var name = InterfaceString(iface, InterfaceFriendlyNameKey)
                ?? NodeString(devInst, FriendlyNameKey) ?? NodeString(devInst, DeviceDescKey) ?? "Camera";
            string bus = instanceId.Split('\\')[0].ToUpperInvariant();

            result.Add(new CameraDevice
            {
                Id = instanceId,
                Name = name,
                Connection = bus switch
                {
                    "USB" => "USB",
                    "PCI" => "PCIe",
                    "SWD" or "ROOT" => "Virtual",
                    "BTHENUM" => "Bluetooth",
                    _ => "Built-in",
                },
                IsVirtual = bus is "SWD" or "ROOT",
                IsEnabled = !disabled,
            });
        }
        return result.OrderByDescending(c => c.IsEnabled).ThenBy(c => c.IsVirtual).ThenBy(c => c.Name).ToList();
    }

    static List<string> Interfaces()
    {
        var cls = VideoCamera;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref cls, null, ListAllDevices) != 0 || len <= 1) return [];
            var buffer = new char[len];
            int cr = CM_Get_Device_Interface_ListW(ref cls, null, buffer, len, ListAllDevices);
            if (cr == CrBufferSmall) continue; // a camera arrived between the two calls
            if (cr != 0) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return [];
    }

    static string? InterfaceString(string iface, DevPropKey key)
    {
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(iface, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(iface, ref key, out _, buffer, ref size, 0) != 0) return null;
        return Decode(buffer);
    }

    static string? NodeString(uint devInst, DevPropKey key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) != 0) return null;
        return Decode(buffer);
    }

    static string? Decode(byte[] utf16)
    {
        var s = Encoding.Unicode.GetString(utf16).TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }

    // ---- interop ----

    const uint ListAllDevices = 0x1;
    const int CrBufferSmall = 0x1A;
    const uint DnHasProblem = 0x400;
    const uint ProblemDisabled = 22;

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    static DevPropKey InstanceIdKey = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    static DevPropKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static DevPropKey DeviceDescKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    static DevPropKey InterfaceFriendlyNameKey = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);
}
