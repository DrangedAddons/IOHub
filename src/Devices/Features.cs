using IOHub.Profiles;
using IOHub.Triggers;
using Trigger = IOHub.Triggers.Trigger;

namespace IOHub.Devices;

public enum FeatureKind
{
    /// <summary>On or off: toggle, turn on, turn off (and "while held").</summary>
    Toggle,
    /// <summary>One of a list: set to a value.</summary>
    Choice,
    /// <summary>Something you do: run it.</summary>
    Action,
}

public sealed record FeatureChoice(string Value, string Label);

/// <summary>
/// One thing about one device that a trigger (or a button on the Devices page) can change: mouse mode on an Xbox
/// controller, power on an LG TV, mute on a microphone. Built fresh from what's connected by <see cref="FeatureService"/>.
/// </summary>
public sealed class DeviceFeature
{
    /// <summary>Menu group: "Controllers", "TVs", "Sound output", "Microphones".</summary>
    public required string Section { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required FeatureKind Kind { get; init; }
    public IReadOnlyList<FeatureChoice> Choices { get; init; } = [];
    /// <summary>On/off right now (for an action: already done), if it can be read cheaply; null when unknown.</summary>
    public Func<bool?> State { get; init; } = () => null;
    /// <summary>Does it. Returns an error message, or null.</summary>
    public required Func<FeatureAction, string?, Task<string?>> Run { get; init; }

    /// <summary>The actions offered for it, with their labels.</summary>
    public IEnumerable<(FeatureAction Action, string? Value, string Label)> Actions => Kind switch
    {
        FeatureKind.Toggle => [(FeatureAction.Toggle, null, "Toggle"), (FeatureAction.On, null, OnLabel), (FeatureAction.Off, null, OffLabel)],
        FeatureKind.Choice => Choices.Select(c => (FeatureAction.Set, (string?)c.Value, c.Label)),
        _ => [(FeatureAction.Run, null, Title)],
    };

    /// <summary>
    /// For "while held": how it is now, to put back when let go. Defaults to the on/off <see cref="State"/>; features
    /// whose state takes asking (a TV) or isn't on/off (an input, the default device) give their own.
    /// </summary>
    public Func<Task<string?>>? Capture { get; init; }
    /// <summary>Puts back what <see cref="Capture"/> noted. Defaults to turning on or off.</summary>
    public Func<string, Task<string?>>? Restore { get; init; }

    public async Task<string?> CaptureAsync() =>
        Capture != null ? await Capture() : State() switch { true => "on", false => "off", _ => null };

    public Task<string?> RestoreAsync(string noted) =>
        Restore != null ? Restore(noted) : Run(noted == "on" ? FeatureAction.On : FeatureAction.Off, null);

    public string OnLabel { get; init; } = "Turn on";
    public string OffLabel { get; init; } = "Turn off";

    public string Describe(FeatureAction action, string? value) => action switch
    {
        _ when Kind == FeatureKind.Action => Title,
        FeatureAction.Toggle => $"{Title}: toggle",
        FeatureAction.On => $"{Title}: {OnLabel.ToLowerInvariant()}",
        FeatureAction.Off => $"{Title}: {OffLabel.ToLowerInvariant()}",
        FeatureAction.Set => $"{Title}: {Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value}",
        _ => Title,
    };
}

/// <summary>
/// Lists every changeable device feature IO Hub knows about right now, and runs them. New features are one entry
/// here: the Triggers and Devices pages pick them up from this list.
/// </summary>
public sealed class FeatureService(DeviceHub hub, ProfileService profiles, ProfileSwitcher switcher, ControllerMouse mouse)
{
    /// <summary>Mouse mode belongs to Xbox controllers as a group: any of them drives the pointer.</summary>
    public const string ControllersId = "xinput";

    public ControllerMouse Mouse => mouse;

