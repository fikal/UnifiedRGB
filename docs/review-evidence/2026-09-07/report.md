# Security Review: UnifiedRgb

## Scope

Repository-wide request; focused security review with explicitly partial coverage, alongside ordinary repair backlog.

- Scan mode: repository
- Target kind: git_worktree
- Target ID: target_sha256_abda6e9caeabf825abc71929912f9fa7b11bd991f6f2e10d2c6aef7c19276286
- Revision: 7716aab111436d883829edbcdf75f4e58edbcc2a
- Snapshot digest: codex-security-snapshot/v1:sha256:0f2bb43efa48048ee5ab6909624e9778b77f7ac6408995c9e7078d264bde071c
- Inventory strategy: repository
- Included paths: .
- Excluded paths: none
- Runtime or test status: Security paths not executed. Two unrelated lighting composition bugs reproduced with isolated fake devices.

Limitations and exclusions:
- No active exploit or hardware testing. No dependency-CVE scan. Full repository coverage not claimed.
- Excluded src/UnifiedRgb.Core/Modules/\*.bin: Third-party embedded kernel module binaries not reverse engineered.
- Excluded third-party dependency implementations: No source audit or online CVE/version check of external libraries/bundles.

### Scan Summary

| Field | Value |
| --- | --- |
| Scan outcome | completed |
| Reportable findings | 4 |
| Severity mix | medium: 2, low: 2 |
| Confidence mix | high: 4 |
| Coverage | partial |
| Validation mode | static source review |

Canonical artifacts: `scan-manifest.json`, `findings.json`, and `coverage.json`. This report is a deterministic projection of those files.

## Threat Model

UnifiedRGB is a Windows WPF RGB/fan/LCD controller whose normal GUI requires administrator (src/UnifiedRgb.App/app.manifest:13). GUI startup enforces a same-session singleton (src/UnifiedRgb.App/App.xaml.cs:17), starts Chroma named-pipe and localhost REST ingestion unconditionally (src/UnifiedRgb.App/MainViewModel.cs:1239), and conditionally starts the OpenRGB bridge from persisted settings (src/UnifiedRgb.App/MainViewModel.cs:1257). Core contains device drivers, sensors, effects, networking, updates, and shared persistence; separate CLI and diagnostic programs expose operator/developer workflows (src/UnifiedRgb.Cli/Program.cs:805; src/UnifiedRgb.Diag/Program.cs:21). This is an architecture map, not completed audit coverage.

### Assets

- Elevated process execution and executable integrity: GUI manifest requires administrator; updater replaces Environment.ProcessPath and relaunches it (src/UnifiedRgb.App/app.manifest:13; src/UnifiedRgb.App/Services/UpdateService.cs:109; src/UnifiedRgb.App/Services/UpdateService.cs:227).
- Hardware lighting and thermal-control availability: fatal exception handling attempts to restore controlled fans to BIOS and shut sensors down (src/UnifiedRgb.App/App.xaml.cs:68). Fan configuration lives at %LOCALAPPDATA%/UnifiedRgb/fan-config.json (src/UnifiedRgb.Core/Sensors/SensorHub.cs:810; src/UnifiedRgb.Core/AppPaths.cs:16).
- User configuration and diagnostic privacy: settings.json, profiles.json, and unifiedrgb.log reside under %APPDATA%/UnifiedRgb (src/UnifiedRgb.App/ProfileStore.cs:152; src/UnifiedRgb.Core/Log.cs:9; src/UnifiedRgb.Core/AppPaths.cs:11). Support exports include hardware diagnostics, app state, current and rotated logs (src/UnifiedRgb.App/Services/SupportService.cs:38; src/UnifiedRgb.App/Services/SupportService.cs:44; src/UnifiedRgb.App/Services/SupportService.cs:58).

### Trust Boundaries

- Local or explicitly enabled LAN SDK clients submit RGB protocol packets to elevated GUI. OpenRgbServer binds 127.0.0.1 by default or 0.0.0.0 when LAN is selected; caps clients at 16, packet payloads at 1 MiB, and sets read/write timeouts (src/UnifiedRgb.Core/Net/OpenRgbServer.cs:113; src/UnifiedRgb.Core/Net/OpenRgbServer.cs:62; src/UnifiedRgb.Core/Net/OpenRgbServer.cs:215; src/UnifiedRgb.Core/Net/OpenRgbServer.cs:234). GUI passes its persisted LAN preference at startup (src/UnifiedRgb.App/MainViewModel.Settings.cs:275).
- Chroma hosts submit color data through \\\\.\\pipe\\UnifiedRgbChroma. Pipe ACL grants administrators/system full access and everyone read/write at medium integrity; first-instance protection refuses an already-owned pipe name (src/UnifiedRgb.Core/Effects/ChromaSync.cs:100; src/UnifiedRgb.Core/Effects/ChromaSync.cs:134). Native shim connects to that exact pipe (native/chroma-shim/RzChromaSDK.cpp:165). Separately, Chroma REST listens on http://localhost:54235/ (src/UnifiedRgb.Core/Net/ChromaRestServer.cs:27; src/UnifiedRgb.Core/Net/ChromaRestServer.cs:45).
- CS2 GSI enters through a localhost HttpListener with a persisted shared token and a 512 KiB body limit; parser receives expected token (src/UnifiedRgb.Core/Games/GsiServer.cs:73; src/UnifiedRgb.Core/Games/GsiServer.cs:128; src/UnifiedRgb.Core/Games/GsiServer.cs:156). Token originates in settings and is written into game configuration by explicit setup (src/UnifiedRgb.App/MainViewModel.Settings.cs:325; src/UnifiedRgb.App/MainViewModel.Settings.cs:375).
- Optional OpenRGB executable runs with inherited GUI authority from the first recursively discovered OpenRGB.exe beneath %LOCALAPPDATA%/UnifiedRgb/openrgb. Configuration is %LOCALAPPDATA%/UnifiedRgb/openrgb/config/OpenRGB.json; process uses --server --server-port 6742 --loglevel 6 --config \<config directory\>. An existing reachable server is reused instead of launching (src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:113; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:166; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178).
- Update/support destination selection crosses user configuration into elevated network and execution behavior. %APPDATA%/UnifiedRgb/backend.json fields override assembly metadata independently; HTTPS or loopback HTTP accepted. Configured backend selects private feed, otherwise GitHub Releases (src/UnifiedRgb.Core/AppPaths.cs:54; src/UnifiedRgb.Core/AppPaths.cs:72; src/UnifiedRgb.Core/AppPaths.cs:79; src/UnifiedRgb.Core/UpdateClient.cs:43).
- PawnIO installation downloads a random temporary executable, holds a read-sharing lock, checks Authenticode signer CN=namazso.eu, then launches installer (src/UnifiedRgb.Core/Native/PawnIoInstaller.cs:29; src/UnifiedRgb.Core/Native/PawnIoInstaller.cs:41; src/UnifiedRgb.Core/Native/PawnIoInstaller.cs:47; src/UnifiedRgb.Core/Native/PawnIoInstaller.cs:58).

### Attacker Capabilities

