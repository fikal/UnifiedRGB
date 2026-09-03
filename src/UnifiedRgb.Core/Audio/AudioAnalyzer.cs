using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Audio;

/*-----------------------------------------------------------*\
| Shared audio analysis for the audio-reactive effects.       |
|                                                             |
| Lazy lifecycle: the first effect frame that reads it starts |
| WASAPI loopback capture; when nothing has read it for a few |
| seconds it shuts the capture down again. Effects read the   |
| smoothed band array lock-free (float stores are atomic).    |
\*-----------------------------------------------------------*/
public static class AudioAnalyzer
{
    public const int BandCount = 24;

    const int FftSize = 2048;                  // ~43 ms @ 48 kHz
    const int Hop = 1024;
    const double FMin = 40, FMax = 16000;
    const double IdleStopSeconds = 5;

    static readonly object _gate = new();
    static WasapiLoopback? _capture;
    static Timer? _watchdog;
    static long _lastReadTicks;
    static long _failedUntilTicks;

    // Ring of mono samples from the capture thread.
    static readonly float[] _ring = new float[FftSize * 4];
    static int _ringWrite;
    static int _sinceAnalysis;
    static int _sampleRate = 48000;

    // FFT workspace (capture thread only).
    static readonly double[] _window = BuildHann();
    static readonly double[] _re = new double[FftSize];
    static readonly double[] _im = new double[FftSize];

    // Published state (written by capture thread, read by effect threads).
    static readonly float[] _bands = new float[BandCount];
    static readonly int[] _bandI0 = new int[BandCount];   // per-band bin ranges,
    static readonly int[] _bandI1 = new int[BandCount];   // computed once per sample rate
    static int _bandRate = -1;
    static float _level, _bass;
    static float _agcPeak = 0.05f;     // band-magnitude scale
    static float _agcRms = 0.02f;      // loudness scale (separate: different units)

    /// <summary>Smoothed 0..1 level of band i (0 = lowest frequency).</summary>
    public static float Band(int i) => _bands[Math.Clamp(i, 0, BandCount - 1)];

    /// <summary>Overall loudness 0..1 (RMS through the AGC).</summary>
    public static float Level => _level;

    /// <summary>Low-end energy 0..1 (bottom few bands) — the beat.</summary>
    public static float Bass => _bass;

