using System.Diagnostics;
using System.IO;
using IOHub.Devices;

namespace IOHub.Profiles;

/// <summary>How things were before a switch, so a trigger can put them back.</summary>
public sealed record SetupSnapshot(DisplaySnapshot? Displays, string? Output, string? Input);

/// <summary>
/// Applies profiles to the PC: LG TVs on, audio defaults, the display layout (with each display's remembered mode),
/// then LG TVs that are no longer used off. One switch at a time: a second request waits for the first rather than
/// being dropped, and display changes are spaced out, because graphics drivers can hang when mode changes pile up.
/// A display layout nobody has confirmed yet is kept only if someone presses Keep. Every step goes to switch.log.
/// UI thread only.
/// </summary>
public sealed class ProfileSwitcher(DeviceHub hub, ProfileService profiles)
{
    static readonly TimeSpan DisplayCooldown = TimeSpan.FromSeconds(3);
    static readonly TimeSpan SettleAfterDisplayChange = TimeSpan.FromSeconds(8);

    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _lastDisplayChange = DateTime.MinValue;

    /// <summary>Shows the "keep these displays?" prompt. True = keep. Set by the main window.</summary>
    public Func<Profile, Task<bool>>? ConfirmDisplays { get; set; }

    /// <summary>A short message for the on-screen toast: title, detail, and the profile's colour if there is one.</summary>
    public event Action<string, string, string?>? Notice;

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>When the displays last changed (for anything that should let them settle first).</summary>
    public DateTime LastDisplayChange => _lastDisplayChange;

    public SetupSnapshot Capture() => new(
        TryOrNull(DisplayDetector.Capture),
        hub.Audio.FirstOrDefault(a => a.Flow == AudioFlow.Output && a.IsDefault)?.Id,
        hub.Audio.FirstOrDefault(a => a.Flow == AudioFlow.Input && a.IsDefault)?.Id);

