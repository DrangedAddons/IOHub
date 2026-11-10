namespace IOHub.Integrations;

/// <summary>How closely the last 400 ms of WoW's sound matched the start of a bite splash.</summary>
/// <param name="Corr">How well the loudness rose and fell like a splash, -1..1 (1 = the same shape).</param>
/// <param name="ShapeDb">How far its tone was from a splash's, in dB per band (0 = the same).</param>
/// <param name="RiseDb">How much louder it was than the 200 ms before.</param>
/// <param name="LevelDb">The loudest of those 400 ms, dBFS.</param>
/// <param name="Rank">How close to the bite as one number: match minus tone off / 10.</param>
/// <param name="DecoyRank">The same against someone jumping or landing in water.</param>
public readonly record struct SplashScore(float Corr, float ShapeDb, float RiseDb, float LevelDb, float Rank, float DecoyRank)
{
    /// <summary>How much closer it is to the bite than to a jump. Below 0 it sounds more like a jump.</summary>
    public float OverDecoy => Rank - DecoyRank;

    public override string ToString() => $"match {Corr:0.00}, tone off by {ShapeDb:0.0} dB, {RiseDb:0} dB over before, peak {LevelDb:0} dBFS";
}

/// <summary>
/// Recognises the fishing bite by its sound. The bite plays one of three splash files (WoW sound kit 3355); each starts
/// soft, builds over about half a second and hisses on for one and a half to two, with most of its sound above 1 kHz.
/// The cast, the reel-in and looting are short and shaped differently.
///
/// WoW's sound is cut into 10 ms frames. For each frame: its loudness, and how its sound splits over five bands
/// (0-500 Hz, 0.5-1.5 k, 1.5-4 k, 4-8 k, 8-11 k), from a 512-point FFT. After every frame the last 40 frames are
/// compared with the first 40 of each splash: the loudness curve by correlation (so how loud doesn't matter), the band
/// split by the average difference in dB. <see cref="SplashTemplates"/> holds those numbers for the three splashes.
///
/// It does the same against ten decoys, WoW's splashes for someone jumping or landing in water, which start much like
/// the bite. <see cref="WowFishing"/> listens 300 ms past a match and only rumbles if the sound came closer to the bite
/// than to any of those. Footsteps in water never come close to the bite to begin with.
///
/// Pure logic, fed 48 kHz stereo 16-bit audio, so it can be tested on recordings.
/// </summary>
public sealed class SplashMatcher
{
    public const int Rate = 48000, Hop = 480, FrameMs = 10;
    const int Fft = 512, N = SplashTemplates.Frames, Bands = SplashTemplates.Bands, Before = 20;
    const int History = N + Before;
    static readonly int[] Edges = [0, 500, 1500, 4000, 8000, 11000];

    static readonly float[] Window = MakeWindow();
    static readonly int[] BandOfBin = MakeBands();
    static readonly (float[] Cos, float[] Sin) Twiddles = MakeTwiddles();
    static readonly int[] BitReverse = MakeBitReverse();
    static readonly (float Mean, float Norm)[] TemplateStats = SplashTemplates.Level.Select(Stats).ToArray();
    static readonly (float Mean, float Norm)[] DecoyStats = SplashTemplates.DecoyLevel.Select(Stats).ToArray();

    readonly float[] _frame = new float[Hop];
    int _filled;
    readonly float[] _re = new float[Fft], _im = new float[Fft];
    readonly float[] _level = new float[History];          // ring: total band power per frame, dB
    readonly float[] _shape = new float[History * Bands];  // ring: each band's share, dB
    readonly float[] _peak = new float[History];           // ring: frame peak, dBFS
    readonly float[] _sorted = new float[Before];
    readonly float[] _window = new float[N];
    long _frames;   // frames seen