- A local process can attempt Chroma pipe/REST and enabled SDK access without possessing administrator authority; intended color control itself is not an exploit (src/UnifiedRgb.Core/Effects/ChromaSync.cs:100; src/UnifiedRgb.Core/Net/ChromaRestServer.cs:45).
- A network peer can reach the SDK only when LAN listening is enabled and host networking permits it (src/UnifiedRgb.Core/Net/OpenRgbServer.cs:113). No unsupported public Internet deployment is assumed.
- A same-user medium-integrity process may modify per-user configuration and bundle storage; source explicitly recognizes backend.json as user-writable while the app runs elevated (src/UnifiedRgb.Core/AppPaths.cs:66). Any resulting elevated execution claim still needs its complete reachable consumer and prerequisite demonstrated.
- A compromised trusted download publisher/feed can influence downloaded executable bytes. Transport trust, hash comparison, and Authenticode are distinct controls; no publisher compromise is assumed as a discovered vulnerability.

### Security Objectives

- Network and IPC inputs should remain constrained to supported color/game-state operations and bounded resource consumption, without escaping into elevated execution or destabilizing thermal control.
- Executable updates and optional helper installation should preserve integrity across download, validation, staging, and actual process creation.
- Diagnostics should leave the machine only through the selected support workflow, with privacy controls applied consistently (src/UnifiedRgb.App/Services/SupportService.cs:74; src/UnifiedRgb.App/Services/SupportService.cs:76).
- Hardware ownership and recovery should prevent conflicting device writes and restore fan control after fatal failure (src/UnifiedRgb.App/App.xaml.cs:17; src/UnifiedRgb.App/App.xaml.cs:68).

### Assumptions

- User context requests an actionable document of bugs, exploits, refactorings, smells, CPU and memory issues for another model to fix. No supplied threat model or authoritative knowledge base. Scope policy resolver for src returned empty.
- Updater staging inherits protection of the installed executable directory; that directory is derived from Environment.ProcessPath and is not necessarily Program Files. Random staging names are not an ACL (src/UnifiedRgb.App/Services/UpdateService.cs:109; src/UnifiedRgb.App/Services/UpdateService.cs:117; src/UnifiedRgb.App/Services/UpdateService.cs:122). Actual installation ACLs remain deployment-dependent.
- Private feed source documentation says public update checks skip, but actual UpdateClient falls back to GitHub Releases. Retain this documentation/code disagreement (src/UnifiedRgb.Core/AppPaths.cs:32; src/UnifiedRgb.Core/UpdateClient.cs:43; src/UnifiedRgb.Core/UpdateClient.cs:81).
- OpenRGB introductory comment says --startminimized; actual startup deliberately omits it (src/UnifiedRgb.Core/Net/OpenRgbManager.cs:12; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:171; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:181).
- OpenRGB download is described as pinned but falls back to moving master when the pinned request fails; executable discovery depends on archive contents (src/UnifiedRgb.Core/Net/OpenRgbManager.cs:23; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57; src/UnifiedRgb.Core/Net/OpenRgbManager.cs:90).
- Bounded architecture pass did not inspect all device protocols, effect lifecycles, native shim exports, image/media parsers, or release script. Those are audit work remaining, not negative findings.

## Findings