    /// <summary>Switches to <paramref name="profile"/>. Returns false if it was refused, undone or partly failed.</summary>
    public async Task<bool> SwitchTo(Profile profile, string? because = null)
    {
        await _gate.WaitAsync();
        try
        {
            SwitchLog.Write($"Switch to \"{profile.Name}\"" + (because != null ? $" ({because})" : ""));
            var previous = profiles.Profiles.FirstOrDefault(p => p.Id == profiles.LastAppliedId);
            var problems = new List<string>();

            if (profile.Displays.Count > 0) await PowerOnTvs(profile.Displays, profile, previous, problems);
            SetAudio(profile.AudioOutput, "sound output", problems);
            SetAudio(profile.AudioInput, "microphone", problems);

            if (profile.Displays.Count > 0 && !DisplaysMatch(profile))
            {
                var before = TryOrNull(DisplayDetector.Capture);
                var error = await ChangeDisplays(() => DisplayDetector.Apply(profile.Displays, profile.PrimaryDisplay, profiles.Data.DisplayModes));
                if (error != null) problems.Add(error);
                else
                {
                    await ReassertInputs(profile, problems);
                    problems.AddRange(await CheckRefreshRates(profile.Displays));
                    if (!profile.DisplaysConfirmed && ConfirmDisplays != null)
                    {
                        await Task.Delay(1500); // let the screens come up before asking
                        if (await ConfirmDisplays(profile))
                        {
                            profile.DisplaysConfirmed = true;
                            profiles.Save();
                        }
                        else
                        {
                            if (before != null) await ChangeDisplays(() => DisplayDetector.Restore(before));
                            SwitchLog.Write("  displays not kept, reverted");
                            hub.ScheduleRefresh();
                            Notice?.Invoke("Display change undone", $"{profile.Name}'s displays weren't kept.", null);
                            return false;
                        }
                    }
                }
            }

            // TVs the previous profile turned on and this one doesn't use. A profile that leaves video alone keeps them.
            if (previous != null && previous != profile && profile.Displays.Count > 0)
                await PowerOffTvs(previous, keep: profile.Displays, problems);

            profiles.LastAppliedId = profile.Id;
            hub.ScheduleRefresh();
            foreach (var p in problems) SwitchLog.Write("  problem: " + p);
            SwitchLog.Write(problems.Count == 0 ? "  done" : "  done with problems");
            Notice?.Invoke(
                problems.Count == 0 ? $"Switched to {profile.Name}" : $"Switched to {profile.Name}, with problems",
                problems.Count == 0 ? because ?? "" : string.Join(" ", problems),
                profile.Color);
            return problems.Count == 0;
        }
        catch (Exception ex)
        {
            SwitchLog.Write("  failed: " + ex);
            Notice?.Invoke($"Couldn't switch to {profile.Name}", ex.Message, null);
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Puts back a captured setup. No prompt: it's a layout that was already working.</summary>
    public async Task Restore(SetupSnapshot snapshot, string title, string detail)
    {
        await _gate.WaitAsync();
        try
        {
            SwitchLog.Write($"Restore previous setup ({detail})");
            var previous = profiles.Profiles.FirstOrDefault(p => p.Id == profiles.LastAppliedId);
            var problems = new List<string>();
            if (snapshot.Displays != null)
            {
                // A TV the old setup used may have been turned off since; it has to be on before Windows can use it.
                var missing = snapshot.Displays.ActiveIds.Where(id => hub.Displays.All(d => d.Id != id)).ToList();
                if (missing.Count > 0) await PowerOnTvs(missing, null, null, problems);
            }
            SetAudio(snapshot.Output, "sound output", problems);
            SetAudio(snapshot.Input, "microphone", problems);
            if (snapshot.Displays != null && await ChangeDisplays(() => DisplayDetector.Restore(snapshot.Displays)) is { } error)
                problems.Add(error);
            else if (snapshot.Displays != null)
                problems.AddRange(await CheckRefreshRates(snapshot.Displays.ActiveIds));

            if (previous != null && snapshot.Displays != null)
                await PowerOffTvs(previous, keep: snapshot.Displays.ActiveIds, problems);

            profiles.LastAppliedId = null;
            hub.ScheduleRefresh();
            foreach (var p in problems) SwitchLog.Write("  problem: " + p);
            SwitchLog.Write(problems.Count == 0 ? "  done" : "  done with problems");
            Notice?.Invoke(title, problems.Count == 0 ? detail : string.Join(" ", problems), null);
        }
        catch (Exception ex)
        {
            SwitchLog.Write("  failed: " + ex);
            Notice?.Invoke("Couldn't put the previous setup back", ex.Message, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Runs a display change off the UI thread, spaced at least <see cref="DisplayCooldown"/> after the last one, and
    /// logs how long Windows took.
    /// </summary>
    async Task<string?> ChangeDisplays(Func<string?> change)
    {
        var wait = _lastDisplayChange + DisplayCooldown - DateTime.Now;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        var clock = Stopwatch.StartNew();
        var error = await Task.Run(change);
        _lastDisplayChange = DateTime.Now;
        SwitchLog.Write($"  displays changed in {clock.ElapsedMilliseconds} ms" + (error != null ? $": {error}" : ""));
        return error;
    }

    /// <summary>Compares each display's refresh rate after a change with the one it had last time it was on.</summary>
    async Task<List<string>> CheckRefreshRates(IEnumerable<string> ids)
    {
        await Task.Delay(1000); // Windows can report the old mode for a moment
        var now = await Task.Run(() => TryOrNull(DisplayDetector.ActiveModes)) ?? [];
        var problems = new List<string>();
        foreach (var id in ids)
        {
            if (!now.TryGetValue(id, out var mode) || !profiles.Data.DisplayModes.TryGetValue(id, out var expected)) continue;
            SwitchLog.Write($"  {NameOf(id)}: {mode.Width}×{mode.Height} at {mode.RefreshHz:0.##} Hz (last time {expected.RefreshHz:0.##} Hz)");
            if (Math.Abs(mode.RefreshHz - expected.RefreshHz) > 1)
                problems.Add($"{NameOf(id)} came up at {mode.RefreshHz:0} Hz instead of {expected.RefreshHz:0} Hz.");
        }
        return problems;
    }

    /// <summary>
    /// Called after every device scan: remembers each display's mode while it's on (so it can be restored), and which
    /// displays are LG TVs. Skipped while a switch is running or settling, so a mode Windows picked mid-switch isn't
    /// taken as the one to keep. Saved without notifying the pages: nothing they show changes.
    /// </summary>
    public void Observe()
    {
        if (IsBusy || DateTime.Now - _lastDisplayChange < SettleAfterDisplayChange) return;
        bool changed = false;
        var modes = TryOrNull(DisplayDetector.ActiveModes);
        if (modes != null)
            foreach (var (id, mode) in modes)
                if (!profiles.Data.DisplayModes.TryGetValue(id, out var known) || known != mode)
                {
                    profiles.Data.DisplayModes[id] = mode;
                    changed = true;
                }

        // An LG display with "TV" in its name gets an LG TV row in Profiles, to pair it. Someone with an LG TV whose
        // name doesn't say so can still pair it from there once it's listed; LG monitors aren't offered.
        foreach (var d in hub.Displays.Where(d => d.IsLg && d.Name.Contains("TV", StringComparison.OrdinalIgnoreCase)
                                                  && !profiles.Data.LgTvs.ContainsKey(d.Id)))
        {
            profiles.Data.LgTvs[d.Id] = new LgTvLink { Name = d.Name };
            changed = true;
        }
        // A paired TV that's on: note its network card for waking it later, if that isn't known yet.
        foreach (var (id, tv) in profiles.Data.LgTvs)
            if (tv.IsPaired && !tv.Macs.Any() && hub.Displays.Any(d => d.Id == id && d.IsActive) && tv.AddMacs([LgWebOs.MacOf(tv.Ip)]))
            {
                SwitchLog.Write($"LG {NameOf(id)}: learned MAC {tv.Mac}");
                changed = true;
            }
        // An LG entry on a display that's connected and isn't an LG is wrong (imported or stale data): drop it.
        foreach (var d in hub.Displays.Where(d => !d.IsLg && profiles.Data.LgTvs.ContainsKey(d.Id)).ToList())
        {
            profiles.Data.LgTvs.Remove(d.Id);
            changed = true;
        }
        if (changed) profiles.SaveQuietly();
    }

    /// <summary>
    /// Before a profile uses the TVs: notes how each one is (for Auto return), turns on the ones it should, sets
    /// their input, and waits for Windows to see the ones that were off. Whether a TV needs turning on is the TV's
    /// call (its power state), not whether Windows still lists it: a TV in standby can stay listed.
    /// </summary>
    async Task PowerOnTvs(IEnumerable<string> displayIds, Profile? profile, Profile? previous, List<string> problems)
    {
        var waitFor = new List<string>();
        foreach (var id in displayIds)
        {
            if (!IsLgTv(id, out var tv)) continue;
            var options = profile != null && profile.LgTv.TryGetValue(id, out var o) ? o : null;
            void Log(string s) => SwitchLog.Write($"  LG {NameOf(id)}: {s}");

            // Coming from a profile that already used this TV, it's showing the PC: what was there before is already noted.
            if (options is { Leaving: LgLeave.AutoReturn } && previous?.Displays.Contains(id) != true)
                await NoteReturn(tv, options, Log);

            // A restore turns a TV back on if the old setup used it; a profile only if it's set to.
            if (profile == null || options is { PowerOn: true })
            {
                bool detected = hub.Displays.Any(d => d.Id == id);
                Log("power on" + (detected ? " (Windows still lists it)" : ""));
                if (await LgWebOs.PowerOnAsync(tv, Log) is { } e) { problems.Add(e); continue; }
                if (!detected) waitFor.Add(id);
            }
            if (options is { HdmiInput: >= 1 and <= 4 }) await SetInput(id, tv, options.HdmiInput, problems);
        }
        profiles.SaveQuietly(); // MAC addresses learned, what to return to

        // Windows only sees the TV once it's on and the HDMI link is up, which can take a while.
        if (waitFor.Count == 0) return;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var seen = await Task.Run(() => TryOrNull(DisplayDetector.Detect)) ?? [];
            if (waitFor.All(id => seen.Any(d => d.Id == id))) { SwitchLog.Write($"  TV ready after {clock.ElapsedMilliseconds} ms"); return; }
            await Task.Delay(1000);
        }
        problems.Add($"{string.Join(", ", waitFor.Select(NameOf))} didn't come on within 30 seconds.");
    }

    /// <summary>
    /// Remembers what the TV is doing before IO Hub takes it over: "off", or the input or app it's showing. If it's
    /// already on this profile's input, the PC is on it already, so an earlier note is kept.
    /// </summary>
    async Task NoteReturn(LgTvLink tv, LgTvOptions options, Action<string> log)
    {
        var state = await LgWebOs.GetStateAsync(tv);
        var returnTo = state.Power is LgPower.Unreachable or LgPower.Standby ? "off" : state.AppId ?? "";
        if (options.HdmiInput is >= 1 and <= 4 && returnTo.Equals($"com.webos.app.hdmi{options.HdmiInput}", StringComparison.OrdinalIgnoreCase))
        {
            log($"already on HDMI {options.HdmiInput}; keeping \"{(tv.ReturnTo == "" ? "nothing" : tv.ReturnTo)}\" to return to");
            return;
        }
        tv.ReturnTo = returnTo;
        log($"was {state.Power} ({state.State ?? "no answer"}), showing {state.AppId ?? "nothing"}; will return to \"{(returnTo == "" ? "nothing" : returnTo)}\"");
    }

    /// <summary>TVs the previous profile used and the next one doesn't: left, turned off, or returned to how they were.</summary>
    async Task PowerOffTvs(Profile previous, IEnumerable<string> keep, List<string> problems)
    {
        var keeping = keep.ToHashSet();
        foreach (var (id, options) in previous.LgTv)
        {
            if (options.Leaving == LgLeave.Stay || keeping.Contains(id) || !previous.Displays.Contains(id)) continue;
            if (!IsLgTv(id, out var tv)) continue;
            if (options.Leaving == LgLeave.TurnOff)
            {
                SwitchLog.Write($"  LG {NameOf(id)}: power off");
                if (await LgWebOs.PowerOffAsync(tv) is { } e) problems.Add(e);
                continue;
            }
            var returnTo = tv.ReturnTo;
            if (returnTo == "") { SwitchLog.Write($"  LG {NameOf(id)}: nothing noted to return to, left as it is"); continue; }
            SwitchLog.Write($"  LG {NameOf(id)}: return to \"{returnTo}\"");
            if (await LgWebOs.ReturnAsync(tv, returnTo) is { } error) problems.Add(error);
            tv.ReturnTo = "";
            profiles.SaveQuietly();
        }
    }

    /// <summary>
    /// Switches a TV's input. A TV that's just woken up often ignores the first request, so this tries a few times.
    /// (The scripts IO Hub replaced sent the same input four times for the same reason.)
    /// </summary>
    async Task SetInput(string displayId, LgTvLink tv, int input, List<string> problems)
    {
        string? error = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            error = await LgWebOs.SetHdmiAsync(tv, input);
            SwitchLog.Write($"  LG {NameOf(displayId)}: HDMI {input}, attempt {attempt}" + (error != null ? $": {error}" : ""));
            if (error == null) return;
            await Task.Delay(1500);
        }
        problems.Add(error!);
    }

    /// <summary>
    /// Sets the input again once the displays have changed: a TV can jump to whichever input the PC's signal arrived
    /// on when Windows starts sending to it.
    /// </summary>
    async Task ReassertInputs(Profile profile, List<string> problems)
    {
        foreach (var (id, options) in profile.LgTv)
        {
            if (options.HdmiInput is < 1 or > 4 || !profile.Displays.Contains(id)) continue;
            if (!IsLgTv(id, out var tv)) continue;
            await SetInput(id, tv, options.HdmiInput, problems);
        }
    }

    /// <summary>
    /// A paired LG TV, and the display really is an LG: commands never go out for a display that's connected and
    /// isn't one, whatever the saved data says. (One that isn't detected may be a TV that's off.)
    /// </summary>
    bool IsLgTv(string displayId, out LgTvLink tv)
    {
        if (!profiles.Data.LgTvs.TryGetValue(displayId, out tv!) || !tv.IsPaired || tv.NotATv) return false;
        return hub.Displays.FirstOrDefault(d => d.Id == displayId) is not { IsLg: false };
    }

    string NameOf(string displayId) =>
        hub.Displays.FirstOrDefault(d => d.Id == displayId)?.Name
        ?? profiles.Data.Devices.FirstOrDefault(k => k.Id == displayId)?.Name ?? "A display";

    static void SetAudio(string? id, string what, List<string> problems)
    {
        if (id == null) return;
        try { AudioSwitcher.SetDefault(id); }
        catch (Exception ex) { problems.Add($"Couldn't set the {what}: {ex.Message}"); }
    }

    // Skip SetDisplayConfig when nothing would change: it blanks the screens for a moment even then.
    bool DisplaysMatch(Profile p)
    {
        var active = hub.Displays.Where(d => d.IsActive).Select(d => d.Id).ToHashSet();
        return active.SetEquals(p.Displays)
            && (p.PrimaryDisplay == null || hub.Displays.FirstOrDefault(d => d.IsPrimary)?.Id == p.PrimaryDisplay);
    }

    static T? TryOrNull<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }
}

/// <summary>
/// A plain-text record of every switch: what was asked, each step, how long the display change took and any
/// problem. Kept to about the last 200 KB in %LocalAppData%\IOHub\switch.log, for working out what happened.
/// </summary>
public static class SwitchLog
{
    static readonly string FilePath = Path.Combine(AppInfo.DataDir, "switch.log");
    const long MaxBytes = 200_000;
    static readonly object Lock = new();

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    // Keep the newer half.
                    var text = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, text[(text.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { } // logging must never break a switch
    }
}
