using System.Diagnostics;
using System.IO;
using IOHub.Profiles;
using Microsoft.Win32;

namespace IOHub.Integrations;

/// <summary>FishIO found in one WoW version's AddOns folder. DevCopy: a git working copy, which IO Hub never touches.</summary>
public sealed record FishIOInstall(string Flavor, string Version, string Folder, bool DevCopy = false)
{
    /// <summary>Older than the copy inside IO Hub, so updating it would bring it up to date.</summary>
    public bool Outdated => !DevCopy && FishIOAddon.IsOlder(Version, FishIOAddon.Bundled);

    /// <summary>The WoW version folder it's in: ...\World of Warcraft\_forever_.</summary>
    public string FlavorFolder => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Folder)!)!)!;

    /// <summary>"WoW Forever", "Retail", "Classic Era"…</summary>
    public string Game => FishIOAddon.GameName(Flavor);
}

/// <summary>
/// The FishIO companion addon (focus mode: music and ambience off while a fishing pole is in hand). IO Hub carries it
/// (its source is addons/FishIO in this repo, embedded in the exe) and installs, updates and removes it, so it only
/// has one owner: it isn't in the DrangedAddons manager. Installed copies are updated when IO Hub starts with a newer
/// one. A folder with .git in it is a development copy and is never written to or deleted.
///
/// WoW's folder comes from a running Wow.exe, Blizzard's registry entry, or the default install path. IO Hub can't
/// tell whether the addon is turned on in game, only that it's there.
/// </summary>
public static class FishIOAddon
{
    public const string Name = "FishIO";

    /// <summary>The WoW versions FishIO is made for (its .toc is for WoW Forever's client).</summary>
    public static readonly string[] Flavors = ["_forever_"];

    static readonly string[] Files = ["FishIO.toc", "Core.lua"];

    /// <summary>The version inside IO Hub, from its embedded .toc.</summary>
    public static string Bundled { get; } = BundledVersion();

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
                found.Add(new FishIOInstall(Path.GetFileName(flavorDir), TocVersion(toc), folder,
                    Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"))));
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

    /// <summary>
    /// Every WoW version installed (a _name_ folder with a Wow*.exe in it), as its folder (...\World of Warcraft\_forever_),
    /// and whether FishIO is made for it. FishIO can go in any of them; outside WoW Forever, WoW may list it as out of date.
    /// </summary>
    public static IReadOnlyList<(string Flavor, string Folder, bool Made)> Games() =>
        WowRoots().SelectMany(root =>
            {
                try { return Directory.GetDirectories(root, "_*_"); }
                catch { return []; }
            })
            .Where(dir => { try { return Directory.EnumerateFiles(dir, "Wow*.exe").Any(); } catch { return false; } })
            .Select(dir => (Flavor: Path.GetFileName(dir), Folder: dir, Made: Flavors.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)))
            .DistinctBy(g => g.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Made).ThenBy(g => GameName(g.Flavor))
            .ToList();

    /// <summary>The name a WoW version goes by: "WoW Forever", "Classic Era PTR".</summary>
    public static string GameName(string flavor) => flavor.ToLowerInvariant() switch
    {
        "_forever_" => "WoW Forever",
        "_retail_" => "Retail",
        "_ptr_" => "Retail PTR",
        "_xptr_" => "Retail Experimental PTR",
        "_beta_" => "Retail Beta",
        "_classic_" => "Classic",
        "_classic_ptr_" => "Classic PTR",
        "_classic_beta_" => "Classic Beta",
        "_classic_era_" => "Classic Era",
        "_classic_era_ptr_" => "Classic Era PTR",
        "_anniversary_" => "Anniversary",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(flavor.Trim('_').Replace('_', ' ')),
    };

    /// <summary>Writes IO Hub's copy into a WoW version folder (..\_forever_). Returns an error, or null.</summary>
    public static string? Install(string flavorFolder)
    {
        var folder = Path.Combine(flavorFolder, "Interface", "AddOns", Name);
        if (Directory.Exists(Path.Combine(folder, ".git"))) return "That's a development copy (it has .git), so IO Hub leaves it alone.";
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in Files)
            {
                // Through a temp file, so WoW never reads half a file.
                var target = Path.Combine(folder, file);
                var temp = target + ".iohub";
                using (var res = typeof(FishIOAddon).Assembly.GetManifestResourceStream($"{Name}/{file}")!)
                using (var output = File.Create(temp))
                    res.CopyTo(output);
                File.Move(temp, target, overwrite: true);
            }
            SwitchLog.Write($"FishIO {Bundled} installed in {folder}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _checkedAt = default; }
    }

    /// <summary>Deletes an install's folder. Never a development copy. Returns an error, or null.</summary>
    public static string? Remove(FishIOInstall install)
    {
        if (install.DevCopy) return "That's a development copy (it has .git), so IO Hub leaves it alone.";
        try
        {
            Directory.Delete(install.Folder, recursive: true);
            SwitchLog.Write($"FishIO removed from {install.Folder}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _checkedAt = default; }
    }

    /// <summary>Updates every install older than IO Hub's copy. At startup, off the UI thread.</summary>
    public static void UpdateOutdated()
    {
        foreach (var install in Find().Where(i => i.Outdated))
        {
            var error = Install(install.FlavorFolder);
            SwitchLog.Write(error == null ? $"FishIO updated from {install.Version} to {Bundled} in {install.Game}" : $"FishIO couldn't be updated in {install.Game}: {error}");
        }
    }

    public static bool IsOlder(string version, string than) =>
        Version.TryParse(version, out var a) && Version.TryParse(than, out var b) ? a < b : version != than && version == "?";

    static string BundledVersion()
    {
        using var res = typeof(FishIOAddon).Assembly.GetManifestResourceStream($"{Name}/{Name}.toc");
        if (res == null) return "?";
        using var reader = new StreamReader(res);
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return line[11..].Trim();
        return "?";
    }

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
