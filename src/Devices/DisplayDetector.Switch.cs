using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace IOHub.Devices;

/// <summary>An opaque copy of the live display layout, for putting it back exactly as it was.</summary>
public sealed class DisplaySnapshot
{
    internal object Payload { get; init; } = null!;
    /// <summary>The displays that were on.</summary>
    public IReadOnlyList<string> ActiveIds { get; init; } = [];
}

/// <summary>
/// A display's resolution and exact signal timing (which is what sets the refresh rate), as Windows reports it while
/// the display is on. Remembered so a display that's turned back on comes up the way it was, not at Windows' default.
/// </summary>
public sealed record DisplayMode(
    uint Width, uint Height, uint PixelFormat,
    ulong PixelRate, uint HSyncNum, uint HSyncDen, uint VSyncNum, uint VSyncDen,
    uint ActiveCx, uint ActiveCy, uint TotalCx, uint TotalCy, uint VideoStandard, uint ScanLineOrdering)
{
    [JsonIgnore] public double RefreshHz => VSyncDen == 0 ? 0 : (double)VSyncNum / VSyncDen;
}

/// <summary>Turning displays on and off and picking the main one, through SetDisplayConfig.</summary>
internal static partial class DisplayDetector
{
    sealed record Saved(Path[] Paths, Mode[] Modes);

