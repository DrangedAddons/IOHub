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

    // Mouse mode (an Xbox controller drives the mouse)
    /// <summary>IO Hub is allowed to drive the mouse with a controller (instead of Steam). Off until someone turns it on.</summary>
    public bool ControllerMouse { get; set; }
    /// <summary>Pointer speed at full stick, in pixels a second.</summary>
    public int MouseSpeed { get; set; } = 1400;
    public bool MousePauseInFullscreen { get; set; } = true;

    // Integrations: World of Warcraft
    /// <summary>Rumble the controllers when WoW's fishing bobber splashes. Off until someone turns it on.</summary>
    public bool WowFishingRumble { get; set; }
    /// <summary>"low", "normal" or "high" (WowFishing.Sensitivities).</summary>
    public string WowFishingSensitivity { get; set; } = "normal";
    /// <summary>"light", "medium" or "strong" (WowFishing.Strengths).</summary>
    public string WowRumbleStrength { get; set; } = "medium";

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
