using System.IO;
using System.Windows;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>The support pipeline's app side: the one-button diagnostic+log
/// bundle collection. Extracted from the view model; the VM keeps only
/// bindable state and thin wrappers.</summary>
public sealed class SupportService
{
    static string AppVersion => AppInfo.VersionString;

    /// <summary>Full hardware survey + session log + note, as one upload.
    /// Collection shells out to WMI, so call from off the UI thread context;
    /// progress lands in the status callback.</summary>
    /// <param name="appState">What the app itself is doing right now. The
    /// report is a hardware survey; without this a bundle could say which
    /// devices exist and nothing about what was sent to them, which is the
    /// half every lighting question needs.</param>
    public async Task<(bool Ok, string Message)> SendBundleAsync(string? note, Action<string> status,
                                                                Func<string>? appState = null)
    {
        // Collected HERE, on the calling thread, not inside the Task.Run
        // below: it reads the device collection and the composed frames, which
        // belong to the UI thread.
        string state;
        try { state = appState?.Invoke() ?? "(not collected)"; }
        catch (Exception ex) { state = $"(app state failed: {ex.Message})"; }

        string bundle = await Task.Run(() =>
        {
            string diag;
            try
            {
                // The manifest requires administrator, so the in-process
                // report always includes the admin-only SMBus/RAM scan.
                diag = DiagnosticReport.Collect(section =>
                    Application.Current.Dispatcher.Invoke(() => status($"collecting: {section}...")));
            }
            catch (Exception ex) { diag = $"(diagnostic failed: {ex})"; }

            string log;
            try { log = File.ReadAllText(Log.FilePath); }
            catch (Exception ex) { log = $"(log unavailable: {ex.Message})"; }

            // The rotated half too. Once a log passed the cap, every future
            // bundle silently dropped the entire history before it, which is
            // usually where the problem started.
            string older = "";
            try
            {
                string old = Log.FilePath + ".old";
                if (File.Exists(old))
                    older = "\r\n\r\n==============================================\r\n"
                          + " EARLIER LOG (unifiedrgb.log.old)\r\n"
                          + "==============================================\r\n"
                          + File.ReadAllText(old);
            }
            catch { /* the current log is the part that matters */ }

            return diag
                + "\r\n\r\n==============================================\r\n"
                + " UNIFIEDRGB STATE\r\n"
                + "==============================================\r\n"
                + state
                + "\r\n\r\n==============================================\r\n"
                + " APP LOG (unifiedrgb.log)\r\n"
                + "==============================================\r\n"
                + log
                + older;
        });

        bundle = Redact(bundle);

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
            // The note is scrubbed too. It was concatenated AFTER the redaction,
            // which made the one field people type their own name or email into
            // the one field that was never cleaned.
            string header = string.IsNullOrWhiteSpace(note) ? "" : $"note: {Redact(note)}\r\n\r\n";
            await File.WriteAllTextAsync(outPath, header + bundle);
            OpenGitHubIssue(note, fileName);
            return (true, $"bundle saved to your Desktop — drag {fileName} into the GitHub issue that just opened");
        }
        catch (Exception ex)
        {
            return (false, $"couldn't save the bundle: {ex.Message}");
        }
    }

    /// <summary>Shared with the standalone diagnostic exe, which used to write
    /// its report with no scrubbing at all. See Core/Redaction.</summary>
    static string Redact(string text) => UnifiedRgb.Core.Redaction.Scrub(text);

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
                $"`{bundleFileName}` was just saved to your Desktop. Drag it into this box.\n\n" +
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
}
