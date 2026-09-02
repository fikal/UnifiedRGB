# UnifiedRGB — Bug / Memory / Lifetime Review

**Date:** 2026-09-02 · **Scope:** full solution + C++ shim + scripts + docs (~25k lines) ·
**Method:** six parallel reviewers, one per layer, each reading every file in its slice; every
finding re-verified against the code (and the machine) before a fix. Companion to
`PERFORMANCE_REVIEW.md` (2026-08-31), which this pass does not repeat.

**Everything below marked ✅ shipped in this session.** Verified after every batch: clean build
(zero warnings), the console harness (**79 → 98 tests**, 19 added), and a 30 s launch of the
rebuilt app on the dev rig: every device detected, fan curves restored, **zero WARN/ERR** lines.

Legend: 🔴 real bug with user-visible or safety impact · 🟠 latent bug / leak · 🟡 hygiene · ❌ reviewer claim verified FALSE

---

## Verified-false findings (do not "fix" these)

- ❌ **"Shipped releases contain no Chroma shim."** The publish folder shows only the exe + pdbs,
  but the csproj's `None` item is classified as a native library by the single-file bundler
  (`IncludeNativeLibrariesForSelfExtract=true`), embedded, and extracted at startup to
  `%TEMP%\.net\UnifiedRgb.App\…` — where `AppContext.BaseDirectory` points. Confirmed: the name is
  in the bundle manifest of the 1.0.18 exe and the extracted DLL exists on this machine.
  `release.ps1` now refuses to run without the DLL so a dev checkout can't cut a shim-less release.
- ❌ **"build.bat links the dynamic CRT."** `/LD` implies `/MT`. Made explicit anyway.

---

## Core / Native + Sensors

- [x] 🔴 **ISA mutex left owned for the thread's lifetime** — `Sensors/IteSuperIo.cs` `TryOpen`.
      Every early return disposed the `Global\Access_ISABUS.HTP.Method` mutex while still holding
      it; closing an owned mutex handle does not release it, so the kernel object stayed owned by
      the probing thread (often the UI thread) forever — starving HWiNFO/LHM-class tools and our own
      timer-thread reads (250 ms timeout per call). Release now happens in `finally`, and the mutex
      is disposed only when no chip keeps it. `DumpLdns` also exits config mode in a `finally`.
- [x] 🔴 **Thermal failsafe erased the user's fan curves** — `Sensors/SensorHub.cs`. 92 °C / 2 ticks
      was inside the designed all-core steady state of Zen 4/5 parts (95 °C Tctl target; this rig's
      9950X3D has Tjmax 95), and tripping it `File.Delete`d `fan-config.json`. Now **96 °C / 3
      ticks**, and `RestoreAllFans(keepConfig: true)` — fans go to auto (wireless fans to 100 %) but
      the configuration survives.
- [x] 🟠 **WASAPI COM interfaces lacked `[PreserveSig]`** — `Native/Wasapi.cs`. Without it the CLR
      turns a failing HRESULT into a `COMException` and hands back an unrelated "retval" as the int,
      so `Initialize(...) >= 0` could never observe a refusal: the documented event→polling
      fallback was unreachable and `Check()`'s messages were dead. All 18 methods now `[PreserveSig]`.
      `Start()` disposes a half-built client on failure; `Poll` releases the buffer in a `finally`.
- [x] 🟠 **`HidD_*` return `BOOLEAN` (1 byte), declared as 4-byte `bool`** — `Native/HidNative.cs`.
      Marshalled `U1` on all eight imports so undefined upper register bits can't read as `true`.
- [x] 🟠 **`HidHandle.Dispose` freed the OVERLAPPED blocks under an in-flight transfer** — now
      idempotent and taken under both transfer locks; `SetFeature/GetFeature/GetInputReport` guard
      `_disposed`. A cancel that doesn't complete within 1 s now blocks on `GetOverlappedResult`
      instead of unpinning a buffer the kernel may still write.
- [x] 🟠 **WinUSB bulk OUT had an infinite timeout** — `Native/WinUsbNative.cs`: 1000 ms
      `PIPE_TRANSFER_TIMEOUT` on the OUT pipe; `Dispose` idempotent (double `WinUsb_Free` was a
      double free); `needed == 0` guard before `AllocHGlobal`.
- [x] 🟠 **SensorHub tick re-entrant + unguarded** — `System.Threading.Timer` overlapped a stalled
      sweep (concurrent LHM `Update()`, double-counted hot ticks). `Interlocked` guard + outer
      try/catch (an exception from a Timer callback is process-fatal). `FanCurve.Points` null-tolerant.
