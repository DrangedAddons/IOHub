using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IOHub.Triggers;

/// <summary>
/// Xbox controller buttons as flags: XInput's own button bits, plus two made-up bits for the analog triggers
/// (LT and RT count as pressed past halfway). Saved in <see cref="Trigger.Buttons"/>, so the values must not change.
/// </summary>
public static class ControllerButtons
{
    public const int DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const int Menu = 0x0010, View = 0x0020, LeftStick = 0x0040, RightStick = 0x0080;
    public const int LB = 0x0100, RB = 0x0200, Guide = 0x0400;
    public const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public const int LT = 0x10000, RT = 0x20000;

    /// <summary>In the order combinations are written: shoulders first, face buttons last.</summary>
    static readonly int[] Order = [Guide, View, Menu, LB, RB, LT, RT, LeftStick, RightStick, DPadUp, DPadDown, DPadLeft, DPadRight, A, B, X, Y];

    /// <summary>The controller profile picker's answers, in profile order: profile 1 is A, 2 is B, and so on.</summary>
    public static readonly int[] PickerButtons = [A, B, X, Y, DPadUp, DPadRight, DPadDown, DPadLeft];

    public static string Name(int button) => button switch
    {
        A => "A", B => "B", X => "X", Y => "Y",
        LB => "LB", RB => "RB", LT => "LT", RT => "RT",
        View => "View", Menu => "Menu", Guide => "Xbox",
        LeftStick => "Left stick", RightStick => "Right stick",
        DPadUp => "D-pad up", DPadDown => "D-pad down", DPadLeft => "D-pad left", DPadRight => "D-pad right",
        _ => $"Button {button:X}",
    };

    /// <summary>One or two characters, for the picker's badges: D-pad directions are arrows.</summary>
    public static string ShortName(int button) => button switch
    {
        DPadUp => "↑", DPadDown => "↓", DPadLeft => "←", DPadRight => "→",
        _ => Name(button),
    };

    public static IEnumerable<int> Each(int buttons) => Order.Where(b => (buttons & b) != 0);

    public static int Count(int buttons) => BitOperations.PopCount((uint)buttons);

    public static string Combo(int buttons) => string.Join(" + ", Each(buttons).Select(Name));

    public static string Gesture(GestureKind g, int buttons, int holdMs) => g switch
    {
        GestureKind.Press => $"Press {Combo(buttons)}",
        GestureKind.DoubleTap => $"Double-tap {Combo(buttons)}",
        GestureKind.TripleTap => $"Triple-tap {Combo(buttons)}",
        _ => $"Hold {Combo(buttons)} {holdMs / 1000.0:0.#} s",
    };
}

/// <summary>Reads Xbox controllers (anything XInput sees, up to four).</summary>
public static class XInputPad
{
    public const int Slots = 4;
    static bool _noGuide;

    /// <summary>The buttons held on one controller, as <see cref="ControllerButtons"/> flags. False if nothing's in that slot.</summary>
    public static bool TryRead(int slot, out int buttons)
    {
        buttons = 0;
        XInputState state;
        uint result;
        // The undocumented ordinal 100 is XInputGetState plus the Xbox (Guide) button. Every Windows 10/11 xinput1_4 has it.
        if (!_noGuide)
        {
            try { result = XInputGetStateEx((uint)slot, out state); }
            catch (EntryPointNotFoundException) { _noGuide = true; result = XInputGetState((uint)slot, out state); }
        }
        else result = XInputGetState((uint)slot, out state);
        if (result != 0) return false;
        buttons = state.Buttons;
        if (state.LeftTrigger > 127) buttons |= ControllerButtons.LT;
        if (state.RightTrigger > 127) buttons |= ControllerButtons.RT;
        return true;
    }

    /// <summary>Which slots have a controller. Slow-ish for empty slots (XInput probes them), so don't call it often.</summary>
    public static List<int> Connected()
    {
        var list = new List<int>();
        for (int i = 0; i < Slots; i++) if (TryRead(i, out _)) list.Add(i);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint XInputGetStateEx(uint slot, out XInputState state);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint slot, out XInputState state);
}

/// <summary>
/// Turns controller button states into press, tap and hold gestures of button combinations. Pure logic, so a test
/// can drive it. Each controller gets its own <see cref="KeyGestureDetector"/>, fed one made-up key per combination.
/// A combination only starts when exactly its buttons are held: View + Menu doesn't fire while you're also holding
/// RT in a game. Extra buttons pressed during a hold or between taps cancel it.
/// </summary>
public sealed class ControllerGestures
{
    // Made-up key codes, well clear of anything KeyNames treats as a modifier.
    const int ComboKeyBase = 0x1000, OtherKey = 0x0FFF;

    public sealed record Entry(int Buttons, GestureKind Gesture, int HoldMs, object Tag);

    readonly List<int> _combos = [];
    List<KeyGestureDetector.Entry> _detectorEntries = [];
    readonly Dictionary<int, Pad> _pads = [];

