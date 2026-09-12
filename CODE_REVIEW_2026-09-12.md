# Code review, 2026-09-12

Whole-codebase pass over the 101 commits since the last one (86fa3aa, 2026-09-02):
about 35k new lines - sensor rules, schedules, the LCD designer's undo, battery,
now-playing, hardware persistence, the OpenRGB SDK server, CS2 game state, the
whole-desk canvas, the settings redesign, device recovery, setup backups,
calibration, the activity log, the Shows page, Wallpaper Engine, the Razer driver.
The brief was "issues and code improvements, especially XAML and UI interactions".

**Method.** Seven read-only review passes, one per layer (XAML + code-behind, view
models, App services, Core devices + native, Core net + effects, Core sensors +
automation, tests + CLI + release tooling), each told to verify every claim in code
before reporting it. On top of that a static cross-check of the XAML: every event
handler named in XAML exists in its code-behind, every `{Binding}` root path exists
as a member, every `StaticResource` key is defined and every `x:Key` is used - all
clean. Two of the 99 findings were reproduced with a throwaway WPF program
(`ObservableCollection.Clear()` pushes null into a bound `SelectedItem`
synchronously; `Thread.Join` on the UI thread does NOT pump a `DispatcherTimer`).
One claim was checked against upstream OpenRGB source rather than memory.

**Outcome.** 99 findings; 90 fixed in the review pass, 9 deferred. A follow-up
pass then closed 7 of the 9 (see the table): the two left need hardware. NE-02
was put to the owner and kept as shipped.
Verified: Debug and Release builds with 0 warnings, harness 2143 -> 2198 checks,
0 failures. The app could not be launched for a runtime check in either session
(the shell was not elevated and the app requires UAC), so the "needs a runtime
check" list at the end is still the manual pass to do before a release.

Legend: **[fixed]** shipped here - **[deferred]** left alone, reason given -
**[runtime]** the fix is in, but only hardware or a running app can confirm it.

## UI: XAML and interactions (the priority)

- **[fixed] UX-01 Nav clicks did nothing while the Shows page was open.** A
  device/Pump LCD/Cooling pick only closed Settings; every content pane is gated
  on `!IsShowOpen`, so the row highlighted and the Shows page stayed. Same class
  as the "Shows page permanently visible" bug (1feaf27). `SelectLeftItem` and the
  nav click handler now close both top-level pages.
- **[fixed] UX-02 Cooling gauges froze after Shows -> Back.** The refresh timer
  self-stops when the pane is covered and only the Settings setter restarted it.
  `IsShowOpen` now restarts it too.
- **[fixed] UX-03 Deleting a profile blanked (and saved) the show steps that used
  it** once the Shows page had been opened: the step combos re-read
  `ProfileChoices`, WPF deselected the missing name and pushed `null` through the
  binding, and the step handler saved `Profile: null` - while the delete dialog
  promised the steps would keep the name. `ProfileChoices` now keeps any name a
  step still carries.
- **[fixed] UX-04 / VM-03 Every rescan snapped the nav to the first device.**
  `BuildLeftItems` read the previous selection AFTER clearing the bound lists; the
  ListBox had already pushed null. Captured first now.
- **[fixed] UX-05 Sensor-rule threshold parsed in the OS culture** while the rows'
  bound boxes format in en-US: "85.5" copied from a row became 855 on a
  comma-decimal locale. Invariant first, then the OS culture; the hint formats
  invariant.
