# UnifiedRGB — Final Review Pass (before the feature stretch)

**Date:** 2026-09-02 · **Scope:** full solution + C++ shim + CLI/Diag/Tests + scripts + docs, every file read in full ·
**Method:** 13 slice readers (one per file group) + 7 cross-cutting lens sweeps (threading, hot paths, P/Invoke, WPF,
error handling, duplication, untrusted input) → **170 unique findings** after dedup. **52** were confirmed by 2-3
independent adversarial verifiers (code-path, context and impact lenses) before the usage limit cut verification off;
the remaining **118** were judged by the engineer that owned the file while fixing (each skip reason is recorded below).
Fixes applied by **13 file-disjoint engineer agents**; then **14 adversarial reviewers** over the full diff (11 per group
+ a threading lens + an API/callers lens) found **0 blockers / 18 should-fix / 15 nits**, all of which went into a
follow-up round (appended at the end). Fourth pass of the day and the FINAL one before a long feature-development
stretch; companion to `CODE_REVIEW_2026-09-02.md`, whose items are not repeated here.

**Everything below marked `[x]` shipped in this session.** Verified at the end: Release build of the whole solution with
**zero warnings**; console harness **101 → 303 checks** passing (27 new test groups: SafeFile/AppPaths, Authenticode pin,
hardware.json header order, OpenRgbDevice bounds, Tinyuz, engine helpers, Chroma feed/REST parsing, UpdateClient
parsing, the self-update swap script, repo invariants); a 45 s launch of the rebuilt app on the dev rig with every device
detected, fan curves restored and **zero WARN/ERR** lines (details in the follow-up section). Changes are left uncommitted.

Legend: 🔴 real bug with user-visible or safety impact · 🟠 latent bug / leak · 🟡 hygiene · ❌ verified false / decided no

---

## Verified-false / decided-no (do not "fix" these)

Skipped by the owning engineer, reason verified by the group reviewer:

