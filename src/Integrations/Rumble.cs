using System.Runtime.InteropServices;

namespace IOHub.Integrations;

/// <summary>Rumbles Xbox controllers (anything XInput sees) through XInputSetState.</summary>
public static class Rumble
{
    static int _busy;

    /// <summary>
    /// Two short buzzes on every connected controller, at <paramref name="strength"/> (0..1). Returns at once; a
    /// pattern already playing isn't stacked on.
    /// </summary>
    public static void Pulse(double strength)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        ushort level = (ushort)Math.Clamp(strength * ushort.MaxValue, 0, ushort.MaxValue);
        Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    SetAll(level, level);
                    await Task.Delay(220);
                    SetAll(0, 0);
                    if (i == 0) await Task.Delay(130);
                }
            }
            finally
            {
                SetAll(0, 0);
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    // Empty slots answer ERROR_DEVICE_NOT_CONNECTED straight away, so all four are tried.
    static void SetAll(ushort left, ushort right)
    {
        var v = new XInputVibration { Left = left, Right = right };
        for (uint slot = 0; slot < 4; slot++) XInputSetState(slot, ref v);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputVibration
    {
        public ushort Left, Right;
    }

    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint slot, ref XInputVibration vibration);
}