- **[fixed] UX-06 Mouse wheel over the fan or mode list did not scroll the Cooling
  page** (a ListBox's own ScrollViewer eats the wheel even with nothing to scroll).
  `WheelPolicy.Bubble` re-raises the event from the list; wired on both lists and
  the Settings category strip.
- **[fixed] UX-07 `DragMove()` unguarded in nine windows** - a fast click on a busy
  UI thread threw into the app-wide error dialog. One `TryDragMove` helper.
- **[fixed] UX-08 Settings > Automation summaries went stale** after in-place edits
  in the rules dialogs: `AutomationRule` and `SensorRule` now implement
  `INotifyPropertyChanged` like `ScheduleRule`.
- **[fixed] UX-09 Save with the name box cleared left Delete disabled and the
  startup checkbox stale** (`SaveActiveProfile` bypassed the selection setter).
- **[fixed] UX-10 / VM-04 / VM-05 LCD designer undo baseline.** The first property
  edit of a session was not undoable; undo after picking a screen jumped back to
  the previous canvas while the dropdown stayed on the new one, and an empty-name
  "Save screen" then overwrote it; Fill/Fit/Center were not undoable; the Height
  and aspect-lock setters mutated before capturing. Baseline now follows every
  load, a screen pick is one undo step, `ApplySnapshot` re-derives the selected
  screen, the setters capture first.
- **[fixed] UX-11 No disabled look for the themed CheckBox, Slider, TextBox and
  ComboBox** (Button had one). `IsEnabled=False -> Opacity 0.45` on all four.
- **[fixed] UX-12 Two pill lists bypassed the mouse-first key policy** (Settings
  category strip, Lian fan pills): arrow keys after a click flipped them.
- **[fixed] UX-13 Speed slider tooltip talked about sound for every effect** - a
  `SpeedHint` beside `SpeedLabel`.
- **[fixed] UX-14 ARGB header dialog: Test clamped to 64 LEDs, Save to 256** - one
  constant.
- **[fixed] UX-15 Escape left Settings but not Shows.**
- **[fixed] UX-16 Two NoResize dialogs could be taller than a 768 px display**
  (Import setup, Your desk): capped to the work area, the desk window scrolls.
- **[fixed] OWN-04 WPF binding failures were invisible in a shipped build.** A
  trace listener now logs each distinct data-binding error once (`[binding]`), so
  a support bundle can answer "why is that box empty".
- **[fixed] OWN-05 The idle memory trim continuations could throw on a shut-down
  dispatcher** (unobserved) - guarded.

## View models and app logic

- **[fixed] VM-01 (data loss) A restore left the name box holding another
  profile's name.** `RestoreState` (an automation return, an SDK client letting
  go) and `ReloadAfterImport` moved the selection without the name box; the next
  Save read "name box != selected name" as a RENAME: the real profile of that name
  overwritten, the selected one deleted, every step/rule reference rewritten. One
  `SelectProfileRestored` helper moves both.
- **[fixed] VM-02 Fan renames and the Lian exit-handoff toggle were lost after a
  setup import**: `CoolingViewModel` captured the `SettingsData` instance that
  `ProfileStore.Reload()` replaces. It takes a getter now.
