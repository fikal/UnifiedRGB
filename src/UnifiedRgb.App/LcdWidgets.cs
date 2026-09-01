using System.Net.Http;
using System.Net.NetworkInformation;

namespace UnifiedRgb.App;

/// <summary>Whole-machine network throughput. Sums bytes across every "real"
/// interface (skips loopback/tunnels) and divides by the wall-clock gap between
/// reads, so the LCD's 1 Hz refresh yields a live ↓/↑ rate. Self-priming: the
/// first read has no prior sample and reports zero.</summary>
public static class NetMeter
{
    static long _rx, _tx;
    static long _lastTicks;
    static double _down, _up;   // bytes/sec, smoothed a touch to stop the jitter
    static string _text = "↓0 KB/s ↑0 KB/s";

    static (long rx, long tx) Totals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var s = ni.GetIPv4Statistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    /// <summary>Cached "↓12.4 MB/s ↑0.8 KB/s"-style text. Rate-limited to ~2 Hz so
    /// it's safe to call twice per tick (panel + editor) without splitting the
    /// delta between callers and corrupting the computed rate.</summary>
    public static string Read()
    {
        long now = Environment.TickCount64;
        double dt = _lastTicks == 0 ? 0 : (now - _lastTicks) / 1000.0;
        if (dt is > 0 and < 0.4) return _text;   // too soon to resample; reuse

        var (rx, tx) = Totals();
        if (_lastTicks != 0 && dt > 0)
        {
            // Counters wrap/reset on adapter changes: a negative delta is
            // garbage, so clamp to zero rather than show a huge spike.
            double dRx = Math.Max(0, rx - _rx) / dt;
            double dTx = Math.Max(0, tx - _tx) / dt;
            // Light EMA so the number doesn't strobe every second.
            _down = _down * 0.4 + dRx * 0.6;
            _up = _up * 0.4 + dTx * 0.6;
        }
        _rx = rx; _tx = tx; _lastTicks = now;
        return _text = $"↓{Rate(_down)} ↑{Rate(_up)}";
    }

    static string Rate(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1) return "0 KB/s";
        if (kb < 1024) return $"{kb:0} KB/s";
        return $"{kb / 1024.0:0.0} MB/s";
    }
}

/// <summary>Local weather via wttr.in - no API key, auto-locates from the public
/// IP, one HTTP call. Refreshed on a slow background loop; the LCD reads the
/// cached string so a slow or offline fetch never stalls rendering.</summary>
public static class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static volatile string _current = "--°";
    static volatile bool _started;

    public static string Current => _current;

    public static void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(Loop);
    }

    static async Task Loop()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.0");   // wttr.in serves the compact format to curl-like agents
        while (true)
        {
            try
            {
                // %t = temperature, %C = condition text; u = US units (°F).
                var raw = (await Http.GetStringAsync("https://wttr.in/?format=%t+%C&u")).Trim();
                if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                    _current = raw.TrimStart('+');                    // wttr prefixes positive temps with '+'
            }
            catch (Exception ex)
            {
                UnifiedRgb.Core.Log.Occasional("lcd", "weather", $"fetch failed: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromMinutes(15));
        }
    }
}
