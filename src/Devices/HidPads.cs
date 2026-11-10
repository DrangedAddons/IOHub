using System.Runtime.InteropServices;
using IOHub.Triggers;
using Microsoft.Win32.SafeHandles;

namespace IOHub.Devices;

/// <summary>
/// PlayStation and Switch controllers, read straight from their HID reports so they work without DS4Windows,
/// BetterJoy or Steam. Each pad gets a background thread that opens it shared (Steam and games keep working), reads
/// its reports and sends its rumble. Pads show up in <see cref="Pads"/> next to the Xbox ones, in slots from 4 up.
///
/// The thread reads as often as someone needs it: every 6 ms for mouse mode, 25 ms for controller triggers, and
/// 150 ms otherwise (just enough to notice it's still there and keep the battery level).
/// </summary>
public static class HidPads
{
    static readonly object Lock = new();
    static readonly Dictionary<string, HidPad> Open = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> SlotOfPath = new(StringComparer.OrdinalIgnoreCase); // stable for the session
    static int _nextSlot = Pads.HidBase;
    static long _lastScan = -10_000;
    static int _listeners, _fastListeners;

    internal static int ReadEveryMs => Volatile.Read(ref _fastListeners) > 0 ? 6 : Volatile.Read(ref _listeners) > 0 ? 25 : 150;

    /// <summary>While the returned lease is held the pads are read often enough for triggers (or mouse mode, if fast).</summary>
    public static IDisposable Listen(bool fast = false)
    {
        Interlocked.Increment(ref _listeners);
        if (fast) Interlocked.Increment(ref _fastListeners);
        Scan(force: true);
        return new Lease(fast);
    }

    sealed class Lease(bool fast) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            Interlocked.Decrement(ref _listeners);
            if (fast) Interlocked.Decrement(ref _fastListeners);
        }
    }

    /// <summary>Opens pads that were plugged in and lets go of ones that went. At most once a second unless forced.</summary>
    public static void Scan(bool force = false)
    {
        long now = Environment.TickCount64;
        lock (Lock)
        {
            if (!force && now - _lastScan < 1000) return;
            _lastScan = now;
        }
        List<InputDetector.GamepadPath> found;
        try { found = InputDetector.GamepadPaths(); }
        catch { return; }
        Sync(found);
    }

    /// <summary>Brings the open pads in line with the HID game controllers Windows has right now.</summary>
    internal static void Sync(IEnumerable<InputDetector.GamepadPath> found)
    {
        var wanted = new Dictionary<string, (PadModel Model, bool Bluetooth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in found)
            if (PadReports.ModelOf(g.Vid, g.Pid) is { } model) wanted[g.Path] = (model, g.Bluetooth);

        lock (Lock)
        {
            _lastScan = Environment.TickCount64;
            foreach (var (path, pad) in Open.ToList())
            {
                if (wanted.ContainsKey(path) && !pad.ShouldRetry) continue;
                pad.Stop();
                Open.Remove(path);
            }
            foreach (var (path, (model, bluetooth)) in wanted)
            {
                if (Open.ContainsKey(path)) continue;
                if (!SlotOfPath.TryGetValue(path, out int slot)) SlotOfPath[path] = slot = _nextSlot++;
                int player = Open.Values.Count(p => PadReports.IsSwitch(p.Model));
                var pad = new HidPad(path, model, bluetooth, slot, player);
                Open[path] = pad;
                pad.Start();
            }
        }
    }

    /// <summary>The slots of pads that are sending. A left and right Joy-Con count as one controller, in the left's slot.</summary>
    public static List<int> Slots()
    {
        Scan();
        lock (Lock)
        {
            var alive = Open.Values.Where(p => p.Alive).ToList();
            var hidden = Pairs(alive).Select(pair => pair.Right.Slot).ToHashSet();
            return alive.Where(p => !hidden.Contains(p.Slot)).Select(p => p.Slot).OrderBy(s => s).ToList();
        }
    }

    static IEnumerable<(HidPad Left, HidPad Right)> Pairs(List<HidPad> alive) =>
        alive.Where(p => p.Model == PadModel.JoyConLeft).OrderBy(p => p.Slot)
            .Zip(alive.Where(p => p.Model == PadModel.JoyConRight).OrderBy(p => p.Slot));

    public static bool TryReadPad(int slot, out XInputPad.Pad pad)
    {
        pad = default;
        HidPad? one, partner = null;
        lock (Lock)
        {
            one = Open.Values.FirstOrDefault(p => p.Slot == slot);
            if (one == null || !one.Alive) return false;
            if (one.Model == PadModel.JoyConLeft)
                partner = Pairs(Open.Values.Where(p => p.Alive).ToList()).FirstOrDefault(x => x.Left == one).Right;
        }
        pad = one.State;
        if (partner != null)
        {
            var r = partner.State;
            pad = new XInputPad.Pad(pad.Buttons | r.Buttons, pad.LeftX, pad.LeftY, r.RightX, r.RightY);
        }
        return true;
    }

    public static bool TryRead(int slot, out int buttons)
    {
        bool ok = TryReadPad(slot, out var pad);
        buttons = pad.Buttons;
        return ok;
    }

    /// <summary>Rumbles every open pad. Strengths are XInput's 0-65535: left the big motor, right the small one.</summary>
    public static void SetRumble(ushort left, ushort right)
    {
        List<HidPad> pads;
        lock (Lock) pads = Open.Values.Where(p => p.Alive).ToList();
        foreach (var p in pads) p.SetRumble((byte)(left >> 8), (byte)(right >> 8));
    }

    public readonly record struct Info(PadModel Model, bool Bluetooth, bool Alive, int Battery);

    /// <summary>What's known about the pad at a HID path, if IO Hub reads it.</summary>
    public static Info? About(string path)
    {
        lock (Lock)
            return Open.TryGetValue(path, out var p) ? new Info(p.Model, p.Bluetooth, p.Alive, p.Battery) : null;
    }

    /// <summary>Changes when a pad starts or stops sending or its battery level moves, for the Devices page's change poll.</summary>
    public static int Fingerprint()
    {
        lock (Lock)
        {
            int fp = 17;
            foreach (var p in Open.Values.OrderBy(p => p.Slot))
                fp = fp * 31 + (p.Alive ? p.Slot * 8 + p.Battery + 2 : -p.Slot);
            return fp;
        }
    }
}

