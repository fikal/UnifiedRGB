using System.Net.Http;
using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>Checks GitHub Releases for a newer published build and downloads
/// it. Publishing happens in release.ps1 (gh release create).</summary>
public static class UpdateClient
{
    /// <summary>Where releases are published. Public information.</summary>
    public const string GitHubRepo = "fikal/UnifiedRGB";

    /// <summary>For the release JSON and the .sha256 asset. Both clients race a
    /// host's addresses (RacingConnect): GitHub's download host has several, and
    /// one that never answers used to cost the full 21 s connect timeout before
    /// the next was tried - twice per install, on a badge that said nothing.</summary>
    static readonly HttpClient Http = new(Net.RacingConnect.Handler()) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>For the release exe itself (~270 MB).</summary>
    static readonly HttpClient HttpDownload = new(Net.RacingConnect.Handler()) { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>How often a download reports progress: often enough that the
    /// bar visibly moves, rarely enough to cost the UI nothing.</summary>
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(150);

    /// <summary>Sha256 is null until <see cref="ResolveShaAsync"/> runs: the
    /// startup check is ONE request (the release JSON); the .sha256 asset is
    /// only fetched when an install actually happens.</summary>
    public sealed record LatestBuild(
        string Version, string? Notes, long Size, string? Sha256, string? DownloadUrl = null, string? ShaUrl = null);

    /// <summary>The published SHA-256 for a build: the .sha256 asset when the
    /// release has one, else the first 64-hex token in the notes.</summary>
    public static async Task<string?> ResolveShaAsync(LatestBuild build)
    {
        if (build.Sha256 != null) return build.Sha256;
        if (build.ShaUrl is not null)
            try
            {
                using var shaReq = new HttpRequestMessage(HttpMethod.Get, build.ShaUrl);
                shaReq.Headers.TryAddWithoutValidation("User-Agent", "UnifiedRGB-updater");
                using var resp = await Http.SendAsync(shaReq).ConfigureAwait(false);
                var sha = ExtractSha(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (sha != null) return sha;
            }
            catch { /* fall through to notes scan */ }
        return ExtractSha(build.Notes);
    }

    /// <summary>Latest GitHub release, or null when none / unreachable: version
    /// from the tag (leading 'v' stripped), the .exe asset's direct URL + size,
    /// and its SHA-256 — from a companion .sha256 asset when one exists, else a
    /// 64-hex token in the release notes. Unauthenticated; GitHub requires a
    /// User-Agent.</summary>
    public static async Task<LatestBuild?> GetLatestAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", "UnifiedRGB-updater");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await Http.SendAsync(req).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("update", $"github release check: {(int)response.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            string version = t.GetString()!.TrimStart('v', 'V');

            string? exeUrl = null, shaUrl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    string? url = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (exeUrl is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = url;
                        size = SizeOf(a);
                    }
                    else if (shaUrl is null && name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                        shaUrl = url;
                }
            if (exeUrl is null) return null;   // release without a binary — nothing to offer

            string? notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;

            // SHA resolved lazily (ResolveShaAsync) so the startup check stays
            // a single request; the notes scan is kept as the no-asset fallback.
            return new LatestBuild(version, notes, size, null, exeUrl, shaUrl);
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"github release check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The object's "size" when it is an integral number, else 0: the
    /// field is cosmetic (progress text), so a reply that emits it as a string
    /// or null must not fail the whole version check. Internal for the harness.</summary>
    internal static long SizeOf(JsonElement obj)
        => obj.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var size)
            ? size : 0;

    /// <summary>First standalone 64-hex-char token in the text (a SHA-256), or null.</summary>
    static string? ExtractSha(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"\b[0-9a-fA-F]{64}\b");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>SHA-256 (lowercase hex) of a downloaded file, for verifying
    /// against the hash the release published.</summary>
    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Download a release exe (its GitHub asset URL) to destPath.
    /// Returns null on success, else the failure reason.
    ///
    /// Runs entirely on the thread pool (ConfigureAwait(false) on every await):
    /// called from a click, the ~4,000 chunk continuations of a 270 MB download
    /// used to come back to the UI thread one by one, and the window crawled
    /// for the whole minute it took.</summary>
    /// <param name="progress">Bytes so far and the total (0 when the server
    /// sends no length), at most every <see cref="ProgressEvery"/> and once at
    /// the end. Called on a pool thread: marshal before touching the UI.</param>
    public static async Task<string?> DownloadAsync(string url, string destPath, Action<long, long>? progress = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "UnifiedRGB-updater");
            using var response = await HttpDownload.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return $"server said {(int)response.StatusCode}";

            long total = response.Content.Headers.ContentLength ?? 0;
            progress?.Invoke(0, total);
            await using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1 << 18];
            long done = 0;
            int n;
            var since = System.Diagnostics.Stopwatch.StartNew();
            while ((n = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                done += n;
                if (since.Elapsed >= ProgressEvery) { since.Restart(); progress?.Invoke(done, total); }
            }
            progress?.Invoke(done, total);
            Log.Info("update", $"downloaded {done:n0} bytes to {destPath}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            // The stream is already disposed (its `await using` unwound before
            // this ran); don't strand a partial ~260 MB exe beside the app —
            // each attempt uses a fresh name, so retries used to accumulate.
            try { File.Delete(destPath); } catch { }
            return $"download failed: {ex.Message}";
        }
    }
}
