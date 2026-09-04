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
    public async Task<(bool Ok, string Message)> SendBundleAsync(string? note, Action<string> status)
    {
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