/// <summary>One PlayStation or Switch controller and the thread that reads it.</summary>
internal sealed class HidPad(string path, PadModel model, bool bluetooth, int slot, int player)
{
    public readonly string Path = path;
    public readonly PadModel Model = model;
    public readonly bool Bluetooth = bluetooth;
    public readonly int Slot = slot;

    readonly object _stateLock = new();
    XInputPad.Pad _state;
    volatile int _battery = -1;
    long _lastReport = long.MinValue / 2;
    long _ended = long.MaxValue;
    volatile bool _stop;
    volatile HidDevice? _device;
    int _counter;                       // Switch packet number / DualSense output sequence
    double _switchAmplitude;            // Switch rumble must be re-sent while it lasts
    long _nextSwitchRumble;
    bool _switchReady;
    long _nextSwitchInit;
    readonly PadReports.StickCalibration _left = new(), _right = new();

    /// <summary>Has sent a report in the last couple of seconds. A pad that's paired but off isn't alive.</summary>
    public bool Alive => _device != null && Environment.TickCount64 - Interlocked.Read(ref _lastReport) < 2500;
    public int Battery => _battery;
    public XInputPad.Pad State { get { lock (_stateLock) return _state; } }

    /// <summary>Its thread ended (couldn't open it, or it went away) a while ago: worth trying again if it's still listed.</summary>
    public bool ShouldRetry => Environment.TickCount64 - Interlocked.Read(ref _ended) > 5000;

    public void Start() => new Thread(Run) { IsBackground = true, Name = $"Controller {PadReports.NameOf(Model)}" }.Start();
    public void Stop() => _stop = true;

    void Run()
    {
        var dev = HidDevice.Open(Path);
        try
        {
            if (dev == null) return;
            _device = dev;
            var buffer = new byte[Math.Max(dev.InputLength, 64)];
            using var timer = new PreciseTimer();
            while (!_stop)
            {
                if (PadReports.IsSwitch(Model) && !_switchReady && Environment.TickCount64 >= _nextSwitchInit)
                {
                    _switchReady = InitSwitch(dev, buffer);
                    _nextSwitchInit = Environment.TickCount64 + 3000;
                }
                int n = dev.Read(buffer, 1000);
                if (n < 0) break; // unplugged or turned off
                // Take everything that's waiting, so the state is the newest and no command reply is missed.
                for (int drained = 0; n > 0 && drained < 16; drained++)
                {
                    Handle(buffer.AsSpan(0, n));
                    n = dev.Read(buffer, 0);
                }
                if (n < 0) break;
                if (_switchAmplitude > 0 && Environment.TickCount64 >= _nextSwitchRumble) SendSwitchRumble(dev);
                int wait = HidPads.ReadEveryMs;
                if (_switchAmplitude > 0) wait = Math.Min(wait, 40);
                timer.Wait(wait);
            }
        }
        catch { }
        finally
        {
            _device = null;
            dev?.Dispose();
            Interlocked.Exchange(ref _ended, Environment.TickCount64);
        }
    }

