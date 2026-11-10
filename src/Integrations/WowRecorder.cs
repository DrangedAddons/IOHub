using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IOHub.Profiles;

namespace IOHub.Integrations;

/// <summary>
/// Records World of Warcraft's own sound (nothing from other apps) to a WAV file, for tuning the fishing bite
/// detector: every time it fires, a line goes into a markers file beside the WAV with the time into the recording.
/// Uses Windows' per-app loopback capture (Windows 11, or Windows 10 build 20348 and later).
///
/// 48 kHz stereo 16-bit: about 11 MB a minute. Stops by itself after <see cref="MaxLength"/>. Its own thread, which
/// owns every COM object it makes.
/// </summary>
public sealed class WowRecorder : IDisposable
{
    public const int Rate = 48000, Channels = 2, BytesPerFrame = Channels * 2;
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(15);
    public static string Folder => Path.Combine(AppInfo.DataDir, "wow-recordings");

    readonly object _gate = new();
    Thread? _thread;
    volatile bool _stop;
    StreamWriter? _markers;
    long _startQpc;   // 100 ns units, the clock GetBuffer's positions use; 0 until the first packet
    long _frames;

    /// <summary>Started, stopped, or failed. Raised on the recording thread.</summary>
    public event Action? Changed;

    public bool IsRecording => _thread != null;
    public string? FilePath { get; private set; }
    public string? Error { get; private set; }
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _frames) / (double)Rate);

    /// <summary>Starts recording the WoW process <paramref name="pid"/> (and anything it starts).</summary>
    public void Start(uint pid)
    {
        lock (_gate)
        {
            if (_thread != null) return;
            Error = null;
            _stop = false;
            Interlocked.Exchange(ref _frames, 0);
            _startQpc = 0;
            Directory.CreateDirectory(Folder);
            FilePath = Path.Combine(Folder, $"wow-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _markers = new StreamWriter(Path.ChangeExtension(FilePath, ".txt")) { AutoFlush = true };
            _markers.WriteLine($"# WoW sound recorded {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Seconds into the recording, then what happened.");
            _thread = new Thread(() => Record(pid)) { IsBackground = true, Name = "WoW recorder", Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        SwitchLog.Write($"WoW recording started: {FilePath}");
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { t = _thread; _stop = true; }
        t?.Join(2000);
    }

    /// <summary>Notes something at this moment in the recording ("splash -18 dB"). Any thread.</summary>
    public void Mark(string what)
    {
        lock (_gate)
        {
            if (_markers == null || _startQpc == 0) return;
            double seconds = (NowQpc() - _startQpc) / 1e7;
            _markers.WriteLine($"{seconds:0.000}\t{DateTime.Now:HH:mm:ss.fff}\t{what}");
        }
    }

    static long NowQpc() => (long)(Stopwatch.GetTimestamp() * (1e7 / Stopwatch.Frequency));

    void Record(uint pid)
    {
        IntPtr pParams = IntPtr.Zero, pVariant = IntPtr.Zero, pFormat = IntPtr.Zero;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        FileStream? wav = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: process loopback of the WoW process and its children, in a VT_BLOB PROPVARIANT.
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
            if (!done.Done.Wait(5000)) throw new Exception("Windows didn't start the recording.");
            op.GetActivateResult(out int hr, out var iface);
            if (hr != 0) throw new Exception($"Windows refused to record WoW (0x{hr:X8}). It needs Windows 11.");
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
            Check(client.Initialize(0, Loopback | EventCallback | AutoConvertPcm, 2_000_000, 0, pFormat, IntPtr.Zero), "set up");
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "set up");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var svc), "set up");
            capture = (IAudioCaptureClient)svc;

            wav = new FileStream(FilePath!, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            WriteHeader(wav, 0);
            Check(client.Start(), "start");
            Changed?.Invoke();

            var buffer = new byte[Rate * BytesPerFrame / 10];
            long nextQpc = 0;
            while (!_stop && Elapsed < MaxLength)
            {
                ready.WaitOne(100);
                while (capture.GetNextPacketSize(out uint size) == 0 && size > 0)
                {
                    if (capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc) != 0) break;
                    try
                    {
                        lock (_gate) { if (_startQpc == 0) _startQpc = (long)qpc; }
                        // WoW playing nothing means no packets: fill the gap with silence so the markers line up.
                        if (nextQpc != 0 && (long)qpc - nextQpc > 200_000)
                            WriteSilence(wav, Math.Min(((long)qpc - nextQpc) * Rate / 10_000_000, Rate * 60L), ref buffer);
                        nextQpc = (long)qpc + frames * 10_000_000L / Rate;

                        int bytes = (int)frames * BytesPerFrame;
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        if ((flags & BufferSilent) != 0) Array.Clear(buffer, 0, bytes);
                        else Marshal.Copy(data, buffer, 0, bytes);
                        wav.Write(buffer, 0, bytes);
                        Interlocked.Add(ref _frames, frames);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SwitchLog.Write($"WoW recording failed: {ex.Message}");
        }
        finally
        {
            if (wav != null)
            {
                long dataBytes = Interlocked.Read(ref _frames) * BytesPerFrame;
                wav.Position = 0;
                WriteHeader(wav, dataBytes);
                wav.Dispose();
            }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (pFormat != IntPtr.Zero) Marshal.FreeHGlobal(pFormat);
            if (pVariant != IntPtr.Zero) Marshal.FreeHGlobal(pVariant);
            if (pParams != IntPtr.Zero) Marshal.FreeHGlobal(pParams);
            lock (_gate)
            {
                _markers?.Dispose();
                _markers = null;
                _thread = null;
            }
            SwitchLog.Write($"WoW recording stopped after {Elapsed:mm\\:ss}" + (Error != null ? $": {Error}" : ""));
            Changed?.Invoke();
        }
    }

    void WriteSilence(FileStream wav, long frames, ref byte[] buffer)
    {
        while (frames > 0)
        {
            int chunk = (int)Math.Min(frames, buffer.Length / BytesPerFrame);
            Array.Clear(buffer, 0, chunk * BytesPerFrame);
            wav.Write(buffer, 0, chunk * BytesPerFrame);
            Interlocked.Add(ref _frames, chunk);
            frames -= chunk;
        }
    }

    static void WriteHeader(Stream s, long dataBytes)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8); w.Write((uint)Math.Min(36 + dataBytes, uint.MaxValue)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)Channels); w.Write(Rate);
        w.Write(Rate * BytesPerFrame); w.Write((short)BytesPerFrame); w.Write((short)16);
        w.Write("data"u8); w.Write((uint)Math.Min(dataBytes, uint.MaxValue));
    }

    static void Check(int hr, string what)
    {
        if (hr != 0) throw new Exception($"Couldn't {what} the recording (0x{hr:X8}).");
    }

    public void Dispose() => Stop();

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
