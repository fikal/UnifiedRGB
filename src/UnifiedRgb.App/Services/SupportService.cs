using System.IO;
using System.Windows;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>The support pipeline's app side: the one-button diagnostic+log
/// bundle upload, the elevated-collect helper handoff, and the admin inbox
/// operations (list / save-to-file / delete). Extracted from the view model;
/// the VM keeps only bindable state and thin wrappers.</summary>
public sealed class SupportService
{
    public static bool IsAdminMachine => File.Exists(AppPaths.AdminKeyFile);

    static string? ReadAdminKey()
    {
        try { return File.ReadAllText(AppPaths.AdminKeyFile).Trim(); }
        catch { return null; }
    }

    static string AppVersion =>
        typeof(SupportService).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>Full hardware survey + session log + note, as one upload.
    /// Collection shells out to WMI, so call from off the UI thread context;
    /// progress lands in the status callback.</summary>
    public async Task<(bool Ok, string Message)> SendBundleAsync(string? note, Action<string> status)
    {
        string bundle = await Task.Run(() =>
        {
            string diag;
            try
            {
                // Non-elevated app: relaunch ourselves elevated in helper mode
                // so the report includes the SMBus/RAM scan. The UAC prompt is
                // expected; declining falls back to a reduced in-process
                // report (which says what was skipped).
                diag = DiagnosticReport.IsAdmin() ? null! : TryElevatedCollect(status)!;
                diag ??= DiagnosticReport.Collect(section =>
                    Application.Current.Dispatcher.Invoke(() => status($"collecting: {section}...")));
            }
            catch (Exception ex) { diag = $"(diagnostic failed: {ex})"; }

            string log;
            try { log = File.ReadAllText(Log.FilePath); }
            catch (Exception ex) { log = $"(log unavailable: {ex.Message})"; }

            return diag
                + "\r\n\r\n==============================================\r\n"
                + " APP LOG (unifiedrgb.log)\r\n"
                + "==============================================\r\n"
                + log;
        });

        if (SupportUpload.CanUpload)
        {
            status("sending...");
            return await SupportUpload.SendAsync("diag", bundle, note, AppVersion);
        }

        // No backend (public/open-source build): save the bundle locally and
        // open a prefilled GitHub issue — the user drags the file in. Nothing
        // leaves the machine except what they choose to post.
        status("saving bundle...");
        try
        {
            string fileName = $"UnifiedRGB-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
            string header = string.IsNullOrWhiteSpace(note) ? "" : $"note: {note}\r\n\r\n";
            await File.WriteAllTextAsync(outPath, header + bundle);
            OpenGitHubIssue(note, fileName);
            return (true, $"bundle saved to your Desktop — drag {fileName} into the GitHub issue that just opened");
        }
        catch (Exception ex)
        {
            return (false, $"couldn't save the bundle: {ex.Message}");
        }
    }

    /// <summary>Browser to a new-issue page with version/OS prefilled and a
    /// reminder to attach the just-saved bundle. Best-effort — the saved file
    /// is the part that matters if no browser opens.</summary>
    static void OpenGitHubIssue(string? note, string bundleFileName)
    {
        try
        {
            string body =
                "**What happened?**\n\n" +
                (string.IsNullOrWhiteSpace(note) ? "(describe the problem)" : note) + "\n\n" +
                "**Diagnostic bundle**\n\n" +
                $"`{bundleFileName}` was just saved to your Desktop — drag it into this box.\n\n" +
                "**Build**\n" +
                $"- UnifiedRGB {AppVersion}\n" +
                $"- Windows {Environment.OSVersion.Version}\n";
            string url = $"https://github.com/{UpdateClient.GitHubRepo}/issues/new" +
                         $"?title={Uri.EscapeDataString("[bug] ")}&body={Uri.EscapeDataString(body)}";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("support", $"couldn't open issue page: {ex.Message}"); }
    }

    /// <summary>Run the diagnostic in an elevated copy of this exe (UAC prompt)
    /// so the admin-only sections are included. Null = declined/failed; the
    /// caller falls back to the in-process non-admin report.</summary>
    static string? TryElevatedCollect(Action<string> status)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"unifiedrgb-diag-{Guid.NewGuid():N}.txt");
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
                status("requesting admin rights for the full hardware scan..."));
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--collect-diag \"{tmp}\"",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (p == null) return null;
            Application.Current.Dispatcher.Invoke(() =>
                status("collecting hardware report (elevated, ~15s)..."));
            if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } return null; }
            return File.Exists(tmp) ? File.ReadAllText(tmp) : null;
        }
        catch { return null; }               // UAC declined (Win32Exception) etc.
        finally { try { File.Delete(tmp); } catch { } }
    }

    /*-----------------------------------------------------*\
    | Admin inbox (developer machine only)                  |
    \*-----------------------------------------------------*/

    public async Task<(bool Ok, string Message, List<SupportUpload.ReportSummary> Reports)> ListAsync()
    {
        if (ReadAdminKey() is not string key)
            return (false, "can't read admin.key", new());
        return await SupportUpload.ListReportsAsync(key);
    }

    /// <summary>Fetch a report, save its full text under reports\, and return
    /// the one-line description + path for the clipboard.</summary>
    public async Task<(bool Ok, string Message, string ClipboardLine)> SaveReportAsync(
        SupportUpload.ReportSummary r)
    {
        if (ReadAdminKey() is not string key) return (false, "can't read admin.key", "");
        var (ok, msg, text) = await SupportUpload.GetReportTextAsync(key, r.Id);
        if (!ok) return (false, msg, "");

        string dir = AppPaths.Config("reports");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"report-{r.Id}-{Sanitize(r.User)}.txt");
        File.WriteAllText(file, text);
        string line =
            $"UnifiedRGB support report from {r.User}@{r.Machine}, " +
            $"{r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} local, kind={r.Kind}, app {r.AppVersion} — " +
            $"full text: {file}";
        return (true, $"saved ({text.Length:n0} chars) + path on clipboard — paste it to Claude", line);
    }

    public async Task<(bool Ok, string Message)> DeleteAsync(string id)
    {
        if (ReadAdminKey() is not string key) return (false, "can't read admin.key");
        return await SupportUpload.DeleteReportAsync(key, id);
    }

    static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
