using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IOHub.Triggers;

/// <summary>
/// Controller buttons as flags: XInput's own button bits, plus made-up bits for the analog triggers (LT and RT count as
/// pressed past halfway) and for buttons only PlayStation and Switch pads have. PlayStation and Switch buttons map onto
/// these by position (see <see cref="PadReports"/>). Saved in <see cref="Trigger.Buttons"/>, so the values must not change.
/// </summary>
public static class ControllerButtons
{
    public const int DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const int Menu = 0x0010, View = 0x0020, LeftStick = 0x0040, RightStick = 0x0080;
    public const int LB = 0x0100, RB = 0x0200, Guide = 0x0400;
    public const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    public const int LT = 0x10000, RT = 0x20000;
    /// <summary>PlayStation touchpad click, DualSense mic button, Switch Capture button.</summary>
    public const int Touchpad = 0x40000, Mute = 0x80000, Capture = 0x100000;

    /// <summary>In the order combinations are written: shoulders first, face buttons last.</summary>
    static readonly int[] Order = [Guide, View, Menu, Touchpad, Mute, Capture, LB, RB, LT, RT, LeftStick, RightStick, DPadUp, DPadDown, DPadLeft, DPadRight, A, B, X, Y];

    /// <summary>The controller profile picker's answers, in profile order: profile 1 is A, 2 is B, and so on.</summary>
    public static readonly int[] PickerButtons = [A, B, X, Y, DPadUp, DPadRight, DPadDown, DPadLeft];

    public enum Layout { Xbox, DualShock4, DualSense, Switch }

    /// <summary>
    /// Whose button names to show. Set from the connected controllers: Xbox names unless every usable pad is a
    /// PlayStation or Switch one of the same kind. Only the names change; a trigger works on every controller.
    /// </summary>
    public static Layout Names { get; set; } = Layout.Xbox;

    public static Layout LayoutOf(PadModel model) => model switch
    {
        PadModel.DualShock4 => Layout.DualShock4,
        PadModel.DualSense => Layout.DualSense,
        _ => Layout.Switch,
    };

    static bool PlayStation => Names is Layout.DualShock4 or Layout.DualSense;

    public static string Name(int button) => Names switch
    {
        Layout.DualShock4 or Layout.DualSense => button switch
        {
            A => "Cross", B => "Circle", X => "Square", Y => "Triangle",
            LB => "L1", RB => "R1", LT => "L2", RT => "R2",
            View => Names == Layout.DualSense ? "Create" : "Share", Menu => "Options", Guide => "PS",
            LeftStick => "L3", RightStick => "R3",
            _ => XboxName(button),
        },
        Layout.Switch => button switch
        {
            A => "B", B => "A", X => "Y", Y => "X", // by position: Switch's bottom button is B
            LB => "L", RB => "R", LT => "ZL", RT => "ZR",
            View => "Minus", Menu => "Plus", Guide => "Home",
            _ => XboxName(button),
        },
        _ => XboxName(button),
    };

    static string XboxName(int button) => button switch
    {
        A => "A", B => "B", X => "X", Y => "Y",
        LB => "LB", RB => "RB", LT => "LT", RT => "RT",
        View => "View", Menu => "Menu", Guide => "Xbox",
        LeftStick => "Left stick", RightStick => "Right stick",
        DPadUp => "D-pad up", DPadDown => "D-pad down", DPadLeft => "D-pad left", DPadRight => "D-pad right",
        Touchpad => "Touchpad", Mute => "Mic button", Capture => "Capture",
        _ => $"Button {button:X}",
    };

    /// <summary>One or two characters, for the picker's badges: D-pad directions are arrows.</summary>
    public static string ShortName(int button) => button switch
    {
        DPadUp => "↑", DPadDown => "↓", DPadLeft => "←", DPadRight => "→",
        A when PlayStation => "✕",
        B when PlayStation => "○",
        X when PlayStation => "□",
        Y when PlayStation => "△",
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
        GestureKind.WhileHeld => $"While holding {Combo(buttons)}",
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
        if (!TryReadState(slot, out var state)) return false;
        buttons = ButtonsOf(state);
        return true;
    }

    static int ButtonsOf(XInputState state)
    {
        int buttons = state.Buttons;
        if (state.LeftTrigger > 127) buttons |= ControllerButtons.LT;
        if (state.RightTrigger > 127) buttons |= ControllerButtons.RT;
        return buttons;
    }

    static bool TryReadState(int slot, out XInputState state)
    {
        uint result;
        // The undocumented ordinal 100 is XInputGetState plus the Xbox (Guide) button. Every Windows 10/11 xinput1_4 has it.
        if (!_noGuide)
        {
            try { result = XInputGetStateEx((uint)slot, out state); }
            catch (EntryPointNotFoundException) { _noGuide = true; result = XInputGetState((uint)slot, out state); }
        }
        else result = XInputGetState((uint)slot, out state);
        return result == 0;
    }