    /// <summary>
    /// Feeds audio: interleaved 16-bit stereo, or <paramref name="silentFrames"/> of silence when WoW played nothing.
    /// <paramref name="onFrame"/> gets every 10 ms frame's score, or null while there's nothing to score.
    /// </summary>
    public void Feed(ReadOnlySpan<short> stereo, Action<SplashScore?> onFrame)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            _frame[_filled++] = (stereo[i] + stereo[i + 1]) * (0.5f / 32768f);
            if (_filled == Hop) { _filled = 0; onFrame(EndFrame()); }
        }
    }

    public void FeedSilence(int samples, Action<SplashScore?> onFrame)
    {
        for (int i = 0; i < samples; i++)
        {
            _frame[_filled++] = 0;
            if (_filled == Hop) { _filled = 0; onFrame(EndFrame()); }
        }
    }

    public void Reset()
    {
        _filled = 0;
        _frames = 0;
    }

    SplashScore? EndFrame()
    {
        // Power per band.
        float peak = 0;
        for (int i = 0; i < Fft; i++)
        {
            float x = i < Hop ? _frame[i] : 0;
            if (MathF.Abs(x) > peak) peak = MathF.Abs(x);
            _re[BitReverse[i]] = i < Hop ? x * Window[i] : 0;
            _im[BitReverse[i]] = 0;
        }
        Transform();
        Span<double> band = stackalloc double[Bands];
        for (int k = 0; k <= Fft / 2; k++)
            if (BandOfBin[k] is var b and >= 0) band[b] += (double)_re[k] * _re[k] + (double)_im[k] * _im[k];
        double total = 0;
        for (int b = 0; b < Bands; b++) total += band[b] += 1e-12;

        int slot = (int)(_frames % History);
        _level[slot] = (float)(10 * Math.Log10(total));
        for (int b = 0; b < Bands; b++) _shape[slot * Bands + b] = (float)(10 * Math.Log10(band[b] / total));
        _peak[slot] = peak > 1e-6f ? 20 * MathF.Log10(peak) : -120;
        _frames++;
        return _frames >= History ? Score() : null;
    }

    /// <summary>The best of the three splashes against the last <see cref="N"/> frames.</summary>
    SplashScore? Score()
    {
        int first = (int)((_frames - N) % History);     // the window's first frame
        float peak = -120;
        for (int i = 0; i < N; i++)
        {
            int s = (first + i) % History;
            _window[i] = _level[s];
            if (_peak[s] > peak) peak = _peak[s];
        }
        // Digital silence or the noise floor: nothing worth matching.
        if (peak < -70) return null;

        for (int i = 0; i < Before; i++) _sorted[i] = _level[(int)((_frames - N - Before + i) % History)];
        Array.Sort(_sorted);
        float before = (_sorted[Before / 2 - 1] + _sorted[Before / 2]) / 2;
        var (mean, norm) = Stats(_window);
        float rise = mean - before;

        var (corr, shape, rank) = Best(SplashTemplates.Level, SplashTemplates.Shape, SplashTemplates.Loud, TemplateStats, first, mean, norm);
        var (_, _, decoy) = Best(SplashTemplates.DecoyLevel, SplashTemplates.DecoyShape, SplashTemplates.DecoyLoud, DecoyStats, first, mean, norm);
        return new SplashScore(corr, shape, rise, peak, rank, decoy);
    }

    /// <summary>The closest of a set of templates to the window: loudness correlation, tone difference, and the two as one rank.</summary>
    (float Corr, float ShapeDb, float Rank) Best(float[][] levels, float[][] shapes, bool[][] louds, (float Mean, float Norm)[] stats, int first, float mean, float norm)
    {
        (float, float, float) best = (0, 99, float.NegativeInfinity);
        for (int t = 0; t < levels.Length; t++)
        {
            var tl = levels[t];
            var (tm, tn) = stats[t];
            float cov = 0;
            for (int i = 0; i < N; i++) cov += (_window[i] - mean) * (tl[i] - tm);
            float corr = norm > 0 && tn > 0 ? cov / (norm * tn) : 0;

            var ts = shapes[t];
            var loud = louds[t];
            float sum = 0;
            int count = 0;
            for (int i = 0; i < N; i++)
            {
                if (!loud[i]) continue;
                int s = (first + i) % History;
                for (int b = 0; b < Bands; b++)
                {
                    float d = _shape[s * Bands + b] - ts[i * Bands + b];
                    sum += d * d;
                }
                count += Bands;
            }
            float shape = count > 0 ? MathF.Sqrt(sum / count) : 99;
            float rank = corr - shape / 10;
            if (rank > best.Item3) best = (corr, shape, rank);
        }
        return best;
    }

    static (float Mean, float Norm) Stats(float[] v)
    {
        float mean = v.Average();
        float ss = 0;
        foreach (var x in v) ss += (x - mean) * (x - mean);
        return (mean, MathF.Sqrt(ss));
    }

    // ---------------- FFT ----------------

    void Transform()
    {
        for (int size = 2; size <= Fft; size <<= 1)
        {
            int half = size / 2, step = Fft / size;
            for (int start = 0; start < Fft; start += size)
                for (int j = 0; j < half; j++)
                {
                    float c = Twiddles.Cos[j * step], s = Twiddles.Sin[j * step];
                    int a = start + j, b = a + half;
                    float tr = _re[b] * c - _im[b] * s, ti = _re[b] * s + _im[b] * c;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
        }
    }

    static float[] MakeWindow()
    {
        // numpy's hanning: 0.5 - 0.5 cos(2 pi n / (M - 1)).
        var w = new float[Hop];
        for (int n = 0; n < Hop; n++) w[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (Hop - 1)));
        return w;
    }

    static int[] MakeBands()
    {
        var map = new int[Fft / 2 + 1];
        for (int k = 0; k <= Fft / 2; k++)
        {
            double f = k * (double)Rate / Fft;
            map[k] = -1;
            for (int b = 0; b < Bands; b++) if (f >= Edges[b] && f < Edges[b + 1]) map[k] = b;
        }
        return map;
    }

    static (float[], float[]) MakeTwiddles()
    {
        var c = new float[Fft / 2];
        var s = new float[Fft / 2];
        for (int i = 0; i < Fft / 2; i++) { c[i] = (float)Math.Cos(-2 * Math.PI * i / Fft); s[i] = (float)Math.Sin(-2 * Math.PI * i / Fft); }
        return (c, s);
    }

    static int[] MakeBitReverse()
    {
        int bits = (int)Math.Log2(Fft);
        var r = new int[Fft];
        for (int i = 0; i < Fft; i++)
        {
            int x = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) x |= 1 << (bits - 1 - b);
            r[i] = x;
        }
        return r;
    }
}
