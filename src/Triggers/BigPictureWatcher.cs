using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace IOHub.Triggers;

/// <summary>
/// Watches for Steam Big Picture: a top-level window titled "Steam Big Picture…" owned by steamwebhelper.exe or
/// steam.exe. Window events give a near-instant reaction and a slow poll covers anything they miss. Big Picture
/// sometimes destroys and recreates its window, so "closed" waits a moment and checks again before it counts.
/// UI thread only.
/// </summary>
public sealed class BigPictureWatcher : IDisposable
{
    const int ClosedGraceMs = 2000;

    readonly Dispatcher _ui;
    readonly DispatcherTimer _poll;
    readonly DispatcherTimer _settle;
    readonly WinEventProc _proc; // held so the delegate isn't collected while Windows still calls it
    readonly List<IntPtr> _hooks = [];
    readonly Dictionary<uint, bool> _steamPids = [];
    DateTime _closedSince = DateTime.MaxValue;
    DateTime _ignoreClosesUntil = DateTime.MinValue;

    public bool IsOpen { get; private set; }

    /// <summary>Raised on the UI thread with true when Big Picture opens, false when it has closed.</summary>
    public event Action<bool>? Changed;

    public BigPictureWatcher(Dispatcher ui)
    {
        _ui = ui;
        _proc = OnWinEvent;
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) => Check();
        _settle = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(150) };
        _settle.Tick += (_, _) => { _settle.Stop(); Check(); };
    }

    public bool IsRunning => _poll.IsEnabled;

    public void Start()
    {
        if (IsRunning) return;
        // Already open when we start: that's the current state, not a change to act on.
        IsOpen = Find() != IntPtr.Zero;
        _closedSince = DateTime.MaxValue;
        foreach (var (from, to) in new[] { (EventObjectDestroy, EventObjectHide), (EventObjectNameChange, EventObjectNameChange) })
            _hooks.Add(SetWinEventHook(from, to, IntPtr.Zero, _proc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess));
        _poll.Start();
    }

    public void Stop()
    {
        foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _hooks.Clear();
        _poll.Stop();
        _settle.Stop();
    }

    /// <summary>Ignore Big Picture closing for a while, e.g. while we close and reopen it on purpose.</summary>
    public void IgnoreClosesFor(TimeSpan span) => _ignoreClosesUntil = DateTime.Now + span;

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only whole windows; Chromium raises these for every control inside Steam's UI too.
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (!_settle.IsEnabled) _settle.Start();
    }

    void Check()
    {
        bool open = Find() != IntPtr.Zero;
        if (open)
        {
            _closedSince = DateTime.MaxValue;
            if (!IsOpen) { IsOpen = true; Changed?.Invoke(true); }
            return;
        }
        if (!IsOpen) return;

        var now = DateTime.Now;
        if (now < _ignoreClosesUntil) return;
        if (_closedSince == DateTime.MaxValue)
        {
            _closedSince = now;
            // Look again once the grace period is up, in case no window event comes.
            _ui.InvokeAsync(async () => { await Task.Delay(ClosedGraceMs + 100); Check(); });
            return;
        }
        if ((now - _closedSince).TotalMilliseconds < ClosedGraceMs) return;
        _closedSince = DateTime.MaxValue;
        IsOpen = false;
        Changed?.Invoke(false);
    }

    /// <summary>The Big Picture window, or zero.</summary>
    public IntPtr Find()
    {
        IntPtr found = IntPtr.Zero;
        var title = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            title.Clear();
            // InternalGetWindowText doesn't message the window, so a hung app can't stall this.
            InternalGetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0 || title.ToString().IndexOf("Big Picture", StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (!IsSteam(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    bool IsSteam(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        if (_steamPids.TryGetValue(pid, out var known)) return known;
        bool steam = false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var path = new StringBuilder(1024);
                int size = path.Capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref size))
                {
                    var exe = path.ToString();
                    steam = exe.EndsWith("\\steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
                        || exe.EndsWith("\\steam.exe", StringComparison.OrdinalIgnoreCase);
                }
            }
            finally { CloseHandle(handle); }
        }
        if (_steamPids.Count > 256) _steamPids.Clear(); // pids get reused; keep the cache small
        _steamPids[pid] = steam;
        return steam;
    }

    public void Dispose() => Stop();

    const uint EventObjectDestroy = 0x8001, EventObjectHide = 0x8003, EventObjectNameChange = 0x800C;
    const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    const uint ProcessQueryLimitedInformation = 0x1000;

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
