using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| Manages a bundled, invisible OpenRGB instance so friends    |
| never have to install or touch OpenRGB themselves:          |
|   download official portable build (pinned URL) ->          |
|   launch "--server --startminimized --config <ours>" ->     |
|   wait for the SDK port. GPL note: the binary is aggregated |
|   unmodified next to us and spoken to over a socket — no    |
|   linking; license text + source pointer written alongside. |
\*-----------------------------------------------------------*/
public static class OpenRgbManager
{
    // The OpenRGB 1.0 release (2026-09-11): openrgb.org links its Windows
    // downloads to these Codeberg release assets, which stay up for good. The
    // pin used to be a GitLab master CI job, whose artifact GitLab deletes after
    // 30 days: every pin expired, and after expiry a machine with no OpenRGB
    // installed could not get one. Pinned to the exact bytes as well, because
    // this process runs elevated and launches what it unpacks; there is NO
    // fallback to an unverified download. A machine that has an older build
    // installed keeps running it, with a warning, if this one cannot be fetched
    // (see EnsureRunningAsync).
    //
    // To move to a newer release, take the "Windows_64 ... .zip" asset from its
    // Codeberg release, check its size against the release API, and update all
    // three constants together. 1.0 kept every launch flag (--server,
    // --server-port, --loglevel, --config) and the Detectors.detectors config
    // format this class relies on; check both again on a version bump.
    internal const string BundleVersion = "release-1.0-81bbe18";
    internal const string PinnedUrl = "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.0/OpenRGB_1.0_Windows_64_81bbe18.zip";
    internal const string BundleSha256 = "182a52a3c97c4c4ae52c80286b4260c9666c51c3447dbc416ee8945f66192e90";   // 22,012,179 bytes
    public const int Port = 6742;

    // ProgramData, administrators only - not LocalAppData. This process runs
    // elevated and LAUNCHES what it finds under Root, and every process of the
    // user can write to LocalAppData without elevation: the bundle there was a
    // binary of anyone's choosing, run as administrator at every logon. The old
    // per-user root is only ever looked at to carry the detector config over
    // once and to sweep an instance still running from it (see ProtectedFolder).
    static readonly string Root = AppPaths.Machine("openrgb");
    static readonly string LegacyRoot = AppPaths.Local("openrgb");
    const string ConfigName = "config", StampName = "bundle-version.txt";
    static readonly string ConfigDir = Path.Combine(Root, ConfigName);
    static readonly string StampPath = Path.Combine(Root, StampName);

