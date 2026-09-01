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

    /// <summary>%LOCALAPPDATA%\UnifiedRgb — machine-local bulk (OpenRGB bundle).</summary>
    public static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb");

    public static string Config(string file) => Path.Combine(ConfigDir, file);
    public static string Local(string file) => Path.Combine(LocalDir, file);

    /// <summary>The admin key that unlocks the support inbox — exists only on
    /// the developer's machine, never ships in builds.</summary>
    public static string AdminKeyFile => Config("admin.key");

    static AppPaths()
    {
        try { Directory.CreateDirectory(ConfigDir); } catch { }
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
        try
        {
            string f = AppPaths.Config("backend.json");
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
            }
        }
        catch (Exception ex) { Log.Warn("backend", $"backend.json unreadable: {ex.Message}"); }

        url ??= Meta("RgbBackendUrl");
        key ??= Meta("RgbBackendKey");

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
