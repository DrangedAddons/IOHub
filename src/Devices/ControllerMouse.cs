using System.Diagnostics;
using System.Runtime.InteropServices;
using IOHub.Triggers;

namespace IOHub.Devices;

/// <summary>
/// Mouse mode: an Xbox controller drives the mouse. Left stick moves the pointer (holding LT slows it for precision),
/// right stick scrolls, A/B/X click left/right/middle, LB/RB are back/forward, the D-pad is the arrow keys, Menu is
/// Enter, View is Esc and Y opens the on-screen keyboard.
///
/// Only runs while it's on: a worker thread reads the controllers about 120 times a second while they're moving and
/// 30 times when idle. While a fullscreen app (a game, Big Picture) is in front it pauses, because the game still gets
/// the controller too and would see both.
///
/// Menu, View and Y act when let go, and only if nothing else was pressed meanwhile, so a View + Menu trigger doesn't
/// also press Esc and Enter.
/// </summary>
public sealed class ControllerMouse : IDisposable
{
    readonly AppSettings _settings;
    readonly object _lock = new();
    volatile bool _on;
    volatile int _generation; // each start gets a new number; a loop keeps going only while it's still current

    /// <summary>On, off or paused changed. Raised on a worker thread.</summary>
    public event Action? Changed;

    public ControllerMouse(AppSettings settings) => _settings = settings;

    /// <summary>The setting on the Devices page: IO Hub is allowed to drive the mouse. Off means turning on does nothing.</summary>
    public bool Allowed => _settings.ControllerMouse;
    public bool IsOn => _on;
    /// <summary>On, but holding off because a fullscreen app is in front.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Turns mouse mode on or off. Returns an error message, or null.</summary>
    public string? Set(bool on)
    {
        if (on && !Allowed) return "Mouse mode is switched off on the Devices page (Input → Mouse mode).";
        lock (_lock)
        {
            if (on == _on) return null;
            _on = on;
            int generation = ++_generation;
            if (on) new Thread(() => Run(generation)) { IsBackground = true, Name = "Controller mouse", Priority = ThreadPriority.AboveNormal }.Start();
        }
        Changed?.Invoke();
        return null;
    }

    public void Dispose() => Set(false);

    // ---------------- the loop ----------------

    const float StickDeadzone = 0.22f, ScrollDeadzone = 0.25f;
    const int RepeatDelayMs = 400, RepeatEveryMs = 45;

