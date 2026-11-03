using System.Net.Http;
using System.Windows.Threading;

namespace IOHub.Updates;

/// <summary>
/// Looks for a newer release a little after start-up and then every six hours (if allowed), and holds what it
/// found for the Settings page and the tray. One small request per check; nothing runs in between. UI thread only.
/// </summary>
public sealed class UpdateChecker
{
    readonly AppSettings _settings;
    readonly DispatcherTimer _timer;

    public AppRelease? Available { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public string? Error { get; private set; }
    public bool Busy { get; private set; }
    /// <summary>0..1 while an update downloads.</summary>
    public double? Progress { get; private set; }

    /// <summary>Something here changed (for the Settings page).</summary>
    public event Action? Changed;
    /// <summary>A check found a release that wasn't known before.</summary>
    public event Action<AppRelease>? Found;

    public UpdateChecker(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            if (_settings.CheckForUpdates) await CheckAsync();
        };
    }

    public void Start()
    {
        if (AppUpdater.CanSelfUpdate) _timer.Start();
    }

    public async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            var found = await AppUpdater.CheckAsync(_settings.BetaUpdates);
            bool isNew = found != null && found.Version != Available?.Version;
            Available = found;
            if (isNew) Found?.Invoke(found!);
        }
        catch (HttpRequestException) { Error = "Couldn't reach GitHub. Check your connection and try again."; }
        catch (TaskCanceledException) { Error = "GitHub took too long to answer. Try again later."; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            LastChecked = DateTime.Now;
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads and swaps in the update. True when the new copy has started and this one should quit.</summary>
    public async Task<bool> InstallAsync(bool startInTray)
    {
        if (Available is not { } release || Busy) return false;
        Busy = true;
        Error = null;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            await AppUpdater.ApplyAsync(release, startInTray, new Progress<double>(p =>
            {
                double percent = Math.Floor(p * 100) / 100;
                if (percent == Progress) return;
                Progress = percent;
                Changed?.Invoke();
            }));
            return true;
        }
        catch (Exception ex)
        {
            Error = $"The update didn't install: {ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
            Progress = null;
            Changed?.Invoke();
        }
    }
}
