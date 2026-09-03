using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UnifiedRgb.Core;

/// <summary>Uploads logs / diagnostic reports to the maintainer's support inbox
/// so remote users can send data with one click instead of
/// hunting for files. The key is a spam guard, not a secret.</summary>
public static class SupportUpload
{
    static string Endpoint => $"{Backend.BaseUrl}/report";

    /// <summary>True when reports can be uploaded (a backend is configured).
    /// Public builds save bundles to a local file instead.</summary>
    public static bool CanUpload => Backend.Configured;

    /// <summary>Send a report. kind = "log" or "diag". Returns (ok, message):
    /// the server's report id on success, or the failure reason.</summary>
    public static async Task<(bool Ok, string Message)> SendAsync(
        string kind, string content, string? note = null, string? appVersion = null)
    {
        if (!Backend.Configured) return (false, "no support endpoint configured");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above

            var payload = JsonSerializer.Serialize(new
            {
                kind,
                user = Environment.UserName,
                machine = Environment.MachineName,
                note,
                content,
                appVersion,
            });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Backend.Http.SendAsync(req);

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, $"server said {(int)response.StatusCode}: {Truncate(body, 200)}");

            Log.Info("upload", $"{kind} report sent ({content.Length} chars)");
            return (true, "sent - thanks!");
        }
        catch (Exception ex)
        {
            Log.Error("upload", ex);
            return (false, $"upload failed: {ex.Message}");
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