    void Run(int generation)
    {
        var clock = Stopwatch.StartNew();
        using var timer = new PreciseTimer();
        var slots = XInputPad.Connected();
        long lastScan = 0, lastFullscreenCheck = -1000, lastActive = 0;
        double lastT = 0, carryX = 0, carryY = 0, carryWheel = 0, carryHWheel = 0;
        int previous = -1;     // buttons last read; -1 = not read yet
        int ignore = 0;        // buttons that were already held when mouse mode started (the trigger's combo)
        int tapCandidates = 0; // Menu/View/Y pressed with nothing else since
        var heldKeys = new Dictionary<int, long>(); // D-pad bit → when to repeat next
        bool paused = false;

        try
        {
            while (generation == _generation)
            {
                double t = clock.Elapsed.TotalSeconds, dt = Math.Min(t - lastT, 0.05);
                lastT = t;
                long ms = clock.ElapsedMilliseconds;

                if (ms - lastScan > 2000) { slots = XInputPad.Connected(); lastScan = ms; }
                if (_settings.MousePauseInFullscreen && ms - lastFullscreenCheck >= 500)
                {
                    lastFullscreenCheck = ms;
                    bool nowPaused = FullscreenAppInFront();
                    if (nowPaused != paused)
                    {
                        paused = nowPaused;
                        IsPaused = paused;
                        if (paused) { ReleaseAll(previous < 0 ? 0 : previous, heldKeys); previous = -1; }
                        Changed?.Invoke();
                    }
                }
                else if (!_settings.MousePauseInFullscreen && paused) { paused = false; IsPaused = false; Changed?.Invoke(); }

                // Every controller counts: buttons from any of them, the sticks from whichever is pushed furthest.
                int buttons = 0;
                XInputPad.Pad stick = default;
                foreach (var slot in slots)
                {
                    if (!XInputPad.TryReadPad(slot, out var pad)) continue;
                    buttons |= pad.Buttons;
                    if (pad.LeftMagnitude + pad.RightMagnitude > stick.LeftMagnitude + stick.RightMagnitude) stick = pad;
                }

                if (paused)
                {
                    timer.Wait(30);
                    continue;
                }
                if (previous < 0) { previous = buttons; ignore = buttons; }
                ignore &= buttons; // a button stops being ignored once it's let go

                // Pointer.
                var (mx, my) = Shape(stick.LeftX, stick.LeftY, StickDeadzone);
                if (mx != 0 || my != 0)
                {
                    double speed = _settings.MouseSpeed * ((buttons & ControllerButtons.LT) != 0 ? 0.3 : 1.0);
                    carryX += mx * speed * dt;
                    carryY -= my * speed * dt; // stick up is positive, screen up is negative
                    int dx = (int)carryX, dy = (int)carryY;
                    carryX -= dx; carryY -= dy;
                    if ((dx != 0 || dy != 0) && GetCursorPos(out var p)) SetCursorPos(p.X + dx, p.Y + dy);
                }
                else { carryX = carryY = 0; }

                // Scrolling: up to about 20 notches a second.
                var (sx, sy) = Shape(stick.RightX, stick.RightY, ScrollDeadzone);
                carryWheel += sy * 20 * 120 * dt;
                carryHWheel += sx * 20 * 120 * dt;
                int wheel = (int)carryWheel, hwheel = (int)carryHWheel;
                carryWheel -= wheel; carryHWheel -= hwheel;
                if (sy == 0) carryWheel = 0;
                if (sx == 0) carryHWheel = 0;
                if (wheel != 0) Mouse(MouseWheel, wheel);
                if (hwheel != 0) Mouse(MouseHWheel, hwheel);

                // Buttons.
                int live = buttons & ~ignore;
                int pressed = live & ~previous, released = previous & ~buttons & ~ignore;
                const int TapButtons = ControllerButtons.Menu | ControllerButtons.View | ControllerButtons.Y;
                if (pressed != 0)
                {
                    // Anything pressed alongside a tap button spoils that tap.
                    tapCandidates = ControllerButtons.Count(live) == 1 ? pressed & TapButtons : 0;
                }
                foreach (var (bit, down, up) in MouseButtons)
                {
                    if ((pressed & bit) != 0) Mouse(down, 0, XButtonOf(bit));
                    if ((released & bit) != 0) Mouse(up, 0, XButtonOf(bit));
                }
                foreach (var (bit, vk) in Arrows)
                {
                    if ((pressed & bit) != 0) { Key(vk, true); Key(vk, false); heldKeys[bit] = ms + RepeatDelayMs; }
                    else if ((live & bit) != 0 && heldKeys.TryGetValue(bit, out var next) && ms >= next) { Key(vk, true); Key(vk, false); heldKeys[bit] = ms + RepeatEveryMs; }
                    if ((buttons & bit) == 0) heldKeys.Remove(bit);
                }
                int taps = released & tapCandidates;
                if (taps != 0)
                {
                    if ((taps & ControllerButtons.Menu) != 0) { Key(0x0D, true); Key(0x0D, false); }        // Enter
                    if ((taps & ControllerButtons.View) != 0) { Key(0x1B, true); Key(0x1B, false); }        // Esc
                    if ((taps & ControllerButtons.Y) != 0) Chord(0x5B, 0x11, 0x4F);                          // Win + Ctrl + O: on-screen keyboard
                    tapCandidates &= ~taps;
                }
                previous = buttons;

                bool active = live != 0 || mx != 0 || my != 0 || sx != 0 || sy != 0;
                if (active) lastActive = ms;
                // Quick while something's happening, easy on the CPU when the controller's sitting there.
                timer.Wait(ms - lastActive < 1500 ? 8 : 33);
            }
        }
        finally
        {
            if (previous > 0) ReleaseAll(previous & ~ignore, heldKeys);
            IsPaused = false;
            Changed?.Invoke();
        }
    }

