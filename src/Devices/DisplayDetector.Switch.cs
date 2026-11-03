using System.Runtime.InteropServices;

namespace IOHub.Devices;

/// <summary>An opaque copy of the live display layout, for putting it back exactly as it was.</summary>
public sealed class DisplaySnapshot
{
    internal object Payload { get; init; } = null!;
}

/// <summary>Turning displays on and off and picking the main one, through SetDisplayConfig.</summary>
internal static partial class DisplayDetector
{
    sealed record Saved(Path[] Paths, Mode[] Modes);

    public static DisplaySnapshot Capture()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        return new DisplaySnapshot { Payload = new Saved(paths, modes) };
    }

    /// <summary>Puts a captured layout back. Returns an error message, or null.</summary>
    public static string? Restore(DisplaySnapshot snapshot)
    {
        var saved = (Saved)snapshot.Payload;
        int err = SetDisplayConfig((uint)saved.Paths.Length, saved.Paths, (uint)saved.Modes.Length, saved.Modes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        return err == 0 ? null : $"Windows refused the old display layout (error {err}).";
    }

    /// <summary>
    /// Turns on exactly <paramref name="targetIds"/> (the rest go off) and makes <paramref name="primaryId"/> the
    /// main display. With <paramref name="validateOnly"/> Windows checks the layout without changing anything.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Apply(IReadOnlyCollection<string> targetIds, string? primaryId, bool validateOnly = false)
    {
        var (all, modes) = Query(QdcAllPaths);
        var names = new Dictionary<(uint, int, uint), string>();
        string IdOf(PathTargetInfo t)
        {
            var key = (t.adapterId.LowPart, t.adapterId.HighPart, t.id);
            if (!names.TryGetValue(key, out var id))
                names[key] = id = GetTargetName(t.adapterId, t.id)?.monitorDevicePath is { Length: > 0 } path
                    ? path : $"{t.adapterId.HighPart:X}{t.adapterId.LowPart:X8}:{t.id}";
            return id;
        }

        var wanted = targetIds.ToHashSet();
        var chosen = new List<Path>();
        var usedSources = new HashSet<(uint, int, uint)>();
        var placed = new HashSet<string>();

        // Displays that are already on keep their current path and mode.
        foreach (var p in all.Where(p => (p.flags & PathActive) != 0 && p.targetInfo.targetAvailable != 0))
        {
            var id = IdOf(p.targetInfo);
            if (!wanted.Contains(id) || !placed.Add(id)) continue;
            chosen.Add(p);
            usedSources.Add((p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id));
        }

        // Displays being turned on get a free source on their adapter and let Windows pick the mode.
        foreach (var id in wanted.Where(id => !placed.Contains(id)))
        {
            foreach (var p in all.Where(p => p.targetInfo.targetAvailable != 0 && IdOf(p.targetInfo) == id))
            {
                var source = (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id);
                if (usedSources.Contains(source)) continue;
                var np = p;
                np.flags = PathActive;
                np.sourceInfo.modeInfoIdx = ModeIdxInvalid;
                np.targetInfo.modeInfoIdx = ModeIdxInvalid;
                chosen.Add(np);
                usedSources.Add(source);
                placed.Add(id);
                break;
            }
        }

        if (chosen.Count == 0) return "None of this profile's displays are connected.";

        uint flags = SdcUseSuppliedDisplayConfig | SdcAllowChanges | (validateOnly ? SdcValidate : SdcApply | SdcSaveToDatabase);
        int err = SetDisplayConfig((uint)chosen.Count, [.. chosen], (uint)modes.Length, modes, flags);
        if (err != 0) return $"Windows refused this display layout (error {err}).";
        if (validateOnly || primaryId == null) return null;

        // The main display is whichever sits at 0,0: shift every display so the chosen one does.
        var (active, activeModes) = Query(QdcOnlyActivePaths);
        foreach (var p in active)
        {
            if (IdOf(p.targetInfo) != primaryId || p.sourceInfo.modeInfoIdx >= activeModes.Length) continue;
            var origin = activeModes[p.sourceInfo.modeInfoIdx].sourceMode.position;
            if (origin.x == 0 && origin.y == 0) return null;
            for (int i = 0; i < activeModes.Length; i++)
            {
                if (activeModes[i].infoType != ModeTypeSource) continue;
                activeModes[i].sourceMode.position.x -= origin.x;
                activeModes[i].sourceMode.position.y -= origin.y;
            }
            err = SetDisplayConfig((uint)active.Length, active, (uint)activeModes.Length, activeModes,
                SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
            return err == 0 ? null : $"Displays switched, but Windows refused the new main display (error {err}).";
        }
        return null;
    }

    const uint QdcOnlyActivePaths = 0x2;
    const uint ModeIdxInvalid = 0xFFFFFFFF;
    const uint SdcUseSuppliedDisplayConfig = 0x20, SdcValidate = 0x40, SdcApply = 0x80, SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPathArrayElements, [In] Path[] pathArray, uint numModeInfoArrayElements, [In] Mode[] modeInfoArray, uint flags);
}
