using System.Runtime.InteropServices;

namespace IOHub.Integrations;

/// <summary>Rumbles Xbox controllers (anything XInput sees) through XInputSetState.</summary>
public static class Rumble
{
    static int _busy;

    /// <summary>The controller's Rumble function: how hard it buzzes.</summary>
    public static readonly (string Value, string Label, double Strength)[] Strengths =
        [("light", "Light", 0.35), ("medium", "Medium", 0.65), ("strong", "Strong", 1.0)];

    public static void Pulse(string? strength) =>
        Pulse(Strengths.FirstOrDefault(s => s.Value == strength).Strength is > 0 and var v ? v : 0.65);

    /// <summary>
    /// Two short buzzes on every connected controller, at <paramref name="strength"/> (0..1). Returns at once; a
    /// pattern already playing isn't stacked on.
    /// </summary>
    public static void Pulse(double strength)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        ushort level = (ushort)Math.Clamp(strength * ushort.MaxValue, 0, ushort.MaxValue);
        // Motors at low power sometimes barely spin up, so a light buzz felt different each time. Each buzz starts
        // with a short kick at 80% or more, then holds the chosen strength.
        ushort kick = Math.Max(level, (ushort)(0.8 * ushort.MaxValue));
        Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    SetAll(kick, kick);
                    await Task.Delay(45);
                    SetAll(level, level);
                    await Task.Delay(175);
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