    void Handle(ReadOnlySpan<byte> report)
    {
        bool ok = Model switch
        {
            PadModel.DualShock4 => PadReports.TryParseDualShock4(report, Bluetooth, out var r) && Take(r),
            PadModel.DualSense => PadReports.TryParseDualSense(report, Bluetooth, out var r) && Take(r),
            _ => PadReports.TryParseSwitch(report, Model, _left, _right, out var r) && Take(r),
        };
        if (ok) Interlocked.Exchange(ref _lastReport, Environment.TickCount64);
    }

    bool Take(PadReports.Reading r)
    {
        lock (_stateLock) _state = r.Pad;
        // Only Bluetooth runs on battery; a wired pad is charging, like a wired Xbox pad shows none.
        if (Bluetooth && r.Battery >= 0) _battery = r.Battery;
        return true;
    }

    // ---------------- rumble ----------------

    public void SetRumble(byte strong, byte weak)
    {
        var dev = _device;
        if (dev == null) return;
        switch (Model)
        {
            case PadModel.DualShock4:
                dev.Write(PadReports.DualShock4Rumble(Bluetooth, strong, weak));
                break;
            case PadModel.DualSense:
                dev.Write(PadReports.DualSenseRumble(Bluetooth, Interlocked.Increment(ref _counter), strong, weak));
                break;
            default:
                // Switch HD rumble has one strength per side; the stronger motor's is used for both.
                _switchAmplitude = Math.Max(strong, weak) / 255.0;
                SendSwitchRumble(dev);
                break;
        }
    }

    void SendSwitchRumble(HidDevice dev)
    {
        dev.Write(PadReports.SwitchRumble(Interlocked.Increment(ref _counter), _switchAmplitude));
        _nextSwitchRumble = Environment.TickCount64 + 40;
    }

    // ---------------- Switch start-up ----------------

    /// <summary>
    /// A Switch pad sends nothing useful until it's told to. Over USB it first needs the handshake that keeps it in
    /// USB mode; then it's put in full report mode (0x30, 60-120 reports a second), its rumble is turned on, and its
    /// player light is set so it stops flashing. Steam does the same, so doing it again is harmless.
    /// </summary>
    bool InitSwitch(HidDevice dev, byte[] buffer)
    {
        if (!Bluetooth && Model == PadModel.SwitchPro)
        {
            if (!Usb(dev, buffer, 0x02)) return false; // handshake
            Usb(dev, buffer, 0x03);                    // 3 Mbit
            Usb(dev, buffer, 0x02);                    // handshake again at the new speed
            Usb(dev, buffer, 0x04, waitForReply: false); // USB only, no Bluetooth timeout
        }
        bool ok = Command(dev, buffer, 0x03, 0x30);    // full report mode
        Command(dev, buffer, 0x48, 0x01);              // rumble on
        Command(dev, buffer, 0x30, (byte)(1 << Math.Min(player, 3))); // player light
        return ok;
    }

    bool Usb(HidDevice dev, byte[] buffer, byte command, bool waitForReply = true)
    {
        if (!dev.Write([0x80, command])) return false;
        return !waitForReply || WaitFor(dev, buffer, r => r.Length > 1 && r[0] == 0x81 && r[1] == command);
    }

    bool Command(HidDevice dev, byte[] buffer, byte command, params byte[] args)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!dev.Write(PadReports.SwitchCommand(Interlocked.Increment(ref _counter), command, args))) return false;
            if (WaitFor(dev, buffer, r => PadReports.SwitchReplyTo(r) == command)) return true;
        }
        return false;
    }

    delegate bool ReportTest(ReadOnlySpan<byte> report);

    bool WaitFor(HidDevice dev, byte[] buffer, ReportTest test)
    {
        long until = Environment.TickCount64 + 300;
        while (!_stop)
        {
            int left = (int)(until - Environment.TickCount64);
            if (left <= 0) return false;
            int n = dev.Read(buffer, left);
            if (n < 0) return false;
            if (n == 0) continue;
            Handle(buffer.AsSpan(0, n));
            if (test(buffer.AsSpan(0, n))) return true;
        }
        return false;
    }
}

/// <summary>A HID device opened for overlapped reads and writes, so a read can time out and a stop isn't stuck.</summary>
internal sealed class HidDevice : IDisposable
{
    readonly SafeFileHandle _handle;
    readonly IntPtr _readBuffer, _readOverlapped, _readEvent;
    readonly IntPtr _writeBuffer, _writeOverlapped, _writeEvent;
    readonly object _writeLock = new();
    bool _disposed;

