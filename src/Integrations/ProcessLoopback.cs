using System.Runtime.InteropServices;

namespace IOHub.Integrations;

/// <summary>
/// Captures one process's sound (and its child processes'), nothing from other apps: Windows' per-app loopback
/// (Windows 11, or Windows 10 build 20348 and later). 48 kHz stereo 16-bit, which Windows converts WoW's output to.
/// Used by <see cref="WowFishing"/>'s splash matcher, on its own thread.
/// </summary>
static class ProcessLoopback
{
    public const int Rate = 48000, Channels = 2, BytesPerFrame = Channels * 2;

    /// <summary>One packet of sound, interleaved stereo samples; <paramref name="qpc"/> is when it played, in 100 ns units.</summary>
    public delegate void Packet(ReadOnlySpan<short> stereo, int frames, bool silent, long qpc);

    /// <summary>
    /// Captures <paramref name="pid"/> on the calling thread (MTA) until <paramref name="stop"/> says so, calling
    /// <paramref name="poll"/> every 10 ms at most and <paramref name="onPacket"/> for each packet. Throws if Windows
    /// won't capture. <paramref name="started"/> runs once capture is going.
    /// </summary>
    public static void Run(uint pid, Func<bool> stop, Packet onPacket, Action? started = null, Action? poll = null)
    {
        IntPtr pParams = IntPtr.Zero, pVariant = IntPtr.Zero, pFormat = IntPtr.Zero;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: process loopback of the process and its children, in a VT_BLOB PROPVARIANT.
            pParams = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(pParams, 0, 1);            // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(pParams, 4, (int)pid);
            Marshal.WriteInt32(pParams, 8, 0);            // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            pVariant = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, 65);          // VT_BLOB
            Marshal.WriteInt32(pVariant, 8, 12);          // cbSize
            Marshal.WriteIntPtr(pVariant, 16, pParams);   // pBlobData

            var done = new Completion();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pVariant, done, out var op);
            if (!done.Done.Wait(5000)) throw new Exception("Windows didn't start capturing WoW's sound.");
            op.GetActivateResult(out int hr, out var iface);
            if (hr != 0) throw new Exception($"Windows refused to capture WoW's sound (0x{hr:X8}). It needs Windows 11.");
            client = (IAudioClient)iface;

            // Process loopback has no mix format of its own: ask for 48 kHz stereo 16-bit and let Windows convert.
            pFormat = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(pFormat, 0, 1);                       // WAVE_FORMAT_PCM
            Marshal.WriteInt16(pFormat, 2, Channels);
            Marshal.WriteInt32(pFormat, 4, Rate);
            Marshal.WriteInt32(pFormat, 8, Rate * BytesPerFrame);
            Marshal.WriteInt16(pFormat, 12, BytesPerFrame);
            Marshal.WriteInt16(pFormat, 14, 16);
            Marshal.WriteInt16(pFormat, 16, 0);
            Check(client.Initialize(0, Loopback | EventCallback | AutoConvertPcm, 2_000_000, 0, pFormat, IntPtr.Zero));
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var svc));
            capture = (IAudioCaptureClient)svc;
            Check(client.Start());
            started?.Invoke();

            var buffer = new short[Rate * Channels / 10];
            while (!stop())
            {
                ready.WaitOne(10);
                poll?.Invoke();
                while (capture.GetNextPacketSize(out uint size) == 0 && size > 0)
                {
                    if (capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc) != 0) break;
                    try
                    {
                        int samples = (int)frames * Channels;
                        if (buffer.Length < samples) buffer = new short[samples];
                        bool silent = (flags & BufferSilent) != 0;
                        if (silent) Array.Clear(buffer, 0, samples);
                        else Marshal.Copy(data, buffer, 0, samples);
                        onPacket(buffer.AsSpan(0, samples), (int)frames, silent, (long)qpc);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
            client.Stop();
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (pFormat != IntPtr.Zero) Marshal.FreeHGlobal(pFormat);
            if (pVariant != IntPtr.Zero) Marshal.FreeHGlobal(pVariant);
            if (pParams != IntPtr.Zero) Marshal.FreeHGlobal(pParams);
        }
    }

    static void Check(int hr)
    {
        if (hr != 0) throw new Exception($"Couldn't set up capturing WoW's sound (0x{hr:X8}).");
    }

    // ---------------- interop ----------------

    const uint Loopback = 0x0002_0000, EventCallback = 0x0004_0000, AutoConvertPcm = 0x8000_0000;
    const uint BufferSilent = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>Windows calls it on one of its own threads; IAgileObject says that's fine.</summary>
    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
