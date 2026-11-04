namespace IOHub.Triggers;

/// <summary>Human names for virtual-key codes and trigger descriptions.</summary>
public static class KeyNames
{
    public static string Of(int vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "Caps Lock", 0x1B => "Esc",
        0x20 => "Space", 0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        0x5B => "Left Win", 0x5C => "Right Win", 0x5D => "Menu",
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        0x6A => "Num *", 0x6B => "Num +", 0x6D => "Num -", 0x6E => "Num .", 0x6F => "Num /",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x90 => "Num Lock", 0x91 => "Scroll Lock",
        0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl", 0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0xAD => "Mute", 0xAE => "Volume Down", 0xAF => "Volume Up", 0xB0 => "Next Track", 0xB1 => "Previous Track", 0xB3 => "Play/Pause",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"Key {vk:X2}",
    };

    public static string Combo(KeyMods mods, int vk)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyMods.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyMods.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyMods.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyMods.Win)) parts.Add("Win");
        if (vk != 0) parts.Add(Of(vk));
        return string.Join(" + ", parts);
    }

    public static string Gesture(GestureKind g, int vk, int holdMs) => g switch
    {
        GestureKind.DoubleTap => $"Double-tap {Of(vk)}",
        GestureKind.TripleTap => $"Triple-tap {Of(vk)}",
        GestureKind.WhileHeld => $"While holding {Of(vk)}",
        _ => $"Hold {Of(vk)} {holdMs / 1000.0:0.#} s",
    };

    public static string Describe(Trigger t) => t.Kind switch
    {
        TriggerKind.Hotkey => t.Key == 0 ? "Hotkey not set" : Combo(t.Mods, t.Key),
        TriggerKind.KeyGesture => t.Key == 0 ? "Key not set" : Gesture(t.Gesture, t.Key, t.HoldMs),
        TriggerKind.ProfilePicker => t.Key == 0 ? "Key not set" : $"{Gesture(t.Gesture, t.Key, t.HoldMs)}, then 1–9",
        TriggerKind.SteamBigPicture => "Steam Big Picture",
        TriggerKind.ControllerCombo => t.Buttons == 0 ? "Buttons not set" : ControllerButtons.Gesture(t.Gesture, t.Buttons, t.HoldMs),
        TriggerKind.ControllerPicker => t.Buttons == 0 ? "Buttons not set" : $"{ControllerButtons.Gesture(t.Gesture, t.Buttons, t.HoldMs)}, then A, B, X, Y or the D-pad",
        _ => "Type not chosen",
    };

    /// <summary>Keys that type something. Taking one of these globally without a modifier would break typing.</summary>
    public static bool IsTypingKey(int vk) =>
        vk is 0x08 or 0x09 or 0x0D or 0x20 or (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x60 and <= 0x6F) or (>= 0xBA and <= 0xDF);

    public static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12;

    public static KeyMods ModOf(int vk) => vk switch
    {
        0xA2 or 0xA3 or 0x11 => KeyMods.Ctrl,
        0xA4 or 0xA5 or 0x12 => KeyMods.Alt,
        0xA0 or 0xA1 or 0x10 => KeyMods.Shift,
        0x5B or 0x5C => KeyMods.Win,
        _ => KeyMods.None,
    };
}
