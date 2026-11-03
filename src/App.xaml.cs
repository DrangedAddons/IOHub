using System.IO;
using System.Windows;
using System.Windows.Threading;
using IOHub.Ui;
using IOHub.Updates;

namespace IOHub;

public static class AppInfo
{
    /// <summary>The name people see.</summary>
    public const string Name = "IO Hub";
    /// <summary>The name in paths, registry keys and the exe: no space.</summary>
    public const string Id = "IOHub";
    public const string Org = "DrangedAddons";
    public const string Repo = Org + "/" + Id;
    public const string RepoUrl = "https://github.com/" + Repo;

    /// <summary>Test builds (IOHUB_DEV=1) run in place and use their own single-instance names.</summary>
    public static readonly bool IsDev = Environment.GetEnvironmentVariable("IOHUB_DEV") == "1";

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);

    /// <summary>Before it had a name the app kept its data in %LocalAppData%\CueLight; carry it over once.</summary>
    public static void MoveOldData()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CueLight");
        if (Directory.Exists(DataDir) || !Directory.Exists(old)) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (var file in Directory.GetFiles(old, "*.json"))
                File.Copy(file, Path.Combine(DataDir, Path.GetFileName(file)));
        }
        catch { } // a fresh start is fine if this fails
    }
}

public partial class App : Application
{
    static readonly string InstanceName = $@"Local\{AppInfo.Id}" + (AppInfo.IsDev ? "-dev" : "");

    Mutex? _instance;
    bool _ownsInstance;

    /// <summary>True once the app is really quitting (not just hiding to the tray).</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Quits for real: the tray menu, Settings, updates and uninstall all go through here.</summary>
    public static void ExitApp()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    void App_Startup(object sender, StartupEventArgs e)
    {
        AppInfo.MoveOldData();

        bool keepRunning;
        try
        {
            keepRunning = AppInstaller.HandleStartup(e.Args);
        }
        catch (Exception ex)
        {
            DarkDialog.Show(null, $"Couldn't install {AppInfo.Name}", $"{ex.Message}\n\nIt will run from where it is for now.");
            keepRunning = true;
        }

        if (!keepRunning || !ClaimInstance(replace: e.Args.Contains("--replace")))
        {
            Shutdown();
            return;
        }
        _ = AppUpdater.CleanUpAsync();

        var main = new MainWindow();
        MainWindow = main;
        // --tray: started with Windows, or restarted after an automatic update. Stay out of the way until opened.
        if (e.Args.Contains("--tray")) main.StartInTray();
        else main.Show();
    }

    /// <summary>
    /// Makes this the only running copy. If another copy is running, it's asked to show its window and this one
    /// exits; with --replace (after an update) the old copy is asked to quit instead, and this one takes over.
    /// </summary>
    bool ClaimInstance(bool replace)
    {
        _instance = new Mutex(false, InstanceName + ".Instance");
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");

        _ownsInstance = TryTake(TimeSpan.Zero);
        if (!_ownsInstance && replace)
        {
            quit.Set();
            _ownsInstance = TryTake(TimeSpan.FromSeconds(20));
        }
        if (!_ownsInstance)
        {
            show.Set();
            return false;
        }

        // Listen for later launches (show the window) and for a newer copy taking over (quit).
        var listener = new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny([show, quit]);
                Dispatcher.BeginInvoke(() =>
                {
                    if (which == 0) (MainWindow as MainWindow)?.ShowFromTray();
                    else (MainWindow as MainWindow)?.Quit();
                });
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "Single instance" };
        listener.Start();
        return true;
    }

    bool TryTake(TimeSpan timeout)
    {
        try
        {
            return _instance!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous copy ended without releasing it
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true; // signing out or shutting down: don't hide to the tray
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance) _instance?.ReleaseMutex();
        base.OnExit(e);
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.AppendAllText(Path.Combine(AppInfo.DataDir, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        DarkDialog.Show(null, $"{AppInfo.Name} hit a problem", e.Exception.Message);
        e.Handled = true;
    }
}
