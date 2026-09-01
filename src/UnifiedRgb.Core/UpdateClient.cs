using System.Net.Http;
using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>Checks the configured update feed for a newer published build and
/// downloads it. Reads use the same shipped client key as SupportUpload;
/// publishing happens elsewhere (the publisher tool, admin key).</summary>
public static class UpdateClient
{

    public sealed record LatestBuild(string Version, string? Notes, long Size, string? Sha256);

    /// <summary>Latest published build, or null when none / unreachable /
    /// no backend configured (public builds: updates are distributed however
    /// the build's packager chooses — no phone-home).</summary>
    public static async Task<LatestBuild?> GetLatestAsync()
    {
        if (!Backend.Configured) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/version");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return new LatestBuild(
                    v.GetString()!,
                    root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    root.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
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

    /// <summary>SHA-256 (lowercase hex) of a downloaded file, for verifying
    /// against the hash the publisher registered.</summary>
    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Download the published exe to destPath, reporting whole-percent
    /// progress. Returns null on success, else the failure reason.</summary>
    public static async Task<string?> DownloadAsync(string destPath, Action<int>? percent = null)
    {
        if (!Backend.Configured) return "no update feed configured";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/download");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
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
            return $"download failed: {ex.Message}";
        }
    }
}