- [x] 🟡 `PawnSmbus` ctor: mutex creation failure no longer orphans the loaded kernel module.
- [x] 🟡 `ReadEmbedded` ×3 → one `PawnIO.ReadEmbeddedModule`; dead `_initialVendor*` fields removed.

## Core / Devices

- [x] 🔴 **`LianLiWireless` had no dispose guard** — after a Rescan, `PwmLoop` kept writing through
      the freed WinUSB handle for up to a minute, `TelemetryLoop` re-opened the (exclusive) receiver
      on the dead instance, so the fresh instance had no telemetry/PWM until the zombie expired.
      `_disposed` set under `_lock` (with `_usb.Dispose()` inside it); every loop, `SendRf`,
      `SettleResend`, `ReconcileAnimation`, `SetColors/SetZone/SetFanDuty` bail on it; the RX
      handle is published under the lock so Dispose can't leak one opened mid-poll. The PWM re-arm
      no longer round-trips fan 0's duty through percent (255 → 100 → 254). Warns when a group
      exceeds the one-byte LED-count wire field. *(Not hardware-verified: the wireless kit was not
      present on the rig today — exercise a Rescan with it plugged in.)*
- [x] 🟠 **EneDram leaked the SMBus (PawnIO kernel handle + global mutex) on every Rescan** — no
      stick owned the bus. Ref-counted `BusLease`; the last stick's Dispose releases it.
- [x] 🟠 **`LianLiUniHub`: `innerPerFan + outerPerFan == 0` in the layout file → DivideByZero every
      frame** until the engine breaker silently killed the channel. Falls back to 8/12 with a warning.
- [x] 🟠 **`DeviceManager.Dispose` stopped at the first throwing device** and never cleared the
      lists, so Rescan appended fresh devices onto dead ones. Per-device try/catch, clear in `finally`.
- [x] 🟡 `SteelSeriesApex`: LINQ `SequenceEqual` + fresh 643-byte report + `ToArray` per frame →
      index-loop dedup with reused buffers (the LOCK-2 item that never got its fix).
- [x] 🟡 `ThermalrightLcd` header comment described a 480×480 JPEG protocol; the code (hardware-
      verified) sends 240×320 RGB565 — rewritten to match `ShowFrame`.

## Core / Effects + Net + Updater

- [x] 🔴 **Elevated update swap script lived under a fixed name in the user-writable `%TEMP%`** —
      `App/Services/UpdateService.cs`. cmd re-reads a `.bat` from disk on every `goto`, so any
      medium-integrity process of the same user could inject elevated commands during the 3-minute
      swap window, and the verified payload could be swapped after the hash check. Script and
      download now live **beside the exe** under per-attempt random names, the script **re-verifies
      the SHA-256 with certutil before every move**, `taskkill` is filtered to our image name (PID
      reuse), and the swap starts only from the window's **`Closed`** event — cancelling the save
      prompt now abandons the update instead of getting force-killed 60 s later with unsaved work.
      4-part tags are normalised (a `v1.0.18.0` tag was a permanent "install" button that refused
      to install).
- [x] 🟠 **`ChromaFeed` published grid/rows/cols as three fields** — REST handlers run concurrently,
      so a reader could index a 1-element static grid with 6×22 dims for a whole frame interval.
      One immutable `Frame` record per push; keyboard and ChromaLink frames kept apart (a host
      sending both per frame flickered between a 132-cell and a 5-cell picture). Pipe server passes
      `isConnected: false` (a client racing the create threw on our side and lost its connection).
- [x] 🟠 **Palettes mutated on the UI thread while render threads read them** — `Clear()` + `Add()`
      on the shared `ObservableCollection` made Confetti/Taichi/Pattern/Ripple throw
      `ArgumentOutOfRange` mid-rebuild (dropped frame + WARN per running channel per edit). New
      `LivePalette`: an immutable-array snapshot view refreshed on `CollectionChanged`, indexer
      clamps; effects snapshot once per frame. UI keeps binding to the collection.
- [x] 🟠 `DiagnosticReport.Ps`: `ReadToEnd()` before `WaitForExit(20000)` made the timeout dead
      (a hung `Get-WinEvent` hung the support bundle). Async read + `Kill(entireProcessTree)`.
      Two `Process.GetProcesses()` snapshots now dispose their ~750 handles.
- [x] 🟠 `OpenRgbClient.Connect` disposed the socket only on the timeout path (refused / handshake
      timeout leaked it); remote matrix dimensions validated before allocation.
