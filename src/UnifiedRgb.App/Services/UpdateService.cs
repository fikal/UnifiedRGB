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

    static Version LocalVersion => AppInfo.Version;

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
        if (latest == null || !Version.TryParse(latest.Version, out var serverRaw)) return;
        // Normalize to 3 parts like LocalVersion and the downloaded-binary check:
        // a 4-part tag (v1.0.18.0) compared "newer" against 1.0.18 (undefined
        // revision = -1), then InstallAsync refused the identical binary as "not
        // newer" - a permanent install button that never installed.
        var server = new Version(serverRaw.Major, serverRaw.Minor, Math.Max(0, serverRaw.Build));
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
            string targetDir = Path.GetDirectoryName(target)!;
            // Staged NEXT TO the exe, not in %TEMP%, with per-attempt random
            // names. We run elevated; %TEMP% is writable by every medium-IL
            // process of the same user, and cmd re-reads a .bat from disk on
            // every `goto` - a fixed-name script there was a same-user path to
            // elevated code execution during the 3-minute swap window, and the
            // verified payload could be swapped after the hash check. In the
            // exe's own folder the files inherit whatever protects the exe (and
            // if that folder is user-writable there was never a boundary). Same
            // volume also makes the final `move` a rename. Unique names keep a
            // stale script from a prior attempt off a newer attempt's download.
            string stamp = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
            string temp = Path.Combine(targetDir, $"UnifiedRGB-update-{stamp}.exe");
            var latest = await UpdateClient.GetLatestAsync();
            string? sha = latest != null ? await UpdateClient.ResolveShaAsync(latest) : null;
            setText("downloading 0%");
            string? err = await UpdateClient.DownloadAsync(temp,
                pct => Application.Current.Dispatcher.Invoke(() => setText($"downloading {pct}%")),
                latest?.DownloadUrl);   // GitHub asset when that's the source; feed otherwise
            if (err != null) { setText(err); _running = false; return; }

            // Integrity: the publisher registered the build's SHA-256; refuse
            // a download that doesn't match (corruption or tampering).
            if (!string.IsNullOrEmpty(sha))
            {
                string got = await Task.Run(() => UpdateClient.HashFile(temp));
                if (!string.Equals(got, sha, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error("update", $"download hash mismatch: expected {sha}, got {got}");
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
            string bat = Path.Combine(targetDir, $"unifiedrgb-update-{stamp}.bat");
            // taskkill is filtered to OUR image name: after a minute the PID may
            // already belong to an unrelated process (Windows recycles PIDs fast).
            string imageName = Path.GetFileName(target);
            // Re-verify the payload right before every move attempt - closes the
            // window between the managed hash check and the swap.
            string verify = string.IsNullOrEmpty(sha) ? "" :
                $"certutil -hashfile \"{temp}\" SHA256 | findstr /i /c:\"{sha}\" >nul || goto tampered";
            File.WriteAllText(bat, $"""
                @echo off
                set n=0
                :loop
                set /a n+=1
                if %n% gtr 90 goto fail
                if %n% equ 30 taskkill /f /pid {Environment.ProcessId} /fi "IMAGENAME eq {imageName}" >nul 2>&1
                timeout /t 2 /nobreak >nul
                {verify}
                move /y "{temp}" "{target}" >nul 2>&1
                if errorlevel 1 goto loop
                echo ok %n%>"{SwapResult}"
                start "" "{target}"
                goto done
                :tampered
                echo tampered %n%>"{SwapResult}"
                del "{temp}" >nul 2>&1
                goto done
                :fail
                echo gave-up %n%>"{SwapResult}"
                :done
                del "%~f0"
                """);

            void StartSwap()
            {
                // Marker: next launch verifies the swap actually took (CheckAsync).
                try { if (latest != null) File.WriteAllText(PendingMarker, latest.Version); } catch { }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{bat}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            void Abandon()
            {
                try { File.Delete(temp); } catch { }
                try { File.Delete(bat); } catch { }
                _running = false;
                setText($"Update v{latest?.Version} — install");
                Log.Info("update", "install cancelled at the close prompt - nothing swapped");
            }

            setText("restarting...");
            var win = Application.Current.MainWindow;
            if (win == null) { StartSwap(); return; }
            // Start the swap script only once the window has ACTUALLY closed.
            // Closing runs the save prompt; before this the script was already
            // running when the user hit Cancel there, and 60 s later taskkill
            // took the app down with their unsaved work. Our Closing handler is
            // subscribed after the window's own, so it sees e.Cancel.
            System.ComponentModel.CancelEventHandler? onClosing = null;
            EventHandler? onClosed = null;
            onClosing = (_, e) =>
            {
                if (!e.Cancel) return;
                win.Closing -= onClosing; win.Closed -= onClosed;
                Abandon();
            };
            onClosed = (_, _) => { win.Closing -= onClosing; win.Closed -= onClosed; StartSwap(); };
            win.Closing += onClosing;
            win.Closed += onClosed;
            win.Close();
        }
        catch
        {
            _running = false;
            throw;
        }
    }
}
