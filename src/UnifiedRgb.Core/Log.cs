namespace UnifiedRgb.Core;

/// <summary>Session log at %APPDATA%\UnifiedRgb\unifiedrgb.log — the file a
/// remote user sends back when something doesn't work. Every device detection
/// attempt and failure lands here.</summary>
public static class Log
{
    static readonly object _lock = new();
    // The diagnostic exe links this same class. It used to append to the
    // app's file: run beside the running tray app, the two fought over the
    // handle (see AppendShared) and its lines landed in the app's session.
    static readonly string PathName = AppPaths.Config(
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase) == true
            ? "unifiedrgb-diag.log" : "unifiedrgb.log");
    static readonly Dictionary<string, (DateTime Last, int Suppressed)> _occasional = new();

    static Log()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            // Rotate instead of wiping: the old log is exactly what's needed
            // when debugging whatever made it grow.
            RotateIfBig();
            // No account name: people paste raw log lines into forum threads
            // far more often than they attach a scrubbed bundle, and the name
            // has never helped diagnose anything.
            Write("====", $"session start  v{EntryVersion()}  os={Environment.OSVersion.Version}");
        }
        catch { }
    }

    public static string FilePath => PathName;

    /// <summary>The running exe's version, three parts, matching the release
    /// tags. Read from the entry assembly so this works for both the app and
    /// the standalone diagnostic tool without Core depending on either.</summary>
    public static string EntryVersion()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

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
                if (seen.Suppressed > 0) message += $" (+{seen.Suppressed} suppressed)";
            }
            _occasional[key] = (now, 0);
        }
        Write("WARN", $"[{source}] {message}");
    }

    static long _written;

    static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                string line = $"{DateTime.Now:MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
                AppendShared(line);

                // Rotation used to be checked once, in the static constructor.
                // This is a tray app that runs from Windows startup for weeks,
                // so within one session the file grew without limit and the
                // check only came round on the NEXT launch.
                _written += line.Length;
                if (_written > 200_000) { _written = 0; RotateIfBig(); }
            }
        }
        catch { }
    }

    /// <summary>Move the log aside once it passes the cap. The previous file is
    /// kept as .old, and the new one opens by saying so: a bundle that starts
    /// mid-story should say that there is an earlier part.</summary>
    static void RotateIfBig()
    {
        try
        {
            var f = new FileInfo(PathName);
            if (!f.Exists || f.Length <= 1_000_000) return;
            File.Move(PathName, PathName + ".old", overwrite: true);
            AppendShared($"{DateTime.Now:MM-dd HH:mm:ss} ==== log rotated, the previous one is {Path.GetFileName(PathName)}.old"
                + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Append with the file left readable AND writable by others.
    /// File.AppendAllText opens with FileShare.Read: the support bundle's read of
    /// the log (and anything else holding it open, an editor say) then fails a
    /// write in the other process, and Write's catch drops the line silently.
    /// UTF-8 without a BOM, as AppendAllText writes.</summary>
    static void AppendShared(string text)
    {
        using var fs = new FileStream(PathName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        fs.Write(bytes, 0, bytes.Length);
    }
}
