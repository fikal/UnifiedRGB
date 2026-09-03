using System.Net.Http;
using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>Checks for a newer published build and downloads it. Two sources:
/// the private feed when a backend is configured (maintainer-distributed
/// builds), otherwise GitHub Releases — so public builds get the same
/// one-click update, served from the repo's own releases. Publishing happens
/// elsewhere (the publisher tool / gh release).</summary>
public static class UpdateClient
{
    /// <summary>Where public builds look for releases. Public information.</summary>
    public const string GitHubRepo = "fikal/UnifiedRGB";

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
                using var resp = await Backend.Http.SendAsync(shaReq);
                var sha = ExtractSha(await resp.Content.ReadAsStringAsync());
                if (sha != null) return sha;
            }
            catch { /* fall through to notes scan */ }
        return ExtractSha(build.Notes);
    }

    /// <summary>Latest published build, or null when none / unreachable.
    /// Private feed when configured; GitHub Releases otherwise.</summary>
    public static Task<LatestBuild?> GetLatestAsync()
        => Backend.Configured ? GetLatestFromFeedAsync() : GetLatestFromGitHubAsync();

    static async Task<LatestBuild?> GetLatestFromFeedAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/version");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            using var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return new LatestBuild(
                    v.GetString()!,
                    root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    SizeOf(root),
                    root.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("update", $"version check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Latest GitHub release: version from the tag (leading 'v'
    /// stripped), the .exe asset's direct URL + size, and its SHA-256 — from a
    /// companion .sha256 asset when one exists, else a 64-hex token in the
    /// release notes. Unauthenticated; GitHub requires a User-Agent.</summary>
    static async Task<LatestBuild?> GetLatestFromGitHubAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
            req.Headers.TryAddWithoutValidation("User-Agent", "UnifiedRGB-updater");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("update", $"github release check: {(int)response.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
    /// field is cosmetic (progress text), so a feed that emits it as a string
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
    /// against the hash the publisher registered.</summary>
    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Download the published exe to destPath, reporting whole-percent
    /// progress. Returns null on success, else the failure reason. With a
    /// directUrl (a GitHub release asset) fetches that; otherwise the feed.</summary>
    public static async Task<string?> DownloadAsync(string destPath, Action<int>? percent = null,
        string? directUrl = null)
    {
        if (directUrl is null && !Backend.Configured) return "no update feed configured";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                directUrl ?? $"{Backend.BaseUrl}/download");
            if (directUrl is null)
                req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            else
                req.Headers.TryAddWithoutValidation("User-Agent", "UnifiedRGB-updater");
            using var response = await Backend.HttpDownload.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return $"server said {(int)response.StatusCode}";

            long total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(destPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct) { lastPct = pct; percent?.Invoke(pct); }
                }
            }
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