- [x] 🟠 Crash-bisect culprits re-applied on every launch (`ReapplyCulprits`) — a regenerated
      `OpenRGB.json` re-enabled the convicted detector and the user ate the whole bisect again. All
      three OpenRGB config writers use `SafeFile` (a torn file re-enables everything).
- [x] 🟡 `WallpaperCapture` disposes the per-frame `IDirect3DSurface` (20 finalizable RCWs/s).
- [x] 🟡 `ChromaShimInstaller.Uninstall` handles Razer having reinstalled its DLL over the shim.
- [x] 🟡 `UpdateClient`: `HttpResponseMessage`s disposed; the `.sha256` asset is fetched **only at
      install time** — the startup check is back to the single request the README promises.
- [x] 🟡 `EffectEngine.Channel.IsRunning` exposed (see the VM item below).

## App / View models + services

- [x] 🔴 **Corrupt `profiles.json` / `settings.json` / `lcd.json` / `scenes.json` were silently
      replaced with defaults — and the next routine save overwrote the user's file for good.**
      `ProfileStore.LoadJson<T>` copies the unreadable file to `*.corrupt-<stamp>` and logs; all four
      stores use it. `Profile.Name` is no longer `required` (one entry without a Name failed the
      whole list). Saves are try/catch + log (a locked file was an error dialog from a property
      setter, or aborted `Dispose` — leaving :54235 bound and handles open).
- [x] 🔴 **Cooling fan-row update dereferenced a null record** — `MainViewModel.Cooling.cs` (the one
      compiler warning): a stale index into a shrunk `BoardFans` array crashed the UI timer.
- [x] 🟠 **Scene sequences kept firing under Locked/Night**, relighting the case and clearing the
      automation's return point so unlock left the pump LCD blank. Sequencer holds its steps while
      `LightsSuppressed`; `Transition(Base)` always relights the LCD.
- [x] 🟠 **Stale `TargetFx.Channel` after the engine's failure breaker / `LightsOff`** — static
      color picks silently tinted a dead channel. `LiveChannel()` treats a stopped channel as none.
- [x] 🟠 **Applier had no drain**: `Rescan` and `Dispose` freed device handles under queued static
      writes (the exit-restore writes could land on a disposed handle). `CoalescingApplier.Drain`
      is called before `_manager.Dispose()` in both places; `Dispose` runs each step isolated.
- [x] 🟠 `LoadProfile` / `RestoreState` posted statics **before** stopping the engine — a worker's
      final frame could land after the static write. `StopAll()` first.
- [x] 🟠 `RestoreState` now restores "no profile selected" and the dirty flag (an app-rule profile
      stayed selected over restored ad-hoc lighting; the close-time save prompt was lost).
- [x] 🟠 Effect-name aliases (`Screen Sync → Wallpaper`) applied to **saved profiles**, not only the
      favorites migration — an old profile silently lost that effect.
- [x] 🟠 `LcdBackground` decoded the full image on every bound read — six decodes per mouse-move
      while dragging a background. Cached per (path, mtime). `Label`/`ClockSize` notifications no
      longer trigger LCD re-renders (4 renders per drag step → 2).
- [x] 🟠 `PersistCustomColors` writes settings.json only when the swatches changed (every scene
      step / app-rule apply used to rewrite it). `AutomationService`: process name resolved once,
      foreground pid→name cached (two to three process-table snapshots per 2 s tick before).
- [x] 🟡 `LcdElementKind` persisted by name (`JsonStringEnumConverter`, ints still read).
- [x] 🟡 `StartOpenRgbBridge` (async void) guarded; `InstallPawnIo_Click` awaits instead of
      discarding the task; `PreviewBrush` frozen.
- [x] 🧹 Dead code removed: `PersistSettings`, `PersistScenes`, `CpuTempAvailable`,
      `LianRightClicked` (+ its wiring), `Header()`, and `SensorRow`'s never-constructed
      rename/manual-control surface (~60 lines); stale "Ring0 placeholder" comment fixed.

## App / Views + controls

- [x] 🔴 **The Cooling fan-rename box could not be typed into** — `Pills_PreviewKeyDown` tunnels
      ahead of the nested `TextBox` and marked every key handled, so no `TextInput` was ever
      generated. TextBox keys pass through now.
- [x] 🟠 `LianLayoutWindow`: a click without a drag left press state set — the title bar stopped
      moving the window and the next mouse-down could drag the stale row. Reset on release (the
      AppRules copy already had this).
- [x] 🟠 `LianLiFanView` never received the initially selected device's part layout (the VM's
      raise predates the handler) — a wired hub first in detection order drew 20 outer segments
      from a 12-LED ring. Explicit `SetParts` after wiring.
