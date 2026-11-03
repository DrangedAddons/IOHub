using System.ComponentModel;

namespace IOHub.Devices;

public sealed record DetailItem(string Label, string Value);

public sealed class DisplayDevice
{
    /// <summary>The monitor's device path: stays the same across reboots and while the display is on standby.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Connection { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Gpu { get; init; } = "";
    /// <summary>Windows' display number (from \\.\DISPLAYn); 0 when the display isn't part of the desktop.</summary>
    public int Number { get; init; }
    public bool IsActive { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsInternal { get; init; }
    public bool IsVirtual { get; init; }
    public bool HdrSupported { get; init; }
    public bool HdrEnabled { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double RefreshHz { get; init; }
    public int ScalePercent { get; init; }

    public string Glyph => IsInternal ? "\uE7F8" : "\uE7F4";

    /// <summary>An LG-made display ("GSM" in its EDID). LG Display laptop panels report "LGD" and don't count.</summary>
    public bool IsLg => Manufacturer == "LG";
    public bool IsSecondary => IsActive && !IsPrimary;
    public bool IsInactive => !IsActive;
    public string HdrText => HdrEnabled ? "HDR on" : "HDR capable";
    public string Resolution => $"{Width} × {Height}";

    public string Subtitle => Join(Number > 0 ? $"Display {Number}" : "Not in use", Manufacturer);

    public IReadOnlyList<DetailItem> Details
    {
        get
        {
            var list = new List<DetailItem>();
            if (IsActive)
            {
                list.Add(new("Resolution", Resolution));
                list.Add(new("Refresh rate", FormatHz(RefreshHz)));
                list.Add(new("Scale", ScalePercent > 0 ? $"{ScalePercent}%" : "—"));
                list.Add(new("Position", $"{X}, {Y}"));
            }
            list.Add(new("Graphics", Gpu == "" ? "—" : Gpu));
            return list;
        }
    }

    static string FormatHz(double hz) =>
        hz <= 0 ? "—" : Math.Abs(hz - Math.Round(hz)) < 0.05 ? $"{Math.Round(hz):0} Hz" : $"{hz:0.##} Hz";

    internal static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
}

public enum AudioFlow { Output, Input }

public sealed class AudioEndpoint : INotifyPropertyChanged, IDisposable
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Adapter { get; init; } = "";
    public AudioFlow Flow { get; init; }
    public bool IsActive { get; init; }
    public string State { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsCommsDefault { get; init; }
    /// <summary>Audio that goes over HDMI or DisplayPort to a monitor or TV.</summary>
    public bool IsDisplayAudio { get; init; }
    public string Glyph { get; init; } = "\uE7F5";

    public bool IsInactive => !IsActive;
    public bool IsDisabled => State == "Disabled";

    internal Func<float>? ReadPeak { get; init; }
    internal Action? Release { get; init; }

    double _level;
    /// <summary>0..1 peak level with a short decay so the bar doesn't flicker.</summary>
    public double Level => _level;

    public void UpdateLevel()
    {
        if (ReadPeak == null) return;
        float peak;
        try { peak = ReadPeak(); }
        catch { return; }
        // Square root makes quiet audio visible; linear peaks sit near zero for most content.
        var next = Math.Max(Math.Sqrt(Math.Clamp(peak, 0f, 1f)), _level * 0.82);
        if (next < 0.005) next = 0;
        if (Math.Abs(next - _level) < 0.002) return;
        _level = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() => Release?.Invoke();
}

public enum InputKind { Controller, Keyboard, Mouse }

public sealed class InputDevice
{
    public required InputKind Kind { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    /// <summary>"045E:0B13", or "" when the device has no USB IDs (built-in keyboards and touchpads).</summary>
    public string VidPid { get; init; } = "";
    public string Connection { get; init; } = "";
    /// <summary>"XInput · Slot 1" or "HID"; controllers only.</summary>
    public string Source { get; init; } = "";
    public int Interfaces { get; init; } = 1;
    public bool IsWireless { get; init; }
    /// <summary>0 empty … 3 full, -1 when unknown or wired.</summary>
    public int BatteryLevel { get; init; } = -1;

    /// <summary>Kind, USB IDs and name. XInput slots aren't stable, so the slot isn't part of it.</summary>
    public string Id => $"{Kind}|{VidPid}|{Name}";

    public string Glyph => Kind switch
    {
        InputKind.Controller => "\uE7FC",
        InputKind.Keyboard => "\uE765",
        _ => "\uE962",
    };

    public string Subtitle => DisplayDevice.Join(Kind == InputKind.Controller ? Source : "", Vendor, VidPid);
    public bool HasConnection => Connection != "";
    public bool HasMultipleInterfaces => Interfaces > 1;
    public string InterfacesText => $"{Interfaces} interfaces";

    public bool HasBattery => BatteryLevel >= 0;
    public bool IsBatteryLow => BatteryLevel is 0 or 1;
    public string BatteryText => BatteryLevel switch { 0 => "Empty", 1 => "Low", 2 => "Medium", _ => "Full" };
    public string BatteryGlyph => BatteryLevel switch { 0 => "\uE850", 1 => "\uE852", 2 => "\uE855", _ => "\uE83F" };
}

/// <summary>A webcam or capture card.</summary>
public sealed class CameraDevice
{
    /// <summary>The device instance ID: stable across reboots and USB re-plugs into the same port.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Connection { get; init; } = "";
    public bool IsVirtual { get; init; }
    public bool IsEnabled { get; init; } = true;

    public bool IsDisabled => !IsEnabled;
    public string Glyph => "";
    public string Subtitle => IsVirtual ? "Virtual camera" : Connection == "PCIe" ? "Capture card" : "Camera";
}

public sealed record InputSnapshot(
    IReadOnlyList<InputDevice> Controllers,
    IReadOnlyList<InputDevice> Keyboards,
    IReadOnlyList<InputDevice> Mice);