- **[fixed] VM-06 / TS-05 A show step whose profile no longer exists did nothing,
  silently** (the apply's `bool` was discarded). Logged, and noted in the activity
  history; a test drives the step.
- **[fixed] VM-07 Delete ignored the disk-write verdict**: the row vanished and
  came back next launch, and the startup pointer was cleared meanwhile.
  `ProfileStore.Delete` returns the verdict and puts the profile back on failure;
  the UI keeps it and notes the problem.
- **[fixed] VM-08 `ShowViewModel.Dispose` was never called** and the LCD
  "find the panel" timer never stopped; both in `MainViewModel.Dispose` now.
- **[fixed] VM-09 A profile/schedule starting a show moved the Shows page's
  editing selection** (the same class fixed for profiles in 0650fb8). It follows
  the start only when nothing was selected.
- **[fixed] VM-11 Two comments described WPF behaviour the opposite of what the
  probe measured.** Reworded.
- **[fixed] VM-12 "New show" with an existing name did nothing** - it selects it.
- **[deferred] VM-10 Per-pull allocations on the 30 Hz preview path**
  (`ComposedFrame` clones a frame and a channel list per pull). Real but bounded
  to an open preview; wants a `ComposedFrame(dev, into)` overload and a measured
  Gen0 number, which is a change on its own.

## App services

- **[fixed] SV-01 Returning to Base after a lock could leave the desk black.** A
  user apply during an override dropped the automation's baseline to null; a lock
  or dark window that followed then had nothing to restore and no startup profile
  to apply, and the third arm only relit the pump LCD. The baseline is captured
  instead of nulled, the takeover is remembered, and the no-baseline arm relights
  the stored lighting when the lights were off.
- **[fixed] SN-06 A colour picked by hand mid-override was replaced two seconds
  after the override ended** by the startup profile, although the code said the
  user's choice was the new baseline. With `_userTookOver` set the Base branch
  keeps it (relighting it after a lock or dark window).
- **[fixed] SV-02 The automation tick could re-enter itself** through a modal
  pumping the dispatcher mid-apply (double apply, baseline overwritten with a
  half-applied capture). A latch; a tick arriving mid-tick runs after it. (The
  probe showed `Thread.Join` does NOT pump, so this is insurance against dialogs,
  not the repro the old comment described.)
- **[fixed] SV-03 An SDK client's release restored the pre-claim snapshot over
  whatever the automation decided in the meantime** - the game exited, the
  startup profile came back, and seconds later the whole desk snapped to what was
  on before the game. When a profile was applied during the hold that profile is
  re-applied instead of the snapshot.
- **[fixed] SV-04 Rescan unwound SDK claims through `Stop()`** - a "client
  released X" history line and a restore per device, and the `ResetExternal` path
  the comments promised was unreachable. Rescan calls `DeviceListChanged` first.
- **[fixed] SV-05 A bundle from a newer build carrying a new file under a known
  group was refused outright** ("not allowed to write") instead of skipped with a
  warning, contrary to the manifest's forward-compatibility rule. Unsafe names
  are still refused; unknown-but-safe ones are skipped and said.
- **[fixed] SV-06 A remap collision threw out of the preview**, refusing the whole
  bundle with a sentence about a dialog the user never saw. The preview's
  automatic mapping now drops the stale entry (and says so); Apply, where the
  mapping is the user's own, still refuses.
- **[fixed] SV-07 The LCD renderer could overwrite the frame the stream thread
  was still sending** (it chose the buffer by what was last published, not what
  was in flight). The in-flight buffer is marked; a tick with both buffers busy
  keeps the published frame. [runtime: a GIF background on a full-speed link]
- **[fixed] SV-08 Automatic recovery ignored a running calibration session**: a
  replug or resume mid-calibration replaced the very instances the aid was
  driving, dropped the patch and made every slider move a 400 ms error storm.
  `RecoveryConditions.CalibrationRunning` defers the rescan like a client hold.
- **[fixed] SV-09 `ProfileStore.Unreadable` was never cleared**, so after a
  successful import over a file that failed at startup every later save was
  still skipped. Cleared on a successful read.
- **[deferred] SV-09b Bundle previews inherit `LoadJson`'s side effects** (a
  `.corrupt-*` copy, an `Unreadable` entry from a transient sharing violation).
  A parse-only reader for the preview is the fix; low severity, left alone.
- **[fixed] SV-10 Cleanups**: the duplicate `SetPumpLcdOn(true)` and stale comment
  in the Base branch; the `RegistryKey` the Wallpaper Engine lookup never closed.
  `ProfileStore.Capture` (no app caller) is left for the tests that use it.
- **[fixed] SN-11 The failsafe activity entry claimed every fan went back to the
  board even when the handback failed**; it now names how many are still retried.

## Networking, effects, audio

- **[fixed] NE-01 Every OpenRGB single-LED write was dropped.** The handler
  expected the bulk-write layout (a u32 length prefix, index at offset 4); the
  real packet is 8 bytes - `int led + RGBColor` - as OpenRGB's own
  `RGBController_Network` and openrgb-python (Home Assistant, Stream Deck) send
  it. No write, no claim, no log line. Verified against upstream source; a test
  now sends the raw 8-byte packet.
