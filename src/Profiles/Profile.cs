using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IOHub.Profiles;

/// <summary>
/// What a profile switches. Each section is independent: an empty or null section means the profile leaves that
/// part of the setup alone.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Color { get; set; } = ProfileColors.All[0];

    /// <summary>Displays that are on in this profile; the rest get turned off. Empty = video unchanged.</summary>
    public List<string> Displays { get; set; } = [];
    public string? PrimaryDisplay { get; set; }

    /// <summary>
    /// Set once someone has seen this profile's display layout work and pressed Keep. Until then every switch asks,
    /// and reverts on its own if nobody answers (the screen may be black). Editing the displays clears it.
    /// </summary>
    public bool DisplaysConfirmed { get; set; }

    public string? AudioOutput { get; set; }
    public string? AudioInput { get; set; }

    /// <summary>The device this profile is mainly controlled with.</summary>
    public string? MainInput { get; set; }

    /// <summary>The webcam or capture device this profile uses.</summary>
    public string? Camera { get; set; }

    [JsonIgnore]
    public bool ChangesNothing => Displays.Count == 0 && AudioOutput == null && AudioInput == null && MainInput == null && Camera == null;

    public Profile Clone() => new()
    {
        Name = Name,
        Color = Color,
        Displays = [.. Displays],
        PrimaryDisplay = PrimaryDisplay,
        DisplaysConfirmed = DisplaysConfirmed,
        AudioOutput = AudioOutput,
        AudioInput = AudioInput,
        MainInput = MainInput,
        Camera = Camera,
    };
}

public static class ProfileColors
{
    /// <summary>Handed out in order to new profiles.</summary>
    public static readonly string[] All = ["#7B8CFF", "#46D39A", "#F2B34A", "#62BDF6", "#F07AA8", "#B07BFF", "#4FD1C5", "#F06A6A"];

    /// <summary>The swatches in the colour picker: the defaults plus a few more.</summary>
    public static readonly string[] Presets = [.. All, "#FF8A4C", "#A3E635", "#E879F9", "#94A3B8"];

    public static string Next(string current) => All[(Array.IndexOf(All, current) + 1) % All.Length];
}

/// <summary>A device a profile points at, remembered so it still shows by name while it's unplugged.</summary>
public sealed class KnownDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Section { get; set; } = "";
}

public sealed class ProfileData
{
    public List<Profile> Profiles { get; set; } = [];
    public List<KnownDevice> Devices { get; set; } = [];
    public List<Triggers.Trigger> Triggers { get; set; } = [];

    /// <summary>Set once the first-run "Current setup" profile has been made, so deleting it doesn't bring it back.</summary>
    public bool SeededCurrentSetup { get; set; }
}

/// <summary>The app's one copy of the profiles. Pages that edit them call <see cref="Save"/>; others listen to <see cref="Changed"/>.</summary>
public sealed class ProfileService
{
    public ProfileData Data { get; } = ProfileStore.Load();
    public List<Profile> Profiles => Data.Profiles;

    /// <summary>
    /// The profile last switched to. When several profiles match the PC equally (say two that set the same
    /// devices), this one counts as active. Not saved: after a restart the first match wins again.
    /// </summary>
    public string? LastAppliedId { get; set; }

    public event Action? Changed;

    /// <summary>A profile holding whatever is live now: the active displays, main display, default sound and mic.</summary>
    public static Profile Capture(Devices.DeviceHub hub, string name, string color) => new()
    {
        Name = name,
        Color = color,
        Displays = hub.Displays.Where(d => d.IsActive).Select(d => d.Id).ToList(),
        PrimaryDisplay = hub.Displays.FirstOrDefault(d => d.IsPrimary)?.Id,
        // It's the layout the PC is already using, so there's nothing to confirm.
        DisplaysConfirmed = true,
        AudioOutput = hub.Audio.FirstOrDefault(a => a.Flow == Devices.AudioFlow.Output && a.IsDefault)?.Id,
        AudioInput = hub.Audio.FirstOrDefault(a => a.Flow == Devices.AudioFlow.Input && a.IsDefault)?.Id,
    };

    /// <summary>Swaps in another set of profiles, triggers and remembered devices (import, reset) and saves.</summary>
    public void Replace(ProfileData data)
    {
        Data.Profiles = data.Profiles;
        Data.Devices = data.Devices;
        Data.Triggers = data.Triggers;
        Data.SeededCurrentSetup = data.SeededCurrentSetup;
        LastAppliedId = null;
        Save();
    }

    /// <summary>On first run, start with the current setup as the first profile.</summary>
    public void SeedCurrentSetup(Devices.DeviceHub hub)
    {
        if (Data.SeededCurrentSetup) return;
        Data.SeededCurrentSetup = true;
        if (Profiles.Count == 0 && hub.Displays.Any(d => d.IsActive))
            Profiles.Add(Capture(hub, "Current setup", ProfileColors.All[0]));
        Save();
    }

    /// <summary>Writes to disk and tells listeners. Returns the error message if the write failed.</summary>
    public string? Save()
    {
        string? error = null;
        try { ProfileStore.Save(Data); }
        catch (Exception ex) { error = ex.Message; }
        Changed?.Invoke();
        return error;
    }
}

public static class ProfileStore
{
    static string FilePath => Path.Combine(AppInfo.DataDir, "profiles.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ProfileData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for recovery rather than overwriting it on the next save.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            System.Diagnostics.Debug.WriteLine($"profiles.json unreadable: {ex.Message}");
        }
        return new();
    }

    /// <summary>For export: the same format as profiles.json.</summary>
    public static string ToJson(ProfileData data) => JsonSerializer.Serialize(data, Options);

    /// <summary>For import. Throws on a file that isn't profiles data.</summary>
    public static ProfileData FromJson(string json) =>
        JsonSerializer.Deserialize<ProfileData>(json, Options) ?? throw new InvalidDataException("The file is empty.");

    public static void Save(ProfileData data)
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
