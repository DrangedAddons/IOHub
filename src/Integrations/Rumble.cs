using System.Runtime.InteropServices;

namespace IOHub.Integrations;

/// <summary>
/// Rumbles Xbox controllers (anything XInput sees) through XInputSetState, in a pattern, at a strength, for a length.
/// The three are set once, on the Devices page (AppSettings.Rumble, "double;medium;normal"), and every trigger that
/// rumbles plays that. Trigger values from before patterns ("medium") were a strength alone.
/// </summary>
public static class Rumble
{
    static int _busy;

    /// <summary>What the Rumble function plays. Set from AppSettings at startup and by the Devices page.</summary>
    public static Setting Current { get; set; } = Setting.Default;

    public static void Play() => Play(Current);

    public static readonly (string Value, string Label)[] Patterns =
    [
        ("single", "Single"), ("double", "Double"), ("triple", "Triple"),
        ("long", "Long buzz"), ("heartbeat", "Heartbeat"), ("rising", "Rising"), ("flutter", "Flutter"),
    ];

    public static readonly (string Value, string Label, double Strength)[] Strengths =
        [("light", "Light", 0.35), ("medium", "Medium", 0.65), ("strong", "Strong", 1.0)];

    /// <summary>How long each buzz lasts compared with Normal. The gaps between buzzes stay the same.</summary>
    public static readonly (string Value, string Label, double Scale)[] Lengths =
        [("short", "Short", 0.6), ("normal", "Normal", 1.0), ("long", "Long", 1.8)];

    public readonly record struct Setting(string Pattern, string Strength, string Length)
    {
        public static readonly Setting Default = new("double", "medium", "normal");

        public static Setting Parse(string? value)
        {
            var parts = (value ?? "").Split(';');
            if (parts.Length == 1)   // before patterns: a strength alone
                return Default with { Strength = Known(Strengths.Select(s => s.Value), parts[0], Default.Strength) };
            return new(
                Known(Patterns.Select(p => p.Value), parts[0], Default.Pattern),
                Known(Strengths.Select(s => s.Value), parts.ElementAtOrDefault(1), Default.Strength),
                Known(Lengths.Select(l => l.Value), parts.ElementAtOrDefault(2), Default.Length));
        }

        static string Known(IEnumerable<string> values, string? value, string fallback) =>
            value != null && values.Contains(value) ? value : fallback;

        public override string ToString() => $"{Pattern};{Strength};{Length}";

        /// <summary>"Double, medium" (or "Double, medium, long" when the length isn't normal).</summary>
        public string Describe()
        {
            var (pattern, strength, length) = this;
            var p = Patterns.First(x => x.Value == pattern).Label;
            var s = Strengths.First(x => x.Value == strength).Label.ToLowerInvariant();
            return length == "normal" ? $"{p}, {s}" : $"{p}, {s}, {Lengths.First(x => x.Value == length).Label.ToLowerInvariant()}";
        }
    }

    /// <summary>Plays <paramref name="s"/> on every connected controller. Returns at once; one already playing isn't stacked on.</summary>
    public static void Play(Setting s)
    {
        double level = Strengths.First(x => x.Value == s.Strength).Strength;
        double scale = Lengths.First(x => x.Value == s.Length).Scale;
        Play(Steps(s.Pattern, level, scale));
    }

    /// <summary>
    /// A pattern as steps: how long, and how strong (0 = a gap). Buzz times are Normal length and scale with the
    /// length; gaps don't. A step with Kick starts with a short burst at 80% or more, because motors at low power
    /// sometimes barely spin up and a light buzz then felt different each time.
    /// </summary>
    static List<(int Ms, double Level, bool Kick)> Steps(string pattern, double level, double scale)
    {
        var steps = new List<(int, double, bool)>();
        void Buzz(int ms, double factor = 1, bool kick = true) => steps.Add(((int)Math.Round(ms * scale), level * factor, kick));
        void Gap(int ms) => steps.Add((ms, 0, false));
        switch (pattern)
        {
            case "single": Buzz(260); break;
            case "triple": Buzz(150); Gap(110); Buzz(150); Gap(110); Buzz(150); break;
            case "long": Buzz(750); break;
            // A soft beat then a firm one, like a pulse.
            case "heartbeat": Buzz(110, 0.7); Gap(90); Buzz(200); break;
            // From faint to the chosen strength over half a second; no kick, so it really starts soft.
            case "rising":
                for (int i = 0; i < 10; i++) Buzz(60, 0.25 + 0.75 * i / 9, kick: i == 0);
                break;
            // Quick taps.
            case "flutter":
                for (int i = 0; i < 6; i++) { if (i > 0) Gap(55); Buzz(70); }
                break;
            default: Buzz(175); Gap(130); Buzz(175); break;   // double
        }
        return steps;
    }

    static void Play(List<(int Ms, double Level, bool Kick)> steps)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                foreach (var (ms, level, kick) in steps)
                {
                    ushort v = (ushort)Math.Clamp(level * ushort.MaxValue, 0, ushort.MaxValue);
                    int rest = ms;
                    if (kick && v > 0)
                    {
                        ushort k = Math.Max(v, (ushort)(0.8 * ushort.MaxValue));
                        int kickMs = Math.Min(45, ms);
                        SetAll(k, k);
                        await Task.Delay(kickMs);
                        rest -= kickMs;
                    }
                    SetAll(v, v);
                    if (rest > 0) await Task.Delay(rest);
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
