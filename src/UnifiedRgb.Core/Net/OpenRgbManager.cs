using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
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
    // Master pipeline build (what openrgb.org itself now points at) — covers
    // hardware 0.9 predates (newer NZXT/Razer/50-series). Pinned to one CI
    // job for reproducibility; the moving master URL is the fallback if the
    // pinned artifact ever expires.
    const string BundleVersion = "master-15972623581";   // resolved 2026-08-19
    const string PinnedUrl = "https://gitlab.com/CalcProgrammer1/OpenRGB/-/jobs/15972623581/artifacts/download";
    const string LatestUrl = "https://gitlab.com/CalcProgrammer1/OpenRGB/-/jobs/artifacts/master/download?job=Windows+64";
    public const int Port = 6742;

    static readonly string Root = AppPaths.Local("openrgb");
    static readonly string ConfigDir = Path.Combine(Root, "config");
    static readonly string StampPath = Path.Combine(Root, "bundle-version.txt");

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

    /// <summary>True when a server is reachable (ours or the user's own).</summary>
    public static bool IsServerUp() => OpenRgbClient.IsServerUp(port: Port);

    static string? FindExe() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>Download + extract the pinned build (idempotent per version).
    /// Upgrades in place when the pinned version changes, preserving the
    /// managed config (detector tweaks) across the swap.</summary>
    public static async Task InstallAsync(Action<string>? status = null)
    {
        if (IsInstalled) return;
        Stop();                                  // never upgrade under a running instance
        Directory.CreateDirectory(Root);

        // Clear the previous build but keep config/ (detector state).
        foreach (var dir in Directory.GetDirectories(Root))
            if (!string.Equals(dir, ConfigDir, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(dir, recursive: true); } catch { }
        foreach (var file in Directory.GetFiles(Root))
            try { File.Delete(file); } catch { }

        status?.Invoke("downloading OpenRGB...");
        string zipPath = Path.Combine(Root, "openrgb.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            byte[] bytes;
            try
            {
                bytes = await http.GetByteArrayAsync(PinnedUrl);
                Log.Info("openrgb", $"downloaded pinned build ({bytes.Length:n0} bytes) from {PinnedUrl}");
            }
            catch (Exception ex)
            {
                Log.Warn("openrgb", $"pinned build unavailable ({ex.Message}); falling back to latest master");
                bytes = await http.GetByteArrayAsync(LatestUrl);
                Log.Info("openrgb", $"downloaded MOVING master build ({bytes.Length:n0} bytes) from {LatestUrl}");
            }
            await File.WriteAllBytesAsync(zipPath, bytes);
        }
        status?.Invoke("extracting...");
        ZipFile.ExtractToDirectory(zipPath, Root, overwriteFiles: true);
        File.Delete(zipPath);
        File.WriteAllText(StampPath, BundleVersion);
        _installedStamp = 0;   // invalidate the IsInstalled memo immediately
        File.WriteAllText(Path.Combine(Root, "LICENSE-NOTE.txt"),
            "This folder contains an unmodified official OpenRGB build (GPLv2),\r\n" +
            "from the project's own CI pipeline (what openrgb.org links to).\r\n" +
            "Source: https://gitlab.com/CalcProgrammer1/OpenRGB\r\n" +
            "UnifiedRGB communicates with it over its SDK network protocol only.\r\n");
        Log.Info("openrgb", $"installed {BundleVersion} to {Root}");
    }

    /// <summary>Ensure a server is running: reuse an existing one, else launch
    /// our bundled copy (installing first if needed). Returns false on failure.</summary>
    public static async Task<bool> EnsureRunningAsync(Action<string>? status = null)
    {
        if (IsServerUp()) return true;
        try
        {
            await InstallAsync(status);
            return Launch(status);
        }
        catch (Exception ex)
        {
            Log.Error("openrgb", ex);
            status?.Invoke($"OpenRGB setup failed: {ex.Message}");
            return false;
        }
    }

    static bool Launch(Action<string>? status)
    {
        // Bounded relaunch loop: the crash-bisect (one buggy detector killing
        // the whole scan) and the first-run conflict-policy restart both work
        // by narrowing the detector config and relaunching. log2(~300
        // detectors) + slack fits comfortably in the bound.
        bool policyRestartDone = false;
        for (int attempt = 0; attempt < 14; attempt++)
        {
            switch (LaunchOnce(status, ref policyRestartDone))
            {
                case LaunchResult.Ok: return true;
                case LaunchResult.Failed: return false;
                case LaunchResult.Relaunch: Stop(); continue;
            }
        }
        status?.Invoke("OpenRGB kept restarting — giving up for this session");
        return false;
    }

    enum LaunchResult { Ok, Failed, Relaunch }

    static LaunchResult LaunchOnce(Action<string>? status, ref bool policyRestartDone)
    {
        var exe = FindExe();
        if (exe == null) { status?.Invoke("OpenRGB.exe not found after install"); return LaunchResult.Failed; }
        Stop();                    // never stack a second bundled instance on the port
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
            Arguments = $"--server --server-port {Port} --loglevel 6 --config \"{ConfigDir}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        if (_proc != null) HideWindowsOf(_proc.Id, 30_000);
        for (int i = 0; i < 60; i++)
        {
            if (IsServerUp())
            {
                Log.Info("openrgb", "bundled server is up");
                // The SDK port opens BEFORE detection finishes — wait for the
                // device list to settle or every first query sees 0 devices.
                if (!WaitForDetection(status))
                {
                    // The server DIED during its hardware scan: a detector
                    // crashed on this machine's hardware. Hand it to the
                    // bisect; when armed it narrows the detector list and we
                    // relaunch until the culprit is isolated.
                    if (OpenRgbCrashBisect.OnCrash(ConfigDir))
                    {
                        status?.Invoke("OpenRGB crashed while scanning — isolating the responsible detector...");
                        return LaunchResult.Relaunch;
                    }
                    status?.Invoke("OpenRGB crashed while scanning your hardware — hit Send in Support so we can see which device");
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
                // (early detectors run before the server socket comes up).
                if (OpenRgbCrashBisect.OnCrash(ConfigDir))
                {
                    status?.Invoke("OpenRGB crashed while scanning — isolating the responsible detector...");
                    return LaunchResult.Relaunch;
                }
                status?.Invoke("OpenRGB exited during startup");
                return LaunchResult.Failed;
            }
        }
        status?.Invoke("OpenRGB did not open its server port");
        return LaunchResult.Failed;
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
    /// own install). Sweeping by path is what actually guarantees the port is
    /// free — a tracked-handle kill alone can miss a survivor and leave the old
    /// instance owning 6742 with stale config.</summary>
    public static void Stop()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
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
            string path = Path.Combine(ConfigDir, "OpenRGB.json");
            JsonNode root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
                : new JsonObject();
            var detectors = root["Detectors"]?["detectors"] as JsonObject;
            if (detectors == null)
            {
                detectors = new JsonObject();
                root["Detectors"] = new JsonObject { ["detectors"] = detectors };
            }
            bool changed = false;
            foreach (var n in names.SelectMany(n =>
                         DetectorAliases.TryGetValue(n, out var alias) ? new[] { n, alias } : new[] { n }))
            {
                if (detectors[n]?.GetValue<bool>() != false)
                {
                    detectors[n] = false;
                    changed = true;
                }
            }
            if (changed)
                SafeFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return changed;
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
        Stop();
        return Launch(status);
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
            string path = Path.Combine(ConfigDir, "OpenRGB.json");
            if (!File.Exists(path)) return 0;
            JsonNode root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
            if (root["Detectors"]?["detectors"] is not JsonObject detectors) return 0;

            // One enumeration, names only, everything disposed — the old code
            // called Process.GetProcesses() INSIDE the policy loop (3x) and
            // never disposed the ~250 returned Process objects per pass.
            var running = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try { running.Add(p.ProcessName); } catch { }
                finally { p.Dispose(); }
            }
            foreach (var (procPrefix, detPrefix, reason) in ConflictPolicies)
            {
                if (!running.Any(n => n.StartsWith(procPrefix, StringComparison.OrdinalIgnoreCase))) continue;
                changed += DisablePrefix(detectors, detPrefix, reason);
            }
            foreach (var (detPrefix, reason) in UnstableDetectors)
                changed += DisablePrefix(detectors, detPrefix, reason);
            if (changed > 0)
                SafeFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    /// <summary>Hide every window the process shows during its first moments
    /// (main window, warning dialogs). Hidden windows keep the app alive;
    /// friends never interact with OpenRGB directly anyway.</summary>
    static void HideWindowsOf(int pid, int forMs)
    {
        new Thread(() =>
        {
            // The server's window (if any) appears within the first seconds of
            // launch: sweep fast early, then back way off. The old flat 250 ms
            // cadence was 120 full EnumWindows sweeps over 30 s.
            long start = Environment.TickCount64;
            long end = start + forMs;
            while (Environment.TickCount64 < end)
            {
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint p);
                    if (p == pid && IsWindowVisible(h)) ShowWindow(h, 0 /* SW_HIDE */);
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(Environment.TickCount64 - start < 5000 ? 250 : 1000);
            }
        })
        { IsBackground = true, Name = "openrgb-hide" }.Start();
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
