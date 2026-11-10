using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IOHub.Profiles;

namespace IOHub.Integrations;

/// <summary>
/// Records World of Warcraft's own sound (nothing from other apps) to a WAV file, for tuning the fishing bite
/// detector: every time it fires, and every click, key or controller press (to bookmark real bites), a line goes into
/// a markers file beside the WAV with the time into the recording.
/// Uses <see cref="ProcessLoopback"/>, so Windows 11.
///
/// 48 kHz stereo 16-bit: about 11 MB a minute. Stops by itself after <see cref="MaxLength"/>. Its own thread, which
/// owns every COM object it makes.
/// </summary>
public sealed class WowRecorder : IDisposable
{
    public const int Rate = ProcessLoopback.Rate, Channels = ProcessLoopback.Channels, BytesPerFrame = ProcessLoopback.BytesPerFrame;
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
        FileStream? wav = null;
        try
        {
            wav = new FileStream(FilePath!, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            WriteHeader(wav, 0);
            var file = wav;
            var bytes = new byte[Rate * BytesPerFrame / 10];
            long nextQpc = 0;
            var presses = new PressWatcher(Mark);
            ProcessLoopback.Run(pid,
                stop: () => _stop || Elapsed >= MaxLength,
                onPacket: (stereo, frames, _, qpc) =>
                {
                    lock (_gate) { if (_startQpc == 0) _startQpc = qpc; }
                    // WoW playing nothing means no packets: fill the gap with silence so the markers line up.
                    if (nextQpc != 0 && qpc - nextQpc > 200_000)
                        WriteSilence(file, Math.Min((qpc - nextQpc) * Rate / 10_000_000, Rate * 60L), ref bytes);
                    nextQpc = qpc + frames * 10_000_000L / Rate;
                    var raw = MemoryMarshal.AsBytes(stereo);
                    file.Write(raw);
                    Interlocked.Add(ref _frames, frames);
                },
                started: () => Changed?.Invoke(),
                // Every 10 ms at most, so a click's bookmark is within 10 ms of the click.
                poll: presses.Poll);
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

    /// <summary>
    /// Bookmarks every press while recording: mouse buttons, keys and Xbox controller buttons. Clicking the bobber just
    /// after the splash marks where a real bite was. Typing keys (letters, numbers, space…) are written as "a typing
    /// key" so chat never ends up in the file; only the moment matters.
    /// </summary>
    sealed class PressWatcher(Action<string> mark)
    {
        readonly bool[] _keys = new bool[256];
        readonly int[] _pads = new int[Triggers.XInputPad.Slots];
        long _nextPadRead;

        public void Poll()
        {
            for (int vk = 1; vk < 256; vk++)
            {
                // Generic Shift/Ctrl/Alt also show as their left/right keys; skip the generic ones.
                if (vk is 0x10 or 0x11 or 0x12) continue;
                bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && !_keys[vk]) mark($"pressed: {Describe(vk)}");
                _keys[vk] = down;
            }
            // Empty controller slots are slow to ask about, so the pads are read every 20 ms.
            long now = Environment.TickCount64;
            if (now < _nextPadRead) return;
            _nextPadRead = now + 20;
            for (int slot = 0; slot < _pads.Length; slot++)
            {
                if (!Triggers.XInputPad.TryRead(slot, out int buttons)) { _pads[slot] = 0; continue; }
                int pressed = buttons & ~_pads[slot];
                if (pressed != 0) mark($"pressed: controller {Triggers.ControllerButtons.Combo(pressed)}");
                _pads[slot] = buttons;
            }
        }

        static string Describe(int vk) => vk switch
        {
            0x01 => "left mouse",
            0x02 => "right mouse",
            0x04 => "middle mouse",
            0x05 => "mouse button 4",
            0x06 => "mouse button 5",
            _ when Triggers.KeyNames.IsTypingKey(vk) => "a typing key",
            _ => $"key {Triggers.KeyNames.Of(vk)}",
        };

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    }

    public void Dispose() => Stop();
}
