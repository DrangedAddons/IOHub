using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace IOHub.Devices;

/// <summary>An LG TV set up in LGTV Companion. <see cref="Key"/> is how its command line names it ("Device1").</summary>
public sealed record LgTv(string Key, string Name, string Ip);

/// <summary>
/// Turns LG webOS TVs on and off (and picks their HDMI input) through LGTV Companion
/// (github.com/JPersson77/LGTVCompanion), which handles pairing, Wake-on-LAN and the TV's network API.
/// IO Hub only calls its command line: "LGTV Companion.exe" -poweron Device1, and so on.
/// </summary>
public static class LgCompanion
{
    public const string DownloadUrl = "https://github.com/JPersson77/LGTVCompanion/releases/latest";

    static string ExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LGTV Companion", "LGTV Companion.exe");

    static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGTV Companion", "config.json");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// The TVs LGTV Companion knows. Its config.json has one object per TV, keyed "Device1", "Device2"…, next to its
    /// own settings; a TV is any of those with a name and an IP address.
    /// </summary>
    public static List<LgTv> Tvs()
    {
        var list = new List<LgTv>();
        try
        {
            if (!File.Exists(ConfigPath)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Object) continue;
                if (!item.Value.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                if (!item.Value.TryGetProperty("IP", out var ip) || ip.ValueKind != JsonValueKind.String) continue;
                list.Add(new LgTv(item.Name, name.GetString() ?? item.Name, ip.GetString() ?? ""));
            }
        }
        catch { } // unreadable or mid-write: treat as none
        return list;
    }

    public static Task<string?> PowerOn(string tv) => Run($"-poweron {tv}");
    public static Task<string?> PowerOff(string tv) => Run($"-poweroff {tv}");
    public static Task<string?> SetHdmi(string tv, int input) => Run($"-sethdmi{input} {tv}");

    /// <summary>Runs one command. Returns an error message, or null. Gives up after 15 seconds so a stuck TV can't hold a switch.</summary>
    static async Task<string?> Run(string arguments)
    {
        if (!IsInstalled) return "LGTV Companion isn't installed.";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            });
            if (process == null) return "LGTV Companion didn't start.";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return null;
        }
        catch (OperationCanceledException) { return "LGTV Companion didn't answer in time."; }
        catch (Exception ex) { return $"LGTV Companion failed: {ex.Message}"; }
    }
}
