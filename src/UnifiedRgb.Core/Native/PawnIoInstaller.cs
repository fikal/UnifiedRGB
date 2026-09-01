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
            string path = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(Url));

            status?.Invoke("installing PawnIO...");
            var silent = Process.Start(new ProcessStartInfo
            {
                FileName = path, Arguments = "-install -silent", UseShellExecute = true,
            });
            if (silent != null) await silent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));

            if (!IsInstalled)
            {
                // Silent install didn't land — run the installer's own UI so the
                // user can complete it (still the -install action, just no -silent).
                status?.Invoke("finish the install in the PawnIO window...");
                var visible = Process.Start(new ProcessStartInfo
                {
                    FileName = path, Arguments = "-install", UseShellExecute = true,
                });
                if (visible != null) await visible.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            }

            bool ok = IsInstalled;
            Log.Info("pawnio", ok ? "PawnIO installed" : "PawnIO still not present after installer ran");
            status?.Invoke(ok
                ? "PawnIO installed — rescanning..."
                : "PawnIO doesn't appear to be installed");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("pawnio", $"install failed: {ex.Message}");
            status?.Invoke($"PawnIO install failed: {ex.Message}");
            return false;
        }
    }
}
