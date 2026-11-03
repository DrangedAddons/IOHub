using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IOHub.Updates;

public sealed record AppRelease(string Version, string ExeUrl, string PageUrl, string Notes);

/// <summary>
/// Keeps IOHub.exe up to date from the DrangedAddons/IOHub releases, the same way the DrangedAddons manager does.
/// Windows lets a running .exe be renamed but not overwritten, so an update renames the running copy to .old,
/// moves the new one into its place, starts it, and exits; the new copy deletes the .old file when it starts.
/// </summary>
public static class AppUpdater
{
    const string ExeName = AppInfo.Id + ".exe";

    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Id}/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static string CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The running .exe if it's a release build (one self-contained file), whatever it's called: browsers save
    /// repeat downloads as "IOHub (1).exe" and that copy must still update itself. Null for dev builds, which run
    /// from a .dll beside the .exe.
    /// </summary>
    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            // A single-file release has no assembly file on disk, so Location is empty. That's exactly the
            // test here, so the single-file analyzer's warning about it doesn't apply.
#pragma warning disable IL3000
            return string.IsNullOrEmpty(typeof(AppUpdater).Assembly.Location) ? path : null;
#pragma warning restore IL3000
        }
    }

    public static bool CanSelfUpdate => ExePath is not null;

    /// <summary>The latest release if it's newer than this copy and carries IOHub.exe; null if not (or no releases yet).</summary>
    public static async Task<AppRelease?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // nothing released yet
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = doc.RootElement;
        var version = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (Versions.Compare(version, CurrentVersion) <= 0) return null;

        var exeUrl = release.GetProperty("assets").EnumerateArray()
            .Where(a => (a.GetProperty("name").GetString() ?? "").Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        return exeUrl is null ? null : new AppRelease(version, exeUrl,
            release.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl,
            release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    /// <summary>Downloads and swaps in the new .exe, then starts it. The caller should quit after this returns.</summary>
    public static async Task ApplyAsync(AppRelease release, bool startInTray, IProgress<double>? progress = null)
    {
        var exe = ExePath ?? throw new InvalidOperationException($"This copy of {AppInfo.Name} can't update itself.");
        var newPath = exe + ".new";

        try
        {
            await DownloadAsync(release.ExeUrl, newPath, progress);
        }
        catch (UnauthorizedAccessException)
        {
            // Can't write beside this copy (e.g. it's in Program Files): let the new version install itself instead.
            await HandOverToInstallerAsync(release, startInTray, progress);
            return;
        }
        if (new FileInfo(newPath).Length < 1_000_000)
        {
            File.Delete(newPath);
            throw new InvalidDataException("The downloaded update looks incomplete. Try again later.");
        }

        string oldPath;
        try
        {
            oldPath = SetAside(exe);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(newPath);
            await HandOverToInstallerAsync(release, startInTray, progress);
            return;
        }
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(oldPath, exe); // put the running copy back
            throw;
        }
        // --replace: the new copy waits for this one to finish closing instead of just showing its window.
        Process.Start(new ProcessStartInfo(exe, startInTray ? "--replace --tray" : "--replace") { UseShellExecute = true });
    }

    /// <summary>For a copy that can't replace itself where it is: download to a temporary folder and start that.</summary>
    static async Task HandOverToInstallerAsync(AppRelease release, bool startInTray, IProgress<double>? progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), AppInfo.Id + "-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ExeName);
        await DownloadAsync(release.ExeUrl, path, progress);
        if (new FileInfo(path).Length < 1_000_000)
            throw new InvalidDataException("The downloaded update looks incomplete. Try again later.");
        Process.Start(new ProcessStartInfo(path, startInTray ? "--replace --tray" : "--replace") { UseShellExecute = true });
    }

    static async Task DownloadAsync(string url, string path, IProgress<double>? progress)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0) progress?.Report((double)done / total.Value);
        }
    }

    /// <summary>
    /// Renames <paramref name="exe"/> out of the way (it may be running) and returns where it went. Normally
    /// "IOHub.exe.old"; if an older copy there is still running and can't be removed, a uniquely named one.
    /// </summary>
    internal static string SetAside(string exe)
    {
        var oldPath = exe + ".old";
        try
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            oldPath = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old";
        }
        File.Move(exe, oldPath);
        return oldPath;
    }

    /// <summary>Removes previous copies left by updates. One may still be closing, so retry for a few seconds.</summary>
    public static async Task CleanUpAsync()
    {
        if (ExePath is not { } exe) return;
        var dir = Path.GetDirectoryName(exe)!;
        var pattern = Path.GetFileName(exe) + "*.old";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var leftovers = Directory.GetFiles(dir, pattern);
            if (leftovers.Length == 0) break;
            var locked = false;
            foreach (var file in leftovers)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked = true; }
            }
            if (!locked) break;
            await Task.Delay(500);
        }
        try { File.Delete(exe + ".new"); } catch { } // leftover from an interrupted download
    }
}

public static class Versions
{
    /// <summary>Compares dotted numeric versions ("v0.2.1" vs "0.10.0"), ignoring any prefix.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    static long[] Parts(string v) =>
        Regex.Matches(v, @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToArray();
}