    /// <summary>Is this image path inside a tree the app launches OpenRGB from,
    /// now or before the move to ProgramData. For the conflict-process report,
    /// which must flag a USER-installed OpenRGB and not the bundled one.</summary>
    public static bool IsBundledPath(string path)
        => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(LegacyRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>The root is ours - administrators only, made by us - or the
    /// bundle is not used. Runs before every install and every launch, because
    /// a launch trusts whatever it finds under Root: that has to have been put
    /// there by an administrator, and anything somebody else planted under the
    /// name (a folder, a junction) is moved aside here (after which nothing is
    /// installed, and the install path downloads a fresh copy). False = do
    /// not launch.</summary>
    static bool SecureRoot(Action<string>? status)
    {
        bool ok = ProtectedFolder.Ensure(AppPaths.MachineDir, out string? why)
               && ProtectedFolder.Ensure(Root, out why);
        _installedStamp = 0;   // the folder may just have been emptied: ask again
        if (ok) return true;
        status?.Invoke("OpenRGB could not be set up in a folder only administrators can write");
        Log.Warn("openrgb", $"refusing to use {Root}: {why}");
        return false;
    }

    /// <summary>The detector state the bundle kept in its old per-user home
    /// (which detectors are off, which one crashed the scan) carried into the
    /// protected root, once: JSON the app and OpenRGB read as data, never
    /// anything either of them runs. The binary itself is NOT carried over -
    /// it was in a folder anyone could write to, so it is downloaded again.</summary>
    static void CarryOverLegacyConfig()
    {
        try
        {
            string old = Path.Combine(LegacyRoot, "config");
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(ConfigDir);
            int carried = 0;
            foreach (string name in new[] { "OpenRGB.json", "crash-bisect.json" })
            {
                string from = Path.Combine(old, name), to = Path.Combine(ConfigDir, name);
                if (File.Exists(from) && !File.Exists(to)) { File.Copy(from, to); carried++; }
            }
            if (carried > 0) Log.Info("openrgb", $"detector config carried over from {old}");
        }
        catch (Exception ex) { Log.Warn("openrgb", $"could not carry the old detector config over: {ex.Message}"); }
    }

    /// <summary>The old per-user bundle, gone once the protected one is in.
    /// Best effort: a copy that will not delete is logged and left, and the
    /// launch path never looks there again either way.</summary>
    static void RetireLegacyRoot()
    {
        try
        {
            if (!Directory.Exists(LegacyRoot)) return;
            Directory.Delete(LegacyRoot, recursive: true);
            Log.Info("openrgb", $"removed the old per-user bundle at {LegacyRoot}");
        }
        catch (Exception ex) { Log.Warn("openrgb", $"old bundle at {LegacyRoot} left in place: {ex.Message}"); }
    }

    static Process? _proc;

    // Memoized 30s: this is a PROPERTY doing a recursive directory enumeration
    // plus a file read — trivially called in loops (launch retries re-ran it
    // per attempt). Install/uninstall invalidate by writing the stamp anyway.
    static bool _installedCache;
    static long _installedStamp;
    public static bool IsInstalled
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - _installedStamp > 30_000)
            {
                _installedCache = FindExe() != null &&
                    File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == BundleVersion;
                _installedStamp = now;
            }
            return _installedCache;
        }
    }

    /// <summary>True when an OpenRGB backend is reachable on the bridge port:
    /// the bundled instance or the user's own install. NOT our own SDK server:
    /// it also defaults to 6742 and answers the same probe, and our own SDK
    /// server answering the protocol probe is not a bridge backend. Treating
    /// it as one made EnsureRunningAsync report success without launching
    /// OpenRGB, after which the bridge had nothing behind it. The app moves
    /// the SDK server off this port before starting the bridge; until it has,
    /// this says "down" so the launch path runs.</summary>
    public static bool IsServerUp() =>
        !OpenRgbServer.IsOwnListener(Port) && OpenRgbClient.IsServerUp(port: Port);

    static string? FindExe() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>Download + extract the pinned build (idempotent per version).
    /// Upgrades in place when the pinned version changes, preserving the
    /// managed config (detector tweaks) across the swap. Fails closed: a
    /// download that fails, or bytes that are not exactly the pinned build,
    /// install nothing and leave whatever was there untouched.</summary>
    public static async Task InstallAsync(Action<string>? status = null)
    {
        if (IsInstalled) return;
        if (!SecureRoot(status))
            throw new InvalidOperationException("the OpenRGB folder could not be made administrators-only");
        Directory.CreateDirectory(Root);

        status?.Invoke("downloading OpenRGB...");
        byte[] bytes;
        // Bounded: the pinned build is 22 MB, and the hash refuses anything
        // else anyway, so there is no reason to buffer more than this.
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 64L * 1024 * 1024 })
        {
            try
            {
                bytes = await http.GetByteArrayAsync(PinnedUrl);
                Log.Info("openrgb", $"downloaded pinned build ({bytes.Length:n0} bytes) from {PinnedUrl}");
            }
            catch (Exception ex)
            {
                string why = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                    ? "the release download is gone (404)" : ex.Message;
                status?.Invoke("the pinned OpenRGB build could not be downloaded - nothing was installed");
                Log.Warn("openrgb", $"the pinned OpenRGB build {BundleVersion} is unavailable from {PinnedUrl}: {why}. "
                    + "Nothing was installed; there is no unverified fallback.");
                throw new InvalidOperationException($"the pinned OpenRGB build is unavailable ({why}); nothing was installed", ex);
            }
        }

        status?.Invoke("extracting...");
        // Never upgrade under a running instance - but only stopped now, with
        // the new build in hand: stopping before the download left the bridge
        // dead whenever the download then failed.
        KillBundled();
        try { InstallVerified(bytes, BundleSha256, Root, BundleVersion); }
        catch (UnverifiedBuildException ex)
        {
            status?.Invoke("the downloaded OpenRGB does not match the pinned build - nothing was installed");
            Log.Warn("openrgb", $"{ex.Message}. Nothing was installed.");
            throw;
        }
        _installedStamp = 0;   // invalidate the IsInstalled memo immediately
        Log.Info("openrgb", $"installed {BundleVersion} to {Root}");
        CarryOverLegacyConfig();
        RetireLegacyRoot();
    }

    /// <summary>The downloaded archive is not the build this version pins.</summary>
    internal sealed class UnverifiedBuildException(string message) : Exception(message);

    /// <summary>Verify <paramref name="archive"/> against the pinned SHA-256
    /// BEFORE a single file is touched, then swap it in under
    /// <paramref name="root"/>: the previous build cleared (config/ kept),
    /// the archive unpacked from the very bytes that were hashed, and the
    /// version stamp written LAST, so a stamp on disk always means a verified,
    /// complete install. Throws UnverifiedBuildException on a mismatch, with
    /// root left exactly as it was. Takes the root so the harness can run it
    /// against a scratch folder.</summary>
    internal static void InstallVerified(byte[] archive, string expectedSha256, string root, string version)
    {
        string got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive));
        if (!string.Equals(got, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new UnverifiedBuildException(
                $"the downloaded OpenRGB ({archive.Length:n0} bytes, SHA-256 {got.ToLowerInvariant()}) does not match "
                + $"the pinned build {version} (SHA-256 {expectedSha256})");

        // Clear the previous build but keep config/ (detector state).
        string configDir = Path.Combine(root, ConfigName), stampPath = Path.Combine(root, StampName);
        foreach (var dir in Directory.GetDirectories(root))
            if (!string.Equals(dir, configDir, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(dir, recursive: true); } catch { }   // a file still held: the fresh unpack overwrites it anyway
        foreach (var file in Directory.GetFiles(root))
            try { File.Delete(file); } catch { }   // ...same: leftovers are overwritten, and a half-cleared root is not a reason to refuse the install
        // Not best effort: a stamp that survived would vouch for whatever the
        // unpack below leaves behind if it fails part way.
        File.Delete(stampPath);

        using (var zip = new MemoryStream(archive, writable: false))
            ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
        File.WriteAllText(Path.Combine(root, "LICENSE-NOTE.txt"),
            "This folder contains an unmodified official OpenRGB release (GPLv2),\r\n" +
            "the Windows build openrgb.org links to.\r\n" +
            "Source: https://codeberg.org/OpenRGB/OpenRGB\r\n" +
            "UnifiedRGB communicates with it over its SDK network protocol only.\r\n");
        File.WriteAllText(stampPath, version);
    }

    /// <summary>Ensure a server is running: reuse an existing one, else launch
    /// our bundled copy (installing first if needed). Returns false on failure.</summary>
    public static async Task<bool> EnsureRunningAsync(Action<string>? status = null)
    {
        if (IsServerUp()) return true;
        int generation = Volatile.Read(ref _stopGeneration);
        // The port is spoken for from HERE, download included: a first-run
        // install takes minutes, and the app has already moved its own SDK
        // server aside for us. A Rescan in that window re-created it on 6742
        // and the launch that followed could never bind.
        Interlocked.Exchange(ref _launching, 1);
        try
        {
            try { await InstallAsync(status); }
            catch (Exception ex) when (FindExe() != null)
            {
                // The pinned build could not be installed (offline, its download
                // expired, a mismatch) but a build installed earlier is still in
                // the administrators-only folder. Keep the bridge running on it:
                // a version bump used to read every existing install as missing
                // and leave the bridge dead until a download succeeded.
                Log.Warn("openrgb", $"the pinned build {BundleVersion} could not be installed ({ex.Message}); "
                    + "running the OpenRGB already installed instead");
                status?.Invoke("could not fetch the pinned OpenRGB - running the one already installed");
            }
            return Launch(status, generation);
        }
        catch (Exception ex)
        {
            Log.Error("openrgb", ex);
            status?.Invoke($"OpenRGB setup failed: {ex.Message}");
            Log.Warn("openrgb", $"setup failed: {ex.Message}");
            return false;
        }
        finally { Interlocked.Exchange(ref _launching, 0); }
    }

    /// <summary>True while a launch (or relaunch loop) is in progress: the SDK
    /// port is spoken for even though nothing answers on it yet. The app's own
    /// SDK server consults this so a Rescan the user triggers mid-relaunch does
    /// not bind 6742 and leave OpenRGB unable to.</summary>
    public static bool Launching => Volatile.Read(ref _launching) != 0;
    static int _launching;

    /// <summary>Bumped by every Stop from outside (the bridge switched off, the
    /// app exiting). A launch compares it with the number it started under: a
    /// different one means its process was killed on purpose, which is neither
    /// a detector crash for the bisect to chase nor a reason to relaunch.</summary>
    static int _stopGeneration;

    static bool StoppedSince(int generation) => Volatile.Read(ref _stopGeneration) != generation;

    static bool Launch(Action<string>? status, int generation)
    {
        Interlocked.Exchange(ref _launching, 1);
        try
        {
            // Bounded relaunch loop: the crash-bisect (one buggy detector killing
            // the whole scan) and the first-run conflict-policy restart both work
            // by narrowing the detector config and relaunching. log2(~300
            // detectors) + slack fits comfortably in the bound.
            bool policyRestartDone = false;
            for (int attempt = 0; attempt < 14; attempt++)
            {
                if (StoppedSince(generation)) return false;
                switch (LaunchOnce(status, generation, ref policyRestartDone))
                {
                    case LaunchResult.Ok: return true;
                    case LaunchResult.Failed: return false;
                    case LaunchResult.Relaunch: KillBundled(); continue;
                }
            }
            status?.Invoke("OpenRGB kept restarting — giving up for this session");
            // Also to the log. The bridge going dark for a session takes half
            // someone's devices with it, and the only trace used to be that
            // [openrgb] lines simply stopped appearing. Absence of a line is the
            // one thing nobody can diagnose from.
            Log.Warn("openrgb", "kept restarting, giving up for this session");
            return false;
        }
        finally { Interlocked.Exchange(ref _launching, 0); }
    }

    enum LaunchResult { Ok, Failed, Relaunch }

    static LaunchResult LaunchOnce(Action<string>? status, int generation, ref bool policyRestartDone)
    {
        // Every launch, not only the install: what is about to run is whatever
        // sits under Root, and this is what says that only an administrator
        // could have put it there.
        if (!SecureRoot(status)) return LaunchResult.Failed;
        var exe = FindExe();
        if (exe == null)
        {
            status?.Invoke("OpenRGB.exe not found after install");
            Log.Warn("openrgb", "OpenRGB.exe not found after install");
            return LaunchResult.Failed;
        }
        KillBundled();             // never stack a second bundled instance on the port
        Directory.CreateDirectory(ConfigDir);
        bool hadConfig = File.Exists(Path.Combine(ConfigDir, "OpenRGB.json"));
        if (hadConfig) { ApplyConflictPolicy(); OpenRgbCrashBisect.ReapplyCulprits(ConfigDir); }
        status?.Invoke("starting OpenRGB...");
        // ShellExecute detaches stdio: with inherited handles the child keeps
        // any console pipe open forever (hangs redirected callers).
        // NO --startminimized: with it, a warning dialog (e.g. the PawnIO/I2C
        // one on non-elevated machines, whose "don't show again" is broken
        // upstream) becomes the app's ONLY window — closing it quits OpenRGB
        // via Qt's quit-on-last-window. Killed the bridge ~5s in, every time,
        // in the field. Instead the main window exists and we hide every
        // window of the process ourselves (dialogs included, harmless hidden).
        // --loglevel 6: verbose file log = crash evidence for reports.
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = LaunchArguments(ConfigDir),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        // A Stop that ran between the sweep above and this start found nothing
        // to kill: the instance just started would outlive the switch-off.
        if (StoppedSince(generation)) { KillBundled(); return LaunchResult.Failed; }
        if (_proc != null) HideWindowsOf(_proc, 30_000);
        for (int i = 0; i < 60; i++)
        {
            if (IsServerUp())
            {
                Log.Info("openrgb", "bundled server is up");
                // The SDK port opens BEFORE detection finishes — wait for the
                // device list to settle or every first query sees 0 devices.
                if (!WaitForDetection(status))
                {
                    if (StoppedSince(generation)) return Stopped(status);
                    // The server DIED during its hardware scan: a detector
                    // crashed on this machine's hardware. Hand it to the
                    // bisect; when armed it narrows the detector list and we
                    // relaunch until the culprit is isolated.
                    bool crash = ExitLooksLikeCrash();
                    if (crash && OpenRgbCrashBisect.OnCrash(ConfigDir))
                    {
                        status?.Invoke("OpenRGB crashed while scanning — isolating the responsible detector...");
                        return LaunchResult.Relaunch;
                    }
                    status?.Invoke(crash
                        ? "OpenRGB crashed while scanning your hardware — hit Send in Support so we can see which device"
                        : $"OpenRGB quit during its scan ({ExitCodeText()}) — not a detector crash; the OpenRGB log in Support has the reason");
                    return LaunchResult.Failed;
                }

                // Clean scan: if a bisect is mid-run this narrows further (or
                // convicts and re-enables the innocent); relaunch to continue.
                if (OpenRgbCrashBisect.OnSuccess(ConfigDir))
                    return LaunchResult.Relaunch;

                foreach (var culprit in OpenRgbCrashBisect.Culprits(ConfigDir))
                    LastPolicyNotes.Add($"Detector '{culprit}' crashed OpenRGB on this machine — disabled automatically");

                // First ever run: the config (with the detector list) only
                // exists now — apply the conflict policy and restart once so
                // vendor-owned hardware is never touched again.
                if (!hadConfig && !policyRestartDone && ApplyConflictPolicy() > 0)
                {
                    Log.Info("openrgb", "restarting to apply conflict policy after first run");
                    policyRestartDone = true;
                    return LaunchResult.Relaunch;
                }
                return LaunchResult.Ok;
            }
            Thread.Sleep(500);
            if (_proc is { HasExited: true })
            {
                // Died before the port even opened — also a detection crash
                // (early detectors run before the server socket comes up),
                // unless the exit code says it simply failed to start.
                if (StoppedSince(generation)) return Stopped(status);
                bool crash = ExitLooksLikeCrash();
                if (crash && OpenRgbCrashBisect.OnCrash(ConfigDir))
                {
                    status?.Invoke("OpenRGB crashed while scanning — isolating the responsible detector...");
                    return LaunchResult.Relaunch;
                }
                status?.Invoke(crash
                    ? "OpenRGB exited during startup"
                    : $"OpenRGB failed to start ({ExitCodeText()}) — not a detector crash; the OpenRGB log in Support has the reason");
                return LaunchResult.Failed;
            }
        }
        status?.Invoke("OpenRGB did not open its server port");
        Log.Warn("openrgb", "started but never opened its server port");
        return LaunchResult.Failed;
    }

    /// <summary>The server went away because it was stopped on purpose mid-start
    /// (the bridge switched off, the app exiting). Reading that as a crash fed an
    /// innocent detector to the bisect, and its relaunch fought the stop.</summary>
    static LaunchResult Stopped(Action<string>? status)
    {
        Log.Info("openrgb", "stopped while it was starting");
        status?.Invoke("stopped");
        return LaunchResult.Failed;
    }

    /// <summary>The bundled server's command line. --server-host 127.0.0.1:
    /// the SDK protocol has no password, and anyone who reaches the port drives
    /// every device OpenRGB found. OpenRGB 1.0 already defaults to loopback,
    /// but only until its config says otherwise; this does not depend on that.</summary>
    internal static string LaunchArguments(string configDir) =>
        $"--server --server-host 127.0.0.1 --server-port {Port} --loglevel 6 --config \"{configDir}\"";

    /// <summary>Did the bundled server die the way a detector crash dies (access
    /// violation, fast-fail, abort) rather than quit? Feeding an ordinary exit
    /// (missing VC++ runtime, AV block, a broken build, Qt's
    /// quit-on-last-window) to the bisect would convict an innocent detector
    /// permanently and hide the real failure.</summary>
    static bool ExitLooksLikeCrash()
    {
        if (_proc is not { HasExited: true }) return true;   // port gone but process alive: hung mid-scan, as before
        int code;
        try { code = _proc.ExitCode; } catch { return true; }   // race: it exited between the check and here. Treat as "worth retrying".
        if (code == 3) return true;                            // CRT abort()
        uint u = (uint)code;
        if (u < 0x80000000) return false;                      // ordinary exit code
        // NTSTATUS: a loader failure (missing/incompatible DLL) is not a crash.
        return u is not (0xC0000135 or 0xC0000142 or 0xC000007B or 0xC0000139);
    }

    static string ExitCodeText()
    {
        try { return _proc is { HasExited: true } p ? $"exit code 0x{(uint)p.ExitCode:X8}" : "server port closed"; }
        catch { return "exit code unknown"; }   // this string only ever goes INTO a log line the caller writes
    }

    /// <summary>Wait for the device list to settle. False = the server died
    /// mid-scan (detector crash), true = detection completed.</summary>
    static bool WaitForDetection(Action<string>? status)
    {
        try
        {
            status?.Invoke("waiting for OpenRGB device detection...");
            using var probe = OpenRgbClient.Connect(port: Port);
            int last = -1, stable = 0;
            for (int i = 0; i < 30; i++)
            {
                int n = probe.GetControllerCount();
                if (n == last) { if (++stable >= 3) break; }
                else { stable = 0; last = n; }
                Thread.Sleep(1000);
            }
            Log.Info("openrgb", $"detection settled at {Math.Max(last, 0)} device(s)");
            return true;
        }
        catch (Exception ex)
        {
            bool died = _proc is { HasExited: true } || !IsServerUp();
            Log.Warn("openrgb", $"detection wait: {ex.Message}{(died ? " (server DIED mid-scan)" : "")}");
            return !died;
        }
    }

    /// <summary>Stop every OpenRGB running from OUR install dir (never someone's
    /// own install), the old per-user one included. Sweeping by path is what
    /// actually guarantees the port is free — a tracked-handle kill alone can
    /// miss a survivor and leave the old instance owning 6742 with stale config.
    /// A launch in progress gives up rather than reading the kill as a crash.</summary>
    public static void Stop()
    {
        Interlocked.Increment(ref _stopGeneration);
        KillBundled();
    }

    /// <summary>The sweep alone, for a launch clearing the way for its own
    /// instance: that is not a stop, and must not cancel the launch.</summary>
    static void KillBundled()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path != null && IsBundledPath(path))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                    Log.Info("openrgb", $"bundled server stopped (pid {p.Id})");
                }
            }
            catch (Exception ex) { Log.Warn("openrgb", $"stop pid {p.Id} failed: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        _proc = null;
    }

    public static bool WeLaunchedIt => _proc is { HasExited: false };

    /// <summary>Device name → OpenRGB detector name, where they differ. HID
    /// detectors register under the device name; bus detectors don't always
    /// (the ENE SMBus detector reports devices called "ENE DRAM").</summary>
    static readonly Dictionary<string, string> DetectorAliases = new()
    {
        ["ENE DRAM"] = "ENE SMBus DRAM",
        ["ASUS Aura DRAM"] = "ASUS Aura SMBus DRAM",
    };

    /// <summary>Disable detectors by name in our managed config (best-effort:
    /// unknown names are ignored by OpenRGB). Returns true if the file changed
    /// — caller should restart the bundled instance for it to take effect.</summary>
    public static bool DisableDetectors(IEnumerable<string> names)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var expanded = names.SelectMany(n =>
                DetectorAliases.TryGetValue(n, out var alias) ? new[] { n, alias } : new[] { n });
            return OpenRgbDetectorConfig.Edit(ConfigDir,
                det => OpenRgbDetectorConfig.Set(det, expanded, enabled: false), createIfMissing: true);
        }
        catch (Exception ex)
        {
            Log.Warn("openrgb", $"detector config write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restart the bundled instance (after a detector-config change).</summary>
    public static bool Restart(Action<string>? status = null)
    {
        // Flagged across the Stop too, so the port reads as spoken for during
        // the gap between the old instance dying and the new one binding.
        Interlocked.Exchange(ref _launching, 1);
        try
        {
            int generation = Volatile.Read(ref _stopGeneration);
            KillBundled();
            return Launch(status, generation);
        }
        finally { Interlocked.Exchange(ref _launching, 0); }
    }

    /// <summary>After a bridge-up that skipped natively-driven devices: turn
    /// their detectors off in the managed config and restart, so OpenRGB stops
    /// touching hardware our own drivers own. True = restarted (rescan after).</summary>
    public static async Task<bool> ReleaseNativelyDrivenAsync(IReadOnlyCollection<string> skippedNames)
    {
        if (skippedNames.Count == 0 || !WeLaunchedIt) return false;
        if (!DisableDetectors(skippedNames)) return false;
        return await Task.Run(() => Restart());
    }

    /*-----------------------------------------------------*\
    | Conflict policy: vendor software that must keep its   |
    | hardware (fan/pump control!) — the bridge disables    |
    | those detectors instead of fighting. Two writers on   |
    | one device wire crashes OpenRGB (seen in the field:   |
    | NZXT CAM vs the Kraken/N9 keepalives).                |
    \*-----------------------------------------------------*/
    static readonly (string ProcessPrefix, string DetectorPrefix, string Reason)[] ConflictPolicies =
    {
        ("NZXT CAM", "NZXT", "NZXT CAM is running (it controls your fans/pump) — NZXT lighting stays with CAM"),
        ("iCUE", "Corsair", "Corsair iCUE is running — Corsair lighting stays with iCUE"),
        ("LConnect", "Lian Li", "L-Connect is running — Lian Li lighting stays with it"),
    };

    /// <summary>Detectors whose drivers destabilize OpenRGB in the field —
    /// disabled unconditionally until the device gets native support.
    /// (Empty right now; the mechanism stays for when a report names one.)</summary>
    static readonly (string DetectorPrefix, string Reason)[] UnstableDetectors =
        Array.Empty<(string, string)>();

    /// <summary>Human-readable notes for policies applied on the last launch.</summary>
    public static List<string> LastPolicyNotes { get; } = new();

    /// <summary>Disable detectors for vendors whose own software is running.
    /// Prefix-matches the detector names OpenRGB itself wrote into the managed
    /// config (so it tracks detector naming across versions). Returns the
    /// number of entries changed.</summary>
    static int ApplyConflictPolicy()
    {
        LastPolicyNotes.Clear();
        int changed = 0;
        try
        {
            OpenRgbDetectorConfig.Edit(ConfigDir, detectors =>
            {
                // One enumeration, names only, everything disposed — the old code
                // called Process.GetProcesses() INSIDE the policy loop (3x) and
                // never disposed the ~250 returned Process objects per pass.
                var running = new List<string>();
                foreach (var p in Process.GetProcesses())
                {
                    try { running.Add(p.ProcessName); } catch { }   // process died mid-enumeration; it is not a conflict if it is gone
                    finally { p.Dispose(); }
                }
                foreach (var (procPrefix, detPrefix, reason) in ConflictPolicies)
                {
                    if (!running.Any(n => n.StartsWith(procPrefix, StringComparison.OrdinalIgnoreCase))) continue;
                    changed += DisablePrefix(detectors, detPrefix, reason);
                }
                foreach (var (detPrefix, reason) in UnstableDetectors)
                    changed += DisablePrefix(detectors, detPrefix, reason);
                return changed > 0;
            });
        }
        catch (Exception ex) { Log.Warn("openrgb", $"conflict policy failed: {ex.Message}"); }
        return changed;
    }

    static int DisablePrefix(JsonObject detectors, string detPrefix, string reason)
    {
        int hit = 0;
        foreach (var key in detectors.Select(kv => kv.Key)
                     .Where(k => k.StartsWith(detPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (detectors[key]?.GetValue<bool>() != false) { detectors[key] = false; hit++; }
        }
        if (hit > 0)
        {
            LastPolicyNotes.Add(reason);
            Log.Info("openrgb", $"policy: disabled {hit} '{detPrefix}*' detector(s) — {reason}");
        }
        else if (detectors.Any(kv => kv.Key.StartsWith(detPrefix, StringComparison.OrdinalIgnoreCase)
                                  && kv.Value?.GetValue<bool>() == false))
        {
            LastPolicyNotes.Add(reason);   // already off from a prior run; still worth surfacing
        }
        return hit;
    }

    /*-----------------------------------------------------*\
    | Window hiding: the bundled instance runs with a real  |
    | (hidden) main window so stray dialogs can't quit it.  |
    \*-----------------------------------------------------*/
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    static int _launchGen;   // bumped per launch so a sweeper for a superseded process stops

    /// <summary>Hide every window the process shows during its first moments
    /// (main window, warning dialogs). Hidden windows keep the app alive;
    /// friends never interact with OpenRGB directly anyway.</summary>
    static void HideWindowsOf(Process proc, int forMs)
    {
        int pid = proc.Id;
        int gen = Interlocked.Increment(ref _launchGen);
        new Thread(() =>
        {
            // The server's window (if any) appears within the first seconds of
            // launch: sweep fast early, then back way off. The old flat 250 ms
            // cadence was 120 full EnumWindows sweeps over 30 s. Stops early
            // once the process is gone or a newer launch superseded it - the
            // bisect relaunches up to 14x, and sweepers used to keep walking
            // every window over dead (reusable) pids for their full 30 s.
            // The whole body is guarded: an exception escaping a raw Thread
            // takes the elevated process down with it, and this one only ever
            // hides a window. A failed sweep is logged and the sweeper ends.
            try
            {
                long start = Environment.TickCount64;
                long end = start + forMs;
                while (Environment.TickCount64 < end && gen == Volatile.Read(ref _launchGen) && !HasExited(proc))
                {
                    EnumWindows((h, _) =>
                    {
                        GetWindowThreadProcessId(h, out uint p);
                        if (p == pid && IsWindowVisible(h)) ShowWindow(h, 0 /* SW_HIDE */);
                        return true;
                    }, IntPtr.Zero);
                    Thread.Sleep(Environment.TickCount64 - start < 5000 ? 250 : 1000);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("openrgb", $"window hiding stopped: {ex.Message}");
            }
        })
        { IsBackground = true, Name = "openrgb-hide" }.Start();
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }   // no handle or access: cannot be waited on, so treat it as gone
    }

    /// <summary>Tail of the newest log OpenRGB wrote in our managed config dir.
    /// When the server crashes mid-detection, this names the device it died on
    /// — the single most useful thing a remote report can carry.</summary>
    // 160 lines: a field crash report cut off right before the dying
    // detector's debug lines — the tail must reach past the HID-connect spam.
    public static string? ReadServerLogTail(int lines = 160)
    {
        try
        {
            if (!Directory.Exists(ConfigDir)) return null;
            var newest = Directory.EnumerateFiles(ConfigDir, "*.log", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return null;
            // Tail from the END: the server runs verbose (--loglevel 6), so
            // this file can be tens of MB — reading it all just to TakeLast
            // was a large transient. 256 KB comfortably covers 160 lines.
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - 256 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            if (start > 0) reader.ReadLine();          // drop the partial first line
            var all = new List<string>();
            while (reader.ReadLine() is string line) all.Add(line);
            return $"[{newest.Name}, last write {newest.LastWriteTimeUtc:HH:mm:ss}Z]\r\n"
                 + string.Join("\r\n", all.TakeLast(lines));
        }
        catch (Exception ex) { return $"(couldn't read OpenRGB log: {ex.Message})"; }
    }
}
