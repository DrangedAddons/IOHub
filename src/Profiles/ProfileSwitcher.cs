using IOHub.Devices;

namespace IOHub.Profiles;

/// <summary>How things were before a switch, so a trigger can put them back.</summary>
public sealed record SetupSnapshot(DisplaySnapshot? Displays, string? Output, string? Input);

/// <summary>
/// Applies profiles to the PC: audio defaults straight away, then the display layout. A display layout nobody has
/// confirmed yet is kept only if someone presses Keep; otherwise it reverts by itself, since a wrong layout can
/// leave every screen black. UI thread only.
/// </summary>
public sealed class ProfileSwitcher(DeviceHub hub, ProfileService profiles)
{
    bool _busy;

    /// <summary>Shows the "keep these displays?" prompt. True = keep. Set by the main window.</summary>
    public Func<Profile, Task<bool>>? ConfirmDisplays { get; set; }

    /// <summary>A short message for the on-screen toast: title, detail, and the profile's colour if there is one.</summary>
    public event Action<string, string, string?>? Notice;

    public bool IsBusy => _busy;

    public SetupSnapshot Capture() => new(
        TryOrNull(DisplayDetector.Capture),
        hub.Audio.FirstOrDefault(a => a.Flow == AudioFlow.Output && a.IsDefault)?.Id,
        hub.Audio.FirstOrDefault(a => a.Flow == AudioFlow.Input && a.IsDefault)?.Id);

    /// <summary>Switches to <paramref name="profile"/>. Returns false if it was refused, undone or partly failed.</summary>
    public async Task<bool> SwitchTo(Profile profile, string? because = null)
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            var problems = new List<string>();
            SetAudio(profile.AudioOutput, "sound output", problems);
            SetAudio(profile.AudioInput, "microphone", problems);

            if (profile.Displays.Count > 0 && !DisplaysMatch(profile))
            {
                var before = TryOrNull(DisplayDetector.Capture);
                var error = await Task.Run(() => DisplayDetector.Apply(profile.Displays, profile.PrimaryDisplay));
                if (error != null) problems.Add(error);
                else if (!profile.DisplaysConfirmed && ConfirmDisplays != null)
                {
                    await Task.Delay(1500); // let the screens come up before asking
                    if (await ConfirmDisplays(profile))
                    {
                        profile.DisplaysConfirmed = true;
                        profiles.Save();
                    }
                    else
                    {
                        if (before != null) await Task.Run(() => DisplayDetector.Restore(before));
                        hub.ScheduleRefresh();
                        Notice?.Invoke("Display change undone", $"{profile.Name}'s displays weren't kept.", null);
                        return false;
                    }
                }
            }

            profiles.LastAppliedId = profile.Id;
            hub.ScheduleRefresh();
            Notice?.Invoke(
                problems.Count == 0 ? $"Switched to {profile.Name}" : $"Switched to {profile.Name}, with problems",
                problems.Count == 0 ? because ?? "" : string.Join(" ", problems),
                profile.Color);
            return problems.Count == 0;
        }
        finally { _busy = false; }
    }

    /// <summary>Puts back a captured setup. No prompt: it's a layout that was already working.</summary>
    public async Task Restore(SetupSnapshot snapshot, string title, string detail)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var problems = new List<string>();
            SetAudio(snapshot.Output, "sound output", problems);
            SetAudio(snapshot.Input, "microphone", problems);
            if (snapshot.Displays != null && await Task.Run(() => DisplayDetector.Restore(snapshot.Displays)) is { } error)
                problems.Add(error);
            profiles.LastAppliedId = null;
            hub.ScheduleRefresh();
            Notice?.Invoke(title, problems.Count == 0 ? detail : string.Join(" ", problems), null);
        }
        finally { _busy = false; }
    }

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