    public List<DeviceFeature> All()
    {
        var list = new List<DeviceFeature>();

        // Xbox controllers.
        var pads = hub.Input.Controllers.Where(c => c.Source.StartsWith("XInput")).ToList();
        list.Add(new DeviceFeature
        {
            Section = "Controllers", DeviceId = ControllersId,
            DeviceName = pads.Count == 1 ? pads[0].Name : "Xbox controllers",
            Id = "mouse", Title = "Mouse mode", Kind = FeatureKind.Toggle,
            State = () => mouse.IsOn,
            Run = (action, _) => Task.FromResult(mouse.Set(action switch
            {
                FeatureAction.On => true,
                FeatureAction.Off => false,
                _ => !mouse.IsOn,
            })),
        });
        // A buzz on every controller, e.g. for the WoW fishing bite trigger. Its pattern, strength and length are set on
        // the Devices page (Rumble settings), so a trigger's own action and value don't matter: old ones were "Set medium".
        list.Add(new DeviceFeature
        {
            Section = "Controllers", DeviceId = ControllersId,
            DeviceName = pads.Count == 1 ? pads[0].Name : "Xbox controllers",
            Id = "rumble", Title = "Rumble", Kind = FeatureKind.Action,
            Run = (_, _) => { Integrations.Rumble.Play(); return Task.FromResult<string?>(null); },
        });

        // Paired LG TVs, connected or not (a TV that's off isn't detected, and that's when you'd turn it on).
        foreach (var (displayId, tv) in profiles.Data.LgTvs)
        {
            if (!tv.IsPaired || tv.NotATv) continue;
            if (hub.Displays.FirstOrDefault(d => d.Id == displayId) is { IsLg: false }) continue;
            string name = tv.Name != "" ? tv.Name : "LG TV";
            list.Add(new DeviceFeature
            {
                Section = "TVs", DeviceId = displayId, DeviceName = name,
                Id = "power", Title = "Power", Kind = FeatureKind.Toggle,
                Capture = async () => (await LgWebOs.GetStateAsync(tv)).Power is LgPower.On or LgPower.ScreenOff ? "on" : "off",
                Run = async (action, _) =>
                {
                    bool on = action switch
                    {
                        FeatureAction.On => true,
                        FeatureAction.Off => false,
                        _ => (await LgWebOs.GetStateAsync(tv)).Power is not (LgPower.On or LgPower.ScreenOff),
                    };
                    void Log(string s) => SwitchLog.Write($"LG {name}: {s}");
                    if (!on) { Log("power off (trigger)"); return await LgWebOs.PowerOffAsync(tv); }
                    switcher.StopTurnOff(displayId);
                    Log("power on (trigger)");
                    var error = await LgWebOs.PowerOnAsync(tv, Log);
                    profiles.SaveQuietly(); // MAC addresses learned
                    return error;
                },
            });
            list.Add(new DeviceFeature
            {
                Section = "TVs", DeviceId = displayId, DeviceName = name,
                Id = "input", Title = "Input", Kind = FeatureKind.Choice,
                Choices = [.. Enumerable.Range(1, 4).Select(n => new FeatureChoice(n.ToString(), $"HDMI {n}"))],
                // What it was showing: an input, or an app.
                Capture = async () => (await LgWebOs.GetStateAsync(tv)).AppId,
                Restore = app => LgWebOs.ShowAppAsync(tv, app),
                Run = (_, value) => LgWebOs.SetHdmiAsync(tv, int.TryParse(value, out var n) ? n : 1),
            });
        }

        // Sound devices that are plugged in and enabled.
        foreach (var a in hub.Audio.Where(a => a.IsActive))
        {
            string section = a.Flow == AudioFlow.Output ? "Sound output" : "Microphones";
            string id = a.Id;
            var flow = a.Flow;
            list.Add(new DeviceFeature
            {
                Section = section, DeviceId = id, DeviceName = a.Name,
                Id = "default", Title = "Make it the default", Kind = FeatureKind.Action,
                State = () => a.IsDefault, // for an action: already done
                // Which device was the default, to go back to.
                Capture = () => Task.FromResult(hub.Audio.FirstOrDefault(x => x.Flow == flow && x.IsDefault)?.Id),
                Restore = previous => Task.FromResult(hub.MakeDefaultAudio(previous)),
                Run = (_, _) => Task.FromResult(hub.MakeDefaultAudio(id)),
            });
            list.Add(new DeviceFeature
            {
                Section = section, DeviceId = id, DeviceName = a.Name,
                Id = "mute", Title = "Mute", Kind = FeatureKind.Toggle, OnLabel = "Mute", OffLabel = "Unmute",
                State = () => hub.IsMuted(id),
                Run = (action, _) =>
                {
                    bool mute = action switch
                    {
                        FeatureAction.On => true,
                        FeatureAction.Off => false,
                        _ => hub.IsMuted(id) != true,
                    };
                    return Task.FromResult(hub.SetMute(id, mute));
                },
            });
        }
        return list;
    }

    public DeviceFeature? Find(string? deviceId, string? featureId) =>
        deviceId == null || featureId == null ? null : All().FirstOrDefault(f => f.DeviceId == deviceId && f.Id == featureId);

    /// <summary>"Mouse mode: toggle", or with the device: "LG TV SSCR2 · Power: turn off".</summary>
    public string Describe(Trigger t, bool withDevice)
    {
        if (t.Feature == null) return "Nothing picked yet";
        var f = Find(t.DeviceId, t.Feature);
        string what = f?.Describe(t.Action, t.Value) ?? $"{t.Feature}";
        if (t.Gesture == GestureKind.WhileHeld) what += " while held";
        string device = f?.DeviceName ?? t.DeviceName ?? "a device";
        return withDevice ? $"{device} · {what}" : what;
    }

    /// <summary>For "while held": how the trigger's feature is now, to put back when let go. Null if it can't be told.</summary>
    public async Task<string?> CaptureAsync(Trigger t)
    {
        var f = Find(t.DeviceId, t.Feature);
        try { return f == null ? null : await f.CaptureAsync(); }
        catch { return null; }
    }

    /// <summary>Puts back what <see cref="CaptureAsync"/> noted. Returns an error, or null.</summary>
    public async Task<string?> RestoreAsync(Trigger t, string noted)
    {
        var f = Find(t.DeviceId, t.Feature);
        if (f == null) return $"{t.DeviceName ?? "That device"} isn't connected.";
        try { return await f.RestoreAsync(noted); }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Runs a device trigger's feature with <paramref name="action"/>. Returns an error, or null.</summary>
    public async Task<string?> RunAsync(Trigger t, FeatureAction action)
    {
        var f = Find(t.DeviceId, t.Feature);
        if (f == null) return $"{t.DeviceName ?? "That device"} isn't connected.";
        try { return await f.Run(action, t.Value); }
        catch (Exception ex) { return ex.Message; }
    }
}