| Finding | Severity | Confidence | Detailed write-up |
| --- | --- | --- | --- |
| [User-writable backend override controls elevated self-update execution](#finding-1) | medium | high | inline below |
| [Elevated OpenRGB launches trust executable content in LocalAppData](#finding-2) | medium | high | inline below |
| [Idle pipe clients can permanently exclude legitimate Chroma hosts](#finding-3) | low | high | inline below |
| [OpenRGB client names can forge persistent diagnostic log entries](#finding-4) | low | high | inline below |

### Confidence Scale

| Label | Meaning |
| --- | --- |
| high | Direct evidence supports the finding with no material unresolved blocker. |
| medium | Evidence supports a plausible issue, but material runtime or reachability proof remains. |
| low | Evidence is incomplete and the item is retained only for explicit follow-up. |

<a id="finding-1"></a>

### [1] User-writable backend override controls elevated self-update execution

| Field | Value |
| --- | --- |
| Severity | medium |
| Confidence | high |
| Confidence rationale | Parent independently traced the original source and controls; no live exploit reproduction. |
| Category | code-integrity |
| CWE | CWE-829 |
| Affected lines | src/UnifiedRgb.Core/AppPaths.cs:54-61, src/UnifiedRgb.Core/UpdateClient.cs:42-62, src/UnifiedRgb.App/Services/UpdateService.cs:141-154, src/UnifiedRgb.App/Services/UpdateService.cs:227-230, src/UnifiedRgb.App/app.manifest:13 |

#### Summary

Attacker writes %APPDATA%/UnifiedRgb/backend.json with an attacker-controlled HTTPS endpoint or loopback HTTP endpoint and any nonempty key. Backend prioritizes that file over compiled configuration. The endpoint provides a newer version and arbitrary executable plus its matching SHA-256. UpdateService verifies only the attacker-provided hash and executable version, then the elevated batch process replaces and launches the payload.

#### Root Cause

Attacker writes %APPDATA%/UnifiedRgb/backend.json with an attacker-controlled HTTPS endpoint or loopback HTTP endpoint and any nonempty key. Backend prioritizes that file over compiled configuration. The endpoint provides a newer version and arbitrary executable plus its matching SHA-256. UpdateService verifies only the attacker-provided hash and executable version, then the elevated batch process replaces and launches the payload.

**Mutable backend override** — `src/UnifiedRgb.Core/AppPaths.cs:54-61`

User AppData supplies both endpoint and key before compiled defaults.

```csharp
        string f = AppPaths.Config("backend.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
```

**Feed and hash selection** — `src/UnifiedRgb.Core/UpdateClient.cs:42-62`

Backend.Configured routes version metadata to the override endpoint, which also supplies expected sha256.

```csharp
    public static Task<LatestBuild?> GetLatestAsync()
        => Backend.Configured ? GetLatestFromFeedAsync() : GetLatestFromGitHubAsync();

    static async Task<LatestBuild?> GetLatestFromFeedAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/version");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            using var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return new LatestBuild(
                    v.GetString()!,
                    root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    SizeOf(root),
                    root.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            }
```

**Hash without independent publisher authentication** — `src/UnifiedRgb.App/Services/UpdateService.cs:141-154`

The updater accepts the chosen feed hash as authority; attacker-selected payload and matching hash pass this comparison.

```csharp
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
```

**Swap and restart** — `src/UnifiedRgb.App/Services/UpdateService.cs:227-230`

The elevated swap script replaces the application and starts the supplied executable.

```csharp
                move /y "{temp}" "{target}" >nul 2>&1
                if errorlevel 1 goto loop
                >"{swapResult}" echo ok %n%
                start "" "{target}"
```

**Administrator application** — `src/UnifiedRgb.App/app.manifest:13`

The normal GUI requests administrator authority, which the update workflow inherits.

```csharp
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

#### Validation

Attacker writes %APPDATA%/UnifiedRgb/backend.json with an attacker-controlled HTTPS endpoint or loopback HTTP endpoint and any nonempty key. Backend prioritizes that file over compiled configuration. The endpoint provides a newer version and arbitrary executable plus its matching SHA-256. UpdateService verifies only the attacker-provided hash and executable version, then the elevated batch process replaces and launches the payload.

Validation method: independent parent static source trace

**Mutable backend override** — `src/UnifiedRgb.Core/AppPaths.cs:54-61`

User AppData supplies both endpoint and key before compiled defaults.

```csharp
        string f = AppPaths.Config("backend.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
```

**Feed and hash selection** — `src/UnifiedRgb.Core/UpdateClient.cs:42-62`

Backend.Configured routes version metadata to the override endpoint, which also supplies expected sha256.

```csharp
    public static Task<LatestBuild?> GetLatestAsync()
        => Backend.Configured ? GetLatestFromFeedAsync() : GetLatestFromGitHubAsync();

    static async Task<LatestBuild?> GetLatestFromFeedAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/version");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            using var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return new LatestBuild(
                    v.GetString()!,
                    root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    SizeOf(root),
                    root.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            }
```

**Hash without independent publisher authentication** — `src/UnifiedRgb.App/Services/UpdateService.cs:141-154`

The updater accepts the chosen feed hash as authority; attacker-selected payload and matching hash pass this comparison.

```csharp
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
```

**Swap and restart** — `src/UnifiedRgb.App/Services/UpdateService.cs:227-230`

The elevated swap script replaces the application and starts the supplied executable.

```csharp
                move /y "{temp}" "{target}" >nul 2>&1
                if errorlevel 1 goto loop
                >"{swapResult}" echo ok %n%
                start "" "{target}"
```

**Administrator application** — `src/UnifiedRgb.App/app.manifest:13`

The normal GUI requests administrator authority, which the update workflow inherits.

```csharp
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

Assertions:
- An unelevated process must not choose code that the elevated updater installs and executes.

Limitations:
- No live attacker traffic, privileged executable replacement, or hardware tests were run.
- Endpoint schemes are restricted and overrides are logged; neither establishes publisher identity.
- Updates require user action, so this is not silent remote compromise.
- Protected staging paths and repeated hash checks defend payload replacement, but the attacker supplies both payload and expected hash.
- The backend override is intentionally supported, so remediation should preserve legitimate configuration through a protected mechanism.

#### Dataflow

Attacker writes %APPDATA%/UnifiedRgb/backend.json with an attacker-controlled HTTPS endpoint or loopback HTTP endpoint and any nonempty key. Backend prioritizes that file over compiled configuration. The endpoint provides a newer version and arbitrary executable plus its matching SHA-256. UpdateService verifies only the attacker-provided hash and executable version, then the elevated batch process replaces and launches the payload.

**Mutable backend override** — `src/UnifiedRgb.Core/AppPaths.cs:54-61`

User AppData supplies both endpoint and key before compiled defaults.

```csharp
        string f = AppPaths.Config("backend.json");
        try
        {
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k)) key = k.GetString();
```

**Feed and hash selection** — `src/UnifiedRgb.Core/UpdateClient.cs:42-62`

Backend.Configured routes version metadata to the override endpoint, which also supplies expected sha256.

```csharp
    public static Task<LatestBuild?> GetLatestAsync()
        => Backend.Configured ? GetLatestFromFeedAsync() : GetLatestFromGitHubAsync();

    static async Task<LatestBuild?> GetLatestFromFeedAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Backend.BaseUrl}/version");
            req.Headers.Add("X-Rgb-Key", Backend.ClientKey!);   // gated by Configured above
            using var response = await Backend.Http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                return new LatestBuild(
                    v.GetString()!,
                    root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    SizeOf(root),
                    root.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            }
```

**Hash without independent publisher authentication** — `src/UnifiedRgb.App/Services/UpdateService.cs:141-154`

The updater accepts the chosen feed hash as authority; attacker-selected payload and matching hash pass this comparison.

```csharp
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
```

**Swap and restart** — `src/UnifiedRgb.App/Services/UpdateService.cs:227-230`

The elevated swap script replaces the application and starts the supplied executable.

```csharp
                move /y "{temp}" "{target}" >nul 2>&1
                if errorlevel 1 goto loop
                >"{swapResult}" echo ok %n%
                start "" "{target}"
```

**Administrator application** — `src/UnifiedRgb.App/app.manifest:13`

The normal GUI requests administrator authority, which the update workflow inherits.

```csharp
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

#### Reachability

A medium-integrity process running as the same Windows user; requires the user subsequently to launch UnifiedRGB and click its update action.

- **Attacker:** A medium-integrity process running as the same Windows user; requires the user subsequently to launch UnifiedRGB and click its update action.

- **Outcome:** Arbitrary code execution with UnifiedRGB's administrator privileges, including when the original installation directory is protected.

#### Severity

**Medium** — Administrator code execution requires a same-user lower-integrity foothold and a subsequent user update click.

Additional runtime or deployment evidence could raise or lower this severity.

Impact assessment:
- **Level:** high
- **Why:** Arbitrary code execution with UnifiedRGB's administrator privileges, including when the original installation directory is protected.

Likelihood assessment:
- **Level:** medium
- **Why:** Administrator code execution requires a same-user lower-integrity foothold and a subsequent user update click.

#### Remediation

Authenticate update binaries or signed update metadata using an identity/key anchored in protected application code. Store endpoint overrides in an administrator-protected location or prevent untrusted overrides from determining elevated executable updates. Reject missing verification metadata. Test that a medium-writable backend override cannot cause an unsigned newer-version test executable to reach the swap stage.

Tests:
- Reject an override-controlled payload even when its attacker-supplied hash and newer version agree.
- Accept only fixtures verified against the protected publisher/key policy; never execute test payloads.

Preventive controls:
- Centralize executable trust independently of mutable user configuration.

<a id="finding-2"></a>

### [2] Elevated OpenRGB launches trust executable content in LocalAppData

| Field | Value |
| --- | --- |
| Severity | medium |
| Confidence | high |
| Confidence rationale | Parent independently traced the original source and controls; no live exploit reproduction. |
| Category | untrusted-search-path |
| CWE | CWE-427 |
| Affected lines | src/UnifiedRgb.Core/AppPaths.cs:15-19, src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28-30, src/UnifiedRgb.Core/Net/OpenRgbManager.cs:46-48, src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57-60, src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178-184 |

#### Summary

AppPaths.Local resolves to user-writable LocalAppData. OpenRgbManager accepts a bundle as installed when any recursively found OpenRGB.exe and a version-text marker exist. A later bridge start or restart launches that executable with the elevated parent's token. An attacker replaces the existing executable or dependencies while the bundle is stopped; the marker remains valid.

#### Root Cause

AppPaths.Local resolves to user-writable LocalAppData. OpenRgbManager accepts a bundle as installed when any recursively found OpenRGB.exe and a version-text marker exist. A later bridge start or restart launches that executable with the elevated parent's token. An attacker replaces the existing executable or dependencies while the bundle is stopped; the marker remains valid.

**Per-user binary root** — `src/UnifiedRgb.Core/AppPaths.cs:15-19`

The OpenRGB bundle root derives from LocalApplicationData, accessible to the same-user process.

```csharp
    /// fan-config.json).</summary>
    public static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb");

    public static string Config(string file) => Path.Combine(ConfigDir, file);
```

**Bundle paths** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28-30`

Executable storage and config currently share the per-user bundle root.

```csharp
    static readonly string Root = AppPaths.Local("openrgb");
    static readonly string ConfigDir = Path.Combine(Root, "config");
    static readonly string StampPath = Path.Combine(Root, "bundle-version.txt");
```

**Presence and version stamp** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:46-48`

The installation check establishes existence/version text, not authentic executable contents or protected storage.

```csharp
                _installedCache = FindExe() != null &&
                    File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == BundleVersion;
                _installedStamp = now;
```

**Recursive executable selection** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57-60`

The selected executable is the first match under that writable tree.

```csharp
    static string? FindExe() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
```

**Elevated child launch** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178-184`

The process starts the selected file and its working directory with inherited caller authority.

```csharp
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--server --server-port {Port} --loglevel 6 --config \"{ConfigDir}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
```

#### Validation

AppPaths.Local resolves to user-writable LocalAppData. OpenRgbManager accepts a bundle as installed when any recursively found OpenRGB.exe and a version-text marker exist. A later bridge start or restart launches that executable with the elevated parent's token. An attacker replaces the existing executable or dependencies while the bundle is stopped; the marker remains valid.

Validation method: independent parent static source trace

**Per-user binary root** — `src/UnifiedRgb.Core/AppPaths.cs:15-19`

The OpenRGB bundle root derives from LocalApplicationData, accessible to the same-user process.

```csharp
    /// fan-config.json).</summary>
    public static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb");

    public static string Config(string file) => Path.Combine(ConfigDir, file);
```

**Bundle paths** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28-30`

Executable storage and config currently share the per-user bundle root.

```csharp
    static readonly string Root = AppPaths.Local("openrgb");
    static readonly string ConfigDir = Path.Combine(Root, "config");
    static readonly string StampPath = Path.Combine(Root, "bundle-version.txt");
```

**Presence and version stamp** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:46-48`

The installation check establishes existence/version text, not authentic executable contents or protected storage.

```csharp
                _installedCache = FindExe() != null &&
                    File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == BundleVersion;
                _installedStamp = now;
```

**Recursive executable selection** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57-60`

The selected executable is the first match under that writable tree.

```csharp
    static string? FindExe() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
```

**Elevated child launch** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178-184`

The process starts the selected file and its working directory with inherited caller authority.

```csharp
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--server --server-port {Port} --loglevel 6 --config \"{ConfigDir}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
```

Assertions:
- Administrator child processes must not execute binaries or DLLs replaceable by an unelevated process.

Limitations:
- No live attacker traffic, privileged executable replacement, or hardware tests were run.
- The initial archive is downloaded through HTTPS from the upstream project.
- A valid existing server causes EnsureRunningAsync to return without launching; exploitation targets a later start when that server is absent.
- No code establishing a protected bundle ACL or authenticating its contents before launch exists in OpenRgbManager/AppPaths.

#### Dataflow

AppPaths.Local resolves to user-writable LocalAppData. OpenRgbManager accepts a bundle as installed when any recursively found OpenRGB.exe and a version-text marker exist. A later bridge start or restart launches that executable with the elevated parent's token. An attacker replaces the existing executable or dependencies while the bundle is stopped; the marker remains valid.

**Per-user binary root** — `src/UnifiedRgb.Core/AppPaths.cs:15-19`

The OpenRGB bundle root derives from LocalApplicationData, accessible to the same-user process.

```csharp
    /// fan-config.json).</summary>
    public static readonly string LocalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedRgb");

    public static string Config(string file) => Path.Combine(ConfigDir, file);
```

**Bundle paths** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28-30`

Executable storage and config currently share the per-user bundle root.

```csharp
    static readonly string Root = AppPaths.Local("openrgb");
    static readonly string ConfigDir = Path.Combine(Root, "config");
    static readonly string StampPath = Path.Combine(Root, "bundle-version.txt");
```

**Presence and version stamp** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:46-48`

The installation check establishes existence/version text, not authentic executable contents or protected storage.

```csharp
                _installedCache = FindExe() != null &&
                    File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == BundleVersion;
                _installedStamp = now;
```

**Recursive executable selection** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:57-60`

The selected executable is the first match under that writable tree.

```csharp
    static string? FindExe() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "OpenRGB.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
```

**Elevated child launch** — `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178-184`

The process starts the selected file and its working directory with inherited caller authority.

```csharp
        _proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--server --server-port {Port} --loglevel 6 --config \"{ConfigDir}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
```

#### Reachability

A medium-integrity process under the same user, with write access to the user's LocalAppData OpenRGB bundle.

- **Attacker:** A medium-integrity process under the same user, with write access to the user's LocalAppData OpenRGB bundle.

- **Outcome:** Arbitrary administrator code execution on a subsequent bridge start/restart. No compromised download source is necessary.

#### Severity

**Medium** — Administrator code execution requires a same-user lower-integrity foothold and an optional bridge launch/restart with no reused server.

Additional runtime or deployment evidence could raise or lower this severity.

Impact assessment:
- **Level:** high
- **Why:** Arbitrary administrator code execution on a subsequent bridge start/restart. No compromised download source is necessary.

Likelihood assessment:
- **Level:** medium
- **Why:** Administrator code execution requires a same-user lower-integrity foothold and an optional bridge launch/restart with no reused server.

#### Remediation

Install executable bundle files and their dependencies beneath an administrator-protected directory with verified ACLs. Keep mutable configuration separately. Use a deterministic executable path, verify trusted bundle metadata, and avoid relying on a user-writable version marker as an integrity check. Test executable and DLL replacement attempts from a medium-integrity process.

Tests:
- Verify medium-integrity EXE and dependency replacement is denied in installed binary storage.
- Do not execute a tampered legacy bundle during migration or bridge restart.

Preventive controls:
- Centralize executable trust independently of mutable user configuration.

<a id="finding-3"></a>

### [3] Idle pipe clients can permanently exclude legitimate Chroma hosts

| Field | Value |
| --- | --- |
| Severity | low |
| Confidence | high |
| Confidence rationale | Parent independently traced the original source and controls; no live exploit reproduction. |
| Category | resource-exhaustion |
| CWE | CWE-400 |
| Affected lines | src/UnifiedRgb.Core/Effects/ChromaSync.cs:100, src/UnifiedRgb.Core/Effects/ChromaSync.cs:161-169, src/UnifiedRgb.Core/Effects/ChromaSync.cs:197-205, src/UnifiedRgb.Core/Effects/ChromaSync.cs:224-233 |

#### Summary

CreateServer grants Everyone GRGW (line 100). Each accepted connection gets a reader thread. A client sending no frame blocks ReadExact indefinitely. Sixteen such connections occupy MaxClients; later legitimate connections are immediately closed. There is no idle deadline, cancellation, disconnect sweep, or Stop API to reclaim silent clients.

#### Root Cause

CreateServer grants Everyone GRGW (line 100). Each accepted connection gets a reader thread. A client sending no frame blocks ReadExact indefinitely. Sixteen such connections occupy MaxClients; later legitimate connections are immediately closed. There is no idle deadline, cancellation, disconnect sweep, or Stop API to reclaim silent clients.

**Medium local callers may connect** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:100`

The intended SDDL gives Everyone read/write access and uses a medium integrity label.

```csharp
    const string PipeSddl = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGW;;;WD)S:(ML;;NW;;;ME)";
```

**Finite admission slots** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:161-169`

Each accepted connection consumes a client slot; new clients are refused at the cap.

```csharp
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                pipe.Dispose();
                Log.Occasional("chroma-clients", "chroma", $"more than {MaxClients} pipe clients - refusing extra connections");
                continue;
            }
            if (_connLog.Allow()) Log.Info("chroma", "host connected to the pipe");
            new Thread(() => ServeClient(pipe)) { IsBackground = true, Name = "chroma-feed-client" }.Start();
```

**First frame read owns the slot** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:197-205`

The client reader retains its slot while attempting to read a complete header/body.

```csharp
                bool firstFrame = true;
                while (ReadExact(pipe, head, 5))
                {
                    int rows = head[1] | (head[2] << 8);
                    int cols = head[3] | (head[4] << 8);
                    int n = rows * cols;
                    if (n <= 0 || n > 4096) break;               // sanity
                    if (body.Length < n * 4) body = new byte[n * 4];
                    if (!ReadExact(pipe, body, n * 4)) break;
```

**Blocking read with no deadline** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:224-233`

A connected client can remain silent; no deadline or sweeper closes this stream to release admission capacity.

```csharp
    static bool ReadExact(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
```

#### Validation

CreateServer grants Everyone GRGW (line 100). Each accepted connection gets a reader thread. A client sending no frame blocks ReadExact indefinitely. Sixteen such connections occupy MaxClients; later legitimate connections are immediately closed. There is no idle deadline, cancellation, disconnect sweep, or Stop API to reclaim silent clients.

Validation method: independent parent static source trace

**Medium local callers may connect** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:100`

The intended SDDL gives Everyone read/write access and uses a medium integrity label.

```csharp
    const string PipeSddl = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGW;;;WD)S:(ML;;NW;;;ME)";
```

**Finite admission slots** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:161-169`

Each accepted connection consumes a client slot; new clients are refused at the cap.

```csharp
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                pipe.Dispose();
                Log.Occasional("chroma-clients", "chroma", $"more than {MaxClients} pipe clients - refusing extra connections");
                continue;
            }
            if (_connLog.Allow()) Log.Info("chroma", "host connected to the pipe");
            new Thread(() => ServeClient(pipe)) { IsBackground = true, Name = "chroma-feed-client" }.Start();
```

**First frame read owns the slot** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:197-205`

The client reader retains its slot while attempting to read a complete header/body.

```csharp
                bool firstFrame = true;
                while (ReadExact(pipe, head, 5))
                {
                    int rows = head[1] | (head[2] << 8);
                    int cols = head[3] | (head[4] << 8);
                    int n = rows * cols;
                    if (n <= 0 || n > 4096) break;               // sanity
                    if (body.Length < n * 4) body = new byte[n * 4];
                    if (!ReadExact(pipe, body, n * 4)) break;
```

**Blocking read with no deadline** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:224-233`

A connected client can remain silent; no deadline or sweeper closes this stream to release admission capacity.

```csharp
    static bool ReadExact(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
```

Assertions:
- Admission slots must be reclaimed from silent untrusted clients.

Limitations:
- No live attacker traffic, privileged executable replacement, or hardware tests were run.
- MaxClients=16 bounds reader threads and per-client buffers; frame sizes are capped at 4096 cells. These prevent unbounded allocation but do not restore availability of occupied admission slots.

#### Dataflow

CreateServer grants Everyone GRGW (line 100). Each accepted connection gets a reader thread. A client sending no frame blocks ReadExact indefinitely. Sixteen such connections occupy MaxClients; later legitimate connections are immediately closed. There is no idle deadline, cancellation, disconnect sweep, or Stop API to reclaim silent clients.

**Medium local callers may connect** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:100`

The intended SDDL gives Everyone read/write access and uses a medium integrity label.

```csharp
    const string PipeSddl = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGW;;;WD)S:(ML;;NW;;;ME)";
```

**Finite admission slots** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:161-169`

Each accepted connection consumes a client slot; new clients are refused at the cap.

```csharp
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                pipe.Dispose();
                Log.Occasional("chroma-clients", "chroma", $"more than {MaxClients} pipe clients - refusing extra connections");
                continue;
            }
            if (_connLog.Allow()) Log.Info("chroma", "host connected to the pipe");
            new Thread(() => ServeClient(pipe)) { IsBackground = true, Name = "chroma-feed-client" }.Start();
```

**First frame read owns the slot** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:197-205`

The client reader retains its slot while attempting to read a complete header/body.

```csharp
                bool firstFrame = true;
                while (ReadExact(pipe, head, 5))
                {
                    int rows = head[1] | (head[2] << 8);
                    int cols = head[3] | (head[4] << 8);
                    int n = rows * cols;
                    if (n <= 0 || n > 4096) break;               // sanity
                    if (body.Length < n * 4) body = new byte[n * 4];
                    if (!ReadExact(pipe, body, n * 4)) break;
```

**Blocking read with no deadline** — `src/UnifiedRgb.Core/Effects/ChromaSync.cs:224-233`

A connected client can remain silent; no deadline or sweeper closes this stream to release admission capacity.

```csharp
    static bool ReadExact(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
```

#### Reachability

A local medium-integrity process able to open the Everyone-writable UnifiedRgbChroma pipe. ChromaFeed is running; attacker occupies free slots before legitimate hosts connect.

- **Attacker:** A local medium-integrity process able to open the Everyone-writable UnifiedRgbChroma pipe.

- **Outcome:** Persistent loss of new DLL-shim lighting feeds until the attacker disconnects or the application restarts. Existing legitimate connections survive if they already occupy slots. This does not demonstrate code execution or entire-app failure.

#### Severity

**Low** — Local denial of new Chroma feeds; existing feeds and bounded app resources reduce impact.

Additional runtime or deployment evidence could raise or lower this severity.

Impact assessment:
- **Level:** low
- **Why:** Persistent loss of new DLL-shim lighting feeds until the attacker disconnects or the application restarts. Existing legitimate connections survive if they already occupy slots. This does not demonstrate code execution or entire-app failure.

Likelihood assessment:
- **Level:** medium
- **Why:** Local denial of new Chroma feeds; existing feeds and bounded app resources reduce impact.

#### Remediation

Add a first-frame deadline and subsequent idle/frame deadline. Use cancelable asynchronous named-pipe reads with handles configured for asynchronous operation, or a tracked-client sweeper that disposes expired streams. Always decrement the client count in the existing finally block. Test that sixteen silent clients are evicted and a real client can subsequently publish.

Tests:
- Evict sixteen silent test clients, then accept and publish a legitimate frame.
- A slow header/body cannot extend a total-frame deadline indefinitely.

Preventive controls:
- Apply bounded field/admission policies at protocol entry points.

<a id="finding-4"></a>

### [4] OpenRGB client names can forge persistent diagnostic log entries

| Field | Value |
| --- | --- |
| Severity | low |
| Confidence | high |
| Confidence rationale | Parent independently traced the original source and controls; no live exploit reproduction. |
| Category | log-injection |
| CWE | CWE-117 |
| Affected lines | src/UnifiedRgb.Core/Net/OpenRgbServer.cs:280-284, src/UnifiedRgb.Core/Log.cs:92-93 |

#### Summary

A PktSetClientName payload is decoded as ASCII, trimmed only at its boundaries, and interpolated directly into Log.Info. Embedded CR/LF creates arbitrary apparent log entries. Client names are reused in ownership/disconnection messages and exposed to the settings UI.

#### Root Cause

A PktSetClientName payload is decoded as ASCII, trimmed only at its boundaries, and interpolated directly into Log.Info. Embedded CR/LF creates arbitrary apparent log entries. Client names are reused in ownership/disconnection messages and exposed to the settings UI.

**Untrusted name becomes a log field** — `src/UnifiedRgb.Core/Net/OpenRgbServer.cs:280-284`

Trim leaves embedded controls in the client-supplied ASCII name, which is sent to logging and UI refresh.

```csharp
            case OpenRgbProtocol.PktSetClientName:
                client.Name = Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
                if (client.Name.Length == 0) client.Name = "unnamed";
                Log.Info("orgb-server", $"client '{client.Name}' connected (protocol {client.Version})");
                ClientsChanged?.Invoke();
```

**Persistent line append** — `src/UnifiedRgb.Core/Log.cs:92-93`

Embedded line breaks are written directly into the diagnostic log.

```csharp
                string line = $"{DateTime.Now:MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
                File.AppendAllText(PathName, line);
```

#### Validation

A PktSetClientName payload is decoded as ASCII, trimmed only at its boundaries, and interpolated directly into Log.Info. Embedded CR/LF creates arbitrary apparent log entries. Client names are reused in ownership/disconnection messages and exposed to the settings UI.

Validation method: independent parent static source trace

**Untrusted name becomes a log field** — `src/UnifiedRgb.Core/Net/OpenRgbServer.cs:280-284`

Trim leaves embedded controls in the client-supplied ASCII name, which is sent to logging and UI refresh.

```csharp
            case OpenRgbProtocol.PktSetClientName:
                client.Name = Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
                if (client.Name.Length == 0) client.Name = "unnamed";
                Log.Info("orgb-server", $"client '{client.Name}' connected (protocol {client.Version})");
                ClientsChanged?.Invoke();
```

**Persistent line append** — `src/UnifiedRgb.Core/Log.cs:92-93`

Embedded line breaks are written directly into the diagnostic log.

```csharp
                string line = $"{DateTime.Now:MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
                File.AppendAllText(PathName, line);
```

Assertions:
- Peer-controlled client names must remain one bounded display/log field.

Limitations:
- No live attacker traffic, privileged executable replacement, or hardware tests were run.
- Server packets are capped at 1 MiB and concurrent clients at 16.
- Log rotation caps retained log size; this is not an unlimited disk-growth finding.
- Unauthenticated LED control is an explicitly documented protocol feature, not an additional finding.

#### Dataflow

A PktSetClientName payload is decoded as ASCII, trimmed only at its boundaries, and interpolated directly into Log.Info. Embedded CR/LF creates arbitrary apparent log entries. Client names are reused in ownership/disconnection messages and exposed to the settings UI.

**Untrusted name becomes a log field** — `src/UnifiedRgb.Core/Net/OpenRgbServer.cs:280-284`

Trim leaves embedded controls in the client-supplied ASCII name, which is sent to logging and UI refresh.

```csharp
            case OpenRgbProtocol.PktSetClientName:
                client.Name = Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
                if (client.Name.Length == 0) client.Name = "unnamed";
                Log.Info("orgb-server", $"client '{client.Name}' connected (protocol {client.Version})");
                ClientsChanged?.Invoke();
```

**Persistent line append** — `src/UnifiedRgb.Core/Log.cs:92-93`

Embedded line breaks are written directly into the diagnostic log.

```csharp
                string line = $"{DateTime.Now:MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";
                File.AppendAllText(PathName, line);
```

#### Reachability

Any local OpenRGB protocol client; any reachable network client when LAN listening is enabled.

- **Attacker:** Any local OpenRGB protocol client; any reachable network client when LAN listening is enabled.

- **Outcome:** Forged diagnostics/support evidence; repeated large names also generate allocation, UI-update, and logging load.

#### Severity

**Low** — Diagnostic integrity impact is limited by local/opt-in LAN reachability and bounded packet/log storage.

Additional runtime or deployment evidence could raise or lower this severity.

Impact assessment:
- **Level:** low
- **Why:** Forged diagnostics/support evidence; repeated large names also generate allocation, UI-update, and logging load.

Likelihood assessment:
- **Level:** medium
- **Why:** Diagnostic integrity impact is limited by local/opt-in LAN reachability and bounded packet/log storage.

#### Remediation

Cap names to a small number of characters, replace all control characters, and rate-limit rename logging/UI notifications. Reuse the intent of ChromaRestServer.AppTitle's existing sanitization. Test a name containing embedded newlines and a near-limit payload.

Tests:
- Embedded CR/LF/NUL remain one bounded diagnostic/display field.
- Repeated names do not create per-packet UI refreshes or unbounded-length log records.

Preventive controls:
- Apply bounded field/admission policies at protocol entry points.

## Reviewed Surfaces

| Surface | Risk Area | Outcome | Notes |
| --- | --- | --- | --- |
| Update and helper executable trust | not recorded | Reported | S1/S2 retain same-user and user-action/optional-bridge prerequisites. PawnIO installer separately verifies Authenticode and sharing protection. AppPaths.cs:54; OpenRgbManager.cs:178; PawnIoInstaller.cs:41. |
| OpenRGB input and logging | not recorded | Reported | S3: embedded controls in names reach logs. Packet/client/time bounds already exist; intended LED control is not a finding. OpenRgbServer.cs:62,215,234,280. |
| Chroma pipe admission | not recorded | Reported | S4: finite slots have no idle deadline. Dimensions/client count bound memory; shim uses identification-level pipe access, and no cross-boundary native memory corruption was validated. ChromaSync.cs:100,161,198,229. |
| HTTP body availability | not recorded | Needs follow-up | I1/I2 retained as candidates pending Windows HTTP.sys behavior measurement. ChromaRestServer.cs:83,112; GsiServer.cs:119,142. |
| Baseline controls and source coverage | not recorded | No issue found | Root/src/native security-policy resolver outputs were empty. The app explicitly requires administrator privileges. PawnIO installer verification pins an exact certificate RDN, obtains the signer from WinVerifyTrust state, and holds a no-write/no-delete sharing handle across verification and launches; the obvious temporary-file replacement attack is addressed. Chroma named-pipe dimensions are bounded before allocation; ushort dimensions cannot wrap their product to a small positive int, so no integer-overflow allocation bypass was found. Chroma pipe clients use SECURITY_IDENTIFICATION; a malicious pipe server cannot obtain an impersonation-capable token from the current shim. OpenRGB packet allocations and controller counts have explicit caps; malformed managed parsing does not establish native memory corruption. Native shim parameters are supplied by code already inside the hosting process; malformed pointers alone do not constitute a cross-boundary exploit. Native shim full read found potential ordinary lifecycle/exception-handling concerns, but no validated additional security exploit. No application execution, network access, exploit tests, or source modifications were performed. Coverage focused on exposed listeners, updater, privileged executable loading and native shim; this is not a claim that every repository file was fully audited. OpenRGB has a 16-client cap, 120-second per-read timeout, 3-second write timeout, and 1 MiB payload limit. No unbounded connection/thread allocation finding is warranted from this path. OpenRGB ReadExactly uses per-read timeouts rather than a total packet deadline; slow progress can extend a packet lifetime. With intentional unauthenticated lighting control and bounded clients this is primarily optional hardening unless stronger availability requirements apply. GSI stores at most approximately 512K decoded characters, despite the MaxBodyBytes name; it does not accept an unbounded body into memory. ContentLength64 is separately checked against 512 KiB. No TimeoutManager, EntityBody, DrainEntityBody, or RequestQueue customization was found in src. Chroma REST is started from MainViewModel alongside ChromaFeed and stopped during app shutdown. Native shim opens the same named pipe and uses overlapped writes with a 50 ms wait plus cancellation. Only relevant pipe I/O sections were inspected, not the full native file. The pipe DACL intentionally retains GENERIC_WRITE compatibility for installed shims; do not present the documented compatibility decision alone as an undiscovered exploit. HTTP.sys effective timeout defaults were not verified because this task prohibited network and application execution. |
| Architecture consumer reconciliation | not recorded | Needs follow-up | Chroma pipe and REST ingestion are unconditional normal GUI startup surfaces, not only active-effect initialization (src/UnifiedRgb.App/MainViewModel.cs:1239). GUI OpenRGB SDK server is distinct from the managed OpenRGB bridge process. Do not transfer GUI loopback-binding guarantees to the external child without inspecting the external server's effective configuration. SDK ingress already caps connection count, packet size, and socket timeouts; claims of unrestricted payload allocation must account for those controls (src/UnifiedRgb.Core/Net/OpenRgbServer.cs:62; src/UnifiedRgb.Core/Net/OpenRgbServer.cs:215; src/UnifiedRgb.Core/Net/OpenRgbServer.cs:234). Public update checks do occur via GitHub despite stale Backend comments saying they skip (src/UnifiedRgb.Core/AppPaths.cs:32; src/UnifiedRgb.Core/UpdateClient.cs:43). AppPaths separates roaming/local state but does not itself apply privileged ACLs (src/UnifiedRgb.Core/AppPaths.cs:27). Update payload/script staging is beside the actual process executable, not TEMP; any staging concern must use that complete location (src/UnifiedRgb.App/Services/UpdateService.cs:109). Actual installation ACLs and external OpenRGB server binding/dependency internals remain deployment questions. |
| Baseline executable trust and inbound/native controls | not recorded | Needs follow-up | Root/src/native security-policy resolver outputs were empty. The app explicitly requires administrator privileges. PawnIO installer verification pins an exact certificate RDN, obtains the signer from WinVerifyTrust state, and holds a no-write/no-delete sharing handle across verification and launches; the obvious temporary-file replacement attack is addressed. Chroma named-pipe dimensions are bounded before allocation; ushort dimensions cannot wrap their product to a small positive int, so no integer-overflow allocation bypass was found. Chroma pipe clients use SECURITY_IDENTIFICATION; a malicious pipe server cannot obtain an impersonation-capable token from the current shim. OpenRGB packet allocations and controller counts have explicit caps; malformed managed parsing does not establish native memory corruption. Native shim parameters are supplied by code already inside the hosting process; malformed pointers alone do not constitute a cross-boundary exploit. Native shim full read found potential ordinary lifecycle/exception-handling concerns, but no validated additional security exploit. No application execution, network access, exploit tests, or source modifications were performed. Coverage focused on exposed listeners, updater, privileged executable loading and native shim; this is not a claim that every repository file was fully audited. |
| Focused inbound availability | not recorded | Needs follow-up | OpenRGB has a 16-client cap, 120-second per-read timeout, 3-second write timeout, and 1 MiB payload limit. No unbounded connection/thread allocation finding is warranted from this path. OpenRGB ReadExactly uses per-read timeouts rather than a total packet deadline; slow progress can extend a packet lifetime. With intentional unauthenticated lighting control and bounded clients this is primarily optional hardening unless stronger availability requirements apply. GSI stores at most approximately 512K decoded characters, despite the MaxBodyBytes name; it does not accept an unbounded body into memory. ContentLength64 is separately checked against 512 KiB. No TimeoutManager, EntityBody, DrainEntityBody, or RequestQueue customization was found in src. Chroma REST is started from MainViewModel alongside ChromaFeed and stopped during app shutdown. Native shim opens the same named pipe and uses overlapped writes with a 50 ms wait plus cancellation. Only relevant pipe I/O sections were inspected, not the full native file. The pipe DACL intentionally retains GENERIC_WRITE compatibility for installed shims; do not present the documented compatibility decision alone as an undiscovered exploit. HTTP.sys effective timeout defaults were not verified because this task prohibited network and application execution. |

## Open Questions And Follow Up

- Measure HTTP.sys request timeouts/queue effects before promoting I1/I2.
- Actual installation ACLs and supplied external OpenRGB server behavior were not inspected.
- The full repository has not been audited; deferred paths record remaining source scope.
- Application concurrency gap confirmed, but whole-app starvation/HTTP.sys effective limits require bounded runtime measurement.
  - Follow-up prompt: Review deferred unit chroma-http-queue and close its stated proof gap. Paths: src/UnifiedRgb.Core/Net/ChromaRestServer.cs.
- Serial pre-authentication body read confirmed; effective HTTP.sys timeout and game-silence impact need isolated Windows validation.
  - Follow-up prompt: Review deferred unit gsi-serial-body and close its stated proof gap. Paths: src/UnifiedRgb.Core/Games/GsiServer.cs.
- Not fully security-audited in this focused review. Ordinary source review and architecture mapping do not count as exhaustive security coverage.
  - Follow-up prompt: Review deferred unit deferred-104da4d472b12adb and close its stated proof gap. Paths: .github/workflows/build.yml, release.ps1, src/UnifiedRgb.App/App.xaml, src/UnifiedRgb.App/App.xaml.cs, src/UnifiedRgb.App/AppInfo.cs, src/UnifiedRgb.App/AppRulesWindow.xaml, src/UnifiedRgb.App/AppRulesWindow.xaml.cs, src/UnifiedRgb.App/AssemblyInfo.cs, src/UnifiedRgb.App/CanvasWindow.xaml, src/UnifiedRgb.App/CanvasWindow.xaml.cs, src/UnifiedRgb.App/CoalescingApplier.cs, src/UnifiedRgb.App/ColorWheel.cs, src/UnifiedRgb.App/Controls/FanCurveEditor.xaml, src/UnifiedRgb.App/Controls/FanCurveEditor.xaml.cs, src/UnifiedRgb.App/Controls/LianLiFanView.cs, src/UnifiedRgb.App/Controls/PaletteStrip.xaml, src/UnifiedRgb.App/Controls/PaletteStrip.xaml.cs, src/UnifiedRgb.App/Controls/SwatchGrid.xaml, src/UnifiedRgb.App/Controls/SwatchGrid.xaml.cs, src/UnifiedRgb.App/Controls/TempGauge.xaml, src/UnifiedRgb.App/Controls/TempGauge.xaml.cs, src/UnifiedRgb.App/Converters.cs, src/UnifiedRgb.App/Dialogs.cs, src/UnifiedRgb.App/ExitBehaviorWindow.xaml, src/UnifiedRgb.App/ExitBehaviorWindow.xaml.cs, src/UnifiedRgb.App/HeaderConfigDialog.cs, src/UnifiedRgb.App/LcdController.cs, src/UnifiedRgb.App/LcdDesign.cs, src/UnifiedRgb.App/LcdWidgets.cs, src/UnifiedRgb.App/LedPreview.cs, src/UnifiedRgb.App/LianLayoutWindow.xaml, src/UnifiedRgb.App/LianLayoutWindow.xaml.cs, src/UnifiedRgb.App/MainViewModel.Cooling.cs, src/UnifiedRgb.App/MainViewModel.Lcd.cs, src/UnifiedRgb.App/MainViewModel.Lian.cs, src/UnifiedRgb.App/MainViewModel.Profiles.cs, src/UnifiedRgb.App/MainViewModel.Settings.cs, src/UnifiedRgb.App/MainViewModel.Support.cs, src/UnifiedRgb.App/MainViewModel.cs, src/UnifiedRgb.App/MainWindow.xaml, src/UnifiedRgb.App/MainWindow.xaml.cs, src/UnifiedRgb.App/MediaService.cs, src/UnifiedRgb.App/Models.cs, src/UnifiedRgb.App/PaletteLibrary.cs, src/UnifiedRgb.App/PaletteLibraryWindow.xaml, src/UnifiedRgb.App/PaletteLibraryWindow.xaml.cs, src/UnifiedRgb.App/PawnIoCpuTempProvider.cs, src/UnifiedRgb.App/ProfileStore.cs, src/UnifiedRgb.App/RazerLayoutDialog.cs, src/UnifiedRgb.App/RelayCommand.cs, src/UnifiedRgb.App/Scenes.cs, src/UnifiedRgb.App/SchedulesWindow.xaml, src/UnifiedRgb.App/SchedulesWindow.xaml.cs, src/UnifiedRgb.App/SensorRulesWindow.xaml, src/UnifiedRgb.App/SensorRulesWindow.xaml.cs, src/UnifiedRgb.App/Services/AutomationService.cs, src/UnifiedRgb.App/Services/BatteryMonitor.cs, src/UnifiedRgb.App/Services/LianBakeService.cs, src/UnifiedRgb.App/Services/LightingController.cs, src/UnifiedRgb.App/Services/OpenRgbHost.cs, src/UnifiedRgb.App/Services/SupportService.cs, src/UnifiedRgb.App/Themes/Styles.xaml, src/UnifiedRgb.App/Themes/Styles.xaml.cs, src/UnifiedRgb.App/UnifiedRgb.App.csproj, src/UnifiedRgb.App/ViewModels/CoolingViewModel.cs, src/UnifiedRgb.App/ViewModels/LcdDesignerViewModel.cs, src/UnifiedRgb.App/ViewModels/LianFanSelection.cs, src/UnifiedRgb.App/ViewModels/MainWindowState.cs, src/UnifiedRgb.App/Views/CoolingPane.xaml, src/UnifiedRgb.App/Views/CoolingPane.xaml.cs, src/UnifiedRgb.App/Views/KeyPolicy.cs, src/UnifiedRgb.App/Views/LcdDesignerPane.xaml, src/UnifiedRgb.App/Views/LcdDesignerPane.xaml.cs, src/UnifiedRgb.App/Views/LeftNavPanel.xaml, src/UnifiedRgb.App/Views/LeftNavPanel.xaml.cs, src/UnifiedRgb.App/Views/LightingPane.xaml, src/UnifiedRgb.App/Views/LightingPane.xaml.cs, src/UnifiedRgb.App/Views/SettingsPane.xaml, src/UnifiedRgb.App/Views/SettingsPane.xaml.cs, src/UnifiedRgb.App/WizardWindow.xaml, src/UnifiedRgb.App/WizardWindow.xaml.cs, src/UnifiedRgb.Cli/Program.cs, src/UnifiedRgb.Cli/UnifiedRgb.Cli.csproj, src/UnifiedRgb.Core/Audio/AudioAnalyzer.cs, src/UnifiedRgb.Core/Automation/AutomationDecision.cs, src/UnifiedRgb.Core/Automation/AutomationRule.cs, src/UnifiedRgb.Core/Automation/ScheduleRule.cs, src/UnifiedRgb.Core/Automation/SensorRule.cs, src/UnifiedRgb.Core/ColorUtil.cs, src/UnifiedRgb.Core/DeviceManager.cs, src/UnifiedRgb.Core/Devices/CorsairStrafeMk2.cs, src/UnifiedRgb.Core/Devices/EneDram.cs, src/UnifiedRgb.Core/Devices/GigabyteIt5711.cs, src/UnifiedRgb.Core/Devices/ILianFanDevice.cs, src/UnifiedRgb.Core/Devices/LianLiTinyuz.cs, src/UnifiedRgb.Core/Devices/LianLiUniHub.cs, src/UnifiedRgb.Core/Devices/LianLiWireless.cs, src/UnifiedRgb.Core/Devices/LogitechG403.cs, src/UnifiedRgb.Core/Devices/MsiGpu.cs, src/UnifiedRgb.Core/Devices/OpenRgbDevice.cs, src/UnifiedRgb.Core/Devices/RazerHid.cs, src/UnifiedRgb.Core/Devices/SayoDevice.cs, src/UnifiedRgb.Core/Devices/SteelSeriesApex.cs, src/UnifiedRgb.Core/Devices/ThermalrightLcd.cs, src/UnifiedRgb.Core/DiagnosticReport.cs, src/UnifiedRgb.Core/Effects/AudioEffects.cs, src/UnifiedRgb.Core/Effects/CanvasLayout.cs, src/UnifiedRgb.Core/Effects/CanvasMapper.cs, src/UnifiedRgb.Core/Effects/ColorGrid.cs, src/UnifiedRgb.Core/Effects/Cs2Effect.cs, src/UnifiedRgb.Core/Effects/EffectEngine.cs, src/UnifiedRgb.Core/Effects/ExtraEffects.cs, src/UnifiedRgb.Core/Effects/Geo.cs, src/UnifiedRgb.Core/Effects/IEffect.cs, src/UnifiedRgb.Core/Effects/LianStackEffects.cs, src/UnifiedRgb.Core/Effects/MoreEffects.cs, src/UnifiedRgb.Core/Effects/PatternEffect.cs, src/UnifiedRgb.Core/Effects/ReactiveEffects.cs, src/UnifiedRgb.Core/Effects/ScreenSync.cs, src/UnifiedRgb.Core/Effects/TempEffects.cs, src/UnifiedRgb.Core/Effects/WallpaperCapture.cs, src/UnifiedRgb.Core/Games/GsiConfig.cs, src/UnifiedRgb.Core/HardwareConfig.cs, src/UnifiedRgb.Core/HardwareExit.cs, src/UnifiedRgb.Core/IRgbDevice.cs, src/UnifiedRgb.Core/Input/HidUsageVk.cs, src/UnifiedRgb.Core/Input/KeyboardTap.cs, src/UnifiedRgb.Core/Master.cs, src/UnifiedRgb.Core/MemoryTrimmer.cs, src/UnifiedRgb.Core/Native/HidNative.cs, src/UnifiedRgb.Core/Native/NvApi.cs, src/UnifiedRgb.Core/Native/SetupDiEnum.cs, src/UnifiedRgb.Core/Native/SmbusPiix4.cs, src/UnifiedRgb.Core/Native/Wasapi.cs, src/UnifiedRgb.Core/Native/WinUsbNative.cs, src/UnifiedRgb.Core/Net/ExternalOwnership.cs, src/UnifiedRgb.Core/Net/OpenRgbCrashBisect.cs, src/UnifiedRgb.Core/Net/OpenRgbDetectorConfig.cs, src/UnifiedRgb.Core/Net/OpenRgbLink.cs, src/UnifiedRgb.Core/Net/OpenRgbProtocol.cs, src/UnifiedRgb.Core/NowPlayingText.cs, src/UnifiedRgb.Core/Redaction.cs, src/UnifiedRgb.Core/Rgb.cs, src/UnifiedRgb.Core/SafeFile.cs, src/UnifiedRgb.Core/Sensors/FanCurve.cs, src/UnifiedRgb.Core/Sensors/GigabyteEcio.cs, src/UnifiedRgb.Core/Sensors/GigabyteIsaBridge.cs, src/UnifiedRgb.Core/Sensors/IteSuperIo.cs, src/UnifiedRgb.Core/Sensors/LhmFans.cs, src/UnifiedRgb.Core/Sensors/RyzenCpuTemperature.cs, src/UnifiedRgb.Core/Sensors/SensorHub.cs, src/UnifiedRgb.Core/SnapGuides.cs, src/UnifiedRgb.Core/UndoStack.cs, src/UnifiedRgb.Core/UnifiedRgb.Core.csproj, src/UnifiedRgb.Diag/Program.cs, src/UnifiedRgb.Diag/UnifiedRgb.Diag.csproj, src/UnifiedRgb.Tests/Program.cs, src/UnifiedRgb.Tests/UnifiedRgb.Tests.csproj.