- ❌ **"WinUSB `Read` should break on a short packet"** (#34) — `Native/WinUsbNative.cs`. The only caller reads a fixed
  434-byte telemetry page (7 × 62 B packets); `n < 64 → break` would truncate telemetry to the first record to cure a
  stall nothing in the repo demonstrates. Hardware-validated framing left alone.
- ❌ **"MSI RTX 30-series needs the v2 idle-dance protocol"** (#32) — `Devices/MsiGpu.cs`. Premise wrong: OpenRGB's v2
  detector registers only RTX 40/50, v1 owns every RTX 30 card; `IsV2` mirrors that exactly. The change would have
  broken 30-series.
- ❌ **Debounce `SaveFanConfig` inside the hub** (#79) — `Sensors/SensorHub.cs`. The slider is tick-snapped to 5 % and
  deduped in `FanRowModel` (≤16 writes per full drag); the real storm was the curve editor (#40, fixed at the editor). A
  hub-level timer has a trap: `RestoreAllFans` clears the dictionaries first, so a late save would delete
  `fan-config.json` on the keepConfig exit/failsafe paths.
- ❌ **Cache the GDI DC/DIB in Screen Ambient** (#105) — `Effects/ScreenSync.cs`. Saves microseconds against the
  5-20 ms HALFTONE `StretchBlt` inherent to the GDI design; the real fix is a WGC/DXGI monitor capture — a new capture
  path, not a review fix.
- ❌ **Consolidate the five duplicated Win32 P/Invokes** (#150) — `Effects/ScreenSync.cs` et al. Spans native and
  app-services files; an in-territory dedup would create a third home for the same signatures. One-line `DllImport`s,
  identical, no drift.
- ❌ **OpenRGB bundle under user-writable `%LOCALAPPDATA%` is an EoP** (#131) — `Net/OpenRgbManager.cs`. Accepted risk:
  the app itself is a single exe in a user-writable folder launched elevated by the logon task, so replacing OpenRGB.exe
  grants nothing a same-user process lacks via the parent exe; OpenRGB CI builds are unsigned (no pin possible).
- ❌ **PerMonitorV2 in the manifest** (#99) — `App/app.manifest`. Diagnosis correct (the process is system-DPI-aware, so
  `ColorWheel.OnDpiChanged` can never fire) but opting the whole process in changes layout, `WindowChrome` hit-testing
  and placement restore on mixed-DPI rigs and must be tested under a 100 %/150 % pair. Its own item later; do NOT rely
  on the DpiChanged path meanwhile.

Deliberate non-changes inside `partial` items:

- ❌ **`FreeLibrary(g_real)` in the shim's live detach** (#14) — `native/chroma-shim/RzChromaSDK.cpp`. FreeLibrary from
  DllMain is documented-unsafe; the UnInit alternative changes Chroma-API semantics for hosts that keep issuing effects
  after UnInit and is unverifiable without Synapse + a device. Cost: one pinned reference per load/unload cycle, proxy
  mode only. Now stated in a DllMain comment.
- ❌ **Index-qualified `FanLabels` key** (#116) — `ProfileStore.cs`. Key stays `fan:{rawName}`: `SensorHub` persists
  `fan-config.json` by NAME because LHM indices shift across re-enumeration, so an index key would break more often than
  the duplicate-name case it fixes. The doc comment now states the real key.
- ❌ **`IFanBackend` abstraction across the six SensorHub dispatch switches** (#144) — a structural refactor of the fan
  control core is beyond a final pass's smallest correct change; only the already-drifted RestoreFan/RestoreOne pair was
  folded.
- ❌ **DACL check on `backend.json` / Authenticode on the update payload** (#134) — `AppPaths.cs`. Inherited `%APPDATA%`
  ACEs grant the user full control, so the documented dev-machine override would always fail a DACL test; payload
  signing needs signed releases first. The https/loopback gate + startup WARN shipped instead.
- ❌ **Narrow the Chroma pipe's Everyone ACE below `GENERIC_WRITE`** (#133) — `Effects/ChromaSync.cs`. Every installed
  shim opens the pipe with `GENERIC_WRITE` (`RzChromaSDK.cpp:157`), so `(A;;0x100082;;;WD)` would break Wallpaper Engine
  until users re-toggle the shim. Shim side first (`FILE_WRITE_DATA|FILE_READ_ATTRIBUTES|SYNCHRONIZE`), ACE in a later release.
- ❌ **Mid-session log rotation** (#135) — the `Log.cs` writer is on the do-not-touch list; REST log spam was closed with
  a per-minute `LogBudget` instead.

Carried forward from the three earlier passes (still valid):

- ❌ Thermal failsafe **96 °C / 90 °C, 3 ticks, keeps the saved curves** is policy (Zen 4/5 Tctl target 95 °C; this rig's
  9950X3D). Do not lower it, do not re-add the config delete.
- ❌ **The Chroma shim ships inside the single-file exe** (`IncludeNativeLibrariesForSelfExtract`); a publish folder with
  only exe + pdb is not "missing shim". `release.ps1` refuses to run without both DLLs.
- ❌ `Log`'s per-line writer: do-not-touch (rotation happens at startup only — see #101 and #135).
- ❌ `NvApi` non-blittable structs: closed because those calls run only while the UI is open — #102/#111 restore that
  premise (temp-only readers and a hidden window no longer re-arm the sweep).
- ❌ Async startup / lazy panes, drag-reorder dedup, `ReapplyAllStatic` debounce: earlier decided-no items, unchanged.

---

## Core / Native + Sensors

- [x] 🔴 **PawnIO installer verified once in `%TEMP%`, then run twice elevated (replace-after-verify TOCTOU)** — `Native/PawnIoInstaller.cs`.
      The file is reopened `FileShare.Read` and that handle is held across `IsSignedBy` and BOTH `Process.Start`s, so no same-user
      rewrite/rename/delete can land before either launch. (#132)
- [x] 🟡 Installer left in `%TEMP%` on every run, `Process` objects undisposed — try/finally deletes it on every exit, `using var`
      on both launches. (#170, #38)
- [x] 🟠 **Publisher pin was a substring match on the subject** — `Native/Authenticode.cs`. `SubjectHasRdn` compares a single-element
      RDN exactly (`CN=namazso.eu.example.com` and a CN buried in O/OU are refused). Revocation stays `WTD_REVOKE_NONE` (offline harness). (#36)
- [x] 🟡 `StructureToPtr`'d LPWStr path never freed — guarded `DestroyStructure<WINTRUST_FILE_INFO>` before `FreeHGlobal`. (#37, #77)
- [x] 🟡 `NvApi.DebugVoltStatus` deleted (no caller); three hand-built `NvFanCoolersStatusV1` initialisers → `NewFanCoolersStatus()`. (#148)
- [x] 🟠 **SMBus transactions proceeded after the `Global\Access_SMBUS` mutex timed out** (interleaving on the bus with HWiNFO/L-Connect)
      — `Native/SmbusPiix4.cs`. All four sites return -1/false on a failed 2 s wait; callers already treat that as a dropped frame. (#39)
- [x] 🔴 **Audio effects went dark for good when the default endpoint changed / a DAC was unplugged** — `Native/Wasapi.cs`. A failing
      `GetNextPacketSize` only ended the inner loop; now it throws into the catch, `IsAlive` drops and the watchdog restarts capture. (#166)
- [x] 🟠 Polling fallback left a released RCW in `_client`, masking the WASAPI HRESULT with "separated from its RCW" — released +
      nulled before re-activate; `Dispose` cannot throw from `Start`'s catch. (#35)
- [x] 🔴 **`WinUsbDevice.Dispose` freed the interface under an in-flight `ReadPipe`/`WritePipe`** (native UAF on Rescan/exit) —
      `Native/WinUsbNative.cs`. `_wrLock`/`_rdLock` in the `HidHandle` style, `_disposed` checked inside them and per chunk,
      `CancelIoEx` first, `_freed` makes double-Dispose idempotent. Lian side: #76. (#33)

- [x] 🟠 **IteSuperIo's dead "phase 2" fan-control block deleted** (~140 lines) — `Sensors/IteSuperIo.cs`. It carried a wrong duty
      encoding (8-bit `DutyByte` into the 7-bit ctrl register whose bit7 is SmartFan AUTO). Header rewritten to "monitor only";
      `DutyByte` kept for the harness with a corrected doc. (#140, #87, #86)
- [x] 🟡 The lock + `TryAcquire` + try/finally bracket (the shape of the first pass's 🔴 ISA-mutex bug) is one `WithBus<T>()`. (#88)
- [x] 🟠 A stopped fan (tach 0xFFFF) read as null, so a BIOS fan-stop header vanished for the session on the LHM-blocked fallback —
      0 RPM, as LHM maps it. (#85)
- [x] 🟠 `LhmFans.TryOpen` abandoned the opened ring0 `Computer` when `Collect()` threw — closed in the catch. (#81)
- [x] 🟠 `RyzenCpuTemperature.TryCreate` leaked the PawnIO handle when the sanity-gate read failed — disposed. (#80)
- [x] 🟡 `ReadEmbedded` forwarders in `RyzenCpuTemperature`/`IteSuperIo` removed; callers use `PawnIO.ReadEmbeddedModule`.
      (`SmbusPiix4.cs:137` and the CLI's inline read are other territories — still there.) (#151)
- [x] 🔴 **Fan curves never persisted on a clean install** — `AppPaths.cs`, `SafeFile.cs`, `Sensors/SensorHub.cs`. `%LOCALAPPDATA%\UnifiedRgb`
      existed only via the OpenRGB bundle or the shim log; elsewhere every `SaveFanConfig` threw into an empty catch and fans were back
      on BIOS auto after a restart. `AppPaths` creates `LocalDir`, `SafeFile` creates the parent, the catch WARNs. (#16, #162)
- [x] 🟠 **Nothing closed LHM's ring0 driver, the PawnIO reader or the ITE chips on exit** — new `SensorHub.Shutdown()` (latch, drain the
      in-flight tick ≤2 s, `Computer.Close` unloads the driver service), wired after `Lcd.Dispose` in `MainViewModel.Dispose`. (#90)
- [x] 🟡 **Any temp reader re-armed the full UI-only sweep 24/7** — `Touch()` (Cooling pane, FanRpm element, duty writes) vs new
      `TouchTemps()` (temps only; TempEffects, PatternEffect, LCD temp elements); idle-stop on the newer stamp. (#102)
- [x] 🟠 **Curve duty re-written to GPU/Lian backends every 1.5 s unchanged** (2 NvAPI deep-marshal calls + a thread per tick, forever)
      — per-fan `_lastApplied` dedup recorded on backend success; Lian quantised to 5 % and re-sent only on value/instance change;
      board/GPU re-asserted every ~30 s; cleared on restore and source re-open. (#78)
- [x] 🟠 Manual duties were written once and never re-asserted (a driver reset silently returned a "Manual · 60 %" fan to auto) — the
      tick iterates `_manualFans` too; free under the dedup. (#92)
- [x] 🟠 `IdentifyFan` re-applied the mode captured 4 s earlier over a slider move made during the burst, and the tick cancelled the
      100 % burst after ≤1.5 s — re-reads the current mode after the delay; ticks skip `_identifying` fans. (#83)
- [x] 🟠 `ResetSources` disposed `_cpu`/`_iteChips` under a mid-read tick — capture + null under `_gate`, drain outside it, then
      dispose; `TickCore` snapshots its sources. (#91)
- [x] 🟡 `RestoreFan` delegates to `RestoreOne`, which gained the `_gpuManualEngaged = false` it had drifted from. No `IFanBackend`. (#89, #144)
- [x] 🟠 Restored-from-config `FanCurve` was the instance the UI editor mutates while the timer reads it — `ReconcileFans.Apply` stores
      `Clone()`; VM side #112. (#82)
- [x] 🟠 Any non-`curve`/curve-less config entry was applied as MANUAL at the curve floor (`Curve:null` pinned a fan at 30 % forever) —
      manual only when `Kind == "manual"`, clamped with `ManualFloorFor`; anything else warned and ignored. (#84)

## Core / Devices

- [x] 🔴 **`LianFanNames` counted a single-fan group zone as a fan** (4 fans + `breaks=[3]` showed a fifth "Group 2" pill rendering
      an all-black slice) — `Devices/LianLiWireless.cs`. Whole-fan zone names recorded per slot in the ctor. (#1)
- [x] 🟠 **`TelemetryLoop`'s idle exit raced `UploadAnimation`'s touch**, so the resend/confirm loop never ran for that upload —
      keep-alive polled unlocked, re-checked under `_lock` before retiring (`_animPending` is set under `_lock`). (#3)
- [x] 🔴 **Receiver handle freed by `Dispose` while the telemetry thread was inside `WinUsb_ReadPipe`** — new `_rxLock` (order
      `_lock → _rxLock`): `PollTelemetry` snapshots `_rx` and transfers under it; `Dispose`/idle exit null-then-dispose under the same
      lock (double-free closed). `_lock` is never held across the read. Native half: #33. (#76)
- [x] 🟠 UI-thread callers (duty/speed sliders, hub curve ticks) blocked on the RF `_lock` for a whole 100-300 ms upload —
      `IntervalScale` reset is `Interlocked`; leaf `_pwmLock` guards the PWM state, only RF sends take `_lock`. (#122)
- [x] 🟡 Dead `FanDutyBySlot` removed. (#5)
- [x] 🟡 LogToPhys comment contradicted the code on the 16-LED part — rewritten (20 = "Outer ring", 16 = "Side glow"); names and
      positions were already consistent. `MainViewModel.Lian.cs:108` banner still says "infinity ring" — follow-up. (#7)
- [x] 🟡 `LianLiTinyuz`: `Ev` 12 → 2 bytes; inputs > 4096 B get fresh scratch instead of growing the `[ThreadStatic]` caches (a pool
      thread that once baked no longer pins multi-MB LOH arrays). Wire output byte-identical. (#4)
- [x] 🔴 **`LianLiUniHub` dropped an all-black first frame** (fans saved off / LightsOff at launch / fresh instance after Rescan left
      the hub on its power-on rainbow) — `_primed` flag forces the first flush. (#2)
- [x] 🟡 Ctor issued the tach round trip twice on the synchronous startup path — one read seeds `_lastSpeeds` and `_populated`. (#6)
- [x] 🟠 **`EneDram` latched `_directOn` even when the enable writes failed** (a NAKed first transaction left the stick on its onboard
      effect until restart, no log) — latched only when both writes succeed, throttled WARN, dedup commit skipped meanwhile. (#29)
- [x] 🔴 **Header "Test" bypassed the write lock and dedup caches; a Fan-zone header stayed WHITE after Cancel, a Static-zone header
      lost its built-in effect to the 0x32 mask** — `Devices/GigabyteIt5711.cs`. `SetHeaderLeds` validates 1-4, runs under
      `_writeLock`, keeps the other headers' counts, streams via the shared `StreamHeaderColors` (duplicate loop + `grbOrder` gone,
      `string order = "GRB"` byte-identical), then `InvalidateHeader`; `EnsureDirectMode` assigns the mask. App-side repaint on Cancel:
      follow-up. (#25, #147)
- [x] 🟡 `SetColors`/`SetZone` forward to one `WriteZones(offset, colors, containedOnly)`; the statics-then-fans rule lives once. (#160)
- [x] 🟠 A hand-edited `"colorOrder": "rgb"` silently meant GRB — `NormalizeOrder` at `BuildZoneDefs` (trim/upper, warn + GRB on unknown). (#31)
- [x] 🔴 **Every changed G403 frame set the HID++ "persist" byte** — tens of onboard-memory commits per second under an effect on a
      24/7 app — `Devices/LogitechG403.cs`. Frames stream without it; per-cluster `_persisted[]` + `CommitPersist()` once a colour has
      sat 2 s (engine keepalive) and from `Dispose`. A static followed by a hard crash loses the persist — follow-up. (#26)
- [x] 🟠 `TryOpen` took any HID++ collection with `OutputLength ≥ 20` and gave up on the PID after one failed write — the canonical
      long-report collection (Usage 0x0002, 20 B) is probed first; chosen collection logged. (#27)
- [x] 🟠 **`MsiGpu`/`SayoDevice` recorded the colour before the writes and ignored their result**, so a transient failure was never
      retried — `_last` committed only when every write succeeds, `Log.Occasional` otherwise; the keepalive retries. (#28)
- [x] 🟠 A server-supplied matrix cell ≥ 0x80000000 indexed `pos[]` negatively and threw from the ctor, taking the whole bridge down —
      `Devices/OpenRgbDevice.cs`: `long` index, in-range writes only, negative `LedCount` clamped to 0. (#30)

## Core / Effects + Net + Audio + Input

- [x] 🔴 **Non-zone devices streamed a `BaseFrame` snapshot taken at `Start`, so a later static edit on the same device was overwritten
      within a frame** ("app shows it, device doesn't") — `Effects/EffectEngine.cs`. `BaseFrame` is the live frame + volatile
      `BaseVersion`; `InvalidateBase(dev)` (called from `LightingController.PushFrame`) forces the re-copy/re-scale. (#69)
- [x] 🟠 A superseded worker could land one stale frame after its replacement or the static restore (`Join(300)` timing out) — `Run`
      re-checks `Running` right before the write; `StopRange`/`StopAll` go through `JoinWorker`, which logs a join timeout. (#74, #123)
- [x] 🟠 Idle throttling added up to 100 ms press-to-light latency to the typing/audio effects — `IEffect.LiveInput` (KeyFade,
      KeyRipple, AudioBars, AudioPulse, Pattern audio motions) bypasses the throttle. (#72)
- [x] 🟡 `ChannelsFor` fills a list with a plain `foreach`. (The per-pull buffers in `LightingController.ComposedFrame` need a rewrite — left.) (#75)
- [x] 🔴 **Lian-only stack effects baked with the default 4 s period and snapped at every seam** (Orbit hue jumped 240°, 24/7) —
      `Effects/LianStackEffects.cs`, `ExtraEffects.cs`, `IEffect.cs`. True periods with rates retuned to close: StackOutline
      `Loop(1/0.35)`, Waterfall `Loop(1/0.45)`, Orbit `Loop(6.0)`, TideFx `Loop(2π/0.8)`, Police `Loop(2/0.9)`, Fire `Loop(2π)` — all
      inside the baker's 1.5..12 s clamp. `LoopSeconds` doc: there is NO seam crossfade. (#67)
- [x] 🟠 Step-clock effects froze once `(int)` casts of the engine clock saturated (realistically KeyRipple's palette pick at ~25 days)
      — `Fx.Step`/`StepWrap` (1e6) wrap before the cast, value-identical below it. (#68)
- [x] 🟠 `KeyFade`/`KeyRipple` declared `Bakeable = true` (a future Lian target would bake a dark still) — `false`. (#73)
- [x] 🟠 **Reverse (negative speed) stopped Temp Glow's heartbeat and pinned the audio/typing effects' speed** — non-directional
      effects clamp `Math.Abs(speed)`. (#70)
- [x] 🟡 `KeyRipple.Render` hoists per-press invariants into a stackalloc `Ring[]` pre-pass (one `Sqrt` + `Exp` per LED per press). (#71)
- [x] 🟡 `AudioBars` takes the punch curve once per band into a stackalloc span (24 `Pow`/frame instead of one per LED). (#110)
- [x] 🟡 `PatternEffect.SamplePalette => PaletteFx.Sample(...)`; duplicate wrap/lerp gone. (#143)

- [x] 🟡 `AudioAnalyzer`/`KeyboardTap` watchdogs fired every 2 s for the process lifetime after their capture/hook stopped — paused
      under `_gate` on stop, re-armed in `Touch`. (#109)
- [x] 🟠 **A keyup dropped under `TryEnter` contention wedged that key "held" forever** — `Input/KeyboardTap.cs`: `Snapshot` reconciles
      entries older than 1 s against `GetAsyncKeyState`. (#56)
- [x] 🔴 **Chroma pipe ACL let a Low-IL/anonymous process create its own instance and be handed the game's connection** —
      `Effects/ChromaSync.cs`. Label LW → ME, Anonymous ACE dropped, explicit BA/SY ACEs, `FILE_FLAG_FIRST_PIPE_INSTANCE` on the first
      create (a squatted name logs `chroma-squat` and backs off 5 s). Everyone keeps `GRGW` until the shim changes its open. (#133)
- [x] 🟠 One unbounded reader thread per pipe connection — capped at 16 clients (extras disposed with an Occasional WARN). (#136)
- [x] 🟡 A pipe instance whose `WaitForConnection` threw was left to the finalizer — disposed on a failed accept. (#62)
- [x] 🔴 **REST path published every device class's effect as the keyboard frame** (keyboard/mouse/headset/chromalink PUTs made the
      grid flicker between 132 cells, 9×7, 1×1 and BLACK) — `Net/ChromaRestServer.cs`, `Effects/ChromaSync.cs`. `PushGrid(type)`:
      keyboard=1, chromalink=2 (1-D param → 1×N, N ≤ 64); every other segment and `/effect` only `ChromaFeed.Touch()`. (#54)
- [x] 🟠 Unthrottled Info line per REST init with a peer-controlled title (log growth, `\n` line forgery) — per-minute `LogBudget`s;
      `AppTitle` strips control chars, caps at 64. (#135)
- [x] 🔴 **When WE stopped rendering (static image, paused behind a game) the loop re-ran `EnumWindows` 10×/s + process snapshots
      forever and the LEDs fell to amber** — `Effects/WallpaperCapture.cs`. "Alive" (both HWNDs `IsWindow`) split from "silent" (no
      frame 3 s): silent-but-alive re-finds every 5 s without a snapshot and restarts only when the HWND/root changed; `WindowFound` is
      `_alive && _lastFrame != 0`. Three `GetProcessesByName` → one disposed `GetProcesses()` pass. (#55)
- [x] 🟠 "Giving up" after 20 failures did not stick (respawn + D3D rebuild every ~12 s) — `_failedUntil = now + 5 min`, `Touch()`
      returns early meanwhile. (#57)
- [x] 🟠 Pool size and monitor crop were fixed at session start — `PumpFrame` restarts the session on a `ContentSize` mismatch, crop
      recomputed. (#59)
- [x] 🟡 Every frame copied + mapped the whole desktop texture (33 MB at 4K, 10×/s) for 1/16 of it — GPU mip chain, only the level
      that still gives 4×4 samples per cell is read back (~130 KB); logs `readback WxH (mip k of n)`. (#106)
- [x] 🟡 Per-frame `IDirect3DDxgiInterfaceAccess` RCW — raw `QueryInterface`/`GetInterface` through function pointers, both released. (#66)
- [x] 🟠 `ChromaShimInstaller.Install` reported success when only the non-canonical copy landed (toggle snapped back silently) —
      success needs the canonical `BinDirs[0]` copy; a sharing violation reads "is in use - close Wallpaper Engine…". (#60)
- [x] 🟡 `Uninstall` aborted on the first locked file — per-entry try/catch, first error returned, "shim removed" only when all succeed. (#61)
- [x] 🟡 `Shims` table's always-equal `Source` column and the two hard-coded 64-bit names gone. (#158)
- [x] 🟡 `OpenRgbClient.ListDirty` removed (never read). (#65)
- [x] 🟠 **A hostile listener on :6742 answering count = 2³¹-1 hung startup on the UI thread** — `GetControllerCount` throws outside
      0..256 (also bounds `DiagnosticReport`); `OpenRgbLink.DetectAll` stops after 3 consecutive failures. (#137)
- [x] 🟡 Three copies of "parse OpenRGB.json, flip detectors, SafeFile-write" → internal `Net/OpenRgbDetectorConfig` (writes only when
      the mutate reports a change). (#145)
- [x] 🟠 **Any early exit of OpenRGB.exe was scored as a detector crash** (a missing VC++/Qt runtime convicted a random detector for
      good) — `ExitLooksLikeCrash()`: alive-with-port-gone, CRT abort or a non-loader NTSTATUS feeds the bisect; anything else reports
      "not a detector crash". (#58)
- [x] 🟡 Hide-sweeper enumerated windows for the full 30 s after its process died — exits on `HasExited` or a newer launch generation. (#64)

Core misc (`AppPaths`, `SafeFile`, `HardwareConfig`, `DiagnosticReport`, `UpdateClient`):

- [x] 🔴 **The elevated updater/support uploader honoured an arbitrary URL from user-writable `%APPDATA%\backend.json`** (one file →
      attacker exe as administrator + exfiltrated survey/log) — `AppPaths.cs`. Non-`https://` endpoints rejected and logged (`http`
      only on loopback); an override is WARNed at startup naming file + URL. (#134)
- [x] 🟠 `SafeFile.WriteAllText` never flushed (journaled rename could commit before the data) — `Flush(flushToDisk: true)`, UTF-8
      without BOM, `.tmp` deleted on failure, parent created. (#20)
- [x] 🟠 **`PawnSmbus.TryOpenAny()` threw unguarded and discarded the whole diagnostic report** on exactly the machines it is for —
      `DiagnosticReport.cs`: SMBus and Super-I/O blocks try/catch'd with their handles disposed. (#18)
- [x] 🟠 Triage flagged the app's own bundled OpenRGB as conflicting vendor software — dropped from `conflicts`, kept informational. (#19)
- [x] 🟠 PowerShell stderr was not captured (a failing CIM query looked like "no devices") — `Ps` reads both streams async, appends an
      `(errors: …)` trailer. (#24)
- [x] 🟠 `"GigabyteArgbHeaders": null` made the whole motherboard controller vanish and crashed the header dialog — `Load` coalesces
      null → empty and drops `[null]`. (#21)
- [x] 🔴 **A corrupt `hardware.json` silently substituted THIS rig's header layout and the next Save cemented it** — `HardwareConfig.cs`.
      Unreadable file copied to `hardware.json.corrupt-<stamp>` + WARN before defaults; `Save` logs failures. (Locked-file half:
      follow-up.) (#164)
- [x] 🟡 Dead `IDeviceProvider` removed from `IRgbDevice.cs` (the other listed members live in other groups' files). (#149)
- [x] 🟡 `MemoryTrimmer.Trim` leaked a process handle per call and logged success on failure — pseudo-handle, BOOL + Win32 error checked. (#22)
- [x] 🟡 `UpdateClient`/`SupportUpload` responses disposed; a non-integral feed `"size"` no longer fails the version check (`SizeOf`). (#23)

## App / View models + services

- [x] 🔴 **The unhandled-exception handler stacked a new modal on every tick of a recurring timer exception** — `App.xaml.cs`. Always
      `Log.Error` + `Handled`; a `_errorBoxOpen` gate returns immediately while a box is up. (#95)
- [x] 🟠 A second launch's first `Log` call rotated the running instance's log out from under it — the not-first-instance branch never
      initialises `Log`; the first instance logs the surfacing. (#101)
- [x] 🟡 `App.IsElevated` (copy of `DiagnosticReport.IsAdmin`) deleted. (#153)
- [x] 🟡 The elevated-relaunch diagnostic path was unreachable under `requireAdministrator` — `SupportService.TryElevatedCollect`, the
      `--collect-diag` branch and the manifest comment fixed/deleted. (`RelaunchElevated` + the "Restart as admin" banner are dead for
      the same reason — vm-rest/views follow-up.) (#100)
- [x] 🟠 **The LCD stream re-sent a byte-identical ON frame at 25 fps (~7,500 HID writes/s) all day** — `LcdController.cs` `StreamLoop`:
      an unchanged frame (reference-equal to the last sent) uses the blank frame's 9 × 50 ms keepalive slice; GIFs unaffected. (#103)
- [x] 🟠 **A background that failed to decode was re-decoded and WARNed every tick** (log rotated every ~3 h, bundle useless) — a
      failed path is not retried for 30 s, `Log.Occasional`; a missing file keeps its cheap `File.Exists` so it recovers at once. (#93)
- [x] 🟠 The 1 Hz LCD tick had no exception guard (FontSize 0 = a modal per second) — try/catch, `Log.Occasional`, publishes `_blank`. (#169)
- [x] 🟡 Dead `RotateClockwise` flag and its branch removed. (#155)
- [x] 🟡 `DrawClock` allocated ~18 Pens/Brushes per tick — bounded cache of frozen pens per (colour, radius). (#108)
- [x] 🟡 GIF-rate recompute of per-second content — hardware costs gone via #98/#107/#96; the remaining `DateTime.ToString` per element
      is not worth a cache. (#97)
- [x] 🟠 **The LCD held a second PawnIO handle and read the SMN register concurrently with `SensorHub`, unserialised** (could feed the
      failsafe a garbage temperature) — `PawnIoCpuTempProvider.cs` reads `SensorHub.CpuTempC` after `TouchTemps()`; `Dispose` a no-op. (#98, #107)
- [x] 🟡 `NetMeter` re-enumerated every adapter per sample, 24/7 — cached list refreshed every 30 s; a throwing counter drops it. (#96)
- [x] 🟠 wttr.in reply read with no size bound into the LCD string — `MaxResponseContentBufferSize = 4096`, >48 chars ignored. (#139)
- [x] 🟠 **A bake worker exception was unobserved: fans stayed `SuppressStreaming=true` with no upload and no log** —
      `Services/LianBakeService.cs`: try/catch → `Log.Error`; a still-current bake clears the flag so streaming resumes and the next
      identical request re-bakes. (#94)
- [x] 🟠 **Swap script's `echo ok %n%>"file"` was a cmd handle redirect** (empty/never written for n ≠ 1, so a good swap logged
      "FAILED") — `Services/UpdateService.cs`: redirect-first form. (#163)
- [x] 🟠 Two fixed-name files in `%TEMP%` (pending marker + swap result; a pre-planted symlink = arbitrary file clobber as admin) —
      both beside the exe, per-attempt `.result` read via glob and deleted. (#138)
- [x] 🟠 **A failed download stranded a partial ~260 MB exe beside the app per retry** — `Core/UpdateClient.DownloadAsync` deletes it in
      its catch; `UpdateService` deletes the staged `.exe`/`.bat` when it fails before hand-off, reports the prior install BEFORE any
      network call and sweeps `UnifiedRGB-update-*` older than 1 h. (#165, #17)

- [x] 🔴 **Master brightness never reached the wireless fans while they played a baked effect** — `MainViewModel.cs`:
      `ReapplyAllStatic` calls `_bake.ForgetSignatures()` before `RequestLianRebake()`. (#47)
- [x] 🔴 **"All fans + part" fan-out: speed, direction, tint, palette and pattern settings only reached fan 0** — `CurrentFxSet()` +
      `CopyTargetSettings`; `ApplyFx`, `SetColor`, the speed/reverse/pattern setters and palette edits (`SyncFanOutPalettes`) write
      every fan's `TargetFx`. (#48)
- [x] 🔴 **"All devices" with a palette effect wiped the SOURCE target's palette** (`Clear()` then copy from itself) —
      `CopyTargetSettings` no-ops on `ReferenceEquals` and copies from a snapshot taken before `Clear()`. (#46)
- [x] 🟡 `ApplyModeToAll` routes through `CopyTargetSettings` + `ApplyFxRange`; its private copies of the black-base swap and the
      Static-fallback sweep are gone. (`LoadFrame` helper spans vm-rest files — left.) (#157)
- [x] 🟠 `ApplyToAll` left `SuppressStreaming` stale (first brightness change skipped the fans) — ends with `RequestLianRebake()`;
      `ReapplyAllStatic` trusts the flag only while a channel runs. (#53)
- [x] 🟠 Rescan/launch ran the Lian "land on All fans" check before any channel was restored (pills said Static while the fans animated)
      — `LandOnRunningLianTarget()` after `RestoreEffects` and after the startup `LoadProfile`. (#49)
- [x] 🟠 Every Rescan closed the Settings pane (the PawnIO/OpenRGB status text was never seen) — `SelectLeftItem(value, closeSettings:false)`
      from `BuildLeftItems`. (#50)
- [x] 🟡 LCD designer's on-screen gate is `ShowLcdPanel` (was `IsLcdSelected`, true under Settings). (#127)
- [x] 🟡 Orphaned merged box-comment line removed. (Three other corrupted comment sites are other territories.) (#154)
- [x] 🟠 **Applier lanes were keyed by device instance and never removed — every Rescan leaked a `Lane` and pinned the disposed device**
      — `CoalescingApplier.PruneIdle()` from `LightingController.ForgetFrames` after `StopAndDrain`. (#51)
- [x] 🟠 The "in-order" lane drained a `Dictionary` via `First()` (an older `PushFrame` could run after a newer `PushZone`) —
      `OrderedDictionary` + `GetAt(0)`: FIFO across keys, latest-wins per key. (#52)

- [x] 🟡 `CurrentTargetView` re-copied the geometry and allocated ~5 arrays per 30 Hz preview pull — `MainViewModel.Lian.cs` caches
      pos/rects/style/aspect + one reusable colours buffer per selection. (The duplicate render per pull and the 24-line
      `ZonePositions` copy need engine-side changes — left.) (#104, #119, #141)
- [x] 🔴 **A `profiles.json` with null members failed the main window's constructor on every launch** — `ProfileStore.cs`,
      `MainViewModel.Profiles.cs`: null `DeviceFrames`/entries/frames/assignments tolerated, `ChoiceByName(null)` returns null. (#118)
- [x] 🟡 `UseOpenRgb = false` disposed the shared socket under streaming workers (spurious WARNs) — `StopAndDrain()` first. (#124)
- [x] 🟡 **Manual fan-duty slider wrote hardware + `fan-config.json` per mouse-move delta** — `Models.cs`: 120 ms `DispatcherTimer`
      coalesce; `CoolingViewModel.Stop()` cancels pending writes so exit can never re-assert a duty after the BIOS handback. (#121)
- [x] 🔴 **A transient sharing violation at launch was treated as corruption: zero profiles, then `[]` written over `profiles.json`** —
      `ProfileStore.LoadJson` retries 4× (200 ms); a still-unreadable path goes in a session `Unreadable` set that `ProfileStore.Save`
      (now `internal static`) refuses; `SceneStore.Save` routes through it. (`lcd.json`: follow-up.) (#168)
- [x] 🟠 `schtasks` exit code ignored, checkbox ticked optimistically — `SetAutoStart` returns `bool` and WARNs on failure/timeout/declined
      UAC. (Pushing the real state into the checkbox: follow-up.) (#167)
- [x] 🟡 `SettingsData.FanLabels` doc states the real `fan:{rawName}` key. (#116)
- [x] 🟠 A NaN delay threw `TimeSpan.FromSeconds(NaN)` on the dispatcher and broke every later `scenes.json` save — `DelaySeconds`
      keeps the previous value on NaN. (#117)
- [x] 🟠 **The Cooling refresh timer kept the full sensor sweep alive 24/7 after minimize-to-tray** — `CoolingViewModel.cs` hooks
      `MainWindow.IsVisibleChanged` (hide → `Stop`, show → `Start`); the tick requires the new `MainWindowState.Visible`. (#111)
- [x] 🟠 Any fan-list shape change reset the edited fan to row 1 — the row with the same `Index` is re-selected after the rebuild. (#115)
- [x] 🟠 Fan row edits mutated `SensorHub`'s live `FanCurve` while the timer read it (an `ArgumentOutOfRange` skipped curves AND the
      failsafe for a tick) — `BuildFanModel` stores `Clone()`. (#112)
- [x] 🟡 `RefreshDisplays` ran while hidden in the tray and re-rasterised the clock at GIF rate — gated on `MainWindowState.Visible`,
      clock re-rendered only when the second changed, `Display` assigned only on change. (#114)
- [x] 🟠 **Every scene step rewrote `lcd.json` and the user's canvas on disk was replaced by whichever scene last played** —
      `LoadDesignIntoEditor(fromShow: true)` renders without arming the save; `Dispose` skips the save while a show scene is live. (#113)
- [x] 🟡 `Dispose` neither stopped the sequencer nor unhooked `Ticked` — stops, unhooks, saves, disposes, nulls `_lcd`. (#120)

## App / Views + controls

- [x] 🟡 Per-row profile `ComboBox` saved `settings.json` on binding initialisation (N writes per dialog open) — `AppRulesWindow.xaml.cs`
      persists only when `IsLoaded`. (#43)
- [x] 🟡 Six unreferenced `x:Name`s, the empty `Root_PreviewDragOver` handler and its `RootPanel` removed. (#156, #9004)
- [x] 🟡 `ColorWheel`'s second copy of `HsvToRgb/RgbToHsv` (divergent near-black guards) → adapters over `ColorUtil`. (#142)
- [x] 🟠 Any mouse button on the wheel picked a colour and wrote it to the device — left button only. (#9001)
- [x] 🟠 **Fan-curve drag raised `CurveChanged` per mouse-move: 60-120 `fan-config.json` rewrites + hardware duty writes per second** —
      `Controls/FanCurveEditor.xaml.cs`: visuals update in place, `Handle_LostCapture` raises once at release if the point moved. (#40)
- [x] 🟡 `LianLiFanView` repainted ~44 geometries at 30 Hz through the full-window blur — early-out on `GlobalPause`, repaint on change only. (#41)
- [x] 🟡 `HeaderConfigDialog` re-implemented `Dialogs.MakeDialog`/`Btn`/the blur (three drifted copies) — built on `Dialogs` (now
      `internal`) and `ShowBlurred` with its mid-close guard; `LightingPane`'s hand-rolled blur deleted. (#45)
- [x] 🟠 `lianli-layout.json` was the one store written with `File.WriteAllText` — `SafeFile.WriteAllText`. (#159, #42)
- [x] 🔴 **Clicking a palette card never applied it** (the window's `DragMove` ate the real `WM_LBUTTONUP`; WPF's synthetic up
      hit-tested at (0,0)) — `PaletteLibraryWindow.xaml.cs` applies on bubbling `MouseLeftButtonDown`, `Handled = true`; the delete
      Button still takes its own press. Judged real from the refuter's real-input repro despite the dispute. (#44)
- [x] 🔴 **Logoff/sign-out had no `SessionEnding` path** (modal prompt unanswerable, `Closed` chain not guaranteed: headers left at
      the last app-set PWM with no controller) — `MainWindow.xaml.cs`: `Session_Ending` auto-saves and calls `Close()` synchronously;
      `Window_Closing` skips both modals, so RestoreAllFans / Lian handoff / settings flush / LCD save run. (#128)
- [x] 🟠 Delete on the Settings page deleted the hidden LCD element — gated on `ShowLcdPanel`. (#126)
- [x] 🟠 **Alt+F4 was dead whenever a button/pill/slider/combo had focus** (`Key.System` swallowed) — `Styles.xaml.cs`, `KeyPolicy.cs`
      let it through. (#125)
- [x] 🟠 Clicking a fan's name box did not select that fan (editor kept the previous one) — `GotKeyboardFocus` sets `EditingFan`. (#129)
- [x] 🟡 Element/background drag triggered two full LCD renders per mouse-move — additive `LcdElement.MoveTo`, `MoveBg`/`SetBgSize`
      (same clamps); one render per move. (#130)
- [x] 🟡 Wizard's `async void` PawnIO install had no guard (busy bar stuck on failure) — try/catch + finally, as `SettingsPane`. (#9002)
- [x] 🟡 Owned first-run wizard used `CenterScreen` — `CenterOwner`. (#9003)

## CLI · Diag · Tests · Scripts · Docs

- [x] 🟠 `--fanmap` left the current PWM register at 0xFF on Ctrl+C — `Cli/Program.cs`: the per-register restore is in a finally. (#9)
- [x] 🟠 An exact-arity `--option` with the wrong count fell through to a full device scan and "Done." — a guard before
      `new DeviceManager()` rejects any `--` option that reached the generic path, exit 2. (#10)
- [x] 🟡 `--pump 0` opened the LCD and died in `Enumerable.Min` — parsed first; `< 1` → message, exit 2. (#15)
- [x] 🟡 Five identical `Say` locals → one `Probe(tag)` helper. (#161)
- [x] 🟡 Diag embedded a second, unreadable 55 KB `SmbusI801.bin` with a stale comment — deleted; harness asserts one copy in Core. (#12)
- [x] 🟡 `MAINTAINING.md`/`release.ps1` said `dotnet test` runs nothing — both now describe the `VSTest` hook as intentional wiring. (#11)
- [x] 🟡 **Tests: 27 groups, harness 101 → 303 checks, 0 production bugs found** — `Tests/Program.cs` (+661). SafeFile/AppPaths;
      Authenticode exact-RDN pin on kernel32; IteSuperIo members gone; embedded modules; `FanCurve.Clone`; Tinyuz 4096/4097 B;
      `LoadLayout`; `OpenRgbDevice.BuildPositions` hostile cells; AudioBars bucketing; `LiveInput`/`Bakeable`; KeyRipple/KeyFade
      ±speed + tripwire; `Fx.Step` + six effects at t=2.2e9; baked-loop closure for six effects; Pattern == `PaletteFx.Sample`;
      EffectEngine channels/stop/`InvalidateBase`/throttle; ChromaFeed precedence; `LogBudget`/`AppTitle`; `SizeOf`; `NormalizeOrder`;
      `MemoryTrimmer`; `Ps` stderr; `OpenRgbDetectorConfig`; `ColorUtil` lattice; the cmd redirect line; manifest + shim invariants.
      Four `private → internal` seams (`NormalizeOrder`, `SizeOf`, `AppTitle`, `Ps`).

## C++ Chroma shim (`native/chroma-shim`, both bitnesses rebuilt with VS 18, 15 exports verified)

- [x] 🔴 **The 32-bit shim looked for `RzChromaSDK64_real.dll`; the installer backs the 32-bit Razer DLL up as `RzChromaSDK_real.dll`**
      — every 32-bit Chroma title on a Synapse machine ran standalone and the real Razer gear went dark — `RzChromaSDK.cpp`.
      `kRealName` by `#ifdef _WIN64`; a failed `LoadLibraryW` logs path + `GetLastError` once. Each DLL embeds only its own name. (#8)
- [x] 🟡 `g_real` deliberately not released on live detach — documented in `DllMain` (see decided-no). (#14)
- [x] 🟡 32-bit DLL was stamped "RzChromaSDK64" — `version.rc` picks strings by `SHIM32` (`build.bat` passes `/D SHIM32`);
      `ProductName` untouched so `IsOurs` still matches; the tracked 64-bit `version.res` is byte-identical. (#13)

---

## Needs a hardware / runtime check

Flagged `needsHardwareCheck` by the engineer (32 results) plus the manual-only items the reviewers called out; one look each on the rig.

- `Devices/GigabyteIt5711.cs` (#25, #147): rainbow on the fans → Test header 2 → Cancel: ring back to the effect within a frame; static
  parked → Test → new colour must show; I/O cover statics still light after a Test. `--argb 2 8 FF0000` still lights red (GRB).
- `Devices/LogitechG403.cs` (#26, #27): Rainbow animates; static, then normal exit → colour survives the handle close; parked colour
  survives unplug/replug after 3 s. Startup log shows `via usage 0x0002, reports out 20 B / in 20 B`.
- `Devices/EneDram.cs` (#29) + `Native/SmbusPiix4.cs` (#39): sticks follow a static, no `direct-mode enable failed`; with HWiNFO's SPD
  dump running, a >2 s mutex hold now FAILS our frame (repainted next tick) — no garbage frames, sticks still detected at launch.
- `Devices/MsiGpu.cs` (#28): a static lands, an identical second apply is deduped; `--gpu` completes the register sequence.
- `Devices/LianLiWireless.cs` (#3, #76, #122, #47, #48, #53) + `Native/WinUsbNative.cs` (#33): Cooling pane open → pull the dongle,
  Rescan ×20, exit — no crash, no WinUSB-error burst, each Rescan well under 2 s. >6 s after a bake nudge the speed slider once →
  `upload animation` then `animation confirmed by fans`. Drag speed + duty sliders under a baked effect → UI responsive, values latch.
  Baked Rainbow → master brightness follows within ~150 ms + upload. All fans + Outer ring + Breathing → wheel/speed/Reverse/palette
  reach every fan, saved profile identical per fan. Baked → Static → All devices → the FIRST brightness hotkey dims the fans.
- `Devices/LianLiUniHub.cs` (#2): wired fans black, exit, relaunch → fans go dark (was the power-on rainbow); same after Rescan.
- `Sensors/SensorHub.cs` (#78, #92, #83) + `Sensors/IteSuperIo.cs` (#88, #85): GPU curve, window closed → no `pwm: confirmed and latched`
  every 1.5 s, NvAPI writes ~2/min. GPU Manual 60 % + driver reset/resume → back at 60 % within ~30 s. Identify a Manual 40 % fan, move
  to 60 % mid-burst → stays 60 %; a curve fan holds 100 % for 4 s. `--superio` prints the same table and a second run does not block
  on the ISA mutex. LHM-blocked board: a fan in BIOS fan-stop at launch appears once it spins.
- `Native/PawnIoInstaller.cs` (#132): Install PawnIO on a clean box/VM → `installer signature OK` then `PawnIO installed`; renaming the
  `PawnIO_setup-*.exe` from another process during the run fails with a sharing violation; nothing left in `%TEMP%`.
- `Native/Wasapi.cs` (#166, #35 — manual only): Audio Bars on a USB DAC, unplug it → within ~2 s `capture loop error: WASAPI next packet
  failed` then `loopback capture started` on the remaining endpoint and the bars react again.
- `LcdController.cs` (#103): static design ON for minutes → the firmware screen never flashes through; the 5-minute `stream: N frames`
  line drops from ~3,800 to ~600-900 (shorten the slice count in `StreamLoop` if the panel ever blinks).
- `Effects/LianStackEffects.cs` (#67): Orbit/Outline/Waterfall on the fans, two full loops, no snap. **Orbit's tempo changed** — hue
  turn every 6 s instead of 12 (hub 5/6 laps/s instead of 0.8), chosen from the refuter's notes; `Loop(12)` or the judge's 8 s are
  drop-in via the `LoopS`/`HueRate` consts if the old pace reads better.
- `Effects/ChromaSync.cs` (#133): with Wallpaper Engine running + shim installed, enable Chroma Sync → `host connected` + `first frame`
  still appear under the ME label. Squat: create `\\.\pipe\UnifiedRgbChroma` from PowerShell before launch → `chroma-squat` WARN.
- `Effects/WallpaperCapture.cs` (#55, #59, #106, #66): static-image wallpaper → after 10 s no amber, no repeated `found window`,
  ≤1 `EnumWindows`/5 s, no snapshots; explorer restart → exactly one restart. Resolution change → one `capture size changed` +
  `WGC session started` pair, right region. Log `readback 240x135 (mip 4 of 13)` on the 4K rig, animated wallpaper still ~10 Hz,
  finalized-object count flat.
- `Models.cs` (#121): board fan Manual, drag 30 → 100 in ~1 s → lands on 100, file says 100; set 55 and close within a second → fan
  returns to BIOS with no late 55 % write after `app exit`.
- `MainWindow.xaml.cs` (#128): unsaved tweak + curve-controlled header, sign out → no prompt, next log shows the `app exit`
  RestoreAllFans line, `profiles.json` has the tweak, header PWM back on the BIOS curve.
- `PaletteLibraryWindow.xaml.cs` (#44): click a built-in card → "Applied …" and colours change; ✕ on a custom card deletes only.
- `native/chroma-shim` (#8) + `Cli/Program.cs` (#9): a 32-bit Chroma title on a Synapse box → `chroma-shim.log` says
  `proxy: real DLL loaded` (not `err=193`). `--fanmap` elevated, Ctrl+C inside a 4 s window → the PWM register reads its original value.
- `app.manifest` (#99, skipped): only if PerMonitorV2 is ever adopted — drag between 100 % and 150 % monitors, check hit-testing,
  previews, the wheel and placement restore.

## Behaviour changes worth knowing

Deliberate semantics the engineers changed and the reviewers confirmed; listed so nobody "fixes" them back.

- **`SensorHub` has two recency tiers**: `Touch()` (Cooling pane, FanRpm element, duty writes) arms the full NvAPI/LHM/load sweep;
  `TouchTemps()` (Temp Glow, Pattern Temp, LCD temp elements) keeps only the temperature readers alive; idle-stop on the newer stamp.
  Board/GPU duties are re-asserted every ~30 s; Lian duties are NOT (they latch; re-sent on value or instance change), so a receiver
  that loses its latch without re-enumeration stays put until the curve crosses a 5 % step. `ResetSources`/`Shutdown` drain the tick.
- **The first LCD tick arms the sensor hub** when the design has a CpuTemp element (the default design does). After the follow-up
  round `Touch()`/`TouchTemps()` only stamp and start the timer; the sources (PawnIO, LHM's ring0 driver, ITE, NvAPI) open on the
  timer thread's first tick, so the first sample reads `--` for one tick and nothing blocks the `MainViewModel` constructor.
- **`UpdateService`**: marker and swap result live beside the exe, not `%TEMP%` (a v1.0.19 marker in `%TEMP%` is not read once —
  log-only); the prior-install report runs before the network check; `UnifiedRGB-update-*.exe/.bat` older than 1 h are deleted at startup.
- **`Backend`** rejects any non-`https://` endpoint (`http` only on loopback) and WARNs whenever `backend.json` supplies the URL — a
  dev override pointing at plain-http non-loopback is now ignored and logged.
- **`SafeFile.WriteAllText` fsyncs** every write; the only non-debounced caller left is `SensorHub.SaveFanConfig`, now behind the
  slider's 120 ms coalesce and the editor's once-per-drag raise.
- **Chroma pipe**: ≤16 concurrent clients; refuses a name another process already owns (`chroma-squat`, 5 s back-off) — a CLI `--pump`
  running a Chroma effect while the app is up now backs off instead of silently splitting the host's connections. `PushGrid` has an
  optional `type`; `ChromaFeed.Touch()` keeps a host Active without changing the grid.
- **`ChromaShimInstaller.Install()`** with Wallpaper Engine running returns an explicit "is in use" error (was null); `Uninstall`
  continues past a locked file.
- **`WallpaperCapture.WindowFound`** no longer depends on frame recency: a static wallpaper keeps its last grid; amber only before the
  first frame ever, during the 5-min cooldown, or after a teardown until the new session's first frame.
- **`FanRowModel.DutyPercent`** applies after a 120 ms coalesce (mode switches immediate); **`CoolingViewModel.Stop()`** cancels pending
  writes — the pane-leave case is in the follow-up round.
- **`ProfileStore.SetAutoStart` returns `bool`**; `ProfileStore.Save<T>` is `internal static`; `ChoiceByName` takes `string?`;
  `SetHeaderLeds(header, colors, string order = "GRB")` replaces `bool grbOrder` and throws for a header outside 1-4;
  `OpenRgbClient.GetControllerCount` throws above 256.
- **`ApplyModeToAll` routes through `ApplyFxRange`** per device: a black wheel colour flips the wheel to white during "All devices",
  and `RequestLianRebake`/`MarkDirty` fire per device (debounced/idempotent).
- **`LogitechG403`** persists to onboard memory once per static apply (`SetColors(colors, persist: true)` from the static-frame
  push), after 5 min unchanged under an effect, and at Dispose — instead of on every frame.
- **Both shim DLLs rebuilt**: x86 now loads `RzChromaSDK_real.dll` and is stamped `RzChromaSDK`/`RzChromaSDK.dll`; x64 unchanged in
  behaviour, stamped as before. DLLs are gitignored; only `.cpp`/`.rc`/`.bat` changed in git.
- New API: `IEffect.LiveInput`, `EffectEngine.InvalidateBase`, `Fx.Step/StepWrap`, `SensorHub.TouchTemps/Shutdown`,
  `CoalescingApplier.PruneIdle`, `LcdElement.MoveTo`, `LcdDesignerViewModel.MoveBg/SetBgSize`, `ViewModels/MainWindowState`,
  `Net/OpenRgbDetectorConfig`. Persisted JSON shapes unchanged; nulls now tolerated; a lowercase `ColorOrder` now means what it says.

## Follow-up round

Inputs: the 18 should-fix + 15 nit items from the 14 diff reviewers, the two WPF-lens leftovers, and every cross-territory
hand-off the first round's engineers recorded. Five file-disjoint engineers, **49/49 items closed** (no skips). Where an item
above says "follow-up" or "left", it is closed here.

**Core / Sensors + Native + Lian Li**

- [x] 🟠 `ResetSources` latches `_running` through drain + dispose (timer nulled first, so no tick and no `Touch` can re-open LHM
      under the in-flight `Refresh`); `TickCore`'s idle-stop cannot drop the latch. `Drain` waits on `Timer.DisposeAsync()` — no
      `WaitHandle` the timer could still signal after the 2 s bound. `Sensors/SensorHub.cs`
- [x] 🟠 **`Touch()`/`TouchTemps()` no longer open the sources on the caller's thread** — `OpenSourcesOnce()` runs from the first timer
      tick (once per open cycle, under `_gate` + the `_ticking` guard); the default LCD design no longer loads LHM's ring0 driver inside
      the `MainViewModel` constructor. The first sample reads `--` for one tick.
- [x] 🟡 `OpenIteFallback` / `DutyByte` comments match reality; `SmbusPiix4`'s `ReadEmbedded` forwarder gone (#151 closed);
      `PawnIoInstaller` deletes a partial download too (the try/finally opens before the write).
- [x] 🟠 `LianLiWireless.PollTelemetry` snapshots `_rx` and releases `_rxLock` before the bulk transfer; both dispose sites swap the
      field to null under the lock and dispose outside it — `WinUsbDevice`'s own locks + `CancelIoEx` now bound a Rescan/exit with a
      silent receiver to milliseconds instead of the 2 s pipe timeout (the `WinUsbNative.cs` comment says when that holds).

**Core / Devices + Effects + Net**

- [x] 🔴 **A static colour on the G403 was never persisted until Dispose** — `SetColors(colors, persist)`; `LightingController.PushFrame`
      commits statics in the same write; effects commit only after 5 min parked (Time Warmth's per-minute steps would race a 60 s
      threshold); `PushBlack` deliberately unpersisted. `Devices/LogitechG403.cs`
- [x] 🟠 `HardwareConfig.Load` splits read from parse (`ReadWithRetry` 3 × 200 ms): an unreadable file blocks `Save` for the session
      instead of cementing this rig's defaults; a first-run write failure is logged for what it is; every header's `ColorOrder` is
      normalised at the load boundary, so the dialog and the device agree and a hand-typed `rgb` survives Save.
- [x] 🟠 Baked Lian channels nap in 4 × 100 ms slices re-checking `Running`, so `JoinWorker`'s 300 ms bound is met and its WARN is real.
- [x] 🟡 **The preview no longer re-renders the running effect on the UI thread** — `Channel.LastFrame` (copied under `FrameLock` right
      after `Render`, before `Master.Scale`, lock held only for the copy) + `TryCopyLastFrame`; `ComposedFrame` copies it and falls back
      to `RenderChannel` only before the first frame or in baked mode (#104 closed). `Effects/EffectEngine.cs`, `Services/LightingController.cs`
- [x] 🟡 `IPaletteEffect` summary back where it belongs; Heartbeat `Loop(2/0.9)` and Warning close their loops at the seam (look
      unchanged); `ChromaSync` is `LiveInput` (a game's frame no longer eats the 100 ms idle nap); `OpenRgbLink` skips one malformed
      remote device instead of dropping the bridge; `AudioAnalyzer.BandAt` (unreferenced) deleted; `ChromaShimInstaller` /
      `ThermalrightLcd` / `AudioEffects` comments fixed.
- [x] 🟠 `DiagnosticReport` flags a USER-installed OpenRGB again — `ConflictingProcesses` skips only a process whose image lives under
      `AppPaths.Local("openrgb")` (our bundle).
- [x] 🟠 `WallpaperCapture`: a `ContentSize` change recreates the frame pool in place (`Recreate` + `ComputeCrop`) instead of restarting
      the session with no backoff; five flips within 1 s trip the existing failure path. The no-mip-autogen fallback is real:
      `CheckFormatSupport` first, level-0 copy when unsupported. (`Direct3D11CaptureFramePool.Recreate` compiled; needs a real
      resolution change to exercise.)

**App / View models**

- [x] 🟠 `StartWithWindows` snaps back to the real `schtasks` state when `SetAutoStart` fails (#167 closed).
- [x] 🟠 **`UseOpenRgb = false` no longer blocks the dispatcher** — `StopOpenRgbBridge()` runs Shutdown + `Stop()` in `Task.Run`, rescans
      on the dispatcher, shows "stopping…"; `StartOpenRgbBridge` awaits the in-flight stop so off → on cannot launch over a dying server
      (#63 closed). `MainViewModel.Settings.cs`
- [x] 🟡 `RebuildViewGeometry` calls the now-public `EffectEngine.ZonePositions` (24-line copy gone, #141 closed); `Rgb.ToHex()` at all
      nine sites (#152 closed); the three frame-restore loops are one `RestoreFrame` (#157 closed); the `MainViewModel.Lian.cs` banner
      and the `ProfileStore` comments fixed (#7, #154 closed).
- [x] 🟠 `ProfileStore` scrubs null `Effects` entries and null `Device` at load (the first re-save NRE); a file vanishing between
      `Exists` and the read is "no file", not "unreadable".
- [x] 🟠 The favourites star notifies `StarBrush` (glyph and colour flip together). `SyncFanOutPalettes` is palette-only
      (`CopyPalette`) — it no longer rewrote Speed/Reverse of fans running a different effect. `Models.cs`, `MainViewModel.cs`
- [x] 🟠 `CoalescingApplier.Post` looks up, enqueues and flips `Running` under `_mapLock → lane.Lock`, so `PruneIdle` can never remove a
      lane a `Post` already found. `LedPreview` comment fixed.

**App / LCD + Cooling + dialogs**

- [x] 🟠 **One decoded LCD background** — `LcdController.Background` / `BackgroundSize` (frozen; the file stamp is re-read at most every
      2 s) and `LcdDesignerViewModel.LcdBackground => _lcd?.Background`; the VM's own decode + cache are gone (#146 closed).
- [x] 🟡 `RelaunchElevated` + the "Restart as admin" button deleted (dead under `requireAdministrator`, #100 closed); `LcdDesign.Save`
      routes through `ProfileStore.Save` so `lcd.json` gets the unreadable-file guard (#168 closed); `Format()` builds the
      `FormattedText` before committing the cache entry; the header comment repaired.
- [x] 🟠 `CoolingViewModel.Stop()` FLUSHES a pending manual duty (a pane leave / minimize inside the 120 ms coalesce lands the value);
      `DiscardPendingDuties()` runs only from `MainViewModel.Dispose` right before `RestoreAllFans`. `Refresh()` is guarded
      (`Log.Occasional`), so a per-tick fault never reaches the global handler.
- [x] 🟠 `PawnIoCpuTempProvider.Available` is live and `NotifyPawnIoChanged()` re-raises the banner after an in-app PawnIO install. The
      crash handler calls `SensorHub.Shutdown()` after `RestoreAllFans` (ring0 driver + ISA mutex released on a crash exit).
- [x] 🔴 `HeaderConfigDialog` calls `EndHeaderTest()` from `Closed` — Cancel, ✕ and Save all repaint the tested header from the static
      frame (#25's app half closed); the `ColorOrder` compare is case-insensitive.
- [x] 🔴 **The Lian Li bake uploaded the base frame at full brightness** — every write path scales at the boundary except the bake's
      `Clone()`, so under a dimmed master any LED outside the baked channels played at 100 % in hardware; `Master.Scale(baseFrame)`
      after the clone. The worker-side `SuppressStreaming` reset is marshalled to the UI thread with a generation check.
      `Services/LianBakeService.cs`
- [x] 🟡 `Styles.xaml.cs`: Alt+Up/Down no longer opens a closed combo (the Alt+F4 pass-through had re-enabled it); `FanCurveEditor`
      ignores jitter-only presses (no `SetFanCurve` write at release when the clamped point never moved).

**C++ shim**

- [x] 🟠 The pipe open asks for `FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE` + `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`
      (today's `GRGW` ACE is a superset, so current and rebuilt shims both connect; a later app release can narrow the Everyone ACE
      to `0x100082` once these shims are what's installed). The `DllMain` comment states the per-cycle reference. Both DLLs rebuilt
      (VS 18, `dumpbin`: x64 / x86, 15 exports each, each embeds only its own `_real` name, tracked `version.res` byte-identical).

**Verification (end of session)**

- Release build of the whole solution: **0 warnings, 0 errors**. Console harness: **303 passed, 0 failed** (four consecutive runs).
- **Launch check** on the dev rig (Release build, elevated): 45 s sampled, **25 log lines, zero WARN/ERR**; Corsair Strafe MK.2,
  Gigabyte X870E header controller, G403, MSI RTX 5090, wired SL-Infinity hub and 2 × ENE DRAM detected; `hub started (cpu=ok,
  board=8 fans, gpu=ok+fanctl)`; fan curves restored; working set 35 MB after 45 s. The hub now starts alongside device detection
  instead of ahead of it (the off-thread open). The doubled `restored 'Fan #1'/'Fan #2'` lines are pre-existing (identical in the
  14:02 launch) — two headers share a name.
- **Diff:** 101 files, +3,885 / −1,601; two new files (`Net/OpenRgbDetectorConfig.cs`, `ViewModels/MainWindowState.cs`),
  `Diag/Modules/SmbusI801.bin` deleted. Left **uncommitted** for review.

**Still open (small, documented)**

- `LightingController.ComposedFrame` still returns a cloned frame per preview pull (a `ComposeInto(dev, buffer)` would remove the last
  per-pull allocation).
- `DiagnosticReport.ConflictingProcesses` derives the bundle path itself; a public `OpenRgbManager.InstallDir` would make it one place.
- `PipeSddl`'s Everyone ACE narrowing waits for the rebuilt shims to be what users have installed.
- The harness's `MemoryTrimmer` check writes three `working set trimmed` lines into the real `%APPDATA%\UnifiedRgb\unifiedrgb.log`
  on every test run.
- The hardware / runtime checks listed above were not exercised beyond the launch check.

## Already solid — protect these

Everything in `CODE_REVIEW_2026-09-02.md`'s "already solid" list still holds. Each group reviewer re-read every hunk in its territory
with callers and confirmed the closures above unchanged and correct (verified ids: native 12, sensors 19, core-misc 11, devices-lian 8,
devices-hid 7, effects-engine 10, capture-net 18, vm-main 12, vm-rest 14, app-services 18, views-controls 18, cli-diag-shim 9; the
threading lens 20 across the whole diff, the API/callers lens 30). Lock order **`LianLiWireless._lock → _rxLock → WinUsbDevice._wrLock
→ _rdLock`**; `_pwmLock` and `EffectEngine._lock` are leaves; `SensorHub._gate → LianLiWireless._pwmLock`; `CoalescingApplier._mapLock →
lane.Lock`; `PawnSmbus._lock → Global mutex` — nothing holds a WinUSB lock while taking an outer one, no new `async void`, no lock
newly held across `Dispatcher.Invoke`. No protocol bytes, struct layouts, timing constants, log levels or defaults changed except where
a finding demanded (Tinyuz output and `SetHeaderLeds`' GRB order byte-identical by construction; `PaletteFx.Sample` and
`ColorUtil.HsvToRgb/RgbToHsv` arithmetically identical to the removed copies). No half-applied renames: every removed member
(`FanDutyBySlot`, `ListDirty`, `IDeviceProvider`, `DebugVoltStatus`, the IteSuperIo block, `App.IsElevated`, `--collect-diag`,
`expectedSubjectPart`) has zero remaining references. Per the API/callers reviewer every persisted JSON shape is unchanged and v1.0.19
files load identically. The 303-check harness passed 3/3 consecutive Release runs.