    /// <summary>One controller's buttons and sticks.</summary>
    public readonly record struct Pad(int Buttons, short LeftX, short LeftY, short RightX, short RightY)
    {
        public int LeftMagnitude => Math.Abs((int)LeftX) + Math.Abs((int)LeftY);
        public int RightMagnitude => Math.Abs((int)RightX) + Math.Abs((int)RightY);
    }

    /// <summary>Buttons and sticks of one controller. False if nothing's in that slot.</summary>
    public static bool TryReadPad(int slot, out Pad pad)
    {
        pad = default;
        if (!TryReadState(slot, out var state)) return false;
        pad = new Pad(ButtonsOf(state), state.ThumbLX, state.ThumbLY, state.ThumbRX, state.ThumbRY);
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
/// Every controller IO Hub can read, in one numbering: slots 0-3 are XInput's (Xbox pads), 4 and up are PlayStation
/// and Switch pads read by <see cref="Devices.HidPads"/>.
/// </summary>
public static class Pads
{
    public const int HidBase = XInputPad.Slots;

    /// <summary>Which slots have a controller. Slow-ish (XInput probes empty slots), so don't call it often.</summary>
    public static List<int> Connected()
    {
        var list = XInputPad.Connected();
        try { list.AddRange(Devices.HidPads.Slots()); } catch { }
        return list;
    }

    public static bool TryRead(int slot, out int buttons) =>
        slot < HidBase ? XInputPad.TryRead(slot, out buttons) : Devices.HidPads.TryRead(slot, out buttons);

    public static bool TryReadPad(int slot, out XInputPad.Pad pad) =>
        slot < HidBase ? XInputPad.TryReadPad(slot, out pad) : Devices.HidPads.TryReadPad(slot, out pad);

    /// <summary>Hold the returned lease while reading often: PlayStation and Switch pads are read faster meanwhile.</summary>
    public static IDisposable Listen(bool fast = false) => Devices.HidPads.Listen(fast);
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
    /// <summary>A "while held" combination was let go.</summary>
    public event Action<object>? Released;

    // One press seen by two controllers at once is one press. That happens when Steam adds its virtual Xbox pad for a
    // PlayStation or Switch pad in a game: IO Hub reads both. The first controller to fire a trigger owns it until
    // another fires it more than DuplicateMs later, and only the owner's let-go counts.
    const long DuplicateMs = 300;
    readonly Dictionary<object, (int Slot, long At)> _lastFired = [];
    long _now;

    void OnFired(int slot, object tag)
    {
        if (_lastFired.TryGetValue(tag, out var last) && last.Slot != slot && _now - last.At < DuplicateMs) return;
        _lastFired[tag] = (slot, _now);
        Fired?.Invoke(tag);
    }

    void OnReleased(int slot, object tag)
    {
        if (_lastFired.TryGetValue(tag, out var last) && last.Slot != slot) return;
        Released?.Invoke(tag);
    }

    public bool HasEntries => _combos.Count > 0;

    public void SetEntries(IEnumerable<Entry> entries)
    {
        _combos.Clear();
        _detectorEntries = [];
        _lastFired.Clear();
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
        pad.Detector.Reset();
        pad.Detector.SetEntries(_detectorEntries);
        pad.Down.Clear();
    }

    /// <summary>Feeds one controller's buttons. Returns the buttons that just went down.</summary>
    public int OnButtons(int slot, int buttons, long now)
    {
        _now = now;
        if (!_pads.TryGetValue(slot, out var pad))
        {
            pad = new Pad();
            pad.Detector.Fired += tag => OnFired(slot, tag);
            pad.Detector.Released += tag => OnReleased(slot, tag);
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
        _now = now;
        foreach (var pad in _pads.Values) pad.Detector.Tick(now);
    }

    public void Reset()
    {
        foreach (var pad in _pads.Values) { pad.Detector.Reset(); pad.Down.Clear(); pad.Buttons = 0; }
    }
}

/// <summary>
/// Reads the connected controllers (Xbox, PlayStation and Switch) about 25 times a second while a controller trigger needs it. Empty slots are
/// only checked every couple of seconds, on a worker thread: XInput is slow to answer for a slot with nothing in it.
/// Raises <see cref="Read"/> on the UI thread.
/// </summary>
public sealed class ControllerWatcher : IDisposable
{
    readonly DispatcherTimer _poll;
    readonly DispatcherTimer _scan;
    List<int> _slots = [];
    bool _scanning;
    IDisposable? _listening;

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
        _listening = Pads.Listen();
        _poll.Start();
        _scan.Start();
        _ = Scan();
    }

    public void Stop()
    {
        _poll.Stop();
        _scan.Stop();
        _listening?.Dispose();
        _listening = null;
        foreach (var slot in _slots) Read?.Invoke(slot, -1);
        _slots = [];
    }

    void Poll()
    {
        foreach (var slot in _slots.ToList())
        {
            if (Pads.TryRead(slot, out var buttons)) Read?.Invoke(slot, buttons);
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
            var found = await Task.Run(Pads.Connected);
            if (_poll.IsEnabled) _slots = found;
        }
        catch { } // no xinput1_4 (very old Windows): no controller triggers
        finally { _scanning = false; }
    }

    public void Dispose() => Stop();
}