- [x] 🟠 `LedPreview` compared colors only; a target switch with identical colors but different
      geometry kept the old shape and hit-rects. Layout compared by content (positions/style/aspect/rects).
- [x] 🟠 `RestoreFromTray` during a mid-close save prompt threw ("Show while closing"). `_closing` guard.
- [x] 🟡 Previews pause behind blurred modals (`ShowBlurred`, header dialog): no 30 Hz redraws
      through a full-window `BlurEffect`.
- [x] 🟡 `ColorWheel`: frozen marker pens; drag state cleared on `LostMouseCapture`.

## CLI · Diag · Tests · Scripts · Docs

- [x] 🔴 **CLI fan probes left hardware modified on Ctrl+C** — no `CancelKeyPress` handler, so
      the `finally` blocks that restore PWM registers / vendor fan control never ran. Cooperative
      cancel: every probe delay goes through `Sleep()` which throws on Ctrl+C, and the whole
      program body is wrapped so the restores execute (exit code 130).
- [x] 🟠 CLI: second `--gpu` handler was unreachable → `--gpurgb`; `--pump`'s "app running" guard
      matched `UnifiedRgb.App` but the shipped exe is `UnifiedRGB-v1.0.x` → prefix match; fan index
      bounds-checked; unknown `--option` no longer scans every device and dies in `Rgb.FromHex`.
- [x] 🟠 Diag: "Report saved to" printed even when the write failed; exit code always 0; the
      upload prompt could only ever fail on public builds. Reports only written paths, exit 1 on
      crash/no-save, prompt only when a backend is configured.
- [x] 🟠 **`dotnet test` silently passed having run nothing** — the Tests csproj now runs the
      harness from the `VSTest` target (verified: `dotnet test` reports 98 passed, fails on a
      non-zero count). CI builds the whole solution (CLI/Diag/Tests only compiled via Core before).
- [x] 🟠 `release.ps1`: `git commit`/`push` exit codes checked (a rejected push let `gh release
      create --target main` tag the **remote** tip — the previous, unstamped commit); release
      targets the verified `HEAD`; every pre-commit failure reverts the stamp and stray assets;
      refuses to run without the shim DLL or a `<Version>` element.
- [x] 🟡 Tests added (19): `LivePalette` snapshot/clamp/empty, `ChromaFeed` atomic publish +
      undersized-grid rejection + clamped sampling, `FanCurve` null `Points` (incl. JSON),
      embedded PawnIO modules resolvable.
- [x] 🟡 Docs: README effect count (57 entries, "~55"), shim paragraph (bundled in the exe), bug
      template + CONTRIBUTING button name ("Report a problem", opens a prefilled issue), CLI `--lcd`
      comment (raw RGB565, not JPEG).

## C++ Chroma shim (`native/chroma-shim`, rebuilt with VS 18, exports verified)

- [x] 🔴 **Blocking `WriteFile` under the global mutex** parked the game's render thread — and
      every SDK call on every thread — for as long as UnifiedRGB's pipe buffer stayed full
      (suspended / hung at exit / in a debugger). Overlapped write with a 50 ms bound; an
      undeliverable frame is dropped and the pipe re-opened.
- [x] 🟠 **Per-frame reconnect + disk log while UnifiedRGB isn't running** (60×/s, `chroma-shim.log`
      grew ~5 MB/h) → reconnect at most once per second, log once per minute.
- [x] 🟠 **`CUSTOM2` decoded with the 6×22 stride** — the v2 struct is `Color[8][24]`, so rows
      skewed progressively. Sent as 8×24 now (both capture paths).
- [x] 🟠 `g_pending` unbounded (~110 MB/h in a host that creates per frame and never deletes) →
      capped at 256, oldest evicted; `g_counter` atomic (duplicate ids across threads).
- [x] 🟠 `DllMain` took a `std::mutex` on process-termination detach (deadlock under the loader
      lock if a thread died holding it) → returns immediately when `reserved != nullptr`; the attach-
      time file I/O moved out of the loader lock to the first API call.
- [x] 🟡 Allocating exports wrapped in try/catch → `RZRESULT_FAILED` instead of terminating the
      host; `build.bat` fails loudly instead of printing a stale DLL's exports.

---

## Second pass (same day) — the deferred list, shipped

Everything the first pass parked as "pure refactor / do as its own change" was done in a second
session. Build clean (zero warnings), 98/98 tests, both shim bitnesses built and exports verified.