    static readonly (int Bit, uint Down, uint Up)[] MouseButtons =
    [
        (ControllerButtons.A, LeftDown, LeftUp),
        (ControllerButtons.B, RightDown, RightUp),
        (ControllerButtons.X, MiddleDown, MiddleUp),
        (ControllerButtons.LB, XDown, XUp),
        (ControllerButtons.RB, XDown, XUp),
    ];

    static readonly (int Bit, ushort Vk)[] Arrows =
    [
        (ControllerButtons.DPadUp, 0x26), (ControllerButtons.DPadDown, 0x28), (ControllerButtons.DPadLeft, 0x25), (ControllerButtons.DPadRight, 0x27),
    ];

    /// <summary>Lets go of any mouse button still held down, so pausing or stopping never leaves one stuck.</summary>
    static void ReleaseAll(int held, Dictionary<int, long> keys)
    {
        foreach (var (bit, _, up) in MouseButtons)
            if ((held & bit) != 0) Mouse(up, 0, XButtonOf(bit));
        keys.Clear();
    }

    /// <summary>Back and forward are mouse "X buttons" 1 and 2; Windows needs the number on both the press and the release.</summary>
    static int XButtonOf(int bit) => bit == ControllerButtons.LB ? XButton1 : bit == ControllerButtons.RB ? XButton2 : 0;

    /// <summary>Stick to -1..1 past the deadzone, on a curve so small pushes are fine and full pushes are fast.</summary>
    static (double X, double Y) Shape(short rawX, short rawY, float deadzone)
    {
        double x = rawX / 32768.0, y = rawY / 32768.0;
        double mag = Math.Sqrt(x * x + y * y);
        if (mag < deadzone) return (0, 0);
        double scaled = Math.Min(1, (mag - deadzone) / (1 - deadzone));
        double curved = scaled * scaled;
        return (x / mag * curved, y / mag * curved);
    }

    /// <summary>A window covering its whole screen (a game, Big Picture, fullscreen video), other than the desktop itself.</summary>
    static bool FullscreenAppInFront()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var m = info.Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ---------------- input ----------------

    const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020, MiddleUp = 0x0040;
    const uint XDown = 0x0080, XUp = 0x0100, MouseWheel = 0x0800, MouseHWheel = 0x1000;
    const int XButton1 = 1, XButton2 = 2;

    static void Mouse(uint flags, int wheel, int xButton = 0)
    {
        var input = new Input { Type = 0 };
        input.U.Mouse.Flags = flags;
        input.U.Mouse.Data = flags is MouseWheel or MouseHWheel ? wheel : xButton;
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    static void Key(ushort vk, bool down)
    {
        var input = new Input { Type = 1 };
        input.U.Keyboard.Vk = vk;
        input.U.Keyboard.Flags = down ? 0u : 2u;
        // Arrow keys are "extended" keys; without the flag some apps read them as the number pad.
        if (vk is >= 0x25 and <= 0x28) input.U.Keyboard.Flags |= 1u;
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    static void Chord(params ushort[] keys)
    {
        foreach (var k in keys) Key(k, true);
        foreach (var k in keys.Reverse()) Key(k, false);
    }

    /// <summary>
    /// Waits a few milliseconds accurately. Thread.Sleep rounds up to Windows' 15.6 ms tick, which makes the pointer
    /// judder; a high-resolution waitable timer doesn't, and unlike timeBeginPeriod it doesn't change the whole PC's timer.
    /// </summary>
    sealed class PreciseTimer : IDisposable
    {
        readonly IntPtr _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);

        public void Wait(int ms)
        {
            if (_handle == IntPtr.Zero) { Thread.Sleep(ms); return; }
            long due = -ms * 10_000L; // relative, in 100 ns units
            if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) { Thread.Sleep(ms); return; }
            WaitForSingleObject(_handle, 1000);
        }

        public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }
    }

    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy; public int Data; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint Type; public InputUnion U; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