    public static DisplaySnapshot Capture()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var names = new Dictionary<(uint, int, uint), string>();
        return new DisplaySnapshot
        {
            Payload = new Saved(paths, modes),
            ActiveIds = paths.Select(p => IdOf(p.targetInfo, names)).Distinct().ToList(),
        };
    }

    /// <summary>Puts a captured layout back. Returns an error message, or null.</summary>
    public static string? Restore(DisplaySnapshot snapshot)
    {
        var saved = (Saved)snapshot.Payload;
        int err = SetDisplayConfig((uint)saved.Paths.Length, saved.Paths, (uint)saved.Modes.Length, saved.Modes,
            SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
        return err == 0 ? null : $"Windows refused the old display layout (error {err}).";
    }

    /// <summary>The resolution and timing of every display that's on now.</summary>
    public static Dictionary<string, DisplayMode> ActiveModes()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var names = new Dictionary<(uint, int, uint), string>();
        var result = new Dictionary<string, DisplayMode>();
        foreach (var p in paths)
        {
            if (p.sourceInfo.modeInfoIdx >= modes.Length || p.targetInfo.modeInfoIdx >= modes.Length) continue;
            var s = modes[p.sourceInfo.modeInfoIdx];
            var t = modes[p.targetInfo.modeInfoIdx];
            if (s.infoType != ModeTypeSource || t.infoType != ModeTypeTarget) continue;
            var v = t.targetMode;
            result[IdOf(p.targetInfo, names)] = new DisplayMode(
                s.sourceMode.width, s.sourceMode.height, s.sourceMode.pixelFormat,
                v.pixelRate, v.hSyncFreq.Numerator, v.hSyncFreq.Denominator, v.vSyncFreq.Numerator, v.vSyncFreq.Denominator,
                v.activeSize.cx, v.activeSize.cy, v.totalSize.cx, v.totalSize.cy, v.videoStandard, v.scanLineOrdering);
        }
        return result;
    }

    /// <summary>
    /// Turns on exactly <paramref name="targetIds"/> (the rest go off) and makes <paramref name="primaryId"/> the
    /// main display. A display being turned on gets its <paramref name="remembered"/> mode (resolution and refresh
    /// rate) if there is one; without it Windows picks, which is often 60 Hz. With <paramref name="validateOnly"/>
    /// Windows checks the layout without changing anything. Returns an error message, or null on success.
    /// </summary>
    public static string? Apply(IReadOnlyCollection<string> targetIds, string? primaryId,
        IReadOnlyDictionary<string, DisplayMode>? remembered = null, bool validateOnly = false)
    {
        var err = ApplyLayout(targetIds, remembered, validateOnly);
        // A remembered mode the display no longer accepts (new cable, different TV setting): let Windows choose.
        if (err != 0 && remembered is { Count: > 0 }) err = ApplyLayout(targetIds, null, validateOnly);
        if (err == -1) return "None of this profile's displays are connected.";
        if (err != 0) return $"Windows refused this display layout (error {err}).";
        if (validateOnly || primaryId == null) return null;
        return MakePrimary(primaryId);
    }

    static int ApplyLayout(IReadOnlyCollection<string> targetIds, IReadOnlyDictionary<string, DisplayMode>? remembered, bool validateOnly)
    {
        var (all, modeArray) = Query(QdcAllPaths);
        var modes = modeArray.ToList();
        var names = new Dictionary<(uint, int, uint), string>();
        var wanted = targetIds.ToHashSet();
        var chosen = new List<Path>();
        var usedSources = new HashSet<(uint, int, uint)>();
        var placed = new HashSet<string>();
        int nextX = 0; // displays being turned on go to the right of the ones staying on

        // Displays that are already on keep their current path and mode.
        foreach (var p in all.Where(p => (p.flags & PathActive) != 0 && p.targetInfo.targetAvailable != 0))
        {
            var id = IdOf(p.targetInfo, names);
            if (!wanted.Contains(id) || !placed.Add(id)) continue;
            chosen.Add(p);
            usedSources.Add((p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id));
            if (p.sourceInfo.modeInfoIdx < modes.Count && modes[(int)p.sourceInfo.modeInfoIdx].infoType == ModeTypeSource)
            {
                var sm = modes[(int)p.sourceInfo.modeInfoIdx].sourceMode;
                nextX = Math.Max(nextX, sm.position.x + (int)sm.width);
            }
        }

        // Displays being turned on get a free source on their adapter, and their remembered mode if there is one.
        foreach (var id in wanted.Where(id => !placed.Contains(id)))
        {
            foreach (var p in all.Where(p => p.targetInfo.targetAvailable != 0 && IdOf(p.targetInfo, names) == id))
            {
                var source = (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id);
                if (usedSources.Contains(source)) continue;
                var np = p;
                np.flags = PathActive;
                if (remembered != null && remembered.TryGetValue(id, out var m))
                {
                    np.sourceInfo.modeInfoIdx = (uint)modes.Count;
                    modes.Add(new Mode
                    {
                        infoType = ModeTypeSource, id = p.sourceInfo.id, adapterId = p.sourceInfo.adapterId,
                        sourceMode = new SourceMode { width = m.Width, height = m.Height, pixelFormat = m.PixelFormat, position = new Point { x = nextX, y = 0 } },
                    });
                    np.targetInfo.modeInfoIdx = (uint)modes.Count;
                    modes.Add(new Mode
                    {
                        infoType = ModeTypeTarget, id = p.targetInfo.id, adapterId = p.targetInfo.adapterId,
                        targetMode = new VideoSignalInfo
                        {
                            pixelRate = m.PixelRate,
                            hSyncFreq = new Rational { Numerator = m.HSyncNum, Denominator = m.HSyncDen },
                            vSyncFreq = new Rational { Numerator = m.VSyncNum, Denominator = m.VSyncDen },
                            activeSize = new Region { cx = m.ActiveCx, cy = m.ActiveCy },
                            totalSize = new Region { cx = m.TotalCx, cy = m.TotalCy },
                            videoStandard = m.VideoStandard,
                            scanLineOrdering = m.ScanLineOrdering,
                        },
                    });
                    np.targetInfo.refreshRate = new Rational { Numerator = m.VSyncNum, Denominator = m.VSyncDen };
                    nextX += (int)m.Width;
                }
                else
                {
                    np.sourceInfo.modeInfoIdx = ModeIdxInvalid;
                    np.targetInfo.modeInfoIdx = ModeIdxInvalid;
                }
                chosen.Add(np);
                usedSources.Add(source);
                placed.Add(id);
                break;
            }
        }

        if (chosen.Count == 0) return -1;
        uint flags = SdcUseSuppliedDisplayConfig | SdcAllowChanges | (validateOnly ? SdcValidate : SdcApply | SdcSaveToDatabase);
        return SetDisplayConfig((uint)chosen.Count, [.. chosen], (uint)modes.Count, [.. modes], flags);
    }

    /// <summary>The main display is whichever sits at 0,0: shift every display so the chosen one does.</summary>
    static string? MakePrimary(string primaryId)
    {
        var names = new Dictionary<(uint, int, uint), string>();
        var (active, activeModes) = Query(QdcOnlyActivePaths);
        foreach (var p in active)
        {
            if (IdOf(p.targetInfo, names) != primaryId || p.sourceInfo.modeInfoIdx >= activeModes.Length) continue;
            var origin = activeModes[p.sourceInfo.modeInfoIdx].sourceMode.position;
            if (origin.x == 0 && origin.y == 0) return null;
            for (int i = 0; i < activeModes.Length; i++)
            {
                if (activeModes[i].infoType != ModeTypeSource) continue;
                activeModes[i].sourceMode.position.x -= origin.x;
                activeModes[i].sourceMode.position.y -= origin.y;
            }
            int err = SetDisplayConfig((uint)active.Length, active, (uint)activeModes.Length, activeModes,
                SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges);
            return err == 0 ? null : $"Displays switched, but Windows refused the new main display (error {err}).";
        }
        return null;
    }

    static string IdOf(PathTargetInfo t, Dictionary<(uint, int, uint), string> cache)
    {
        var key = (t.adapterId.LowPart, t.adapterId.HighPart, t.id);
        if (!cache.TryGetValue(key, out var id))
            cache[key] = id = GetTargetName(t.adapterId, t.id)?.monitorDevicePath is { Length: > 0 } path
                ? path : $"{t.adapterId.HighPart:X}{t.adapterId.LowPart:X8}:{t.id}";
        return id;
    }

    const uint QdcOnlyActivePaths = 0x2;
    const uint ModeTypeTarget = 2;
    const uint ModeIdxInvalid = 0xFFFFFFFF;
    const uint SdcUseSuppliedDisplayConfig = 0x20, SdcValidate = 0x40, SdcApply = 0x80, SdcSaveToDatabase = 0x200, SdcAllowChanges = 0x400;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPathArrayElements, [In] Path[] pathArray, uint numModeInfoArrayElements, [In] Mode[] modeInfoArray, uint flags);
}
