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
            var response = await Backend.Http.SendAsync(req);

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

    /*-----------------------------------------------------*\
    | Admin inbox (Ryan's machine only). The admin key is   |
    | never compiled in: it lives in %APPDATA%\UnifiedRgb\  |
    | admin.key, which also gates the UI.                   |
    \*-----------------------------------------------------*/

    public sealed record ReportSummary(
        string Id, DateTime CreatedUtc, string Kind, string User, string Machine,
        string? Note, string? AppVersion);

    sealed record ReportFull(
        string Id, DateTime CreatedUtc, string Kind, string User, string Machine,
        string? Note, string Content, string? AppVersion);

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>List recent support reports, newest first (summaries only).</summary>
    public static async Task<(bool Ok, string Message, List<ReportSummary> Reports)> ListReportsAsync(
        string adminKey, int limit = 25)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}?limit={limit}");
            req.Headers.Add("X-Rgb-Admin", adminKey);
            var response = await Backend.Http.SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, $"server said {(int)response.StatusCode}: {Truncate(body, 200)}", []);
            var reports = JsonSerializer.Deserialize<List<ReportSummary>>(body, JsonOpts) ?? [];
            return (true, $"{reports.Count} report(s)", reports);
        }
        catch (Exception ex)
        {
            Log.Error("admin", ex);
            return (false, $"list failed: {ex.Message}", []);
        }
    }

    /// <summary>Fetch one full report as paste-ready text (header + note + content).</summary>
    public static async Task<(bool Ok, string Message, string Text)> GetReportTextAsync(
        string adminKey, string id)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}/{id}");
            req.Headers.Add("X-Rgb-Admin", adminKey);
            var response = await Backend.Http.SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, $"server said {(int)response.StatusCode}: {Truncate(body, 200)}", "");
            var r = JsonSerializer.Deserialize<ReportFull>(body, JsonOpts);
            if (r == null) return (false, "bad response", "");
            string text =
                $"=== UnifiedRGB support report {r.Id} ===\r\n" +
                $"when: {r.CreatedUtc:yyyy-MM-dd HH:mm} UTC   kind: {r.Kind}   from: {r.User}@{r.Machine}   app: {r.AppVersion}\r\n" +
                $"note: {(string.IsNullOrWhiteSpace(r.Note) ? "(none)" : r.Note)}\r\n\r\n" +
                r.Content;
            return (true, "ok", text);
        }
        catch (Exception ex)
        {
            Log.Error("admin", ex);
            return (false, $"fetch failed: {ex.Message}", "");
        }
    }

    /// <summary>Permanently remove a handled report from the inbox.</summary>
    public static async Task<(bool Ok, string Message)> DeleteReportAsync(string adminKey, string id)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Endpoint}/{id}");
            req.Headers.Add("X-Rgb-Admin", adminKey);
            var response = await Backend.Http.SendAsync(req);
            return response.IsSuccessStatusCode
                ? (true, "removed")
                : (false, $"server said {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Error("admin", ex);
            return (false, $"delete failed: {ex.Message}");
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
