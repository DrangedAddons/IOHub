using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using IOHub.Ui;
using Microsoft.Win32;

namespace IOHub.Updates;

/// <summary>
/// Installs the app per-user like Discord or VS Code: %LocalAppData%\Programs\IOHub, Start menu and desktop shortcuts,
/// and an entry in Settings → Apps. Per-user (not Program Files) so the self-updater can replace the .exe without an
/// administrator prompt. Same approach as the DrangedAddons manager.
/// </summary>
public static class AppInstaller
{
    const string ExeName = AppInfo.Id + ".exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.Id;

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Id);

    static string InstalledExe => Path.Combine(InstallDir, ExeName);
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk");

    public static bool IsRunningInstalled =>
        AppUpdater.ExePath is { } exe && Path.GetFullPath(exe).Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true to carry on starting this process, false when it handed over to the installed copy.</summary>
    public static bool HandleStartup(string[] args)
    {
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Uninstall(); // from Settings → Apps; this process exits either way
            return false;
        }

        // Dev builds (with their .dll alongside) never install, and IOHUB_DEV=1 makes a published test build run in
        // place without offering to install or touching an installed copy.
        if (AppUpdater.ExePath is not { } exe || AppInfo.IsDev) return true;

        if (IsRunningInstalled)
        {
            Register(); // keeps the version shown in Settings → Apps current after self-updates
            return true;
        }

        if (File.Exists(InstalledExe))
        {
            // Already installed: a newer download upgrades it, then the installed copy opens either way.
            // ProductVersion keeps a beta suffix; FileVersion can't.
            var installed = FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion ?? "0";
            var upgrade = Versions.Compare(AppUpdater.CurrentVersion, installed) > 0;
            if (upgrade)
            {
                CopyIn(exe);
                Register();
            }
            Launch(replace: upgrade);
            return false;
        }

        if (!DarkDialog.Show(null, $"Install {AppInfo.Name}?",
                $"It will be installed for your Windows account in\n{InstallDir}\n" +
                "with Start menu and desktop shortcuts, start with Windows (you can turn that off in Settings), " +
                "and can be removed from Settings → Apps.\n\n" +
                "Choose \"Just run it\" to use it this once without installing.",
                primary: "Install", secondary: "Just run it", showLogo: true))
            return true;

        CopyIn(exe);
        Register();
        CreateShortcut(StartMenuShortcut);
        CreateShortcut(DesktopShortcut);
        StartupEntry.Set(enabled: true, InstalledExe, inTray: true);
        DarkDialog.Show(null, $"{AppInfo.Name} is installed",
            "Open it from the Start menu or the desktop shortcut from now on.\n\n" +
            $"You can delete the file you downloaded ({exe}).",
            primary: $"Open {AppInfo.Name}", showLogo: true);
        Launch();
        return false;
    }

    /// <summary>Copies this .exe into the install folder. A running installed copy is renamed aside (.old) first.</summary>
    static void CopyIn(string sourceExe)
    {
        Directory.CreateDirectory(InstallDir);
        if (File.Exists(InstalledExe)) AppUpdater.SetAside(InstalledExe);
        File.Copy(sourceExe, InstalledExe);
    }

    static void Launch(bool replace = false) =>
        Process.Start(new ProcessStartInfo(InstalledExe, replace ? "--replace" : "")
        {
            UseShellExecute = true,
            WorkingDirectory = InstallDir,
        });

    /// <summary>The Settings → Apps entry (per-user, no admin needed).</summary>
    static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var previousVersion = key.GetValue("DisplayVersion") as string;
        key.SetValue("DisplayName", AppInfo.Name);
        key.SetValue("DisplayVersion", AppUpdater.CurrentVersion);
        key.SetValue("Publisher", AppInfo.Org);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", AppInfo.RepoUrl);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (File.Exists(InstalledExe))
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);

        // First run of a new version: Windows caches shortcut icons and won't notice that the .exe changed.
        if (previousVersion != AppUpdater.CurrentVersion) RefreshShellIcons();
    }

    const int ShcneUpdateItem = 0x00002000, ShcneAssocChanged = 0x08000000;
    const uint ShcnfIdList = 0x0000, ShcnfPathW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    static void RefreshShellIcons()
    {
        foreach (var path in new[] { InstalledExe, StartMenuShortcut, DesktopShortcut })
            if (File.Exists(path)) SHChangeNotify(ShcneUpdateItem, ShcnfPathW, path, IntPtr.Zero);
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, null, IntPtr.Zero);
    }

    static void CreateShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Switch displays, audio and input between setups";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Removes the app (from Settings → Apps or the Settings page). Profiles and settings stay unless asked.</summary>
    public static bool Uninstall(Window? owner = null)
    {
        if (!DarkDialog.Show(owner, $"Uninstall {AppInfo.Name}?",
                "This removes the app, its shortcuts and its start-with-Windows entry. " +
                "Your profiles and triggers are kept, so reinstalling picks up where you left off.",
                primary: "Uninstall", secondary: "Cancel", showLogo: true, danger: true))
            return false;

        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
        {
            try { File.Delete(shortcut); } catch { } // already gone or in use: harmless
        }
        StartupEntry.Set(enabled: false, InstalledExe, inTray: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        // This .exe is probably the one being removed, so delete the folder just after this process exits.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        DarkDialog.Show(owner, $"{AppInfo.Name} has been uninstalled", "");
        return true;
    }
}

/// <summary>Start with Windows: a value under HKCU\...\Run pointing at the exe (with --tray to start hidden).</summary>
public static class StartupEntry
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The exe Windows would start, or null when there's no entry.</summary>
    public static string? Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInfo.Name) as string;
        }
    }

    public static bool IsEnabled => Current != null;

    public static void Set(bool enabled, string exe, bool inTray)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppInfo.Name, inTray ? $"\"{exe}\" --tray" : $"\"{exe}\"");
        else key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
    }
}
