using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace IOHub.Integrations;

/// <summary>FishIO found in one WoW version's AddOns folder.</summary>
public sealed record FishIOInstall(string Flavor, string Version, string Folder)
{
    /// <summary>"WoW Forever", "Retail", "Classic Era"…</summary>
    public string Game => Flavor switch
    {
        "_forever_" => "WoW Forever",
        "_retail_" => "Retail",
        "_classic_era_" => "Classic Era",
        "_classic_" => "Classic",
        "_ptr_" => "PTR",
        "_beta_" => "Beta",
        _ => Flavor.Trim('_'),
    };
}

/// <summary>
/// Finds the FishIO companion addon (focus mode: music and ambience off while a fishing pole is in hand) in every WoW
/// version installed. WoW's folder comes from a running Wow.exe, Blizzard's registry entry, or the default install
/// path. IO Hub can't tell whether the addon is turned on in game, only that it's there.
/// </summary>
public static class FishIOAddon
{
    public const string Name = "FishIO";
    public const string Url = "https://github.com/DrangedAddons/FishIO";

    static DateTime _checkedAt;
    static List<FishIOInstall> _found = [];

    /// <summary>Every install found. Looks again at most every 10 seconds; it's a few folder checks.</summary>
    public static IReadOnlyList<FishIOInstall> Find()
    {
        if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(10)) return _found;
        _checkedAt = DateTime.UtcNow;
        var found = new List<FishIOInstall>();
        foreach (var root in WowRoots())
        {
            string[] flavors;
            try { flavors = Directory.GetDirectories(root, "_*_"); }
            catch { continue; }
            foreach (var flavorDir in flavors)
            {
                var folder = Path.Combine(flavorDir, "Interface", "AddOns", Name);
                var toc = Path.Combine(folder, Name + ".toc");
                if (!File.Exists(toc)) continue;
                found.Add(new FishIOInstall(Path.GetFileName(flavorDir), TocVersion(toc), folder));
            }
        }
        return _found = found;
    }

    /// <summary>"0.1.0 in WoW Forever", or "WoW Forever and Retail" for several.</summary>
    public static string Describe(IReadOnlyList<FishIOInstall> installs) => installs.Count switch
    {
        0 => "not installed",
        1 => $"{installs[0].Version} in {installs[0].Game}",
        _ => string.Join(" and ", installs.Select(i => $"{i.Version} in {i.Game}")),
    };

    static string TocVersion(string toc)
    {
        try
        {
            foreach (var line in File.ReadLines(toc))
                if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return line[11..].Trim();
        }
        catch { }
        return "?";
    }

    /// <summary>World of Warcraft folders (the one holding _retail_, _forever_…), each once.</summary>
    static IEnumerable<string> WowRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A running game knows exactly where it is: ...\World of Warcraft\_forever_\Wow.exe.
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!p.ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
                    || p.ProcessName.StartsWith("WowVoice", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe != null && Path.GetDirectoryName(Path.GetDirectoryName(exe)) is { } r) roots.Add(r);
                }
                catch { }
            }
        }

        // The launcher's install path points at one version folder (usually _retail_).
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("InstallPath") is string path && path.Trim().TrimEnd('\\', '/') is { Length: > 0 } dir)
                    roots.Add(Path.GetFileName(dir).StartsWith('_') ? Path.GetDirectoryName(dir) ?? dir : dir);
            }
            catch { }
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "World of Warcraft"));
        return roots.Where(Directory.Exists);
    }
}