- **[fixed] NE-02 A claim lapsed after 5 s of silence**, so a client that set a
  colour once and held its connection (Home Assistant's integration, a Stream
  Deck button) had the user's lighting come back over it. The server now runs
  with an infinite silence: a claim ends on disconnect, a crashed process closes
  its socket, and a dead LAN peer is found by TCP keepalive. Tests keep a short
  silence to exercise the sweep. *Design change - revert `OpenRgbServer`'s
  default if the five-second lapse was wanted.*
- **[fixed] NE-03 Idle SDK clients were cut off after 120 s, silently.** No read
  timeout now (MaxClients bounds the threads, keepalive finds dead peers); an
  abrupt disconnect gets one log line.
- **[fixed] NE-04 On the desk canvas most 2-D devices were treated as strips.**
  `Geo.IsFlat` used an absolute Y span (<= 0.3) written for device-local
  coordinates; desk-relative, a keyboard spans a sixth of the desk's height, so
  Rain, Matrix, Tide and Audio Bars ran their strip path along the keys. Flatness
  is judged by the channel's own aspect now (scale-invariant). [runtime: look at
  Rain on a keyboard with the desk on]
- **[fixed] NE-05 The audio capture never followed a default-output switch**
  (speakers -> headset = the rig goes dark and stays dark: a shared-mode loopback
  does not follow the switch, it just delivers nothing). The watchdog compares
  the captured endpoint id with the current default every 2 s and reopens.
  [runtime]
- **[fixed] NE-06 The GSI listener could bind a port the game's config no longer
  named** (27180 taken by something else at a later launch). The installed
  config's port is read back and the file rewritten for the bound port.
- **[fixed] NE-07 `GsiServer.Connectedchanged` fired only on connect**, so the
  settings line kept saying "in game" after CS2 quit. Renamed `ConnectedChanged`,
  raised on the drop too.
- **[fixed] NE-08 Wallpaper capture kept a removed D3D device across its 20
  retries** after a driver reset, then sat in the five-minute cooldown. The
  device is disposed with the session on failure.
- **[fixed] NE-09 The SDK server allocated per packet** on a streaming client's
  path (payload, colour array, single-colour array). Per-client reusable buffers;
  the host copies before returning.
- **[fixed] NE-10 An unusable audio endpoint logged a WARN every 10 s** while an
  audio effect stayed selected - rate-limited.
- **[fixed] NE-11 The Chroma REST listener answered only on `localhost`**; a
  host spelling the documented URL as 127.0.0.1 got nothing. Both prefixes.

## Device drivers and native interop

- **[fixed] DV-01 ENE DRAM: a write that outlived Dispose threw out of the SMBus
  helper** (a transaction can wait 2 s on the machine-wide mutex; the drain is
  1.5 s). A disposed flag on the driver, and the helper tolerates being disposed
  under a wait.
- **[fixed] DV-02 Lian Li wireless: the settle timer was disposed outside the
  lock**, so a transmit in flight threw `ObjectDisposedException` re-arming it.
- **[fixed] DV-03 The PawnIO installer's Authenticode check never consulted a
  CRL.** `WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT` is set; an unreachable CRL is
  "revocation unknown" and does not fail the check. [runtime: confirm offline]
- **[fixed] DV-04 The wired Uni hub hot-reloaded its layout file by mutating
  `LedCount`/`Zones` in place**, which the driver contract forbids (the engine
  sized its buffers at channel start). A changed file now asks the app for a
  rescan.
- **[fixed] DV-05 SteelSeries Apex handed itself to its onboard profile in
  Dispose**, unconditionally - every rescan flashed the keyboard, and "keeps its
  last colors" (the only exit choice offered) was a lie. It implements
  `IHardwareModes` now; the test pins it.
