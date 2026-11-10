namespace IOHub.Triggers;

// Saved as numbers, so existing values keep their numbers.
public enum TriggerKind
{
    /// <summary>A key combination, e.g. Ctrl + Alt + T.</summary>
    Hotkey = 0,
    /// <summary>Double tap, triple tap or hold of one key.</summary>
    KeyGesture = 1,
    /// <summary>Before 0.6: a key gesture that opens the profile list. Now a KeyGesture with Then = Picker; only read from old files.</summary>
    ProfilePicker = 2,
    /// <summary>Steam Big Picture opening (and closing). An integration.</summary>
    SteamBigPicture = 3,
    /// <summary>A press, double tap, triple tap or hold of a button combination on an Xbox controller.</summary>
    ControllerCombo = 4,
    /// <summary>Before 0.6: the controller version of ProfilePicker. Now a ControllerCombo with Then = Picker; only read from old files.</summary>
    ControllerPicker = 5,
    /// <summary>The fishing bobber splashing in World of Warcraft, heard in WoW's sound. An integration.</summary>
    WowFishingBite = 6,
    /// <summary>Just created; the type hasn't been picked yet. Never fires.</summary>
    Unset = 99,
}

public enum TriggerCategory { Keys, Controller, Integrations }

public enum GestureKind
{
    DoubleTap = 0,
    TripleTap = 1,
    Hold = 2,
    /// <summary>One press. Controller combinations only: a keyboard combo is a Hotkey.</summary>
    Press = 3,
    /// <summary>
    /// Lasts while held: switched to the profile (or a device feature on) while held, back when let go. Not for pickers,
    /// or device features that aren't on/off.
    /// </summary>
    WhileHeld = 4,
}

/// <summary>What a trigger does when it fires.</summary>
public enum ThenKind
{
    /// <summary>Switch to a profile (or, for pickers, the profile picked).</summary>
    Profile = 0,
    /// <summary>Change one feature of one device: mouse mode on, TV off, mic muted…</summary>
    Device = 1,
    /// <summary>Open the profile list; the next number key (or controller button) picks the profile.</summary>
    Picker = 2,
}

public enum FeatureAction { Toggle = 0, On = 1, Off = 2, Set = 3, Run = 4 }

[Flags]
public enum KeyMods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

public sealed class Trigger
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TriggerKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The profile to switch to. Unused by the picker, which picks by number.</summary>
    public string? ProfileId { get; set; }

    /// <summary>Virtual-key code; left/right modifier keys keep their own codes (e.g. Right Ctrl = 0xA3).</summary>
    public int Key { get; set; }
    public KeyMods Mods { get; set; }
    public GestureKind Gesture { get; set; } = GestureKind.DoubleTap;
    public int HoldMs { get; set; } = 800;

    /// <summary>Controller triggers: the buttons held together, as <see cref="ControllerButtons"/> flags.</summary>
    public int Buttons { get; set; }

    public ThenKind Then { get; set; }
    /// <summary>Device triggers: which device (see <see cref="Devices.DeviceFeature.DeviceId"/>).</summary>
    public string? DeviceId { get; set; }
    /// <summary>The device's name when it was picked, to show while it isn't connected.</summary>
    public string? DeviceName { get; set; }
    /// <summary>Device triggers: which of the device's features ("mouse", "power", "mute"…).</summary>
    public string? Feature { get; set; }
    public FeatureAction Action { get; set; }
    /// <summary>For <see cref="FeatureAction.Set"/>: the choice's value ("2" for HDMI 2).</summary>
    public string? Value { get; set; }

    public bool ChangesDevice => Then == ThenKind.Device;

    /// <summary>
    /// Key triggers: firing again while switched puts the previous setup back (a toggle).
    /// Big Picture: closing it puts the previous setup back.
    /// </summary>
    public bool SwitchBack { get; set; }

    /// <summary>Big Picture only: close and reopen it after switching, so it fills the new display.</summary>
    public bool ReopenBigPicture { get; set; }

    /// <summary>WoW fishing bite only: "low", "normal" or "high" (WowFishing.Sensitivities).</summary>
    public string Sensitivity { get; set; } = "normal";

    /// <summary>
    /// WoW fishing bite only: the quietest splash that rumbles, in dBFS, set on the sound meter. Null = automatic
    /// (splashes far quieter than your usual one are someone else's).
    /// </summary>
    public float? MinLevelDb { get; set; }

    /// <summary>True for triggers with a start and an end (Big Picture); false for one-shot key presses.</summary>
    public bool HasEnd => Kind == TriggerKind.SteamBigPicture;

    public bool UsesKeyboard => Kind is TriggerKind.Hotkey or TriggerKind.KeyGesture or TriggerKind.ProfilePicker;
    public bool UsesController => Kind is TriggerKind.ControllerCombo or TriggerKind.ControllerPicker;

    /// <summary>Opens the numbered profile list instead of switching to one profile.</summary>
    public bool IsPicker => Then == ThenKind.Picker;

    /// <summary>Old files had the picker as a kind of trigger; it's now what a trigger does. True if this one changed.</summary>
    public bool Upgrade()
    {
        if (Kind == TriggerKind.ProfilePicker) { Kind = TriggerKind.KeyGesture; Then = ThenKind.Picker; return true; }
        if (Kind == TriggerKind.ControllerPicker) { Kind = TriggerKind.ControllerCombo; Then = ThenKind.Picker; return true; }
        return false;
    }

    /// <summary>Its key or buttons have been set (always true for kinds that don't need any).</summary>
    public bool HasInput => UsesKeyboard ? Key != 0 : !UsesController || Buttons != 0;

    public static TriggerCategory CategoryOf(TriggerKind kind) => kind switch
    {
        TriggerKind.SteamBigPicture or TriggerKind.WowFishingBite => TriggerCategory.Integrations,
        TriggerKind.ControllerCombo or TriggerKind.ControllerPicker => TriggerCategory.Controller,
        _ => TriggerCategory.Keys,
    };
}
