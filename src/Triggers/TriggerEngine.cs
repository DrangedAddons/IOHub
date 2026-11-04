using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using IOHub.Profiles;
using IOHub.Ui;

namespace IOHub.Triggers;

/// <summary>
/// Runs the triggers: the keyboard hook and gesture detector for key triggers, the controller reader and its gesture
/// detector for controller triggers, the Big Picture watcher, and what happens when one fires. Each of those only
/// runs while a trigger needs it. UI thread only.
/// </summary>
public sealed class TriggerEngine : IDisposable
{
    const int PickerWindowMs = 2500, ControllerPickerWindowMs = 4000;

    readonly ProfileService _profiles;
    readonly ProfileSwitcher _switcher;
    readonly Devices.FeatureService _features;
    readonly Toast _toast;
    readonly Dispatcher _ui;
    readonly Func<Profile?> _activeProfile;
    readonly KeyGestureDetector _detector = new();
    readonly KeyboardHook _hook;
    readonly ControllerGestures _pad = new();
    readonly ControllerWatcher _controllers;
    readonly BigPictureWatcher _bigPicture;
    readonly DispatcherTimer _tick;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    // A trigger that switched and can switch back holds the setup from before it fired.
    readonly Dictionary<string, SetupSnapshot> _engaged = [];
    readonly Dictionary<string, DateTime> _lastFired = [];

    // Automatic switches in the last few minutes, to catch a loop (switch → Big Picture recreated → revert → …).
    readonly Queue<DateTime> _recentSwitches = new();
    // A real loop repeats every few seconds; opening and closing Big Picture a few times on purpose stays well under.
    const int LoopLimit = 8;
    static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(2);

    long _pickerUntil = -1;
    bool _pickerByController; // the open picker answers to controller buttons (and shows them)
    readonly HashSet<int> _pickerSwallowed = [];
    bool _paused, _suspended;

    /// <summary>Raised when something the Triggers page shows changes (last fired, Big Picture state).</summary>
    public event Action? StateChanged;

