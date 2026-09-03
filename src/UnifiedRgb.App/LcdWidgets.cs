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

    // The adapter list is re-enumerated every 30 s, not per sample:
    // GetAllNetworkInterfaces walks GetAdaptersAddresses and materialises the
    // whole address/DNS/gateway graph for every adapter, while the byte
    // counters come from a per-call GetIfEntry2 on the cached objects
    // (GetIPv4Statistics is live). A newly-up adapter shows within 30 s.
    static NetworkInterface[]? _ifaces;
    static long _ifacesAt;
    const int IfaceRefreshMs = 30_000;

    /// <summary>Null when a counter read failed (an adapter vanished mid-sample):
    /// the partial sum would otherwise feed the rate as a negative or huge delta.</summary>
    static (long rx, long tx)? Totals()
    {
        long rx = 0, tx = 0;
        try
        {
            long now = Environment.TickCount64;
            if (_ifaces == null || now - _ifacesAt > IfaceRefreshMs)
            {
                _ifaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                              && ni.OperationalStatus == OperationalStatus.Up)
                    .ToArray();
                _ifacesAt = now;
            }
            foreach (var ni in _ifaces)
            {
                var s = ni.GetIPv4Statistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
        }
        catch { _ifaces = null; return null; }   // re-enumerate on the next sample
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

        if (Totals() is not (long rx, long tx)) return _text;
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
    // The reply is one short line; bound the buffer (default 2 GB) so a
    // misbehaving server or captive portal can't balloon the tray process.
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4096 };
    const int MaxTextLength = 48;    // "-12°F Light freezing drizzle" is ~30
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
                if (raw.Length > MaxTextLength)
                    UnifiedRgb.Core.Log.Occasional("lcd", "weather", $"unexpected reply ({raw.Length} chars) ignored");
                else if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
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
