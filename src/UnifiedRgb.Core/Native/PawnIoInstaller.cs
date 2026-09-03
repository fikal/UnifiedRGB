using System.Diagnostics;
using System.Net.Http;

namespace UnifiedRgb.Core.Native;

/// <summary>One-click install of the PawnIO driver (official signed setup
/// from the author's release feed). PawnIO unlocks CPU temperature, RAM RGB
/// (SMBus) and motherboard fan control — machines without it show
/// dashes and empty fan lists. Tries silent first; if that doesn't land it runs
/// the installer's own UI (the user clicked the button, they're present).
///
/// The setup's CLI is `-install [-silent]` (single dash) — NOT NSIS `/S`. An
/// older build passed `/S`, and this newer setup answered with an "Unknown
/// argument: /S" usage dialog on a field machine and installed nothing.</summary>
public static class PawnIoInstaller
{
    const string Url = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    public static bool IsInstalled
    {
        get { try { return PawnIO.IsAvailable; } catch { return false; } }
    }

    public static async Task<bool> InstallAsync(Action<string>? status = null)
    {
        try
        {
            status?.Invoke("downloading PawnIO installer...");
            string path = Path.Combine(Path.GetTempPath(), $"PawnIO_setup-{Guid.NewGuid():N}.exe");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                    await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(Url));
                // %TEMP% is writable by every same-user process, so a file that is
                // verified by path and launched by path later can be swapped in
                // between (the per-attempt name only hides it from a fixed-path
                // watcher). Hold a read handle with no write/delete sharing from
                // before the signature check until the last launch has returned:
                // CreateProcess's read/execute open is compatible with it, while
                // any rewrite, rename or delete of the file gets a sharing violation.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // We are about to RUN this elevated and it installs a kernel driver.
                    // TLS proves who served the bytes; Authenticode proves who signed them.
                    // Publisher pinned to PawnIO's author (matches the installed
                    // PawnIOLib.dll's signer, checked 2026-09-02).
                    if (!Authenticode.IsSignedBy(path, "CN=namazso.eu", out var signer))
                    {
                        Log.Warn("pawnio", $"installer signature check FAILED: {signer} - refusing to run it");
                        status?.Invoke("PawnIO installer failed its signature check — not installed");
                        return false;
                    }
                    Log.Info("pawnio", $"installer signature OK ({signer})");

                    status?.Invoke("installing PawnIO...");
                    using var silent = Process.Start(new ProcessStartInfo
                    {
                        FileName = path, Arguments = "-install -silent", UseShellExecute = true,
                    });
                    // A wait timeout is not a failure - the installer may still be running.
                    try { if (silent != null) await silent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
                    catch (TimeoutException) { Log.Warn("pawnio", "silent installer still running after 2 min"); }

                    if (!IsInstalled)
                    {
                        // Silent install didn't land — run the installer's own UI so the
                        // user can complete it (still the -install action, just no -silent).
                        status?.Invoke("finish the install in the PawnIO window...");
                        using var visible = Process.Start(new ProcessStartInfo
                        {
                            FileName = path, Arguments = "-install", UseShellExecute = true,
                        });
                        try { if (visible != null) await visible.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10)); }
                        catch (TimeoutException) { Log.Warn("pawnio", "installer window still open after 10 min"); }
                    }
                }

                bool ok = IsInstalled;
                Log.Info("pawnio", ok ? "PawnIO installed" : "PawnIO still not present after installer ran");
                status?.Invoke(ok
                    ? "PawnIO installed — rescanning..."
                    : "PawnIO doesn't appear to be installed");
                return ok;
            }
            finally
            {
                // Refused, finished, timed out or only partly downloaded: don't
                // leave a multi-MB installer per attempt in %TEMP%. Fails
                // harmlessly while a timed-out installer still has the image
                // mapped, and is a no-op when the download never created the file.
                try { File.Delete(path); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("pawnio", $"install failed: {ex.Message}");
            status?.Invoke($"PawnIO install failed: {ex.Message}");
            return false;
        }
    }
}
