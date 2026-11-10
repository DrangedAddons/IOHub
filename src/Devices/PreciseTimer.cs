using System.Runtime.InteropServices;

namespace IOHub.Devices;

/// <summary>
/// A sleep that's accurate to about a millisecond without raising the system timer resolution (Thread.Sleep(8) can
/// take 15 ms). Falls back to Thread.Sleep on Windows before 10 1803.
/// </summary>
internal sealed class PreciseTimer : IDisposable
{
    readonly IntPtr _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);

    public void Wait(int ms)
    {
        if (_handle == IntPtr.Zero) { Thread.Sleep(ms); return; }
        long due = -ms * 10_000L; // relative, in 100 ns units
        if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) { Thread.Sleep(ms); return; }
        WaitForSingleObject(_handle, 1000);
    }

    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