    public int InputLength { get; }
    public int OutputLength { get; }

    HidDevice(SafeFileHandle handle, HidpCaps caps)
    {
        _handle = handle;
        InputLength = caps.InputReportByteLength;
        OutputLength = caps.OutputReportByteLength;
        _readBuffer = Marshal.AllocHGlobal(Math.Max(InputLength, 1));
        _writeBuffer = Marshal.AllocHGlobal(Math.Max(OutputLength, 1));
        _readOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        _writeOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        _readEvent = CreateEventW(IntPtr.Zero, true, false, null);
        _writeEvent = CreateEventW(IntPtr.Zero, true, false, null);
    }

    public static HidDevice? Open(string path)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; } // held exclusively by another app, or gone
        if (!HidD_GetPreparsedData(handle, out var data)) { handle.Dispose(); return null; }
        try
        {
            if (HidP_GetCaps(data, out var caps) != HidpStatusSuccess || caps.InputReportByteLength == 0) { handle.Dispose(); return null; }
            HidD_SetNumInputBuffers(handle, 8);
            return new HidDevice(handle, caps);
        }
        finally { HidD_FreePreparsedData(data); }
    }

    /// <summary>One input report into <paramref name="into"/>: its length, 0 if none came in time, -1 if the device is gone.</summary>
    public int Read(byte[] into, int timeoutMs)
    {
        if (_disposed) return -1;
        Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _readEvent }, _readOverlapped, false);
        if (!ReadFile(_handle, _readBuffer, (uint)InputLength, IntPtr.Zero, _readOverlapped))
        {
            if (Marshal.GetLastWin32Error() != ErrorIoPending) return -1;
            if (WaitForSingleObject(_readEvent, (uint)timeoutMs) != WaitObject0)
            {
                CancelIoEx(_handle, _readOverlapped);
                // Wait for the cancel to land (or the read, if it finished first) before the buffer is reused.
                if (!GetOverlappedResult(_handle, _readOverlapped, out uint late, true) || late == 0) return 0;
                return Copy(into, late);
            }
        }
        if (!GetOverlappedResult(_handle, _readOverlapped, out uint n, false))
            return Marshal.GetLastWin32Error() == ErrorOperationAborted ? 0 : -1;
        return Copy(into, n);
    }

    int Copy(byte[] into, uint n)
    {
        int count = (int)Math.Min(n, (uint)into.Length);
        Marshal.Copy(_readBuffer, into, 0, count);
        return count;
    }

    /// <summary>
    /// Sends an output report. Windows wants exactly the device's output length, so a short report is padded with
    /// zeros and a long one loses its trailing (zero) bytes, as hidapi does.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> report)
    {
        if (OutputLength == 0) return false;
        var bytes = new byte[OutputLength];
        report[..Math.Min(report.Length, OutputLength)].CopyTo(bytes);
        lock (_writeLock)
        {
            if (_disposed) return false;
            Marshal.Copy(bytes, 0, _writeBuffer, bytes.Length);
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _writeEvent }, _writeOverlapped, false);
            if (!WriteFile(_handle, _writeBuffer, (uint)OutputLength, IntPtr.Zero, _writeOverlapped))
            {
                if (Marshal.GetLastWin32Error() != ErrorIoPending) return false;
                if (WaitForSingleObject(_writeEvent, 250) != WaitObject0)
                {
                    CancelIoEx(_handle, _writeOverlapped);
                    GetOverlappedResult(_handle, _writeOverlapped, out _, true);
                    return false;
                }
            }
            return GetOverlappedResult(_handle, _writeOverlapped, out _, false);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelIoEx(_handle, IntPtr.Zero);
            _handle.Dispose();
            CloseHandle(_readEvent);
            CloseHandle(_writeEvent);
            Marshal.FreeHGlobal(_readBuffer);
            Marshal.FreeHGlobal(_writeBuffer);
            Marshal.FreeHGlobal(_readOverlapped);
            Marshal.FreeHGlobal(_writeOverlapped);
        }
    }

    // ---- interop ----

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareReadWrite = 3, OpenExisting = 3;
    const uint FileFlagOverlapped = 0x40000000;
    const int ErrorIoPending = 997, ErrorOperationAborted = 995;
    const uint WaitObject0 = 0;
    const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct HidpCaps
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint toRead, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint toWrite, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr security, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);
    [DllImport("hid.dll")] static extern bool HidD_SetNumInputBuffers(SafeFileHandle device, uint count);
}