- **[fixed] DV-06 The Basilisk's dongle identity shared the mouse's name** -
  two devices with one primary key when the mouse charges on its cable.
- **[fixed] DV-07 The HyperFlux pad grouped identities whose serial read failed
  into one**, dropping the second.
- **[fixed] DV-08 Five drivers had no disposed flag**, so a write after a rescan's
  Dispose logged "stopped answering" for a handle the app closed on purpose.
- **[fixed] DV-09 Two present-but-unusable cases never reached DetectionNotes**:
  the Lian Li transmitter held by L-Connect (or unpaired), and a Logitech mouse
  opened feature-only because G HUB holds it. Both say so on the Devices page
  and in the bundle now.
- **[fixed] DV-10 `MsiGpu` claimed any NVIDIA card that answered a byte at I2C
  0x68** and wrote mode registers to it. The board vendor is read through
  `NvAPI_GPU_GetPCIIdentifiers`; a non-MSI vendor is skipped, an unknown one
  falls back to the probe as before. [runtime: your 5090 must still be found]
- **[fixed] DV-11 The Logitech driver committed to the mouse's flash on every
  Dispose** (every rescan). Only after the streaming path's quiet period now.
- **[fixed] DV-12 The Logitech pacing sleep ran before the dedup/backoff checks**,
  so a refused frame inside the retry window still slept 33 ms under the write
  gate. It paces only a frame that will be sent.
- **[fixed] DV-15 Razer settings exchanges ran without the instance lock** that
  Dispose takes; routed through it.
- **[fixed] SN-05 The support bundle printed every Razer serial number** (the
  warranty identifier the driver deliberately never logs). Last three characters
  only.
- **[fixed] DV-14 The driver contract said every driver follows the short-frame
  rule**; three keep a shadow frame instead. Documented in `ADDING_A_DEVICE.md`.
- **[partial] DV-13 Per-frame allocations in several drivers.** The SMBus
  transaction buffers (twice per stick per frame) are reused now. The Razer,
  Logitech, Lian Li wireless and Sayo report buffers are still allocated per
  changed frame - small, bounded to animated effects, listed for the next pass.

## Sensors, automation, infrastructure

- **[fixed] SN-01 The board-fan re-assert was a no-op, and a fan write LHM
  dropped was never retried.** LibreHardwareMonitor writes the chip only from its
  own change events and both `Control` setters dedup, so re-asserting the value
  it already held reached nothing - and its IT87 driver silently drops a write
  when the ISA-bus mutex is busy (`WaitIsaBus(10)`). `LhmFans.SetDuty` nudges
  (two events, the same register byte twice) when the value is unchanged, and a
  retried handback forces a real write. [runtime; the readback comparison the
  finding also asked for is not built - see deferred]
- **[fixed] SN-02 "SetDefault cleans up a crash-stuck duty" was untrue**, and the
  crash handler's "marker file" did not exist. LHM can only restore what it saw
  at its own first write in THIS process; on a fresh process a header still in
  software mode from a dead run is what it captures as "default". The comments
  are corrected and the marker exists now: `fan-control.active` in the local
  folder while a board header is ours, removed on a clean handback; a launch that
  finds it warns in the log and the activity history that Auto may not be the
  BIOS curve until a reboot. [runtime: end the process with a header on Manual]
- **[fixed] SN-03 Installing PawnIO stopped the fan-control loop for good**
  (`ResetSources` took the timer and nothing restarted it while curves were
  configured; the failsafe went unevaluated until the Cooling pane was opened).
- **[fixed] SN-04 The Cooling pane's second refresh froze the window for the
  whole LHM open** (seconds on a first run): `OpenSourcesOnce` held `_gate`, the
  lock every UI getter takes, across the driver load. The backends open into
  locals under a dedicated `_openGate` (outermost in the lock order) and publish
  under `_gate` in one short block; `ResetSources`/`Shutdown` serialise on it.
  [runtime: open Cooling on a cold start and watch for a stall]
