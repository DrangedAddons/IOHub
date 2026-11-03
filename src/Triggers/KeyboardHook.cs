using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IOHub.Triggers;

/// <summary>
/// A low-level keyboard hook (WH_KEYBOARD_LL), the same kind Discord and OBS use for global hotkeys. It only
/// reads keys, except when the callback asks to keep one from other apps. Windows calls it on the thread that
/// installed it, so install it on the UI thread; the callback has to return quickly.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>(virtual key, is down, timestamp ms) → true to swallow the key.</summary>
    readonly Func<int, bool, long, bool> _onKey;
    readonly HookProc _proc; // held so the delegate isn't collected while Windows still calls it
    IntPtr _hook;

    public KeyboardHook(Func<int, bool, long, bool> onKey)
    {
        _onKey = onKey;
        _proc = Callback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int msg = (int)wParam;
            bool down = msg is WmKeyDown or WmSysKeyDown;
            bool up = msg is WmKeyUp or WmSysKeyUp;
            if (down || up)
            {
                bool swallow;
                try { swallow = _onKey((int)info.vkCode, down, info.time); }
                catch { swallow = false; } // never let a bug here eat keystrokes
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();

    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct { public uint vkCode; public uint scanCode; public uint flags; public uint time; public UIntPtr extraInfo; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
