using System.Net.Http;

namespace UnifiedRgb.Core;

/// <summary>The single source of truth for where UnifiedRGB keeps its state.
/// Every path under %APPDATA%/%LOCALAPPDATA% goes through here — seven files
/// were hand-building these before.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\UnifiedRgb — roaming state (settings, profiles, log).</summary>
    public static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedRgb");

    /// <summary>%LOCALAPPDATA%\UnifiedRgb — machine-local state (OpenRGB bundle,
    /// fan-config.json).</summary>
    public static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb");

    public static string Config(string file) => Path.Combine(ConfigDir, file);
    public static string Local(string file) => Path.Combine(LocalDir, file);

    static AppPaths()
    {
        // Both trees, so every store can assume its parent exists: LocalDir
        // used to be created only by the OpenRGB installer, and on a machine
        // that never enabled the bridge fan-config.json silently failed to save.
        try { Directory.CreateDirectory(ConfigDir); } catch { }
        try { Directory.CreateDirectory(LocalDir); } catch { }
    }
}

/// <summary>The support/update backend — OPTIONAL. Public/open-source builds
/// carry no endpoint at all: update checks quietly skip and support bundles
/// save to a local file. Configuration resolves in order:
///   1. %APPDATA%\UnifiedRgb\backend.json  →  { "url": "...", "key": "..." }
///      (the developer machine / power-user override)
///   2. Build-time injection for private-feed builds:
///        dotnet publish -p:RgbBackendUrl=... -p:RgbBackendKey=...
///      (the values land in this assembly's AssemblyMetadata; nothing is
///       hardcoded in the source tree)
/// The key is a spam guard, not a secret — but it doesn't belong in a public
/// repo, where it would invite abuse of a private server.</summary>
public static class Backend
{
    public static readonly string? BaseUrl;
    public static readonly string? ClientKey;

    /// <summary>True when an update/support endpoint is available to talk to.</summary>
    public static bool Configured => BaseUrl != null && ClientKey != null;

    static Backend()
    {
        string? url = null, key = null;
        string f = AppPaths.Config("backend.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
            }
        }
        catch (Exception ex) { Log.Warn("backend", $"backend.json unreadable: {ex.Message}"); }

        // The override file lives in user-writable %APPDATA% while the app runs
        // elevated, so it must be visible in any support bundle: a redirected
        // feed is the first thing to rule out when an "update" looks wrong.
        if (!string.IsNullOrWhiteSpace(url))
            Log.Warn("backend", $"feed/support endpoint overridden by {f}: {url}");

        url ??= Meta("RgbBackendUrl");
        key ??= Meta("RgbBackendKey");

        // Only https (or plain http to this machine, for a local dev backend)
        // is honored: the updater trusts the feed for the payload's hash, and
        // the support upload carries the session log — neither goes in the clear.
        if (!string.IsNullOrWhiteSpace(url)
            && !(Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
        {
            Log.Warn("backend", $"endpoint ignored (must be https, or http on loopback): {url}");
            url = null;
        }

        BaseUrl = string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        ClientKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    static string? Meta(string name) => typeof(Backend).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == name)?.Value;

    /// <summary>For API calls (version checks, report upload/list).</summary>
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>For large binary downloads (the ~260MB update exe).</summary>
    public static readonly HttpClient HttpDownload = new() { Timeout = TimeSpan.FromMinutes(15) };
}
