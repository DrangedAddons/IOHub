using System.Runtime.InteropServices;

namespace IOHub.Devices;

/// <summary>
/// Lists audio endpoints through Windows Core Audio and raises <see cref="Changed"/> when one is added, removed,
/// unplugged or made default. Create and use it on the UI thread; the level meters it hands out are polled there.
/// </summary>
public sealed class AudioService : IDisposable
{
    readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    readonly Notifier _notifier;

    /// <summary>Raised on a Core Audio worker thread.</summary>
    public event Action? Changed;

    public AudioService()
    {
        _notifier = new Notifier(() => Changed?.Invoke());
        _enumerator.RegisterEndpointNotificationCallback(_notifier);
    }

    public List<AudioEndpoint> Detect()
    {
        var list = new List<AudioEndpoint>();
        foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
        {
            int dataFlow = flow == AudioFlow.Output ? ERender : ECapture;
            string? defaultId = DefaultId(dataFlow, EConsole);
            string? commsId = DefaultId(dataFlow, ECommunications);

            if (_enumerator.EnumAudioEndpoints(dataFlow, StateActive | StateDisabled | StateUnplugged, out var collection) != 0)
                continue;
            try
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    var endpoint = Describe(device, flow, defaultId, commsId);
                    if (endpoint != null) list.Add(endpoint);
                }
            }
            finally { Marshal.ReleaseComObject(collection); }
        }

        return list
            .OrderBy(e => e.Flow)
            .ThenByDescending(e => e.IsActive)
            .ThenByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name)
            .ToList();
    }

    static AudioEndpoint? Describe(IMMDevice device, AudioFlow flow, string? defaultId, string? commsId)
    {
        device.GetId(out var id);
        device.GetState(out uint state);
        string name = "", adapter = "";
        uint formFactor = 10;
        if (device.OpenPropertyStore(0, out var props) == 0)
        {
            name = ReadString(props, DeviceDesc) ?? ReadString(props, FriendlyName) ?? "Audio device";
            adapter = ReadString(props, InterfaceFriendlyName) ?? "";
            formFactor = ReadUInt(props, FormFactor) ?? 10;
            Marshal.ReleaseComObject(props);
        }

        bool active = state == StateActive;
        IAudioMeterInformation? meter = null;
        if (active)
        {
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) == 0)
                meter = obj as IAudioMeterInformation;
        }

        return new AudioEndpoint
        {
            Id = id,
            Name = name,
            Adapter = adapter,
            Flow = flow,
            IsActive = active,
            State = state switch { StateActive => "Active", StateDisabled => "Disabled", StateUnplugged => "Unplugged", _ => "Unavailable" },
            IsDefault = id == defaultId,
            IsCommsDefault = id == commsId,
            IsDisplayAudio = formFactor == FormDigitalDisplay,
            Glyph = flow == AudioFlow.Input ? "" : formFactor switch
            {
                3 => "",                  // headphones
                5 => "",                  // headset
                FormDigitalDisplay => "", // HDMI / DisplayPort
                _ => "",
            },
            ReadPeak = meter == null ? null : () => meter.GetPeakValue(out var p) == 0 ? p : 0f,
            Release = () =>
            {
                if (meter != null) Marshal.ReleaseComObject(meter);
                Marshal.ReleaseComObject(device);
            },
        };
    }

    /// <summary>Whether the device is muted, or null if it can't be read (unplugged, disabled).</summary>
    public bool? IsMuted(string id)
    {
        var volume = Volume(id);
        if (volume == null) return null;
        try { return volume.GetMute(out bool muted) == 0 ? muted : null; }
        finally { Marshal.ReleaseComObject(volume); }
    }

    /// <summary>Mutes or unmutes the device. Returns an error message, or null.</summary>
    public string? SetMute(string id, bool mute)
    {
        var volume = Volume(id);
        if (volume == null) return "That device isn't available.";
        try
        {
            var context = Guid.Empty;
            int hr = volume.SetMute(mute, ref context);
            return hr == 0 ? null : $"Windows refused (0x{hr:X8}).";
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    IAudioEndpointVolume? Volume(string id)
    {
        if (_enumerator.GetDevice(id, out var device) != 0) return null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) == 0 ? obj as IAudioEndpointVolume : null;
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    string? DefaultId(int dataFlow, int role)
    {
        // E_NOTFOUND when there's no device of that kind at all.
        if (_enumerator.GetDefaultAudioEndpoint(dataFlow, role, out var device) != 0) return null;
        device.GetId(out var id);
        Marshal.ReleaseComObject(device);
        return id;
    }

    static string? ReadString(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == VtLpwstr ? Marshal.PtrToStringUni(pv.pointer) : null; }
        finally { PropVariantClear(ref pv); }
    }

    static uint? ReadUInt(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == VtUi4 ? pv.uintValue : null; }
        finally { PropVariantClear(ref pv); }
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_notifier);
        Marshal.ReleaseComObject(_enumerator);
    }

    sealed class Notifier(Action changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) { changed(); return 0; }
        public int OnDeviceAdded(string deviceId) { changed(); return 0; }
        public int OnDeviceRemoved(string deviceId) { changed(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId) { changed(); return 0; }
        // Fires constantly on some drivers and nothing we show depends on it.
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    // ---- interop ----

    const int ERender = 0, ECapture = 1;
    const int EConsole = 0, ECommunications = 2;
    const uint StateActive = 1, StateDisabled = 2, StateUnplugged = 8;
    const uint ClsctxAll = 23;
    const ushort VtLpwstr = 31, VtUi4 = 19;
    const uint FormDigitalDisplay = 9;

    static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    static readonly PropertyKey InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    // 24 bytes on x64; only the fields we read are named.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public uint uintValue;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    // Only SetMute and GetMute are called; the methods before them hold their places in the vtable.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
