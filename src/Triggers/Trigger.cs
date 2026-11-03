namespace IOHub.Triggers;

// Saved as numbers, so existing values keep their numbers.
public enum TriggerKind
{
    /// <summary>A key combination, e.g. Ctrl + Alt + T.</summary>
    Hotkey = 0,
    /// <summary>Double tap, triple tap or hold of one key.</summary>
    KeyGesture = 1,
    /// <summary>A gesture that opens a numbered list; the next digit picks the profile.</summary>
    ProfilePicker = 2,
    /// <summary>Steam Big Picture opening (and closing). An integration.</summary>
    SteamBigPicture = 3,
    /// <summary>Just created; the type hasn't been picked yet. Never fires.</summary>
    Unset = 99,
}

public enum TriggerCategory { Keys, Integrations }

public enum GestureKind { DoubleTap, TripleTap, Hold }

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

    /// <summary>
    /// Key triggers: firing again while switched puts the previous setup back (a toggle).
    /// Big Picture: closing it puts the previous setup back.
    /// </summary>
    public bool SwitchBack { get; set; }

    /// <summary>Big Picture only: close and reopen it after switching, so it fills the new display.</summary>
    public bool ReopenBigPicture { get; set; }

    /// <summary>True for triggers with a start and an end (Big Picture); false for one-shot key presses.</summary>
    public bool HasEnd => Kind == TriggerKind.SteamBigPicture;

    public bool UsesKeyboard => Kind is TriggerKind.Hotkey or TriggerKind.KeyGesture or TriggerKind.ProfilePicker;

    public static TriggerCategory CategoryOf(TriggerKind kind) =>
        kind == TriggerKind.SteamBigPicture ? TriggerCategory.Integrations : TriggerCategory.Keys;
}
