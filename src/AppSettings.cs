using System.IO;
using System.Text.Json;

namespace IOHub;

/// <summary>App preferences. Small and best-effort: a failed read or write just means defaults.</summary>
public sealed class AppSettings
{
    // Views
    public string OverviewLayout { get; set; } = "Flow";
    public bool ShowDisabledDevices { get; set; }
    /// <summary>Devices page: the tables of what can be changed on each device, and their triggers.</summary>
    public bool ShowDeviceFunctions { get; set; }
    public string TriggersLayout { get; set; } = "Rows";

    // Startup and tray. Whether it starts with Windows lives in the registry (StartupEntry), not here.
    public bool StartInTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    // Switching
    public bool ShowNotices { get; set; } = true;
    public bool ConfirmNewDisplays { get; set; } = true;

    // Mouse mode (an Xbox controller drives the mouse)
    /// <summary>IO Hub is allowed to drive the mouse with a controller (instead of Steam). Off until someone turns it on.</summary>
    public bool ControllerMouse { get; set; }
    /// <summary>Pointer speed at full stick, in pixels a second.</summary>
    public int MouseSpeed { get; set; } = 1400;
    public bool MousePauseInFullscreen { get; set; } = true;

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