    /// <summary>Effects call this every frame they render; it lazily starts the
    /// capture and keeps it alive. Safe from any thread, cheap when running.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
        if (_capture != null) return;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _failedUntilTicks)) return;

        lock (_gate)
        {
            if (_capture != null) return;
            try
            {
                var c = new WasapiLoopback(OnSamples);
                c.Start();
                _capture = c;
                // The watchdog only has work while a capture is alive: paused
                // when the capture stops, re-armed here - not two wakeups a
                // second for the rest of the process after one audio effect.
                if (_watchdog == null) _watchdog = new Timer(_ => Watchdog(), null, 2000, 2000);
                else _watchdog.Change(2000, 2000);
            }
            catch (Exception ex)
            {
                Log.Warn("audio", $"loopback unavailable: {ex.Message}");
                Interlocked.Exchange(ref _failedUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
            }
        }
    }

    /// <summary>Stops the capture when idle; also clears out a capture whose
    /// polling thread died (device unplugged) so the next Touch restarts it.</summary>
    static void Watchdog()
    {
        var cap = _capture;
        if (cap == null) return;
        var last = new DateTime(Interlocked.Read(ref _lastReadTicks), DateTimeKind.Utc);
        bool idle = (DateTime.UtcNow - last).TotalSeconds >= IdleStopSeconds;
        if (!idle && cap.IsAlive) return;
        lock (_gate)
        {
            if (_capture == null) return;
            _capture.Dispose();
            _capture = null;
            _watchdog?.Change(Timeout.Infinite, Timeout.Infinite);   // nothing to guard until Touch re-arms
            Array.Clear(_bands);
            _level = _bass = 0;
            Log.Info("audio", idle ? "loopback capture stopped (idle)" : "loopback capture died - will restart");
        }
    }

    /*-----------------------------------------------------*\
    | Capture thread: accumulate, analyze every Hop samples |
    \*-----------------------------------------------------*/
    static void OnSamples(float[] samples, int count, int sampleRate)
    {
        _sampleRate = sampleRate;
        for (int i = 0; i < count; i++)
        {
            _ring[_ringWrite] = samples[i];
            _ringWrite = (_ringWrite + 1) % _ring.Length;
        }
        _sinceAnalysis += count;
        while (_sinceAnalysis >= Hop)
        {
            _sinceAnalysis -= Hop;
            Analyze();
        }
    }

    static void Analyze()
    {
        // Latest FftSize samples, windowed.
        int start = (_ringWrite - FftSize + _ring.Length * 4) % _ring.Length;
        double rms = 0;
        for (int i = 0; i < FftSize; i++)
        {
            double s = _ring[(start + i) % _ring.Length];
            rms += s * s;
            _re[i] = s * _window[i];
            _im[i] = 0;
        }
        rms = Math.Sqrt(rms / FftSize);
        Fft(_re, _im);

        // Log-spaced bands over the magnitude spectrum. Band-edge bin indices
        // depend only on the sample rate — precomputed (was 2 Math.Pow per
        // band per Analyze, ~47x/s).
        if (_bandRate != _sampleRate)
        {
            double binHz2 = (double)_sampleRate / FftSize;
            for (int b = 0; b < BandCount; b++)
            {
                double lo = FMin * Math.Pow(FMax / FMin, (double)b / BandCount);
                double hi = FMin * Math.Pow(FMax / FMin, (double)(b + 1) / BandCount);
                _bandI0[b] = Math.Max(1, (int)(lo / binHz2));
                _bandI1[b] = Math.Min(FftSize / 2 - 1, Math.Max(_bandI0[b], (int)(hi / binHz2)));
            }
            _bandRate = _sampleRate;
        }
        Span<float> raw = stackalloc float[BandCount];
        float peak = 0;
        for (int b = 0; b < BandCount; b++)
        {
            int i0 = _bandI0[b], i1 = _bandI1[b];
            double sum = 0;
            for (int i = i0; i <= i1; i++)
                sum += Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
            raw[b] = (float)(sum / (i1 - i0 + 1));
            if (raw[b] > peak) peak = raw[b];
        }

        // TIME-BASED attack/decay: the old constants were per-Analyze-call, so
        // the feel depended on the analysis cadence (and read as strobing —
        // field feedback: "flashes really fast"). Now: ~15 ms rise so hits
        // still snap, ~160 ms fall so bars breathe between beats, identical on
        // every sample rate / capture mode.
        double dt = (double)Hop / Math.Max(8000, _sampleRate);
        float atk = (float)(1 - Math.Exp(-dt / 0.015));
        float dec = (float)Math.Exp(-dt / 0.16);
        float agcDec = (float)Math.Exp(-dt / 14.0);        // slow AGC, ~old 0.9985/call

        _agcPeak = Math.Max(0.02f, Math.Max(peak, _agcPeak * agcDec));

        float bassSum = 0;
        for (int b = 0; b < BandCount; b++)
        {
            float v = Math.Clamp(raw[b] / _agcPeak, 0f, 1f);
            v = (float)Math.Sqrt(v);                       // perceptual lift
            float cur = _bands[b];
            _bands[b] = v > cur ? cur + (v - cur) * atk : cur * dec;
            if (b < 4) bassSum += _bands[b];
        }
        _bass = bassSum / 4f;
        _agcRms = Math.Max(0.005f, Math.Max((float)rms, _agcRms * agcDec));
        float lvl = (float)Math.Sqrt(Math.Clamp(rms / _agcRms, 0, 1));
        float lvlAtk = (float)(1 - Math.Exp(-dt / 0.025));
        float lvlDec = (float)Math.Exp(-dt / 0.18);
        _level = lvl > _level ? _level + (lvl - _level) * lvlAtk : _level * lvlDec;
    }

    /*-----------------------------------------------------*\
    | Iterative radix-2 Cooley-Tukey                        |
    \*-----------------------------------------------------*/
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)                 // bit-reverse permutation
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci;
                    double xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    static double[] BuildHann()
    {
        var w = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        return w;
    }
}