- **[fixed] SN-07 A "Hottest" curve forced the board sweep every tick**, window
  closed, for a value `HottestC` does not read (it is Max(CPU, GPU)).
- **[fixed] SN-08 One duplicate control index threw out of `ToDictionary` and
  took the whole board** down to the read-only fallback. First one wins, logged.
- **[fixed] SN-09 The ITE Super-I/O path proceeded with port I/O when the ISA
  mutex was NOT acquired** ("held" only decided whether to release). It answers
  the failure value now, and a busy bus skips the probe.
- **[fixed] SN-10 The session log was opened `FileShare.Read` per line**: the
  bundle's read failed against a writer mid-append, and the diagnostic exe wrote
  into the app's file. Shared append, shared read, and the diagnostic exe logs to
  `unifiedrgb-diag.log`.
- **[fixed] SN-12 Sensor rules said "PawnIO may not be installed"** for the first
  seconds of every launch and forever on Intel CPUs. The status waits for the
  hub's first read, and the wording follows whether PawnIO is present.
- **[fixed] SN-13 Redaction gaps**: an account name under three characters was
  never replaced (the Wallpaper Engine line prints it); the `hid#vid_...#serial#`
  device-path spelling was not covered. Both are now, and the tests exercise the
  production code rather than a copy of the regex (TS-02).
- **[fixed] SN-14 The LCD's now-playing ellipsis could split a surrogate pair.**

## Tests, CLI and release tooling

- **[fixed] TS-01 A test bound the real OpenRGB port 6742** with its own listener
  while a running app could bridge it. It stands down when `UnifiedRgb.App` is
  running, and a skip is a skip.
- **[fixed] TS-02 The redaction test checked a private copy of the regex.**
- **[fixed] TS-03 Six checks vanished or counted as passes when a fixture was
  absent.** `Harness.Skip` counts and prints them; on CI a skip is a failure.
- **[fixed] TS-04 The hardware-modes fake could not refuse**, so the exit path's
  must-land retry never ran under test. It can now; two checks pin the retry and
  the "did not land" verdict.
- **[fixed] TS-05/06/07/08 New tests**: a show step whose profile was deleted; the
  sequencer's pause/resume timing (observed firing, with a dispatcher pump); the
  SDK server against hand-built packets (the 8-byte single-LED write, a 2 GB
  declared size, an over-declared colour count, device 99, packet 9999, a
  non-protocol client, the 16-client cap); GSI oversize, chunked and unreadable
  bodies.
- **[deferred] TS-09 The LCD designer's drag-is-one-undo-step is tested at the
  stack, not through the view model's gesture API.** Worth a VM-level test; not
  written this pass.
- **[fixed] TS-10..13 CLI**: a typo in a number ended in a stack trace after the
  probes had run (caught, exit 2); `--razer dpi|stages|poll` wrote onboard
  memory on every Razer device including the HyperFlux pad's controller, exit 0
  on FAILED, unknown subcommands fell through (mice only, exit 1 on refusal,
  usage + exit 2); `--razer color` used `Thread.Sleep` (Ctrl+C swallowed);
  unmatched argument shapes printed "Done." having done nothing; `--help`.
- **[fixed] TS-14 Release notes said "Unsigned binary" even when the script had
  just signed and verified it.**
- **[fixed] TS-15 MAINTAINING.md omitted the shim gate, the site stamp and
  signing.**
- **[fixed] TS-16 `release.ps1` embedded whatever shim DLLs were on disk** - a
  `Test-Path` only proved build.bat had run at some point. It runs
  `native/chroma-shim/build.bat` first (the script CI uses), and parse-checks
  clean.

## Deferred, with reasons