    public TriggerEngine(ProfileService profiles, ProfileSwitcher switcher, Devices.FeatureService features, Toast toast, Dispatcher ui, Func<Profile?> activeProfile)
    {
        _profiles = profiles;
        _switcher = switcher;
        _features = features;
        _toast = toast;
        _ui = ui;
        _activeProfile = activeProfile;
        _hook = new KeyboardHook(OnKey);
        _detector.Fired += tag => OnGesture((Trigger)tag);
        _detector.Released += tag => OnReleased((Trigger)tag);
        _pad.Fired += tag => OnGesture((Trigger)tag);
        _pad.Released += tag => OnReleased((Trigger)tag);
        _controllers = new ControllerWatcher(ui);
        _controllers.Read += OnController;
        _controllers.Polled += () => _pad.Tick(_clock.ElapsedMilliseconds);
        _bigPicture = new BigPictureWatcher(ui);
        _bigPicture.Changed += OnBigPicture;
        _tick = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = TimeSpan.FromMilliseconds(25) };
        _tick.Tick += (_, _) => _detector.Tick(_clock.ElapsedMilliseconds);
        _profiles.Changed += Reload;
        Reload();
    }

    public bool BigPictureOpen => _bigPicture.IsOpen;
    /// <summary>Controllers connected, as far as the controller reader knows; null while it isn't running.</summary>
    public int? ControllersConnected => _controllers.IsRunning ? _controllers.ConnectedCount : null;
    public bool IsEngaged(Trigger t) => _engaged.ContainsKey(t.Id);
    public DateTime? LastFired(Trigger t) => _lastFired.TryGetValue(t.Id, out var at) ? at : null;

    /// <summary>All triggers off, e.g. from the tray menu.</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; Reload(); StateChanged?.Invoke(); }
    }

    /// <summary>Key triggers off while the Triggers page is recording a key, so recording doesn't fire them.</summary>
    public bool Suspended
    {
        get => _suspended;
        set { _suspended = value; _detector.Reset(); _pad.Reset(); }
    }

    /// <summary>Re-reads the triggers and starts or stops the hook and watcher to match.</summary>
    public void Reload()
    {
        var active = _paused ? [] : _profiles.Data.Triggers.Where(t => t.Enabled).ToList();

        var keyed = active.Where(t => t.UsesKeyboard && t.Key != 0).ToList();
        _detector.SetEntries(keyed.Select(t => new KeyGestureDetector.Entry(t.Kind, t.Key, t.Mods, t.Gesture, t.HoldMs, t)));
        if (keyed.Count > 0) { _hook.Install(); _tick.Start(); }
        else { _hook.Uninstall(); _tick.Stop(); _detector.Reset(); }

        var padded = active.Where(t => t.UsesController && t.Buttons != 0).ToList();
        _pad.SetEntries(padded.Select(t => new ControllerGestures.Entry(t.Buttons, t.Gesture, t.HoldMs, t)));
        if (padded.Count > 0) _controllers.Start();
        else { _controllers.Stop(); _pad.Reset(); }

        if (active.Any(t => t.Kind == TriggerKind.SteamBigPicture)) _bigPicture.Start();
        else _bigPicture.Stop();

        // Forget switch-back state for triggers that were removed or turned off.
        foreach (var id in _engaged.Keys.Where(id => active.All(t => t.Id != id)).ToList()) _engaged.Remove(id);
    }

    // ---------------- keys ----------------

    bool OnKey(int vk, bool down, long hookTime)
    {
        if (_suspended) return false;
        long now = _clock.ElapsedMilliseconds;

        // While the picker is open the next digit is the answer, and it shouldn't reach the game.
        if (_pickerUntil >= 0)
        {
            if (!down) return _pickerSwallowed.Remove(vk);
            if (now > _pickerUntil) ClosePicker();
            else if (Digit(vk) is int n and > 0)
            {
                _pickerSwallowed.Add(vk);
                ClosePicker();
                Dispatch(() => _ = PickNumber(n));
                return true;
            }
            else if (vk == 0x1B) // Esc
            {
                _pickerSwallowed.Add(vk);
                ClosePicker();
                return true;
            }
            else if (!KeyNames.IsModifier(vk)) ClosePicker();
        }
        return _detector.OnKey(vk, down, now);
    }

    // ---------------- controllers ----------------

    void OnController(int slot, int buttons)
    {
        long now = _clock.ElapsedMilliseconds;
        if (buttons < 0) { _pad.Disconnected(slot, now); return; }
        if (_suspended) return;

        // Only a picker that was already open takes an answer: the buttons that opened it don't count as one.
        bool pickerWasOpen = _pickerUntil >= 0 && _pickerByController;
        int pressed = _pad.OnButtons(slot, buttons, now);
        if (!pickerWasOpen || pressed == 0) return;
        if (now > _pickerUntil) { ClosePicker(); return; }
        if ((pressed & ControllerButtons.View) != 0) { ClosePicker(); return; } // View cancels
        var picks = ControllerButtons.PickerButtons;
        for (int i = 0; i < picks.Length; i++)
        {
            if ((pressed & picks[i]) == 0) continue;
            ClosePicker();
            int n = i + 1;
            Dispatch(() => _ = PickNumber(n));
            return;
        }
    }

    static int? Digit(int vk) => vk switch
    {
        >= 0x31 and <= 0x39 => vk - 0x30,
        >= 0x61 and <= 0x69 => vk - 0x60,
        _ => null,
    };

    void ClosePicker()
    {
        _pickerUntil = -1;
        Dispatch(_toast.HideNow);
    }

    // The hook has to return fast; anything slow runs after it.
    void Dispatch(Action a) => _ui.BeginInvoke(a);

    void OnGesture(Trigger t)
    {
        if (t.IsPicker)
        {
            OpenPicker(byController: t.UsesController);
            return;
        }
        Dispatch(() => _ = Fire(t, starting: true));
    }

    void OpenPicker(bool byController)
    {
        _pickerByController = byController;
        // A controller is slower to answer with than a number key.
        _pickerUntil = _clock.ElapsedMilliseconds + (byController ? ControllerPickerWindowMs : PickerWindowMs);
        Dispatch(ShowPicker);
    }

    /// <summary>The end of a "while held": the buttons or key were let go.</summary>
    void OnReleased(Trigger t)
    {
        if (t.Gesture != GestureKind.WhileHeld) return;
        if (t.ChangesDevice) Dispatch(() => _ = RunDevice(t, starting: false));
        else if (t.Then == ThenKind.Profile) Dispatch(() => _ = Fire(t, starting: false));
    }

    // "While held" profile triggers let go before their switch finished: switch back as soon as it has.
    readonly HashSet<string> _letGoEarly = [];

    /// <summary>A device trigger: changes the feature, says so (except for the quiet ends of "while held").</summary>
    async Task RunDevice(Trigger t, bool starting)
    {
        FeatureAction action;
        if (t.Gesture == GestureKind.WhileHeld && t.UsesController || t.Gesture == GestureKind.WhileHeld && t.UsesKeyboard)
            action = starting ? FeatureAction.On : FeatureAction.Off;
        else if (starting) action = t.Action;
        // Big Picture closing, with "Undo it" on: the opposite of what opening did.
        else if (t.HasEnd && t.SwitchBack && t.Action is FeatureAction.On or FeatureAction.Off or FeatureAction.Toggle)
            action = t.Action switch { FeatureAction.On => FeatureAction.Off, FeatureAction.Off => FeatureAction.On, _ => FeatureAction.Toggle };
        else return;

        _lastFired[t.Id] = DateTime.Now;
        var error = await _features.RunAsync(t, action);
        string what = _features.Describe(t, withDevice: false);
        SwitchLog.Write($"Trigger {KeyNames.Describe(t)}: {_features.Describe(t, withDevice: true)}" + (starting ? "" : " (let go)") + (error != null ? $": {error}" : ""));
        if (error != null) _toast.Show("Couldn't change the device", error);
        else if (t.Gesture != GestureKind.WhileHeld) _toast.Show(what, _features.Describe(t, withDevice: true));
        StateChanged?.Invoke();
    }

    void ShowPicker()
    {
        var active = _activeProfile();
        var profiles = _profiles.Profiles
            .Select(p => (p.Name, (Color)ColorConverter.ConvertFromString(p.Color), p == active))
            .ToList();
        if (_pickerByController)
            _toast.ShowPicker(profiles, ControllerPickerWindowMs / 1000.0, ControllerButtons.PickerButtons.Select(ControllerButtons.ShortName).ToList(),
                "Press its button. View cancels.");
        else
            _toast.ShowPicker(profiles, PickerWindowMs / 1000.0);
    }

    async Task PickNumber(int n)
    {
        if (n > _profiles.Profiles.Count) return;
        var profile = _profiles.Profiles[n - 1];
        await _switcher.SwitchTo(profile, $"Picked with {n}");
    }

    // ---------------- Big Picture ----------------

    void OnBigPicture(bool open)
    {
        StateChanged?.Invoke();
        foreach (var t in _profiles.Data.Triggers.Where(t => t.Enabled && t.Kind == TriggerKind.SteamBigPicture).ToList())
            _ = Fire(t, starting: open);
    }

    async Task ReopenBigPicture()
    {
        // Big Picture opens at the size of the display it started on; after a display switch it can come back
        // the wrong size until it's reopened.
        _bigPicture.IgnoreClosesFor(TimeSpan.FromSeconds(25));
        OpenUrl("steam://close/bigpicture");
        for (int i = 0; i < 75 && _bigPicture.Find() != IntPtr.Zero; i++) await Task.Delay(200);
        await Task.Delay(1000);
        OpenUrl("steam://open/bigpicture");
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ---------------- firing ----------------

    /// <summary>
    /// <paramref name="starting"/> is false only for a trigger with an end (Big Picture closing).
    /// Key triggers with SwitchBack toggle: the second press puts things back.
    /// </summary>
    async Task Fire(Trigger t, bool starting)
    {
        if (_paused) return;
        if (t.ChangesDevice) { await RunDevice(t, starting); return; }
        _lastFired[t.Id] = DateTime.Now;
        StateChanged?.Invoke();

        // Letting go of a "while held" isn't a new switch to count: only the hold is.
        if (!(t.Gesture == GestureKind.WhileHeld && !starting) && TooManySwitches())
        {
            SwitchLog.Write($"Triggers paused: {LoopLimit} automatic switches within {LoopWindow.TotalMinutes:0} minutes");
            Paused = true;
            _toast.Show("Triggers paused", $"They switched {LoopLimit} times in {LoopWindow.TotalMinutes:0} minutes, which looks like a loop. "
                + "Resume them from the tray menu or Settings.", 8);
            return;
        }
        // Changing displays makes Steam recreate the Big Picture window; don't take that as Big Picture closing.
        if (t.Kind == TriggerKind.SteamBigPicture) _bigPicture.IgnoreClosesFor(TimeSpan.FromSeconds(20));

        // "While held" always goes back when let go; others only with "switch back" on.
        bool switchBack = t.SwitchBack || t.Gesture == GestureKind.WhileHeld;
        if (!starting && t.Gesture == GestureKind.WhileHeld && !_engaged.ContainsKey(t.Id))
        {
            _letGoEarly.Add(t.Id); // still switching: go back once it's done
            return;
        }
        if (!starting || (switchBack && !t.HasEnd && _engaged.ContainsKey(t.Id)))
        {
            if (!switchBack || !_engaged.Remove(t.Id, out var before)) return;
            await _switcher.Restore(before, "Back to the previous profile", t.HasEnd ? "Big Picture closed" : KeyNames.Describe(t));
            StateChanged?.Invoke();
            return;
        }

        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == t.ProfileId);
        if (profile == null)
        {
            _toast.Show("Trigger has no profile", $"{KeyNames.Describe(t)} isn't linked to a profile yet.");
            return;
        }

        _letGoEarly.Remove(t.Id);
        var snapshot = switchBack ? _switcher.Capture() : null;
        bool ok = await _switcher.SwitchTo(profile, t.HasEnd ? "Big Picture opened" : KeyNames.Describe(t));
        if (snapshot != null && ok) _engaged[t.Id] = snapshot;
        if (_letGoEarly.Remove(t.Id) && _engaged.Remove(t.Id, out var undo))
        {
            await _switcher.Restore(undo, "Back to the previous profile", KeyNames.Describe(t) + " let go");
            StateChanged?.Invoke();
            return;
        }
        if (ok && t.Kind == TriggerKind.SteamBigPicture && t.ReopenBigPicture) await ReopenBigPicture();
        // The switch itself can take a while (a TV powering up); give the displays time to settle afterwards too.
        if (t.Kind == TriggerKind.SteamBigPicture) _bigPicture.IgnoreClosesFor(TimeSpan.FromSeconds(10));
        StateChanged?.Invoke();
    }

    bool TooManySwitches()
    {
        var now = DateTime.Now;
        while (_recentSwitches.Count > 0 && now - _recentSwitches.Peek() > LoopWindow) _recentSwitches.Dequeue();
        if (_recentSwitches.Count >= LoopLimit) { _recentSwitches.Clear(); return true; }
        _recentSwitches.Enqueue(now);
        return false;
    }

    /// <summary>Fires a trigger by hand from the Triggers page ("Test").</summary>
    public Task Test(Trigger t)
    {
        if (!t.IsPicker) return Fire(t, starting: true);
        OpenPicker(byController: t.UsesController);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _hook.Dispose();
        _controllers.Dispose();
        _bigPicture.Dispose();
        _tick.Stop();
    }
}
