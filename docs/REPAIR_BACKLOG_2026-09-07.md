# UnifiedRGB repair backlog — 2026-09-07

This document is an implementation handoff for a smaller coding model. It records current source-backed defects, security findings, performance work, and bounded refactorings. It does not claim every repository file was audited or that every item is newly introduced. Earlier review checkboxes are not evidence that these current paths are correct.

**Snapshot:** `7716aab111436d883829edbcdf75f4e58edbcc2a`, plus the working tree observed on 2026-09-07. An existing user edit in `src/UnifiedRgb.Core/Net/OpenRgbCrashBisect.cs` was preserved. No product source was changed. Source references below use repository-relative paths and one-based line numbers at that snapshot; search the named symbol if lines move.

## Status — worked 2026-09-07

Every claim was re-verified against current source before anything was changed;
two did not survive that intact, and are marked below. Commits `77180f0`,
`04ccc98`, `87f90f7`. Tests went 799 -> 833. Where a fix had a regression test,
that test was also run against the OLD code to confirm it fails there - a
regression test that passes both ways proves nothing.

**Done:** R1, B1, B2, B3, B4, B5, B9, B10, B11, S1, S3, S4, and a narrowed B6.

**Corrections to this document, found while implementing:**

- **B6 was followed only in part, deliberately.** Honoring the discarded
  `Wait` result and skipping disposal on timeout leaks a kernel driver handle
  that has no finalizer - which is the regression the current code was written
  to avoid. The escalation ("ResetSources can install replacement sources
  before the old callback exits") is also wrong: `_ticking`, the `_running`
  latch and the ordering of `_sourcesOpened` each independently prevent it. The
  real hazard is narrower - `pawnio_execute` on a freed context, an access
  violation that no `catch` can contain - so `PawnIO.Dispose` now waits out an
  in-flight ioctl instead. Note this makes disposal wait on native code; it is
  bounded in practice because a PawnIO call is a register read.
- **B1 has two more sites than it lists.** `SetFanCurve` and `ReconcileFans`
  carry the same `if (t is double temp)` guard, so a curve set or restored
  while its source is already null applies nothing at all. Both are now covered
  by the periodic loop rather than separately: they leave the curve in
  `_fanCurves`, so the tick sees it, starts its grace clock and hands the fan
  back if no reading arrives. The guards themselves are left alone - applying
  a curve with no temperature to apply it at is not something to invent a
  value for.
- **S1 is dormant rather than armed.** The private feed path is inert in a
  public build: no build props are passed, and `backend.json` does not exist in
  a default install. It is "a path an attacker can arm with one file write",
  not a live vulnerability in a shipped build. Fixed anyway, because it is one
  of the few places where same-user code execution can borrow the elevated
  token - and with the `/RL HIGHEST` logon task, keep it.
- **S3's better story is not log forgery.** `PktSetClientName` was the one
  ingress handler that logged unconditionally, so a rename loop could flush the
  whole diagnostic history out of the log and its single rotation in seconds.
  Fixed along with the sanitizing.
- **B11 is real but rarely triggered.** A shared-mode WASAPI packet is
  typically ~480 frames, so the hop loop usually runs 0 or 1 times; it bites on
  long-period endpoints and after a stall.
- **B9's "malformed drawing numbers" case does not exist as written.** A null
  on a non-nullable number makes the whole file fail to deserialize, which the
  store's corrupt-file handling already covers, and JSON cannot express NaN.

**Not done, and why:**

- **S2 (OpenRGB bundle)** - agreed and the highest-value item left, but the
  honest fix is relocating binaries to a protected directory with a migration
  that re-downloads rather than moves. That is an installer change, not a
  patch, and marking the existing directory read-only is not a fix.
- **B7 (LCD buffer lease)**, **B8 (GIF disposal)** - real, both narrower than
  they look. Neither has produced a reported symptom.
- **PERF1/2/3, I1, I2** - these are measurement tasks by their own text. None
  should be "fixed" before the baseline they ask for exists.

**Note on R1:** the harness now references the App project, so it can no longer
build while the app is running. The documented loop already stops the app
first, but it is a real constraint.

## How to use this document

1. Fix one ID per change. Read its source anchors and callers before editing. Reconfirm the defect against the current checkout; do not blindly apply advice after code has moved.
2. Start with R1 (isolated tests), then the P1 correctness and executable-trust items. Security/native lifetime changes need a human or stronger-model review even if a smaller model implements the bounded patch.
3. Write the listed failing regression first where practical; use fake devices and deterministic barriers. Never exercise a failure test against real fans, raw SMBus, kernel handles, or the user's update installation.
4. Keep the fix narrow. Preserve existing brightness, coalescing, saved-profile, device-ownership, and safety-floor contracts. Do not combine a refactor with unrelated behavior changes.
5. For performance items, record a baseline and after measurement on the same workload. Distinguish retained memory, allocation rate, working set, private bytes, and GC pauses.
6. In the completion note, report changed files, tests and outcomes, unresolved limitations, and the ID. If a reproduction contradicts this report, document the counterevidence rather than forcing the proposed fix.

**Priority:** P1 = address early (serious correctness, executable trust, native lifetime, or test safety); P2 = normal corrective work; P3 = measured optimization. Priority is engineering order, not a CVSS score. S1–S4 are validated static security findings; I1/I2 remain candidates. PERF3 is a performance hypothesis, not a measured regression.

**Suggested handoff prompt:**

> Implement only `<ID>` from `docs/REPAIR_BACKLOG_2026-09-07.md`. Reconfirm its source trace, add its focused regression using isolated state/fake hardware, make the smallest coherent fix, and run relevant tests. Preserve unrelated changes, especially any existing OpenRgbCrashBisect edit. Do not launch hardware diagnostics, install drivers, contact update feeds, or execute downloaded payloads. Report evidence, not just a claim that the issue is fixed. Stop for design review if the item’s stated security/native-lifetime invariant cannot be preserved.

## Triage index

| ID | Priority | Work item | Evidence |
|---|---|---|---|
| [S1](#s1) | P1 | Stop user-writable backend settings from selecting elevated update code | High, source validated |
| [S2](#s2) | P1 | Protect the OpenRGB executable bundle before launching it elevated | High, source validated |
| [B1](#b1) | P1 | Return curve-controlled fans to a safe mode when their temperature source disappears | High, source validated |
| [B2](#b2) | P1 | Compose all animated zones before writing a non-zone device | High, isolated fake-device reproduction |
| [B3](#b3) | P1 | Retain previous external partial updates on non-zone devices | High, isolated fake-device reproduction |
| [S3](#s3) | P2 | Bound and sanitize OpenRGB client names | High, source validated |
| [S4](#s4) | P2 | Expire Chroma pipe clients that never send a frame | High, source validated |
| [B4](#b4) | P2 | Update Logitech dedup/persist state only after successful device I/O | High, source validated |
| [B5](#b5) | P2 | Abort a Thermalright frame when an HID report fails | High, source validated |
| [B6](#b6) | P1 | Do not dispose sensor sources after an uncompleted drain timeout | High, source validated |
| [B7](#b7) | P2 | Lease LCD render buffers until the consumer has copied them | High, source validated |
| [B8](#b8) | P2 | Honor GIF disposal and logical canvas metadata | High, source validated |
| [B9](#b9) | P2 | Normalize deserialized scene and LCD collections before startup consumes them | High, source validated |
| [B10](#b10) | P2 | Key foreground-app caching by the actual window and process lifetime | High, source validated |
| [B11](#b11) | P2 | Advance FFT windows per hop instead of analyzing the same final window repeatedly | High, source validated |
| [PERF1](#perf1) | P2 | Bound LCD image decoding before allocating full-resolution pixels | High allocation path; peak size not measured |
| [PERF2](#perf2) | P3 | Let preview callers reuse composed-frame and channel storage | High allocation path; total cost not measured |
| [PERF3](#perf3) | P3 | Measure and coalesce full-GC/working-set trims on minimize | Confirmed scheduling behavior; performance impact unmeasured |
| [R1](#r1) | P1 | Make the test harness independent of the real user profile | High, source validated |
| [I1](#i1) | P2 | Validate and cap Chroma REST outstanding request work | Medium; deployment limits need validation |
| [I2](#i2) | P2 | Validate and bound slow GSI bodies without serializing all updates | Medium; timeout impact needs validation |

## Detailed work items

<a id="s1"></a>

### S1 — Stop user-writable backend settings from selecting elevated update code

**P1 · Security; CWE-829 · Scope: Medium; security review required**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/AppPaths.cs:54` (`string f = AppPaths.Config("backend.json")`)
- `src/UnifiedRgb.Core/UpdateClient.cs:43` (`=> Backend.Configured ?`)
- `src/UnifiedRgb.App/Services/UpdateService.cs:141` (`if (!string.IsNullOrEmpty(sha))`)
- `src/UnifiedRgb.App/app.manifest:13` (`level="requireAdministrator"`)

**Trigger and impact:** A same-user, medium-integrity process changes backend.json before the next app launch. A later click on the ordinary update button consumes that endpoint’s version, payload, and matching hash. This works even if the original app executable is in a protected directory.

**Why it happens:** The endpoint override is read from roaming AppData. HTTPS/loopback scheme checks and logging do not establish publisher identity. The attacker supplies both the executable and the hash accepted by the administrator updater. A newer file-version resource is not authentication.

**Implementation steps:**

1. Give elevated updates a trust policy separate from mutable support/backend configuration. Anchor a signing key or trusted publisher identity in protected application code; verify signed release metadata and the selected payload before staging a swap.
2. For a smaller first patch, disable runtime backend overrides for executable updates in public builds and retain the fixed public release source. Keep legitimate private-feed configuration in a protected, explicitly configured channel. Do not silently change support routing.
3. Fail closed on missing or invalid verification metadata. Preserve checks through the actual swap. Add tests around a pure update-policy/verification boundary; tests must never execute payloads.

**Acceptance checks:**

- [ ] An attacker-controlled override with a newer version and self-consistent SHA cannot authorize a swap.
- [ ] A known-good signed fixture succeeds; wrong signer/key, missing metadata, tampered bytes, and replayed old versions fail.
- [ ] Confirm protected-location and public/private-feed behavior in a Windows test account before release.

**Limits / guardrails:** Security severity: medium under the scan’s likelihood calibration (high impact, same-user foothold plus a subsequent update click). Engineering priority P1 reflects administrator code execution. No live exploit was run; no unauthenticated remote takeover is claimed.

<a id="s2"></a>

### S2 — Protect the OpenRGB executable bundle before launching it elevated

**P1 · Security; CWE-427 · Scope: Medium; security review required**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:28` (`static readonly string Root`)
- `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:59` (`Directory.EnumerateFiles(Root`)
- `src/UnifiedRgb.Core/Net/OpenRgbManager.cs:178` (`_proc = Process.Start`)

**Trigger and impact:** With the optional managed bridge installed but stopped, a same-user unelevated process replaces OpenRGB.exe or a loadable dependency under LocalAppData. A subsequent bridge start launches those bytes with the GUI’s administrator token.

**Why it happens:** IsInstalled checks file presence and a writable version stamp. FindExe recursively selects the first matching executable. The writable bundle remains executable authority after its original HTTPS download; no protected ACL or authenticated launch inventory is established here.

**Implementation steps:**

1. Separate immutable executable/dependency storage from mutable OpenRGB configuration. Install binaries into an administrator-protected directory and validate its effective ACL, including upgrade/migration behavior.
2. Use an exact expected executable path and authenticate the bundle against trusted metadata. Cover DLLs and other executable dependencies; checking only the EXE hash leaves the same class of issue.
3. Migrate existing per-user bundles by reinstalling from the trusted source into protected storage. Preserve detector configuration as data. Do not merely mark the existing writable directory read-only.

**Acceptance checks:**

- [ ] From a medium-integrity test process, EXE/DLL replacement in the new binary directory is denied.
- [ ] Tampered legacy bundles are never executed during migration.
- [ ] Start, stop, restart, and existing-external-server reuse still work.

**Limits / guardrails:** Security severity: medium (high impact, conditional on bridge use and a same-user foothold). A running external server is reused, so that particular call does not launch the bundle. This is separate from S1: both controls need fixes.

<a id="b1"></a>

### B1 — Return curve-controlled fans to a safe mode when their temperature source disappears

**P1 · Functional bug; thermal-control reliability · Scope: Medium; hardware review required**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:302` (`if (t is double temp)`)
- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:316` (`bool tooHot =`)
- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:241` (`try { CpuTempC = cpu?.ReadCelsius(); }`)

**Trigger and impact:** A fan curve has already applied a low duty. Its selected CPU/GPU temperature becomes unavailable on later ticks, such as after a sensor/driver failure.

**Why it happens:** The curve loop simply skips ApplyDuty for a missing temperature. The over-temperature failsafe tests only present readings; missing values do not trip it. The previous manual duty can therefore remain active without a usable input. Other healthy temperature sources may eventually trip the existing heat failsafe, but they do not cover loss of all relevant readings.

**Implementation steps:**

1. Track availability/freshness for each controlled curve’s effective source using a monotonic clock.
2. After an explicit, short grace interval, relinquish the affected fan to firmware control (or the established supported safe fallback). Keep saved configuration, report the reason, and define deliberate re-arming behavior.
3. Preserve intentional GPU fan-stop semantics and hardware floors. Do not invent new duty thresholds or lower existing failsafes. Keep initial warm-up separate from a lost previously valid reading.

**Acceptance checks:**

- [ ] With a fake temperature source: valid cool reading applies a low curve duty; subsequent null readings beyond the grace interval cause exactly one safe handoff.
- [ ] A brief recoverable missing sample does not chatter modes; a continuously missing source cannot leave the fan indefinitely under stale curve control.
- [ ] Test CPU, GPU, Hottest, and healthy sibling fans without physical hardware first.

**Limits / guardrails:** No physical overheating was tested or claimed. Firmware protection may still limit temperatures; the finding is that the application does not honor a fail-safe response to missing control input.

<a id="b2"></a>

### B2 — Compose all animated zones before writing a non-zone device

**P1 · Functional bug; reproduced · Scope: Medium**

**Evidence:** High, isolated fake-device reproduction.

**Start here:**

- `src/UnifiedRgb.Core/Effects/EffectEngine.cs:214` (`var zoneDev = ch.Device as IZoneWritable;`)
- `src/UnifiedRgb.Core/Effects/EffectEngine.cs:287` (`Array.Copy(scaledBase!, full!`)
- `src/UnifiedRgb.Core/Effects/EffectEngine.cs:305` (`else ch.Device.SetColors(full!)`)
- `src/UnifiedRgb.Core/Devices/LogitechG403.cs:23` (`class LogitechG403 : IRgbDevice`)

**Trigger and impact:** Run red on LED/zone 0 and blue on LED/zone 1 of one device that implements IRgbDevice but not IZoneWritable. A two-zone Logitech mouse is a real supported shape.

**Why it happens:** Every channel builds a full-device frame from the stored static base and only its own animated slice. Its write wipes the other channel’s animation. Locks can serialize the competing writes but cannot make their frame contents correct. The preview composes all channels separately and can misleadingly look correct.

**Implementation steps:**

1. Create one composed output owner per non-zone device. Merge every running channel’s current unscaled slice over the static base, scale once, then write the full frame.
2. Keep channel replacement/range-stop behavior and static sibling changes intact. Do not fix this by suppressing one valid channel or by treating stored statics as the other channel’s current output.
3. Retain the IZoneWritable fast path; ensure the full-frame compose/publication is synchronized and has no per-frame allocation.

**Acceptance checks:**

- [ ] Two fake-device channels on disjoint ranges must eventually produce [red, blue], and subsequent writes while both remain active must preserve both slices.
- [ ] Change a static third zone while both effects run; all three ranges must survive.
- [ ] Existing overlapping-range replacement, master brightness, and stopping/restoring tests still pass.

**Limits / guardrails:** The isolated probe compiled the current EffectEngine and LightingController source with fake hardware and unrelated dependency stubs. Observed: engine frames=2; both zones preserved=False. Existing tests exercise channel bookkeeping and one animated plus one static zone, but not two animated siblings.

<a id="b3"></a>

### B3 — Retain previous external partial updates on non-zone devices

**P1 · Functional bug; reproduced · Scope: Small to medium; coordinate with B2**

**Evidence:** High, isolated fake-device reproduction.

**Start here:**

- `src/UnifiedRgb.App/Services/LightingController.cs:97` (`public void PushExternalFrame`)
- `src/UnifiedRgb.App/Services/LightingController.cs:119` (`var whole = (Rgb[])FrameFor(dev).Clone();`)
- `src/UnifiedRgb.App/Services/OpenRgbHost.cs:84` (`public void PushExternal`)

**Trigger and impact:** An SDK client sets zone/LED 0 red, then sets zone/LED 1 blue on a device without IZoneWritable.

**Why it happens:** Each partial update starts from FrameFor(dev), which deliberately remains the user’s original static frame. It does not contain earlier external updates. The second write rebuilds LED 0 from the old static color. The comment promising a merge over the currently displayed frame does not match this implementation.

**Implementation steps:**

1. Maintain a separate external working frame per claimed device, initialized from the takeover state. Apply incoming slices to that frame atomically and queue a stable composed snapshot.
2. Keep the user’s saved static frame untouched for restoration. Clear external state on release, reset, rescan, and shutdown.
3. Specify how simultaneous clients and whole-frame/zone-frame queue keys interact so an older queued partial write cannot follow a newer complete frame.

**Acceptance checks:**

- [ ] With initially black statics, write red at offset 0 and blue at offset 1, draining between calls: hardware ends [red, blue].
- [ ] Repeat with queued rapid updates and non-100% master brightness; scaling is applied once.
- [ ] After release, original user lighting returns; no external pixels leak into saved profiles.

**Limits / guardrails:** Probe output: external final=000000,0000FF; expected=FF0000,0000FF; saved statics unchanged=True. IZoneWritable partial updates take another path and are not the demonstrated failure.

<a id="s3"></a>

### S3 — Bound and sanitize OpenRGB client names

**P2 · Security; CWE-117 · Scope: Small**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Net/OpenRgbServer.cs:280` (`case OpenRgbProtocol.PktSetClientName:`)
- `src/UnifiedRgb.Core/Log.cs:93` (`File.AppendAllText(PathName, line);`)

**Trigger and impact:** An enabled SDK client supplies a name containing embedded CR/LF or a very large name. LAN reachability is conditional on the user enabling LAN listening.

**Why it happens:** ASCII decoding and Trim only remove boundary whitespace/NULs. Embedded controls are interpolated into persistent diagnostics as apparent additional log lines; names are also propagated to UI notifications. Packet bounds and rotation do not sanitize a field.

**Implementation steps:**

1. Normalize once when accepting a name: strip/replace all control characters, cap length (for example 128 characters), and retain the existing unnamed fallback.
2. Skip redundant name updates and use bounded/rate-limited rename notifications/logging. Never use the raw peer value as a rate-limit key.
3. Reuse the sanitization intent of ChromaRestServer.AppTitle without changing valid SDK negotiation.

**Acceptance checks:**

- [ ] An embedded newline/tab/NUL name remains one bounded log/display field.
- [ ] Repeated identical or oversized rename packets do not cause per-packet UI refresh/log append.
- [ ] Ordinary printable names still display correctly.

**Limits / guardrails:** Security severity low. Log rotation already limits retained files, so this is not an unbounded disk-growth exploit; attacker-authored diagnostic lines are the confirmed impact.

<a id="s4"></a>

### S4 — Expire Chroma pipe clients that never send a frame

**P2 · Security; CWE-400 · Scope: Medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Effects/ChromaSync.cs:100` (`const string PipeSddl`)
- `src/UnifiedRgb.Core/Effects/ChromaSync.cs:161` (`if (Interlocked.Increment(ref _clients) > MaxClients)`)
- `src/UnifiedRgb.Core/Effects/ChromaSync.cs:198` (`while (ReadExact(pipe, head, 5))`)
- `src/UnifiedRgb.Core/Effects/ChromaSync.cs:229` (`int n = s.Read(buf, got, len - got);`)

**Trigger and impact:** A local medium-integrity process occupies all 16 Chroma pipe slots without completing a first frame. New legitimate DLL-shim hosts are rejected while those connections remain open.

**Why it happens:** The client cap bounds threads but the blocking read has no application deadline or eviction. Silent readers hold admission capacity indefinitely. The intended pipe ACL allows these local clients to connect.

**Implementation steps:**

1. Give each connection a first-complete-frame deadline and later a total-frame/idle deadline. Reclaim slots through cancellation or disposing tracked expired streams.
2. If switching to asynchronous reads, configure the native pipe handle for overlapped operation too; merely calling ReadAsync on the existing synchronous wrapper is insufficient to establish the intended cancellation contract.
3. Keep decrement/disposal in finally and preserve existing shim access compatibility.

**Acceptance checks:**

- [ ] Sixteen silent fake/isolated pipe clients are evicted within the chosen deadline, then a valid client can publish.
- [ ] A client dripping a header/body cannot reset a total-frame deadline forever.
- [ ] Normal long-running hosts and simultaneous games continue working; counts return to baseline on disconnect and errors.

**Limits / guardrails:** Security severity low. Existing live hosts are not disconnected by this trigger, and memory/thread counts are capped. No code execution or whole-process crash was demonstrated.

<a id="b4"></a>

### B4 — Update Logitech dedup/persist state only after successful device I/O

**P2 · Functional bug; error handling · Scope: Medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Devices/LogitechG403.cs:167` (`if (!hid.Write(buf)) return null;`)
- `src/UnifiedRgb.Core/Devices/LogitechG403.cs:219` (`_lastPer[i] = c;`)
- `src/UnifiedRgb.Core/Devices/LogitechG403.cs:248` (`_persisted[i] = true;`)
- `src/UnifiedRgb.Core/Devices/LogitechG403.cs:230` (`void SendEffect`)

**Trigger and impact:** A color write or persistence command fails once, then the same color is requested again after the transport recovers.

**Why it happens:** Query reports failure as null, but SendEffect discards the result. SetColors records the new color before sending; CommitPersist marks it persisted before sending. Subsequent identical frames may be skipped despite never having reached the mouse. The engine’s breaker cannot see a failure swallowed by the driver.

**Implementation steps:**

1. Propagate an explicit success/failure result from Query/SendEffect through SetColors and CommitPersist.
2. Only update _lastPer and _persisted after the relevant operation succeeds. Verify the correct reply/error semantics for supported HID++ variants rather than treating every non-null packet as success.
3. Surface repeated failures to the existing failure policy without logging at frame rate.

**Acceptance checks:**

- [ ] Fake a failed first color write followed by success: the identical second frame retries.
- [ ] Fake a failed persist command: the next persist attempt retries, while a successful one deduplicates.
- [ ] Successful animation still avoids persist-at-frame-rate behavior.

**Limits / guardrails:** Static trace, no mouse writes performed. Separate hardware transport success from protocol acknowledgment when designing the test seam.

<a id="b5"></a>

### B5 — Abort a Thermalright frame when an HID report fails

**P2 · Functional bug; CPU/I/O amplification on failure · Scope: Small**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Devices/ThermalrightLcd.cs:111` (`_hid.Write(_report);`)
- `src/UnifiedRgb.Core/Devices/ThermalrightLcd.cs:93` (`for (int off = 0; off < total; off += CHUNK)`)
- `src/UnifiedRgb.App/LcdController.cs:81` (`try { _lcd.ShowFrame(frame); }`)

**Trigger and impact:** The LCD disconnects or an output report times out while a frame is being sent.

**Why it happens:** WriteReport ignores HidHandle.Write’s bool. ShowFrame can continue through all 301 reports and return as if successful. StreamLoop’s retry/backoff catch is bypassed by ordinary false returns. With repeated per-report timeouts, one failed frame can take roughly 301 × 400 ms, before additional cancellation delay.

**Implementation steps:**

1. Return failure or throw a specific transport exception on a failed report; stop the remainder of that frame immediately.
2. Make ShowFrame’s outcome observable to StreamLoop so lastSent advances only after a complete successful frame and existing backoff applies.
3. Validate full frame length before serialization. Preserve the pinned reusable output buffers.

**Acceptance checks:**

- [ ] A fake writer failing report 2 causes no report 3 and does not mark the frame successful.
- [ ] After recovery, the entire latest frame is retried from its header.
- [ ] A valid FrameBytes input still emits exactly 301 reports with correct final padding.

**Limits / guardrails:** The timing is a bound derived from per-report timeout configuration, not a measured USB result. Cancellation completion can itself take longer; do not promise a hard maximum.

<a id="b6"></a>

### B6 — Do not dispose sensor sources after an uncompleted drain timeout

**P1 · Functional bug; native lifetime race · Scope: Medium to large; concurrency review required**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:240` (`var cpu = _cpu; var lhm = _lhm; var ite = _iteChips;`)
- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:483` (`timer.DisposeAsync().AsTask().Wait(2000)`)
- `src/UnifiedRgb.Core/Sensors/SensorHub.cs:439` (`DisposeSources(cpu, ite);`)
- `src/UnifiedRgb.Core/Native/PawnIO.cs:71` (`int hr = pawnio_execute`)

**Trigger and impact:** A sensor callback stalls beyond two seconds while ResetSources or Shutdown runs.

**Why it happens:** Drain discards Wait’s false timeout result. The caller closes sources that TickCore already captured, and ResetSources can enable replacement sources before the old callback exits. A bounded wait limits caller latency; it does not transfer ownership away from the still-running callback.

**Implementation steps:**

1. Make drain completion an explicit result/task. Dispose old sources only after the owning callback actually completes.
2. On a deadline, leave that source generation quarantined and prevent reopen/use until deferred cleanup completes. Do not hold _gate while awaiting code that needs it.
3. Give native handles an operation lifetime guard/SafeHandle-based ownership where appropriate. Keep shutdown responsive through a documented asynchronous teardown policy rather than freeing live resources.

**Acceptance checks:**

- [ ] With a fake source blocked on a barrier for more than two seconds, disposal/reopen cannot occur before the barrier releases.
- [ ] After release, exactly one cleanup occurs and reset can start a new generation.
- [ ] Run reset concurrently with Touch/TouchTemps and verify no duplicate timer generation or leaked source.

**Limits / guardrails:** Do not reproduce by stalling actual kernel I/O. Source proves the lifetime race; native crash/handle-reuse outcomes were not exercised. Coordinate later with the analogous timed Join/COM cleanup in WasapiLoopback.Dispose rather than blindly making all joins infinite.

<a id="b7"></a>

### B7 — Lease LCD render buffers until the consumer has copied them

**P2 · Functional bug; concurrent buffer ownership · Scope: Medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.App/LcdController.cs:74` (`var frame = Volatile.Read(ref _latest);`)
- `src/UnifiedRgb.App/LcdController.cs:273` (`var outp = ReferenceEquals(Volatile.Read(ref _latest), _outA)`)
- `src/UnifiedRgb.Core/Devices/ThermalrightLcd.cs:91` (`Array.Copy(rgb565, 0, payload, 20, rgb565.Length);`)

**Trigger and impact:** The stream thread captures buffer A and is delayed before/during ShowFrame’s input copy. The UI publishes B, then renders another refresh into A.

**Why it happens:** The producer avoids only _latest; it does not know which buffer the consumer currently holds. Volatile publishes a reference but does not prevent mutation of the referenced array. A captured older frame can be overwritten before its copy finishes.

**Implementation steps:**

1. Use an explicit free/published/in-use buffer protocol or a short lock around publishing and the consumer’s copy into its private payload.
2. Keep the expensive USB transmission outside the render lock. The device already copies into _payload before transmitting; protect that copy, not all 301 writes.
3. Retain bounded reusable storage and latest-frame coalescing; do not solve this by allocating a 153,600-byte array on every render.

**Acceptance checks:**

- [ ] A deterministic barrier pauses the consumer after acquiring A; two producer publishes cannot mutate A until the consumer releases/copies it.
- [ ] Captured frames consist entirely of one generation’s test pattern, never mixed bytes.
- [ ] Fast UI refresh, GIFs, blank/off frames, and shutdown pass the same ownership invariant.

**Limits / guardrails:** This is a scheduling-dependent copy race, not a claim that rgb565 is read throughout USB transmission. ShowFrame copies the input first. No physical tearing was reproduced.

<a id="b8"></a>

### B8 — Honor GIF disposal and logical canvas metadata

**P2 · Functional bug; image playback · Scope: Medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.App/LcdController.cs:390` (`void LoadGif(string path)`)
- `src/UnifiedRgb.App/LcdController.cs:395` (`int w = dec.Frames[0].PixelWidth`)
- `src/UnifiedRgb.App/LcdController.cs:419` (`if (prev != null) dc.DrawImage(prev`)

**Trigger and impact:** Use a small GIF whose moving transparent patch requires restore-to-background or restore-to-previous disposal. Include an offset partial first image.

**Why it happens:** LoadGif always draws the previous composite then the new patch, and only reads offsets/delay. It never reads/applies disposal metadata. It also assumes the first decoded image dimensions define the logical canvas. These assumptions can leave trails or clip valid animations.

**Implementation steps:**

1. Read the logical screen dimensions and per-frame disposal method. Composite into a bounded panel-scale canvas with correct save/restore state.
2. Apply the previous frame’s disposal before drawing the next frame; preserve transparent regions and offsets.
3. Keep the current frame-count/memory budget explicit. Do not increase limits as a workaround for compositing errors.

**Acceptance checks:**

- [ ] Golden pixel fixtures cover disposal 1/2/3, transparent moving rectangles, and a first patch smaller than the logical screen.
- [ ] Check both the pump render and editor background use the same corrected composite.
- [ ] Existing ordinary full-frame GIFs retain intended timing.

**Limits / guardrails:** Static validation only. Use tiny generated fixtures so testing cannot depend on arbitrary downloaded images. The intentional 96-frame truncation is not classified here as a new bug.

<a id="b9"></a>

### B9 — Normalize deserialized scene and LCD collections before startup consumes them

**P2 · Functional bug; persisted data robustness · Scope: Small to medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.App/Scenes.cs:84` (`public static SceneStore Load()`)
- `src/UnifiedRgb.App/Scenes.cs:78` (`public List<LcdScene> Scenes`)
- `src/UnifiedRgb.App/LcdDesign.cs:149` (`public List<LcdElement> Elements`)
- `src/UnifiedRgb.App/ViewModels/LcdDesignerViewModel.cs:710` (`foreach (var sc in _scenes.Scenes)`)
- `src/UnifiedRgb.App/ProfileStore.cs:234` (`return JsonSerializer.Deserialize<T>(text);`)

**Trigger and impact:** Load syntactically valid scenes.json containing {"Scenes":null}, {"Scenes":[null]}, or a sequence with null Actions. LCD designs may similarly contain null Elements or null entries.

**Why it happens:** Default initializers do not protect properties that JSON explicitly sets to null. The generic loader accepts these values as valid JSON, while InitScenes and sequence/design consumers immediately enumerate or dereference them. MainViewModel calls InitScenes during startup even when no LCD is attached.

**Implementation steps:**

1. Add a semantic normalization/validation step to SceneStore.Load and LcdDesign.Load: replace null collections, remove/reject null entries, and normalize nested Design/Actions/Elements.
2. Validate required names and finite/ranged drawing values at the same boundary, with a clear recovery log. Retain readable originals when repairs are lossy.
3. Keep normalization idempotent and avoid silently deleting unaffected scenes/profiles.

**Acceptance checks:**

- [ ] Each null-shape fixture loads into a usable model without startup exceptions, including no-LCD startup.
- [ ] Nested null actions/elements and malformed drawing numbers receive defined recovery behavior.
- [ ] Valid files round-trip without data loss.

**Limits / guardrails:** This is a user/config-file robustness bug, not an asserted remote exploit. ProfileStore already sanitizes some profile entries; apply equivalent boundary discipline to the scene/LCD models.

<a id="b10"></a>

### B10 — Key foreground-app caching by the actual window and process lifetime

**P2 · Functional bug; automation · Scope: Small**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.App/Services/AutomationService.cs:364` (`if (pid == _lastPid && _lastName != null)`)
- `src/UnifiedRgb.App/Services/AutomationService.cs:375` (`EnumChildWindows(h,`)
- `src/UnifiedRgb.App/Services/AutomationService.cs:388` (`_lastPid = pid; _lastName = name;`)

**Trigger and impact:** Switch between windows hosted by the same ApplicationFrameHost process but belonging to different apps. The cached outer PID can be unchanged while the actual hosted app changes.

**Why it happens:** The PID-only fast path returns before child-window enumeration. It caches the resolved child app name under the shared host PID. PID reuse can also return a previous process’s name. Rules can apply the wrong profile or fail to switch.

**Implementation steps:**

1. Cache by foreground HWND plus process identity; for hosted windows, include/re-evaluate the child process identity when the window changes.
2. Use a bounded refresh or process-lifetime discriminator to address PID/HWND reuse. Preserve the cheap unchanged-window path.
3. Extract the window/process query behind a small interface/delegate so tests do not need to control the desktop.

**Acceptance checks:**

- [ ] Two HWNDs with the same host PID but different child PIDs resolve to different names and trigger the intended transition.
- [ ] A reused PID does not return the old process name indefinitely.
- [ ] An unchanged ordinary app avoids repeated expensive process enumeration.

**Limits / guardrails:** No desktop app switching was performed. This applies when multiple relevant windows share a host; not every modern Windows app uses that hosting model.

<a id="b11"></a>

### B11 — Advance FFT windows per hop instead of analyzing the same final window repeatedly

**P2 · Functional bug; CPU work · Scope: Small to medium**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Core/Audio/AudioAnalyzer.cs:111` (`static void OnSamples`)
- `src/UnifiedRgb.Core/Audio/AudioAnalyzer.cs:119` (`_sinceAnalysis += count;`)
- `src/UnifiedRgb.Core/Audio/AudioAnalyzer.cs:120` (`while (_sinceAnalysis >= Hop)`)
- `src/UnifiedRgb.Core/Audio/AudioAnalyzer.cs:130` (`int start = (_ringWrite - FftSize`)

**Trigger and impact:** A WASAPI callback supplies enough samples to cross multiple 1,024-sample analysis hops, for example a 4,096-sample batch after scheduling delay.

**Why it happens:** OnSamples writes the entire batch into the ring, then runs Analyze repeatedly without moving the analysis endpoint. Every iteration transforms the same latest 2,048 samples, while advancing smoothing/AGC by another hop. Intermediate windows are lost and redundant FFT work changes the response according to packet batching.

**Implementation steps:**

1. Process input in hop-sized segments and analyze at each hop boundary, or track an independent analysis cursor in a ring that retains every needed window.
2. Define behavior for exceptionally large backlog explicitly. Do not run several transforms of the same window to catch up.
3. Keep sample-rate handling and smoothing time increments aligned with the actual analyzed window sequence.

**Acceptance checks:**

- [ ] Feed an identical deterministic waveform in chunks of 256, 1,024, and 4,096 samples; compare published per-hop bands/levels within numerical tolerance.
- [ ] Count analysis endpoints: each expected hop is visited once and endpoints advance by Hop.
- [ ] Benchmark a large callback to confirm duplicate transforms are gone without changing normal cadence.

**Limits / guardrails:** No audio endpoint was opened. The redundant transform path is directly visible in source; real CPU savings depend on callback batch sizes and are not measured here.

<a id="perf1"></a>

### PERF1 — Bound LCD image decoding before allocating full-resolution pixels

**P2 · Memory/latency improvement · Scope: Medium**

**Evidence:** High allocation path; peak size not measured.

**Start here:**

- `src/UnifiedRgb.App/LcdController.cs:367` (`img.CacheOption = BitmapCacheOption.OnLoad;`)
- `src/UnifiedRgb.App/LcdController.cs:368` (`img.UriSource = new Uri(path);`)
- `src/UnifiedRgb.App/LcdController.cs:392` (`var dec = new GifBitmapDecoder`)
- `src/UnifiedRgb.App/LcdController.cs:405` (`int max = Math.Min(dec.Frames.Count, 96);`)

**Trigger and impact:** Select a very large still image or GIF for a 320×240 LCD background.

**Why it happens:** Still images are loaded without DecodePixelWidth/Height. GIF OnLoad decoding occurs before the loop’s 96 composited-frame cap, so that cap is not a pre-decode resource bound. Loading and compositing also occur on the dispatcher, making first selection potentially stall the UI.

**Implementation steps:**

1. Read dimensions safely, retain natural dimensions as metadata, and decode still pixels at an appropriate bounded preview/panel resolution.
2. Establish compressed-size, logical-dimension, decoded-byte, and frame-count budgets before expensive GIF processing where the decoder permits; reject unsupported oversized inputs clearly.
3. Move expensive decode work away from the UI thread using a WPF-compatible worker/frozen results. Drop obsolete results if the selected file changes.

**Acceptance checks:**

- [ ] Compare a normal fixture and a large same-content image: displayed geometry/crop is preserved and memory does not scale with unnecessary source pixels.
- [ ] Record peak private bytes and UI responsiveness for first load and rapid background changes.
- [ ] Test cancellation, missing/corrupt files, reload-by-write-stamp, and animated backgrounds.

**Limits / guardrails:** No measured leak or whole-app OOM is claimed. Full-resolution still decoding is confirmed; exact WPF decoder intermediate allocations require measurement. Coordinate with B8.

<a id="perf2"></a>

### PERF2 — Let preview callers reuse composed-frame and channel storage

**P3 · Managed allocation improvement · Scope: Small to medium; after B2/B3**

**Evidence:** High allocation path; total cost not measured.

**Start here:**

- `src/UnifiedRgb.App/Services/LightingController.cs:138` (`public Rgb[] ComposedFrame`)
- `src/UnifiedRgb.Core/Effects/EffectEngine.cs:136` (`public List<Channel> ChannelsFor`)
- `src/UnifiedRgb.App/CanvasWindow.xaml.cs:82` (`colors = _vm.ComposedFrameFor(dev);`)

**Trigger and impact:** Keep the desk canvas open while multiple devices/effects animate; RefreshDots calls ComposedFrameFor for each device at 10 Hz.

**Why it happens:** ComposedFrame clones a full RGB array on every pull, ChannelsFor allocates a new List, and fallback rendering allocates another channel buffer. These allocations occur even though the visible dot brushes are reused.

**Implementation steps:**

1. Add a copy-into-caller-buffer preview API and a safe way to copy/iterate channel snapshots without a fresh list on every pull.
2. Let each preview own correctly sized scratch storage; resize only when the device inventory/LED count changes.
3. Preserve stable snapshot ownership: never return a mutable engine buffer to WPF or recycle a frame still in use.

**Acceptance checks:**

- [ ] Measure allocated bytes over a fixed warm 60-second canvas session before/after with the same device/channel counts.
- [ ] After warmup, the new copy path does not allocate arrays/lists per pull.
- [ ] Colors, fallback rendering, brightness semantics, and rescan behavior remain correct.

**Limits / guardrails:** The per-call allocations are confirmed. No percentage CPU/RAM reduction is promised. Scope this to active preview pulls; do not claim it runs continuously in the hidden main window.

<a id="perf3"></a>

### PERF3 — Measure and coalesce full-GC/working-set trims on minimize

**P3 · CPU/latency investigation; not a proven leak · Scope: Small experiment first**

**Evidence:** Confirmed scheduling behavior; performance impact unmeasured.

**Start here:**

- `src/UnifiedRgb.Core/MemoryTrimmer.cs:25` (`GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive`)
- `src/UnifiedRgb.Core/MemoryTrimmer.cs:26` (`GC.WaitForPendingFinalizers();`)
- `src/UnifiedRgb.App/MainWindow.xaml.cs:241` (`Task.Delay(2000).ContinueWith`)

**Trigger and impact:** Minimize, restore, and minimize several times within a few seconds, ending with the window hidden while effects/LCD remain active.

**Why it happens:** Every minimize schedules a new delayed trim. Pending work is neither canceled nor coalesced. Multiple callbacks can all see hidden state and request two blocking full collections plus working-set eviction. Hidden does not mean sensor/effect/LCD work is idle.

**Implementation steps:**

1. First instrument trim count, GC pauses, effect cadence, and restore latency. Compare current behavior against disabled trimming and one coalesced idle trim.
2. If supported by measurements, use one cancelable pending task/generation and a cooldown; cancel on restore/shutdown and require an appropriate idle state.
3. Treat EmptyWorkingSet as working-set reclamation, not evidence of reduced retained allocations. Keep policy separate from the P/Invoke helper.

**Acceptance checks:**

- [ ] Five quick minimize/restore cycles produce at most one pending trim and none after restoring.
- [ ] Report median and tail restore/frame latency plus private bytes, working set, and GC counts for comparable sessions.
- [ ] Do not merge a claimed memory optimization that only lowers the displayed working-set number while worsening pauses.

**Limits / guardrails:** This is explicitly a measurement task. Blocking collections and duplicate scheduling are observable from source, but a material regression has not been measured.

<a id="r1"></a>

### R1 — Make the test harness independent of the real user profile

**P1 · Refactoring; test isolation and data safety · Scope: Small to medium; do early**

**Evidence:** High, source validated.

**Start here:**

- `src/UnifiedRgb.Tests/Program.cs:505` (`string path = AppPaths.Config("lianli-layout.json");`)
- `src/UnifiedRgb.Core/AppPaths.cs:11` (`public static readonly string ConfigDir`)
- `src/UnifiedRgb.Tests/UnifiedRgb.Tests.csproj:4` (`<ProjectReference Include=`)

**Trigger and impact:** Run the current test harness while the app or another test process accesses the real layout/config files; or terminate the harness before its finally block.

**Why it happens:** The layout test writes the actual user config and restores an old snapshot in finally. That can expose temporary fixtures to the live app, overwrite concurrent legitimate changes, or leave corrupted fixture data after abrupt exit. The harness references Core only, leaving important App controller paths outside direct integration coverage.

**Implementation steps:**

1. Introduce a test-only injectable configuration root or pass explicit paths to persistence functions, set before static AppPaths initialization. Default production paths stay identical.
2. Run each test invocation in a unique temporary directory. Audit all filesystem tests/logging, not just the layout block; replace fixed listener ports with assigned free endpoints where practical.
3. Add a hardware-free test seam/project for LightingController/CoalescingApplier and pure scene normalization so B2/B3/B9 receive meaningful regressions. Do not boot MainViewModel to get those services.

**Acceptance checks:**

- [ ] Sentinel files in the actual user config directory have unchanged bytes/timestamps after tests, including a deliberately failed/terminated child harness.
- [ ] Two test runs can execute concurrently without sharing settings or fixed temporary files.
- [ ] The test command reports the real assertion count and fails when a deliberately failing isolated assertion is introduced.

**Limits / guardrails:** The full repository harness was not run during this review because it touches real user state. Isolated fake-device probes were used instead. Preserve the existing AfterTargets=VSTest harness behavior while improving isolation.

<a id="i1"></a>

### I1 — Validate and cap Chroma REST outstanding request work

**P2 · Security/performance candidate; CWE-400 · Scope: Small bounded experiment, then medium fix**

**Evidence:** Medium; deployment limits need validation.

**Start here:**

- `src/UnifiedRgb.Core/Net/ChromaRestServer.cs:83` (`ThreadPool.QueueUserWorkItem`)
- `src/UnifiedRgb.Core/Net/ChromaRestServer.cs:109` (`body = _bodyBuf ??= new byte[64 * 1024];`)
- `src/UnifiedRgb.Core/Net/ChromaRestServer.cs:112` (`req.InputStream.Read(body`)

**Trigger and impact:** Several local clients send incomplete request bodies while valid Chroma traffic continues.

**Why it happens:** Every accepted HTTP context enters the shared CLR pool without application admission control. Workers block on body reads and retain 64 KiB thread-static buffers. HTTP.sys supplies additional queuing/timeouts, so source alone does not establish a whole-app starvation threshold or an infinite request lifetime.

**Implementation steps:**

1. In an isolated Windows test process, record effective HTTP.sys entity-body behavior, outstanding application handlers, pool latency, and valid-frame latency using a small bounded number of clients.
2. Add admission before queueing work, an explicit total-body deadline, cancellation, and guaranteed context cleanup. Reject excess work with a defined status.
3. Prefer asynchronous I/O and bounded buffer ownership. Keep existing body/grid limits and title sanitization.

**Acceptance checks:**

- [ ] Valid frames remain serviceable during the bounded slow-client test.
- [ ] Outstanding work never exceeds the configured cap and returns to baseline after timeout/disconnect.
- [ ] Document measured limits; only then classify concrete denial-of-service impact.

**Limits / guardrails:** Deferred in the security report. Do not claim unlimited threads, unlimited body allocation, or a demonstrated whole-process DoS. The missing application-level admission bound is confirmed.

<a id="i2"></a>

### I2 — Validate and bound slow GSI bodies without serializing all updates

**P2 · Security/availability candidate; CWE-400 · Scope: Small bounded experiment, then medium fix**

**Evidence:** Medium; timeout impact needs validation.

**Start here:**

- `src/UnifiedRgb.Core/Games/GsiServer.cs:119` (`try { Handle(ctx); }`)
- `src/UnifiedRgb.Core/Games/GsiServer.cs:142` (`body = ReadBounded(reader);`)
- `src/UnifiedRgb.Core/Games/GsiServer.cs:156` (`var parsed = GsiParser.Parse(body, Token);`)

**Trigger and impact:** A local caller without the GSI token starts an incomplete body before a legitimate token-bearing game update arrives.

**Why it happens:** One thread accepts and fully reads each body synchronously, then checks the token. A slow body therefore head-of-line blocks the following update until transport/application timeout. The code does not set its own deadline; actual HTTP.sys behavior determines whether the 10-second game-silence threshold is crossed.

**Implementation steps:**

1. Measure that scenario in an isolated listener on a test port and document the effective timeout.
2. Use bounded concurrent asynchronous handlers and an explicit body deadline while retaining size limits and token validation. Close/abort responses even on read/parse exceptions.
3. Make Stop and final state publication atomic with a shared lock or generation ownership; the current check-before-parse can race with Stop clearing state.

**Acceptance checks:**

- [ ] A valid update can complete while another bounded test connection stalls.
- [ ] After Stop returns, an in-flight handler released from a deterministic barrier cannot republish State or Connected.
- [ ] Oversized bodies, invalid tokens, reconnects, and normal CS2 traffic retain expected behavior.

**Limits / guardrails:** Deferred as an exploit pending Windows timeout validation. Serial blocking and the Stop/publication race are source-established; no claim of an indefinitely blocked HTTP read is made.

## Validation performed and coverage limits

- Read current source across effect composition, external lighting ownership, LCD rendering/streaming, image playback, audio analysis, automation, persistence, sensors/native lifetimes, networking, updates, and test setup. An independent security baseline and focused inbound review supplemented the parent’s validation.
- Ran a temporary .NET 10.0.302 console probe linking the exact current `EffectEngine.cs`, `LightingController.cs`, `CoalescingApplier.cs`, `IRgbDevice.cs`, `Rgb.cs`, and `Master.cs`. Fake devices, no-op logging, and minimal unrelated geometry/device-type/interface stubs prevented hardware and user-config access. This was not a full product integration test.
- Probe results: `engine frames=2; both zones preserved=False`; `external final=000000,0000FF; expected=FF0000,0000FF`; `saved statics unchanged=True`. These demonstrate B2/B3, not a fix. All other bugs are explicitly source-validated or marked for measurement.
- Did not run the full application/hardware diagnostics, full test harness, active exploit traffic, update/installer flows, dependency-CVE checks, or CPU/heap profiling. Did not open user account services or publish findings. No claims about current upstream dependency vulnerabilities are made.
- Security review fully inspected 18 source files centered on network/IPC ingress, updater/helper execution, installer verification, and native Chroma shim. Architecture-only reads and other ordinary code review do not count as complete security coverage. Remaining device protocols, WPF views, CLI/diagnostic paths, release pipeline, binary modules, and third-party implementations need additional review before any exhaustive-audit claim.
- Security artifacts are generated separately in the Codex Security scan. The actionable source/fix/acceptance information is repeated here so this handoff remains useful without the scan viewer. TAC status could not be verified because its connector was not signed in; the finalized security tool reported 7,386,576 total tokens across four tasks, including 6,995,584 cached input tokens. This is the tool-reported scan accounting, not a billing estimate.

## Existing controls to preserve

- OpenRGB SDK is off by default, loopback unless LAN is enabled, with connection/payload/time bounds. Intended unauthenticated LED control is not itself a newly discovered exploit.
- Chroma pipe has a client cap, first-instance protection, medium-integrity restriction, and bounded grid dimensions. Fix S4 without breaking installed shim compatibility.
- PawnIO installer already verifies its publisher and holds a sharing lock across verification/launch. Do not replace that with a hash fetched from an untrusted endpoint.
- Update staging is beside the actual executable with per-attempt names. S1 concerns the trust source, not the older fixed-name temporary staging bug.
- Log rotation, bounded undo history, clock-pen cache eviction, and hardware frame deduplication already exist. Do not re-file them as missing based on older reviews.

## Small-model scope boundaries

R1, S3, B5, and B10 make good initial bounded changes. B2/B3 share frame-ownership concepts and should be implemented in separate coordinated patches. B8/PERF1 share decoding code but address different correctness/resource invariants. B1/B6 and S1/S2 require deliberate design review because a superficially plausible patch can weaken hardware recovery or privileged execution safety.

Avoid a wholesale rewrite of MainViewModel, a switch to a shared thread-pool render loop without latency measurements, or generic “make everything async” changes. Extract only the seams needed to test the listed invariant. The existing per-channel thread architecture is a scaling question; this review does not establish that it is the dominant real-world CPU cost.

## Preserved security evidence

The completed scan contains two medium-severity and two low-severity findings, with partial coverage and two HTTP candidates retained for follow-up. These are exact copies of the generated artifacts; the repair priorities above are intentionally separate from security severity.

- [Generated security report](review-evidence/2026-09-07/report.md)
- [Canonical findings and source evidence](review-evidence/2026-09-07/findings.json)
- [Coverage, deferred candidates, and remaining paths](review-evidence/2026-09-07/coverage.json)
- [Snapshot manifest](review-evidence/2026-09-07/scan-manifest.json)

The scan reported that the working tree changed during review. This task added documentation; the existing product-source edit remained untouched. The scan retains its original snapshot identity. Artifact references inside the generated files can name the original temporary scan directory; the copies above remain available in the repository.