**Structure (App project)**
- [x] **`MainWindow.xaml` 1,459 → 97 lines.** The five panes are `UserControl`s under `Views/`
      (`LeftNavPanel`, `LightingPane`, `LcdDesignerPane`, `CoolingPane`, `SettingsPane`), sliced
      byte-for-byte from the old markup so nothing re-rendered differently; each pane owns its
      `x:Name`s and handlers (`MainWindow.xaml.cs` keeps the shell: title bar, hotkeys, placement,
      tray, close prompt). The three byte-identical palette swatch strips are one
      `Controls/PaletteStrip` (`SwatchSize`/`SwatchMargin` DPs). Shared key policy in `Views/KeyPolicy`.
- [x] **View-model extractions** — `MainViewModel*.cs` 3,4xx → 2,576 lines:
      `Services/LightingController` (engine + static frames + applier; the snapshot+scale+post block
      that was pasted ×9 is `PushFrame`, the zone slice ×3 is `PushZone`, plus `RestoreStatics`,
      `PushBlack`, `ComposedFrame`, `StopAndDrain`) · `Services/LianBakeService` (the hardware
      animation bake, debounce, signature cache, generation stamp) · `ViewModels/CoolingViewModel`
      (the CoolingPane's DataContext; the main VM only keeps navigation) ·
      `ViewModels/LcdDesignerViewModel` (designer + background rect + scenes/shows; the
      LcdDesignerPane's DataContext; the three Fill/Fit/Center placements share one
      `PlaceBackground`) · `ViewModels/LianFanSelection` (fan-scope × part state and its geometry
      math, pure) · `FxFor(dev, off, count)` replaces the five hand-rolled `_targetFx` lookups ·
      `SetSetting` replaces nine identical settings pass-through setters · `AppInfo` is the one
      place the app reads its own version (was three, with two different shapes).
- [x] `Native/SetupDiEnum` — the SetupAPI enumeration that `HidNative` and `WinUsbDevice` each
      carried verbatim (struct, four imports, the cbSize quirk).
- [x] Dead XAML/code: shadowed app-level `TabItem` style, the never-firing `IsHeader` triggers and
      the `IsHeader` model flag, three unbound key handlers, `Header()`.

**Behaviour**
- [x] `ColorWheel` renders at device pixels (sharp at 125–200 % DPI, re-rasterizes on `DpiChanged`).
- [x] Mouse-capture loss ends the drag on the LCD canvas, the background grip and the fan-curve
      handles (Alt+Tab mid-drag no longer leaves the drag armed).
- [x] **Shim: 32-bit build** — `build.bat` now produces `RzChromaSDK.dll` (x86, its own `.def`)
      beside the 64-bit one; the csproj bundles both, the installer places/backs up/restores both,
      `release.ps1` refuses to ship without either.
- [x] **Pipe server: unlimited instances.** One reader thread per connected host (Wallpaper Engine
      + a game no longer fight over a single instance / `ERROR_PIPE_BUSY`).

**Still open (decided, not forgotten)**
- Shim Create/Set semantics (send only on `SetEffect` when an id is given) — change it with
  Wallpaper Engine on screen; the current double-send is what's hardware-verified.
- `GigabyteIt5711.Cc`/`SendZoneEffect` 64-byte per-frame reports (needs the write lock plumbed
  through the CLI probe path first); `LianLiUniHub.SetFanSpeed/SetFanMoboSync` unused API;
  `SensorHub` `double?` snapshot atomicity; Authenticode check in `PawnIoInstaller`; a `SwatchGrid`
  control for the five remaining swatch grids (they differ in command/size, not worth a DP matrix).
- **Runtime verification of this pass was build + tests only** (an elevated instance from the first
  pass held the output folder and could not be stopped from the session); launch and click through
  Lighting → Pump LCD → Cooling → Settings once before the next release.

## Already solid — protect these

Everything in the previous review's list still holds. New confirmations this pass: NvAPI struct
layouts/sizes all match the SDK · HID/WinUSB struct layouts and the whole-operation GCHandle pin ·
`RyzenCpuTemperature` decode and `IteSuperIo` tach formula · `EffectEngine.Run` dedup/keepalive/
breaker and `ZonePositions` degenerate handling · every `(byte)`/`(int)` cast in the effects is
saturating on net10 (no wraparound class) · `OpenRgbClient` framing (magic, 16 MB cap, looped
`ReadExactly`) · `SafeFile` on all stores · `AppRulesWindow` drop-index math · `LcdController`
rotation/stride/RGB565 math and dispose ordering · `App.xaml.cs` single-instance mutex.
