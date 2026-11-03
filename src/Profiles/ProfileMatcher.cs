using IOHub.Devices;

namespace IOHub.Profiles;

/// <summary>
/// Works out which profile the PC is in by comparing each profile with the live device state. Until profiles can be
/// applied this is the only notion of "active", and it stays correct when someone switches devices by hand.
/// </summary>
public static class ProfileMatcher
{
    public static Profile? Active(ProfileService profiles, DeviceHub hub) =>
        profiles.Profiles
            .Where(p => !p.ChangesNothing && Matches(p, hub))
            // The one last switched to wins if it still matches; otherwise the most specific match.
            .OrderByDescending(p => p.Id == profiles.LastAppliedId)
            .ThenByDescending(SectionsSet)
            .FirstOrDefault();

    public static bool Matches(Profile p, DeviceHub hub)
    {
        if (p.Displays.Count > 0)
        {
            var active = hub.Displays.Where(d => d.IsActive).Select(d => d.Id).ToHashSet();
            if (!active.SetEquals(p.Displays)) return false;
            if (p.PrimaryDisplay != null && hub.Displays.FirstOrDefault(d => d.IsPrimary)?.Id != p.PrimaryDisplay) return false;
        }
        if (p.AudioOutput != null && DefaultId(hub, AudioFlow.Output) != p.AudioOutput) return false;
        if (p.AudioInput != null && DefaultId(hub, AudioFlow.Input) != p.AudioInput) return false;
        // Main input has no Windows-side state to compare; it only has to be connected.
        if (p.MainInput != null && !AllInput(hub).Any(i => i.Id == p.MainInput)) return false;
        // Windows has no default camera either; it has to be connected and enabled.
        if (p.Camera != null && !hub.Cameras.Any(c => c.Id == p.Camera && c.IsEnabled)) return false;
        return true;
    }

    static int SectionsSet(Profile p) =>
        (p.Displays.Count > 0 ? 1 : 0) + (p.AudioOutput != null ? 1 : 0) + (p.AudioInput != null ? 1 : 0)
        + (p.MainInput != null ? 1 : 0) + (p.Camera != null ? 1 : 0);

    static string? DefaultId(DeviceHub hub, AudioFlow flow) =>
        hub.Audio.FirstOrDefault(a => a.Flow == flow && a.IsDefault)?.Id;

    public static IEnumerable<InputDevice> AllInput(DeviceHub hub) =>
        hub.Input.Controllers.Concat(hub.Input.Keyboards).Concat(hub.Input.Mice);
}
