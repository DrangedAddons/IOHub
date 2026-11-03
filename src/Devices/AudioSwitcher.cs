using System.Runtime.InteropServices;

namespace IOHub.Devices;

/// <summary>
/// Sets the Windows default playback or recording device. Uses IPolicyConfig: undocumented, but it's what the
/// Sound control panel itself calls, and every audio switcher relies on it.
/// </summary>
internal static class AudioSwitcher
{
    /// <summary>Makes <paramref name="endpointId"/> the default for all three roles (console, multimedia, communications).</summary>
    public static void SetDefault(string endpointId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            for (int role = 0; role < 3; role++)
            {
                int hr = config.SetDefaultEndpoint(endpointId, role);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally { Marshal.ReleaseComObject(config); }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient;

    // Only SetDefaultEndpoint is called; the methods before it hold its place in the vtable.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr period, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
