using System.Net.Http;

namespace UnifiedRgb.Core;

/// <summary>The single source of truth for where UnifiedRGB keeps its state.
/// Every path under %APPDATA%/%LOCALAPPDATA% goes through here — seven files
/// were hand-building these before.</summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\UnifiedRgb — roaming state (settings, profiles, log).</summary>
    public static readonly string ConfigDir =
        Redirect("UNIFIEDRGB_CONFIG_DIR", Environment.SpecialFolder.ApplicationData);

    /// <summary>%LOCALAPPDATA%\UnifiedRgb — machine-local state (OpenRGB bundle,
    /// fan-config.json).</summary>
    public static readonly string LocalDir =
        Redirect("UNIFIEDRGB_LOCAL_DIR", Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The real per-user location, unless an environment variable moves
    /// it. That override exists for ONE reason: the test harness must not read
    /// or write the running user's settings, profiles and layouts. It ran
    /// against the live files, so a test that was killed between its write and
    /// its restore left fixture JSON in place for the app to load.
    ///
    /// Nothing in the product sets these, so a normal launch resolves exactly
    /// as before. It is deliberately an environment variable rather than a
    /// settable property: the paths are static readonly and read during type
    /// initialization, which can happen before any code gets a chance to
    /// assign a property.</summary>
    static string Redirect(string variable, Environment.SpecialFolder folder)
    {
        string? over = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(over)) return over;
        return Path.Combine(Environment.GetFolderPath(folder), "UnifiedRgb");
    }

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
        // The build's own endpoint, if it was given one at publish time.
        string? url = Meta("RgbBackendUrl"), key = Meta("RgbBackendKey");
        bool privateBuild = !string.IsNullOrWhiteSpace(url);

        // %APPDATA%\UnifiedRgb\backend.json can point a PRIVATE-FEED build
        // somewhere else - a developer aiming at a staging server.
        //
        // It is deliberately ignored in a public build. That file is writable
        // by any process running as the user, while this app runs ELEVATED, and
        // what the endpoint gets to decide is the update payload AND the hash
        // that payload is checked against - so an unprivileged process could
        // plant one file and have the administrator process download and run
        // its executable, which the /RL HIGHEST logon task would then re-run at
        // every boot. Since the shipped build takes its updates from GitHub and
        // carries no endpoint, honoring the file there buys nothing and costs
        // that. A fork wanting its own feed builds with -p:RgbBackendUrl=...,
        // which is a decision made in the build rather than in a writable file.
        string f = AppPaths.Config("backend.json");
        bool present = false;
        try { present = File.Exists(f); } catch { }

        if (present && !privateBuild)
        {
            Log.Warn("backend", $"ignoring {f}: this build has no private feed of its own, "
                              + "so updates come from GitHub and a file cannot redirect them");
        }
        else if (present)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                string? fu = null, fk = null;
                if (doc.RootElement.TryGetProperty("url", out var u)) fu = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) fk = k.GetString();
                if (!string.IsNullOrWhiteSpace(fu)) url = fu;
                if (!string.IsNullOrWhiteSpace(fk)) key = fk;
                // Still elevated, still user-writable: a redirected feed is the
                // first thing to rule out when an "update" looks wrong, so it
                // has to be visible in any support bundle.
                if (!string.IsNullOrWhiteSpace(fu))
                    Log.Warn("backend", $"feed/support endpoint overridden by {f}: {fu}");
            }
            catch (Exception ex) { Log.Warn("backend", $"backend.json unreadable: {ex.Message}"); }
        }

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
