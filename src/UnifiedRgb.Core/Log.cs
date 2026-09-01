namespace UnifiedRgb.Core;

/// <summary>Session log at %APPDATA%\UnifiedRgb\unifiedrgb.log — the file a
/// remote user sends back when something doesn't work. Every device detection
/// attempt and failure lands here.</summary>
public static class Log
{
    static readonly object _lock = new();
    static readonly string PathName = AppPaths.Config("unifiedrgb.log");
    static readonly Dictionary<string, (DateTime Last, int Suppressed)> _occasional = new();

    static Log()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            // Rotate instead of wiping: the old log is exactly what's needed
            // when debugging whatever made it grow.
            if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000)
            {
                try { File.Move(PathName, PathName + ".old", overwrite: true); }
                catch { File.WriteAllText(PathName, ""); }
            }
            Write("====", $"session start  user={Environment.UserName}  os={Environment.OSVersion.Version}");
        }
        catch { }
    }

    public static string FilePath => PathName;

    public static void Info(string source, string message) => Write("info", $"[{source}] {message}");
    public static void Warn(string source, string message) => Write("WARN", $"[{source}] {message}");
    public static void Error(string source, string message) => Write("ERR ", $"[{source}] {message}");
    public static void Error(string source, Exception ex) => Write("ERR ", $"[{source}] {ex}");

    /// <summary>Rate-limited logging where even BUILDING the message is too
    /// expensive to do per call: the factory only runs when the entry will
    /// actually be written (a 60fps failure loop was allocating interpolated
    /// strings every frame just to have them suppressed).</summary>
    public static void Occasional(string key, string source, Func<string> message)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_occasional.TryGetValue(key, out var seen) && now - seen.Last < TimeSpan.FromMinutes(1))
            {
                _occasional[key] = (seen.Last, seen.Suppressed + 1);
                return;
            }
        }
        Occasional(key, source, message());
    }

    /// <summary>Rate-limited logging for hot paths (device write loops): logs
    /// the first occurrence per key, then at most once per minute with a
    /// suppressed-count so a failing device can't flood the file at 60fps.</summary>
    public static void Occasional(string key, string source, string message)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_occasional.TryGetValue(key, out var seen))
            {
                if (now - seen.Last < TimeSpan.FromMinutes(1))
                {
                    _occasional[key] = (seen.Last, seen.Suppressed + 1);
                    return;
                }
                message += $" (+{seen.Suppressed} suppressed)";
            }
            _occasional[key] = (now, 0);
        }
        Write("WARN", $"[{source}] {message}");
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(PathName, $"{DateTime.Now:MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
