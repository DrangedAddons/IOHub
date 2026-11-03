using System.IO;
using System.Text.Json;

namespace IOHub;

/// <summary>App preferences. Small and best-effort: a failed read or write just means defaults.</summary>
public sealed class AppSettings
{
    // Views
    public string OverviewLayout { get; set; } = "Flow";
    public bool ShowDisabledDevices { get; set; }
    public string TriggersLayout { get; set; } = "Rows";

    // Startup and tray. Whether it starts with Windows lives in the registry (StartupEntry), not here.
    public bool StartInTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    // Switching
    public bool ShowNotices { get; set; } = true;
    public bool ConfirmNewDisplays { get; set; } = true;

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; }

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
