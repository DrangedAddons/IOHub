using System.IO;
using System.Text.Json;

namespace IOHub;

/// <summary>App preferences. Small and best-effort: a failed read or write just means defaults.</summary>
public sealed class AppSettings
{
    // Views
    public string OverviewLayout { get; set; } = "Flow";
    public bool ShowDisabledDevices { get; set; }
    /// <summary>Devices page: devices whose functions are opened (by feature device id), so they stay open.</summary>
    public List<string> OpenDeviceFunctions { get; set; } = [];
    public string TriggersLayout { get; set; } = "Rows";

    // Startup and tray. Whether it starts with Windows lives in the registry (StartupEntry), not here.
    public bool StartInTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    // Switching
    public bool ShowNotices { get; set; } = true;
    public bool ConfirmNewDisplays { get; set; } = true;

    // Mouse mode (a controller drives the mouse)
    /// <summary>IO Hub is allowed to drive the mouse with a controller (instead of Steam). Off until someone turns it on.</summary>
    public bool ControllerMouse { get; set; }
    /// <summary>Pointer speed at full stick, in pixels a second.</summary>
    public int MouseSpeed { get; set; } = 1400;
    public bool MousePauseInFullscreen { get; set; } = true;

    /// <summary>
    /// How the controller's Rumble function buzzes, "pattern;strength;length" (Integrations.Rumble.Setting). Null until
    /// set: then it's taken from a trigger's old strength-only rumble value, if there is one.
    /// </summary>
    public string? Rumble { get; set; }

    // Controller power
    /// <summary>Turn a wireless controller off after this many minutes left alone; 0 = never.</summary>
    public int ControllerIdleOffMinutes { get; set; } = 15;
    /// <summary>Wireless controllers seen before, so one that's off or disconnected still shows on the Devices page.</summary>
    public List<KnownController> KnownControllers { get; set; } = [];

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; }
    /// <summary>Also offer pre-releases (betas), not just full releases.</summary>
    public bool BetaUpdates { get; set; }

    static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>A wireless controller IO Hub has seen: its InputDevice.Id, name and (for PlayStation and Switch) model.</summary>
public sealed class KnownController
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Model { get; set; }
}