| Id | What | Outcome |
|---|---|---|
| VM-10 | 30 Hz preview pull allocates a frame clone and a channel list | **Done.** `ComposedFrame(dev, into, channels)` and `ChannelsFor(dev, into)` fill caller-owned buffers; `CanvasWindow` keeps both. Caller-owned rather than fields, because the support bundle and the SDK host compose frames off the UI thread |
| SV-09b | Bundle preview inherits `LoadJson` side effects | **Done.** `ProfileStore.PeekJson` - no `.corrupt-` copy, no `Unreadable` entry, no retry - used by `DiffProfiles`/`DiffSettings`. Apply still uses `LoadJson`, where the consequences are wanted |
| DV-13 (rest) | Razer/Logitech/Lian Li/Sayo per-frame report buffers | **Done.** Razer frame + apply reports, Logitech effect parms and its read-loop buffer, Lian Li wire bytes, Sayo payload + packet. Each reused under the lock its driver already holds |
| TS-09 | Designer undo through the gesture API | **Done.** A drag of twelve moves is one undo step, and a second drag is its own; verified to fail when the gesture coalescing is removed |
| UX-06 (rest) | The Lighting pane's effect pills do not bubble the wheel | **Done.** Same `WheelPolicy.Bubble` handler as the Cooling and Settings panes; the pills cover most of the top of that page |
| DV-04 (`_tune`) | The Uni hub's `tune` flag used to hot-reload; it needs a rescan now too | **Doc corrected.** The class summary still promised live hot-reload. Behaviour unchanged: a changed file asks for a rescan, which still lands in a second or two |
| NE-02 | Design change: no silence-based claim expiry | **Kept**, confirmed by the owner: a claim ends on disconnect. A client that paints once and idles (Home Assistant, a Stream Deck button) keeps its device |
| SN-01 (readback) | Verify a fan write landed by reading the Control sensor back | **Still open.** Needs hardware to tune the tolerance and the tick delay |
| SN-02 (Gigabyte) | Re-enable vendor control before LHM's first open on a dirty start | **Still open.** Hardware; the marker makes the situation visible meanwhile |

## Needs a runtime check (not exercisable here)

The shell was not elevated and the app needs UAC, so nothing below was seen
running. Before a release, on the rig:

1. Launch, open the Cooling pane on a cold start: no multi-second freeze (SN-04),
   gauges refresh; open Shows, Back: gauges keep refreshing (UX-02).
2. Open Shows, click a device in the left list: the device pane appears (UX-01).
3. Scan again from Settings with Cooling selected: the selection stays (UX-04).
4. Desk on, Rain on the keyboard: falls down the keys, not sideways (NE-04).
5. Audio Bars running, switch the Windows output device: the lights follow within
   a few seconds (NE-05).
6. `unifiedrgb.log`: no `[binding]` lines (OWN-04 would show a broken path).
7. End the process with a board fan on Manual, relaunch: the activity history has
   the "did not shut down cleanly" note (SN-02); Auto/Manual re-asserts land (SN-01).
8. An OpenRGB client (openrgb-python) setting one LED: it lands (NE-01); a client
   holding a colour silently for a minute: it is not undone (NE-02/03).
9. The 5090 is still detected as MSI (DV-10); the PawnIO installer's signature
   check still passes, offline too (DV-03).
10. A GIF background on the pump LCD: no torn frames (SV-07).

## Verification

- `dotnet build UnifiedRgb.slnx -c Debug` and `-c Release`: 0 warnings, 0 errors.
- `dotnet run --project src/UnifiedRgb.Tests`: 2198 passed, 0 failed, 34 suites
  (was 2143; +55 checks). Three existing checks were updated to the new
  behaviour (Apex hand-back via `IHardwareModes`; the sensor-status wording); the
  Backup suite's strict Apply mapping is unchanged.
- Each behaviour change from the follow-up pass was verified to FAIL against the
  code before it: the preview's `.corrupt-` copy, the per-mouse-move undo, and
  the buffer-reuse checks.