    sealed class Pad
    {
        public readonly KeyGestureDetector Detector = new() { TapMaxMs = 400, TapGapMs = 400 };
        public int Buttons;
        public readonly HashSet<int> Down = []; // combinations held (index into _combos)
    }

    public event Action<object>? Fired;

    public bool HasEntries => _combos.Count > 0;

    public void SetEntries(IEnumerable<Entry> entries)
    {
        _combos.Clear();
        _detectorEntries = [];
        foreach (var e in entries)
        {
            int i = _combos.IndexOf(e.Buttons);
            if (i < 0) { i = _combos.Count; _combos.Add(e.Buttons); }
            // A plain press is a hotkey: it fires on the way down.
            var kind = e.Gesture == GestureKind.Press ? TriggerKind.Hotkey : TriggerKind.KeyGesture;
            _detectorEntries.Add(new(kind, ComboKeyBase + i, KeyMods.None, e.Gesture, e.HoldMs, e.Tag));
        }
        foreach (var pad in _pads.Values) Configure(pad);
    }

    void Configure(Pad pad)
    {
        pad.Detector.SetEntries(_detectorEntries);
        pad.Detector.Reset();
        pad.Down.Clear();
    }

    /// <summary>Feeds one controller's buttons. Returns the buttons that just went down.</summary>
    public int OnButtons(int slot, int buttons, long now)
    {
        if (!_pads.TryGetValue(slot, out var pad))
        {
            pad = new Pad();
            pad.Detector.Fired += tag => Fired?.Invoke(tag);
            Configure(pad);
            _pads[slot] = pad;
        }
        int pressed = buttons & ~pad.Buttons;
        if (buttons == pad.Buttons) return 0;
        pad.Buttons = buttons;

        // Releases first, so a combination let go and another pressed in the same read both count.
        foreach (var i in pad.Down.ToList())
        {
            if ((buttons & _combos[i]) == _combos[i]) continue;
            pad.Down.Remove(i);
            pad.Detector.OnKey(ComboKeyBase + i, false, now);
        }
        if (pressed == 0) return 0;

        // Something extra went down while a combination was held: not a clean tap or hold of it any more.
        if (pad.Down.Any(i => (pressed & ~_combos[i]) != 0))
        {
            pad.Detector.OnKey(OtherKey, true, now);
            pad.Detector.OnKey(OtherKey, false, now);
        }
        for (int i = 0; i < _combos.Count; i++)
        {
            if (pad.Down.Contains(i) || buttons != _combos[i]) continue;
            pad.Down.Add(i);
            pad.Detector.OnKey(ComboKeyBase + i, true, now);
        }
        return pressed;
    }

    /// <summary>A controller went away: let go of everything it was holding.</summary>
    public void Disconnected(int slot, long now)
    {
        if (_pads.ContainsKey(slot)) OnButtons(slot, 0, now);
        _pads.Remove(slot);
    }

    /// <summary>Call regularly. Fires holds and settled taps.</summary>
    public void Tick(long now)
    {
        foreach (var pad in _pads.Values) pad.Detector.Tick(now);
    }

    public void Reset()
    {
        foreach (var pad in _pads.Values) { pad.Detector.Reset(); pad.Down.Clear(); pad.Buttons = 0; }
    }
}

/// <summary>
/// Reads the connected controllers about 25 times a second while a controller trigger needs it. Empty slots are
/// only checked every couple of seconds, on a worker thread: XInput is slow to answer for a slot with nothing in it.
/// Raises <see cref="Read"/> on the UI thread.
/// </summary>
public sealed class ControllerWatcher : IDisposable
{
    readonly DispatcherTimer _poll;
    readonly DispatcherTimer _scan;
    List<int> _slots = [];
    bool _scanning;

    /// <summary>(slot, buttons) for each connected controller on every read; (slot, -1) when one disconnects.</summary>
    public event Action<int, int>? Read;
    /// <summary>After every read, for timing (holds, settled taps).</summary>
    public event Action? Polled;

    public int ConnectedCount => _slots.Count;
    public bool IsRunning => _poll.IsEnabled;

    public ControllerWatcher(Dispatcher ui)
    {
        _poll = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _poll.Tick += (_, _) => Poll();
        _scan = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2) };
        _scan.Tick += async (_, _) => await Scan();
    }

    public void Start()
    {
        if (_poll.IsEnabled) return;
        _poll.Start();
        _scan.Start();
        _ = Scan();
    }

    public void Stop()
    {
        _poll.Stop();
        _scan.Stop();
        foreach (var slot in _slots) Read?.Invoke(slot, -1);
        _slots = [];
    }

    void Poll()
    {
        foreach (var slot in _slots.ToList())
        {
            if (XInputPad.TryRead(slot, out var buttons)) Read?.Invoke(slot, buttons);
            else { _slots.Remove(slot); Read?.Invoke(slot, -1); }
        }
        Polled?.Invoke();
    }

    async Task Scan()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var found = await Task.Run(XInputPad.Connected);
            if (_poll.IsEnabled) _slots = found;
        }
        catch { } // no xinput1_4 (very old Windows): no controller triggers
        finally { _scanning = false; }
    }

    public void Dispose() => Stop();
}
