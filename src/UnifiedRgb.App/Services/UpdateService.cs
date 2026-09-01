using System.IO;
using System.Windows;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>Self-update: startup check against the feed, and the download →
/// verify → batch-swap → restart install flow. Extracted from the view model;
/// status flows back through the two callbacks.</summary>
public sealed class UpdateService(Action<string> setText, Action<bool> setAvailable)
{
    bool _running;

    static string PendingMarker => Path.Combine(Path.GetTempPath(), "unifiedrgb-update-pending.txt");
    static string SwapResult => Path.Combine(Path.GetTempPath(), "unifiedrgb-swap-result.txt");

    static Version LocalVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public async Task CheckAsync(bool allowGitHub = true)
    {
        // Dev builds run from the build tree; self-replacing them would fight
        // the next compile. Updates only apply to deployed copies.
        string exe = Environment.ProcessPath ?? "";
        if (exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)) return;

        // Public builds check GitHub Releases; that's the one outbound request
        // an unconfigured build makes, and the user can turn it off.
        if (!Backend.Configured && !allowGitHub) return;

        var latest = await UpdateClient.GetLatestAsync();
        if (latest == null || !Version.TryParse(latest.Version, out var server)) return;
        var local = LocalVersion;

        // A pending marker from a previous install attempt that still finds us
        // on the old version = the swap script never replaced the exe.
        if (File.Exists(PendingMarker))
        {
            string wanted = File.ReadAllText(PendingMarker).Trim();
            if (wanted == local.ToString(3))
                Log.Info("update", $"self-update to {wanted} completed");
            else
                Log.Warn("update", $"previous self-update to {wanted} did NOT take effect (still {local.ToString(3)})");
            try { File.Delete(PendingMarker); } catch { }
        }

        // The swap script reports how its last run ended ("ok N" = swapped on
        // try N, "gave-up N" = lock/permissions never released) so a failed
        // install is never silent — it lands in the log the Send button ships.
        if (File.Exists(SwapResult))
        {
            string result = File.ReadAllText(SwapResult).Trim();
            if (result.StartsWith("ok")) Log.Info("update", $"swap script: {result}");
            else Log.Warn("update", $"swap script FAILED: {result} — exe was not replaced (still locked, or the folder needs admin rights)");
            try { File.Delete(SwapResult); } catch { }
        }

        if (server <= local) return;

        setText($"Update v{latest.Version} — install");
        setAvailable(true);
        Log.Info("update", $"newer build available: {latest.Version} ({latest.Size:n0} bytes)");
    }

    /// <summary>Download the new build, verify its published SHA-256, then hand
    /// off to a batch script that swaps the exe once its lock releases and
    /// restarts. See inline comments for the field lessons baked in here.</summary>
    public async Task InstallAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            string target = Environment.ProcessPath!;
            // Unique per attempt: a stale swap script from a prior attempt must
            // never move a file a newer attempt is still downloading into.
            string temp = Path.Combine(Path.GetTempPath(), $"UnifiedRGB-update-{Environment.ProcessId}.exe");
            var latest = await UpdateClient.GetLatestAsync();   // one fetch: hash + marker version
            setText("downloading 0%");
            string? err = await UpdateClient.DownloadAsync(temp,
                pct => Application.Current.Dispatcher.Invoke(() => setText($"downloading {pct}%")),
                latest?.DownloadUrl);   // GitHub asset when that's the source; feed otherwise
            if (err != null) { setText(err); _running = false; return; }

            // Integrity: the publisher registered the build's SHA-256; refuse
            // a download that doesn't match (corruption or tampering).
            if (!string.IsNullOrEmpty(latest?.Sha256))
            {
                string got = await Task.Run(() => UpdateClient.HashFile(temp));
                if (!string.Equals(got, latest!.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("update", $"download hash mismatch: expected {latest.Sha256}, got {got}");
                    try { File.Delete(temp); } catch { }
                    setText("update failed integrity check — try again");
                    _running = false;
                    return;
                }
            }

            // IDENTITY (not just integrity): the feed's version is metadata a
            // publish can get wrong - the payload can be an OLDER build mislabeled
            // as new (a stale-artifact publish). SHA only proves the bytes are the
            // ones the feed points at, not that those bytes are actually newer. So
            // read the DOWNLOADED binary's real version and refuse to swap unless
            // it's genuinely newer than us - otherwise the swap "succeeds", we
            // relaunch, and land right back on the old version (silent no-op).
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(temp);
                if (!Version.TryParse(vi.FileVersion, out var raw))
                {
                    Log.Error("update", $"downloaded build has no readable version ('{vi.FileVersion}') — refusing swap");
                    try { File.Delete(temp); } catch { }
                    setText("update payload looked wrong — not installed");
                    _running = false;
                    return;
                }
                var dl = new Version(raw.Major, raw.Minor, raw.Build);
                if (dl <= LocalVersion)
                {
                    Log.Error("update", $"downloaded build is {dl} — not newer than installed {LocalVersion}; the '{latest?.Version}' feed entry points at a stale binary. Refusing swap (bad publish).");
                    try { File.Delete(temp); } catch { }
                    setText($"update payload was v{dl}, not newer — not installed");
                    _running = false;
                    return;
                }
                if (Version.TryParse(latest?.Version, out var feed)
                    && new Version(feed.Major, feed.Minor, feed.Build) != dl)
                    Log.Warn("update", $"feed says {latest!.Version} but the binary is {dl}; installing the binary's real version");
            }
            catch (Exception ex)
            {
                Log.Warn("update", $"could not read downloaded build version: {ex.Message} — proceeding on SHA match");
            }

            // Retry the move itself until the exe's lock releases — process
            // polling (tasklist|findstr) proved racy in the field: a misfire
            // moved against the still-locked exe, failed silently, and
            // relaunched the OLD version. If the swap never succeeds (user
            // cancelled the close, exe folder needs admin), start nothing —
            // a second instance would fight the first for devices — but WRITE
            // the outcome so the next launch can log what happened. 90 tries
            // = a 3-minute window for slow shutdowns (save prompt, HID teardown).
            // Field case (1.0.5→1.0.6): the window closes but the process
            // can outlive it holding the exe lock (device/bridge teardown), so
            // after a minute of failed moves the script force-kills our exact
            // PID — we're mid-update, the process asked to be replaced — and
            // keeps retrying. "gave-up" after that = folder permissions.
            Log.Info("update", $"installing {latest?.Version} over {target}");
            string bat = Path.Combine(Path.GetTempPath(), "unifiedrgb-update.bat");
            File.WriteAllText(bat, $"""
                @echo off
                set n=0
                :loop
                set /a n+=1
                if %n% gtr 90 goto fail
                if %n% equ 30 taskkill /f /pid {Environment.ProcessId} >nul 2>&1
                timeout /t 2 /nobreak >nul
                move /y "{temp}" "{target}" >nul 2>&1
                if errorlevel 1 goto loop
                echo ok %n%>"{SwapResult}"
                start "" "{target}"
                goto done
                :fail
                echo gave-up %n%>"{SwapResult}"
                :done
                del "%~f0"
                """);
            // Marker: next launch verifies the swap actually took (CheckAsync).
            try
            {
                if (latest != null) File.WriteAllText(PendingMarker, latest.Version);
            }
            catch { }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            setText("restarting...");
            Application.Current.MainWindow?.Close();   // runs the save prompt if needed
        }
        catch
        {
            _running = false;
            throw;
        }
    }
}
