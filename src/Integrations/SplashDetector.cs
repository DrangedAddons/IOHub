namespace IOHub.Integrations;

/// <summary>
/// Spots the fishing bite in World of Warcraft's sound level. The bite is a splash that comes after you've stood
/// waiting in near silence, so a hit needs all of these:
/// - the level jumps <see cref="ThresholdDb"/> above the recent background (a slow-moving baseline),
/// - it stays up for two samples or more (a click or a single footstep doesn't),
/// - the game was quiet for <see cref="QuietMs"/> before the jump (the cast and the bobber landing come in a burst,
///   and fights or towns are never quiet that long),
/// - the last hit was more than <see cref="CooldownMs"/> ago (looting and recasting are noisy).
/// Pure logic, fed one peak level per sample, so it can be tested without sound.
/// </summary>
public sealed class SplashDetector
{
    /// <summary>How far above the background a splash must be. Lower = more sensitive.</summary>
    public float ThresholdDb { get; set; } = 12;
    public int QuietMs { get; set; } = 1500;
    public int CooldownMs { get; set; } = 4000;

    /// <summary>Anything quieter than this counts as silence, however quiet the background is.</summary>
    const float FloorDb = -48;

    float _baseline = -70;
    long _lastBusy = long.MinValue / 2, _lastHit = long.MinValue / 2, _burstQuietFor;
    int _loudRun;
    bool _started;

    /// <summary>The last level fed, 0..1 (for the page's meter).</summary>
    public float Level { get; private set; }
    public float BaselineDb => _baseline;

    /// <summary>Feeds one sample: the peak level (0..1) at <paramref name="nowMs"/>. True when that's a bite.</summary>
    public bool Feed(float peak, long nowMs)
    {
        Level = peak;
        float db = peak > 0.000_01f ? 20 * MathF.Log10(peak) : -100;
        // Starting up (or WoW just came back): take what's playing as the background, and count it as busy, so the
        // sound already going on when listening starts is never a splash.
        if (!_started)
        {
            _started = true;
            _baseline = MathF.Max(db, -90);
            _lastBusy = nowMs;
        }
        float reference = MathF.Max(_baseline, FloorDb);
        bool loud = db > reference + ThresholdDb;
        bool busy = db > reference + ThresholdDb / 2;

        bool hit = false;
        if (loud)
        {
            // How long it was quiet before this burst began, noted on its first loud sample.
            if (_loudRun == 0) _burstQuietFor = nowMs - _lastBusy;
            _loudRun++;
            if (_loudRun == 2 && _burstQuietFor >= QuietMs && nowMs - _lastHit >= CooldownMs)
            {
                _lastHit = nowMs;
                hit = true;
            }
        }
        else _loudRun = 0;

        // The background follows quiet stretches quickly and loud ones slowly, so a splash barely moves it.
        float rate = db < _baseline ? 0.05f : loud ? 0.002f : 0.01f;
        _baseline += (MathF.Max(db, -90) - _baseline) * rate;

        // Updated after the check above, so a burst's own first sample doesn't count against its quiet time.
        if (busy) _lastBusy = nowMs;
        return hit;
    }

    public void Reset()
    {
        _baseline = -70;
        _loudRun = 0;
        _started = false;
        _lastBusy = _lastHit = long.MinValue / 2;
        Level = 0;
    }
}
