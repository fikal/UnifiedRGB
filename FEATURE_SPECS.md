# UnifiedRGB 1.1 feature specs

Nine features that close the gap with the other cross-vendor tools (OpenRGB,
SignalRGB, Artemis, Aurora). Written for whoever implements them next, in a
fresh session, without re-deriving the codebase: every spec names the real
types, files and methods it touches, and says what "done" means.

Work through the **Implementation order** section below. Tick the tracker at the
bottom as features land. Treat the acceptance criteria as the contract; the
design sections are strong recommendations, not law, and the implementer should
read the referenced code before trusting any line here.

---

## Conventions that apply to every feature

Read these first. Each one was learned the hard way in this codebase.

**Where code goes.** Pure logic (parsers, state machines, geometry, protocol
framing) lives in `src/UnifiedRgb.Core` so the console test harness can reach
it. UI, settings storage and view models live in `src/UnifiedRgb.App`. If a
piece of logic can be tested without a window, it belongs in Core.

**Tests.** `src/UnifiedRgb.Tests` is a zero-dependency console harness:
`dotnet run --project src/UnifiedRgb.Tests` (a plain `dotnet test` runs the
same thing). `Check(bool, name)` and `Equal(expected, actual, name)`; exit code
is the failure count; currently 323 pass. Every feature adds tests for its pure
parts. Fixtures (JSON payloads, wire bytes) go inline as strings or byte
arrays; no test files on disk.

**No NuGet.** The product has a no-NuGet rule and the tests inherit it.
`TcpListener`, `HttpListener`, `System.Text.Json` and the WinRT projections
(the App targets `net10.0-windows10.0.22621.0`) are all in the box.

**Performance is a feature** (see `PERFORMANCE_REVIEW.md` for the history):
- No allocations on a render or poll path. Hoist per-frame constants, use
  `stackalloc` for small scratch, cache geometry in `Geo`.
- Every device write goes through `LightingController.Applier` on the device's
  lane (`LightingController.LaneOf`) and is de-duplicated; never write to a
  device from a UI or network thread directly.
- Pollers are gated: nothing polls sensors, media or batteries unless something
  is consuming the value (`SensorHub.Touch()` is the pattern).
- Effects with live input declare `LiveInput => true` and `Bakeable => false`.

**Threading.** Effect channels run on their own worker threads; the engine
shares one `Stopwatch` clock so channels stay phase-locked. Anything that
touches WPF marshals to the dispatcher. `EffectEngine.Start` stops any channel
overlapping the same LED range of the same device.

**Settings are additive.** Older builds strip fields they don't know from
`settings.json`/`profiles.json`, so new fields must have safe defaults when
absent and must never be required. Files: `settings.json` (`SettingsData` in
`ProfileStore.cs`), `profiles.json`, `hardware.json` (`HardwareConfig`),
`lcd.json` (`LcdDesign`), `scenes.json` (`SceneStore`), `lianli-layout.json`.
`ProfileStore.Save` writes atomically; use it.

**XAML lessons from the last sweep.**
- Base control heights are now uniform (~28px): Button padding `14,5`, TextBox
  padding `6,4`, ComboBox height 28. Don't pin taller.
- Never set `Height` on a TextBox; use `MinHeight`. A fixed height clips the
  glyphs. Buttons and combos may take a fixed height (they center content).
- `WrapPanel` and horizontal `StackPanel` children default to `Stretch` and grow
  to the tallest sibling. Pin `VerticalAlignment="Center"` on mixed rows, or use
  a `Grid` with star columns when a row must never overflow (see the Show step
  rows in `Views/LcdDesignerPane.xaml`).
- No em dashes anywhere in UI copy or docs. Ryan's preference; use a comma,
  colon or full stop.
- `BoolToVis` converter lives in `Themes/Styles.xaml`; slider readouts use
  `local:Sl.Unit` (`%`, `x`, `px`).

**Build / run loop on Ryan's machine.**
```
schtasks //End //TN "UnifiedRgb"; taskkill //IM UnifiedRgb.App.exe //F
dotnet build src/UnifiedRgb.App -c Debug
dotnet run --project src/UnifiedRgb.Tests -c Debug
schtasks //Run //TN "UnifiedRgb"
```
The scheduled task launches the Debug build elevated without a UAC prompt.
Debug-tree builds skip the update check. Log: `%APPDATA%\UnifiedRgb\unifiedrgb.log`.

**Git.** Work on `main` and push to remote `github`. `master` is a frozen
local archive; never push it. Commit per feature (or per coherent step) with
the attribution line the harness gives you. Releases: `.\release.ps1 -Version x.y.z`
on a clean `main` (see `MAINTAINING.md`). Don't cut a release per feature;
1.1.0 ships when the set is done.

**Hardware safety.** White is capped to 60% on selection (`MainViewModel.SoftenWhite`);
keep that. SMBus writes only ever go to detected ENE controllers. Anything
that writes to device memory (feature 4) must be reversible and bounded in time.

---

## Implementation order

Small, on-brand wins first; the marquee feature last, on top of a refactor the
earlier features pay for.

| # | Feature | Est. | Why here |
|---|---------|------|----------|
| 1 | Sensor rules | 1 day | Smallest, most on-brand. Also extracts the automation decision into Core, which 2 and 6 reuse. |
| 2 | Scheduler | 1 day | Same refactor; generalizes Night mode. |
| 3 | Undo/redo in the LCD designer | 1 day | Self-contained; users notice its absence immediately. |
| 4 | Battery for wireless gear | 1 day | Small driver work; plugs into 1 as a sensor source. |
| 5 | Now-playing LCD widget | 1 to 2 days | Follows the `WeatherService` pattern. |
| 6 | Hardware persistence | 2 to 3 days | Per-device protocol work; ship Gigabyte, ENE and Lian Li wired first. |
| 7 | OpenRGB SDK server | 3 to 5 days | We already speak the protocol as a client; this is the inverse plus a control-handoff mode. |
| 8 | CS2 Game State Integration | 2 to 3 days | Independent; a strong demo. |
| 9 | Whole-desk canvas | 1 to 2 weeks | The marquee. Reuses the LCD designer's snap guides and the layout-editor pattern. |

Dependencies: 2 depends on 1's `AutomationDecision` refactor. 4's low-battery
lighting cue is delivered *through* 1 (battery becomes a sensor source). 9
reuses 3's undo stack (`UndoStack<T>`) and the snap-guide helper extracted from
`LcdDesignerPane`. Everything else is independent.

---

## 1. Sensor rules

**Goal.** "When CPU temp is at or above 85°C, apply profile *Alert*; when it
drops back, restore what I had." Artemis's conditions, scoped to what this app
already measures.

**Current state.**
- `Services/AutomationService.cs`: a timer `Tick()` computes a desired
  `(Mode, profile)` with precedence `Locked > Night > App > Base`
  (`enum Mode { Base, App, Night, Locked }`), then `Transition()`. Leaving
  `Base` captures `_returnPoint = _vm.CaptureState()`; returning calls
  `_vm.RestoreState(_returnPoint)` (`MainViewModel.Settings.cs:28-64`,
  `LightState`). `_vm.AutomationStatus` is the live status string.
- App rules: `SettingsData.AutomationRules: List<AutomationRule { Process, Profile }>`
  (`ProfileStore.cs:104`), edited in `AppRulesWindow.xaml(.cs)`, first match wins.
- Sensors: `Core/Sensors/SensorHub.cs` (static): `CpuTempC`, `GpuTempC`,
  `HottestC`, `CpuLoadPct`, `GpuLoadPct`, `BoardTemps[] (Name, TempC)`,
  `BoardFans[] (Name, Rpm)`, `GpuFanRpms[]`. Polling is gated: call
  `SensorHub.Touch()` (or `TouchTemps()`) each tick while any rule is enabled,
  or values go stale. Values are null when PawnIO is missing.

**Design.**
1. Refactor first: extract the decision from `Tick()` into a pure Core type,
   `Core/Automation/AutomationDecision.cs`:
   `static (Mode mode, string? profile, string status) Resolve(in Inputs)` where
   `Inputs` carries every fact Tick currently reads (locked, in-night-window,
   idle seconds, foreground process, rule lists, sensor snapshot). `Tick()`
   becomes: gather inputs, call `Resolve`, transition if changed. This is what
   makes 1 and 2 testable and is worth doing even if nothing else ships.
2. Model in Core (`Core/Automation/SensorRule.cs`):
   ```
   sealed class SensorRule {
     string Source;        // "CpuTemp" | "GpuTemp" | "Hottest" | "CpuLoad" | "GpuLoad"
                           // | "Board:<BoardTemp.Name>" | "Fan:<BoardFan.Name>" | "Battery:<device>"
     bool Above = true;    // trigger when value >= Threshold (false: <=)
     double Threshold;
     double ClearMargin = 3;   // hysteresis: clears at Threshold -/+ ClearMargin
     int HoldSeconds = 5;      // condition must persist this long before firing
     string Profile;
     bool Enabled = true;
   }
   ```
   Plus `SensorRuleEvaluator.Step(rule, value, state, nowSeconds) -> state`
   with `state { bool Active; double? SinceSeconds }`, a pure hysteresis +
   hold state machine. Null value = inactive, with a reason.
3. Precedence: `Locked > Night > Sensor > App > Base`. A thermal alert beats
   "you're in a game"; it does not beat lights deliberately off. Add
   `Mode.Sensor`. First enabled, active rule in list order wins.
4. Storage: `SettingsData.SensorRules: List<SensorRule>?`,
   `SettingsData.SensorRulesEnabled: bool`. Additive.
5. UI: `SensorRulesWindow` modelled on `AppRulesWindow` (same list + add row +
   drag reorder). Add row: Source combo (populated from SensorHub, including
   board sensor names and, after feature 4, `Battery:<device>`), Above/Below
   toggle, threshold `TextBox` with unit label (°C, %, RPM), Profile combo,
   Add. Entry point beside "App rules" in `Views/SettingsPane.xaml`. Status
   through `AutomationStatus`, e.g. `CPU 87°C at or above 85 → profile 'Alert'`
   and `Sensor rules need PawnIO for CPU temperature` when the source is null.
6. Return path is the existing one: when the rule clears, `Resolve` yields
   App or Base and `Transition` restores `_returnPoint`. Do not add a second
   restore mechanism.

**Edge cases.** Sensor null (no PawnIO): rule inactive, status explains.
Profile renamed/deleted: rule shows a warning in the dialog and is skipped.
Two rules on the same source: list order. Rule edited while active: re-evaluate
on next tick; if it no longer matches, normal clear. Flapping around the
threshold: `ClearMargin` + `HoldSeconds` must make it impossible to toggle
faster than once per hold period.

**Acceptance.**
- A rule `CpuTemp >= 40` (easy to hit) applies its profile within two ticks
  of the condition; lowering the threshold's clear point restores the exact
  prior state (effects, static frames, LCD).
- Oscillating 84/86 around an 85 rule with margin 3 does not toggle.
- Lock screen and Night mode still take precedence; unlocking with the sensor
  still hot lands in the sensor profile, not Base.
- Tick allocates nothing new per tick beyond the status string, and only when
  it changes.

**Tests (Core).** `SensorRuleEvaluator` hysteresis/hold table; `Resolve`
precedence with every combination of locked/night/sensor/app; null sensor
yields inactive.

---

## 2. Scheduler

**Goal.** "At 18:00 on weekdays apply *Evening*; 23:00 to 07:00 lights off."
Night mode is one row of this.

**Current state.** `SettingsData.NightMode/NightStart/NightEnd/NightIdleOnly`;
`AutomationService.InNightWindow(s)` handles a window crossing midnight;
`Mode.Night` turns lights off via `_vm.LightsOff()`; `NightIdleOnly` waits for
10 minutes idle (`IdleSeconds()` via `GetLastInputInfo`).

**Design.**
1. Model in Core (`Core/Automation/ScheduleRule.cs`):
   ```
   sealed class ScheduleRule {
     bool Enabled = true;
     int Days = 0x7F;            // bit 0 = Monday ... bit 6 = Sunday
     string Start = "23:00", End = "07:00";   // End before Start = crosses midnight
     ScheduleAction Action;      // LightsOff | Profile
     string? Profile;
     bool IdleOnly;              // only once idle 10 min inside the window
   }
   ```
   `ScheduleRule.IsActive(rule, DateTime now, double idleSeconds)` pure.
   Windows that cross midnight belong to the day they *start*.
2. Migration (one-shot, in `ProfileStore` load): if `NightMode` is true and
   `Schedules` is null, create one `LightsOff` rule from the legacy fields with
   the legacy `IdleOnly`. Keep the legacy fields populated for older builds.
3. Precedence in `Resolve`: `Locked > Schedule(LightsOff) > Sensor >
   Schedule(Profile) > App > Base`. Lights-off schedules keep Night's slot;
   profile schedules sit just above app rules. Add `Mode.Schedule`; retire
   `Mode.Night` in favor of it (status text may still say "Night").
4. The existing "you woke the lights" override (`_nightOverride`) applies per
   lights-off schedule: re-arm when the window ends.
5. UI: `SchedulesWindow`, rows `[M T W T F S S chips][start][end][action][profile][idle only]`.
   Replace the Night mode block in `Views/SettingsPane.xaml` with a "Schedules"
   button and a one-line summary of the next scheduled change.

**Acceptance.** Existing night-mode users see identical behavior after
upgrade. A weekday 18:00 to 20:00 profile schedule applies at 18:00, restores
at 20:00, and does nothing on Saturday. Lock still wins. A window 23:00 to
07:00 is active at 01:00 Tuesday only if Monday is enabled.

**Tests (Core).** Window math across midnight and day masks; legacy
migration; precedence with schedules present.

---

## 3. Undo/redo in the LCD designer

**Goal.** Ctrl+Z / Ctrl+Y in the pump LCD designer, like every other designer.

**Current state.** `ViewModels/LcdDesignerViewModel.cs` edits `LcdDesign`
(`LcdDesign.cs`: `Elements: List<LcdElement>`, `BackgroundImagePath`,
`BgX/BgY/BgW/BgH`, `BgAspectLock`) in place. Drag is in
`Views/LcdDesignerPane.xaml.cs` (`Design_Down/Move/Up`, `_drag.MoveTo`,
snap guides). Saves are debounced 700 ms (`_lcdSave`) and `TouchLcd()` marks
dirty. Shows swap designs with `LoadDesignIntoEditor(clone, fromShow: true)`
and must not count as edits. `SceneStore.Clone(LcdDesign)` deep-copies.

**Design.**
1. `Core/UndoStack.cs`: generic `UndoStack<T>` with `Push(T)`, `Undo(T current) -> T?`,
   `Redo(T current) -> T?`, `CanUndo/CanRedo`, capacity (default 50), redo
   cleared on push. Snapshot-based: T is a serialized `LcdDesign` (JSON via
   the same serializer `ProfileStore` uses), a few KB each.
2. Snapshot at the *start* of each gesture, not the end: `Design_Down` on an
   element or the background, grip drag start, add/remove element, scene load
   by the user, and the first property change of a burst (coalesce property
   edits within 500 ms into one entry so a slider drag is one undo).
3. Undo restores with `LoadDesignIntoEditor(design, fromShow: false)` then
   `TouchLcd()`, then re-selects the element at the same index if it exists.
4. Show-driven swaps (`fromShow: true`) never push.
5. Keys: handle in `LcdDesignerPane` (`PreviewKeyDown`) when focus is inside
   the pane and not in a `TextBox`. Check `Views/KeyPolicy.cs`: the app is
   mouse-first and swallows some keys on combos; make sure Ctrl+Z/Y reach the
   pane. Ctrl+Shift+Z also redoes.
6. UI: two small buttons ↶ ↷ in the Design tab header, disabled when the
   stacks are empty, tooltips "Undo (Ctrl+Z)" / "Redo (Ctrl+Y)".

**Acceptance.** Drag an element, Ctrl+Z, it is exactly back. Five distinct
edits undo one at a time and redo forward. Running a show while editing does
not pollute the stack. A slider drag is one undo step. Memory is bounded (50
snapshots).

**Tests (Core).** `UndoStack<T>`: push/undo/redo, redo cleared on push,
capacity eviction, empty-stack behavior.

---

## 4. Battery for wireless gear

**Goal.** Show battery for wireless devices in the sidebar and let a rule act
on it ("battery at or below 15% → profile *Low battery*").

**Current state.** `Core/Devices/RazerHid.cs` drives the Basilisk V3 Pro
(with the HyperFlux V2 pad): lighting via class `0x0F` cmd `0x03`, DPI stages
and polling rate via class `0x04` (`GetDpi/SetDpiStages/GetPollingRate`),
`Exchange(hid, report)` request/response. `LogitechG403` is HID++ 2.0 but the
G403 HERO is wired.

**Design.**
1. Core interface: `IBatteryDevice { (int Percent, bool Charging)? ReadBattery(); }`.
2. `RazerHid`: implement with the standard Razer battery commands documented
   in the openrazer driver (GPLv2): class `0x07`, cmd `0x80` returns level in
   `args[1]` (0..255, scale to percent), cmd `0x84` returns charging in
   `args[1]`. **Verify the exact bytes against openrazer's `razermouse_driver.c`
   for the Basilisk V3 Pro before trusting this paragraph.** Wireless mice may
   answer through the dongle/pad transaction id; reuse the `_tid` logic already
   used for DPI.
3. Polling: a `BatteryMonitor` in the App polls every 60 s on the device's
   applier lane (never per frame), only while the device is present, and
   publishes to the `LeftItem.Subtitle` ("Mouse • 2 LEDs • 74%" or
   "charging") and to `SensorHub` as a source `Battery:<device name>` so
   feature 1's rules can use it. Below 15%: subtitle turns amber.
4. Logitech: leave a `TODO` implementing HID++ 2.0 feature `0x1004`
   (UNIFIED_BATTERY) / `0x1000` (BATTERY_STATUS) for a future wireless device;
   no hardware to test against today.

**Acceptance.** Percentage within ~5 points of Synapse; charging flips when
docked; polling adds no measurable CPU; wired devices show nothing.

**Tests (Core).** Battery report decode from fixture bytes (level scaling,
charging flag, malformed reply returns null).

---

## 5. Now-playing LCD widget

**Goal.** "Artist · Title" and album art on the pump LCD, from whatever is
playing (Spotify, a browser, Apple Music).

**Current state.** `LcdDesign.cs`: `enum LcdElementKind { Time, Date, CpuTemp,
Text, GpuTemp, FanRpm, NetSpeed, AnalogClock, Weather }`; `LcdElement` is a
positioned text element (`Kind, Text, X, Y, FontSize, ColorHex, Bold`).
`LcdController.cs:151-155` turns a kind into its text each frame
(`LcdElementKind.Weather => WeatherText()`). `LcdWidgets.cs` has the pattern
to copy: `WeatherService` is static, `EnsureStarted()`, a background refresh,
`volatile string _current`, `Current` read per frame with no allocation.
Designer add buttons are `RelayCommand`s in `LcdDesignerViewModel` (`AddElement(kind)`),
labels in `LcdDesign.cs:54-59`. Frames render at ~78 ms.

**Design.**
1. `MediaService` (App, next to `WeatherService`) over WinRT
   `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`:
   `RequestAsync()`, `GetCurrentSession()`, `TryGetMediaPropertiesAsync()`
   (Title, Artist, AlbumTitle, Thumbnail), `GetPlaybackInfo().PlaybackStatus`.
   Event-driven: subscribe to `CurrentSessionChanged`, `MediaPropertiesChanged`,
   `PlaybackInfoChanged`; no polling. Publish `volatile` snapshot: `Line`
   ("Artist · Title", no em dash), `IsPlaying`, and a cached thumbnail as a
   frozen `BitmapSource` re-decoded only when the thumbnail stream changes.
   `EnsureStarted()` on first use; if the WinRT call throws (old Windows
   builds), log once and stay empty.
2. New kinds: `NowPlaying` (text; renders `Line`, empty when nothing plays or
   paused for more than 30 s) and `AlbumArt` (image; `FontSize` doubles as the
   height in px, width from aspect). Add labels, add-buttons ("+ Now playing",
   "+ Album art"), and the render cases in `LcdController`.
3. Long titles: truncate with an ellipsis to the element's available width
   first. A marquee is optional later and, if done, must precompute the
   scrolled strings rather than allocate per frame.

**Acceptance.** Play a track: the LCD updates within a second of a change.
Pause: text dims or clears after 30 s. Album art renders at the element's
size. Idle CPU unchanged (events only). No media session: elements are
empty, no errors in the log after the first.

**Tests (Core, if the text helper lives there).** Line composition and
ellipsis truncation.

---

## 6. Hardware persistence

**Goal.** A per-device "when UnifiedRGB isn't running, show…" setting, so
closing the app leaves a chosen static color or onboard effect instead of
whatever the last frame was (or the firmware's boot rainbow).

**Current state.** Everything streams. On exit, `MainWindow.Window_Closing`
runs the save prompt, then shutdown (`App.xaml.cs:75` stops `SensorHub`);
`MainViewModel.cs:1508` already implements one exit behavior,
`LianHandoffOnExit`, for the wireless fans (hand them to the sync wire).
Device facts:
- **Gigabyte IT5711** (`GigabyteIt5711.cs`): has an onboard effect engine we
  already use. `ZoneKind.Static` zones set a per-zone static via effect index
  (`HeaderEffectIdx`), `Cc(0x32, mask)` disables onboard effects per header,
  `ApplyEffect()` = `Cc(0x28, 0xFF, …)` commits. A hardware static color is
  therefore: set static colors on all zones, clear the disabled mask, apply.
- **ENE DRAM** (`EneDram.cs`): direct mode is `REG_DIRECT 0x8020 = 1` +
  `REG_APPLY 0x80A0`. Firmware effect mode is `0x8020 = 0` with a mode
  register (`0x8021`) and effect color registers. OpenRGB's `ENESMBusController`
  (GPLv2, same license) documents: mode static = 1, breathing = 2, flashing = 3,
  spectrum cycle = 4, rainbow = 5, effect colors at `0x8160`. **Verify against
  that source before writing.**
- **Corsair Strafe MK.2** (`CorsairStrafeMk2.cs:244-257`): `RunInit` sends
  `0x07 0x05 0x02 … 0x03` ("LightingControl, software mode"). Returning the
  keyboard to hardware playback is the counterpart command with the hardware
  value; the keyboard then plays its onboard profile. **Investigate the exact
  byte from OpenRGB's Corsair peripheral controller or a capture.**
- **Lian Li wired hub** (`LianLiUniHub.cs`): L-Connect sets hardware effects
  by index; the decompile notes are in memory ("effect_index read-back").
  Static color as a hardware effect is the first target.
- **Razer, Logitech, MSI GPU, Thermalright LCD**: leave as "keeps last
  colors" for 1.1; note candidates in the code.

**Design.**
1. Core interface:
   ```
   interface IHardwareModes {
     IReadOnlyList<string> HardwareEffects { get; }   // device-specific names, may be empty
     void SetHardwareStatic(Rgb color);
     void SetHardwareEffect(string name, Rgb? color);
     void ReturnToHardware();                          // "play your onboard profile"
   }
   ```
   Implemented by Gigabyte, ENE, Lian Li wired (and later Strafe).
2. Storage in `hardware.json`: `ExitBehaviors: Dictionary<string, ExitBehavior>`
   keyed by device name; `ExitBehavior { Mode: KeepLast | Static | Effect |
   Off | ReturnToHardware, ColorHex, Effect }`. Default `KeepLast` (today's
   behavior, no command sent).
3. Apply on exit, after `LightingController.StopAndDrain()` and before
   dispose, bounded: at most ~2 s total, because the logoff path
   (`WM_ENDSESSION`, see `MainWindow.xaml.cs:194`) is time-limited. Log what
   was applied.
4. UI: in each device's panel header a small "When the app is closed…"
   affordance (or one section in Settings listing devices that implement the
   interface). Options come from the interface; devices without it show
   "keeps its last colors". An "Apply now" button sends it immediately so the
   user can see the result, then the app resumes control on the next write.

**Acceptance.** Gigabyte set to static blue: close the app, board and headers
stay blue; reopen, the app takes over normally. ENE static holds after exit.
Lian Li wired static holds. Unsupported devices unchanged. Exit never takes
longer than 2 s extra.

**Tests (Core).** `ExitBehavior` JSON round-trip; Gigabyte static command
bytes match the existing static-zone path; ENE register sequence for static
mode.

---

## 7. OpenRGB SDK server

**Goal.** Let other software control UnifiedRGB over OpenRGB's network
protocol, so Home Assistant integrations, game mods, phone remotes and
Stream Deck plugins written for OpenRGB work unmodified.

**Current state.** `Core/Net/OpenRgbClient.cs` is a full *client*: packets
`0` controller count, `1` controller data, `40` protocol version, `50` client
name, `100` device list updated, `1050` update LEDs, `1051` update zone LEDs,
`1100` set custom mode; `OurProtocolVersion = 1`; `ParseDevice(index, bytes)`
decodes the controller blob into `DeviceInfo(Index, Type, Name, Vendor,
Description, Version, Serial, Location, Zones, LedCount, Colors)` with
`ZoneInfo(Name, Type, LedCount, MatrixW, MatrixH, Matrix)`. The bundled
OpenRGB server (`OpenRgbManager`, port `6742`) runs when the bridge is on.
`LightingController` (`Services/LightingController.cs`): `FrameFor(dev)`,
`ComposedFrame(dev)`, `PushFrame`, `PushZone`, `Applier`, `LaneOf`.

**Design.**
1. `Core/Net/OpenRgbServer.cs`: `TcpListener` bound to `127.0.0.1` by default.
   Port: `6742` when the OpenRGB bridge is off (so ecosystem tools find us at
   the default), `6743` when the bridge owns `6742`. Settings shows the port
   in use. One thread per client; frame the same `ORGB` header the client
   writes.
2. Handle: `40` (reply with `min(client, ours)`), `50` (record name for the
   status line), `0` (count), `1` (serialize each `IRgbDevice`: name, vendor,
   `DeviceType` → OpenRGB `device_type`, zones from `Zones` with type LINEAR,
   or MATRIX with a map when `LedPositions` form a grid, a single mode named
   "Direct" with per-LED color flag, colors = `ComposedFrame`), `1050`/`1051`
   (writes), `1100` (ack, no-op), `1000`/`1001` (zone resize: reject), and
   push `100` to all clients after a rescan. OpenRGB's enums, from
   `RGBController.h` (GPLv2): device types MOTHERBOARD 0, DRAM 1, GPU 2,
   COOLER 3, LEDSTRIP 4, KEYBOARD 5, MOUSE 6, MOUSEMAT 7, …, UNKNOWN 19; zone
   types SINGLE 0, LINEAR 1, MATRIX 2; mode flag `HAS_PER_LED_COLOR = 1 << 5`,
   color mode `MODE_COLORS_PER_LED = 1`. **Verify against the header.**
3. Serialization must be the exact inverse of `ParseDevice`. Write
   `WriteDevice` and round-trip it through the existing parser in tests.
4. Control handoff (the part that is ours, not OpenRGB's): an external write
   to a device makes that client the device's owner. Add to
   `LightingController`: `BeginExternal(dev)` (captures the device's state
   via the `CaptureState`/`RestoreState` machinery, stops our channels on the
   device), `PushExternal(dev, frame)` (through the applier lane, de-duped,
   master brightness applied), `EndExternal(dev)` (restores). End on client
   disconnect or after 5 s of silence. Never touch WPF from the socket thread.
5. Security: loopback only unless the user enables "Listen on LAN", with the
   warning that the protocol has no authentication and anyone on the network
   can set your lights.
6. UI (`Views/SettingsPane.xaml`): "Allow other apps to control lighting
   (OpenRGB SDK)" toggle, port in use, "Listen on LAN" checkbox, status
   "0 clients" / client names.

**Acceptance.** OpenRGB's own GUI in client mode connects to `127.0.0.1` and
lists our devices with correct LED counts, and setting a color there changes
the hardware. `openrgb-python` sets a color. Disconnecting the client restores
the user's prior lighting within 5 s. Bridge on: server on `6743`, shown in
Settings. Idle cost: a listening socket and nothing else.

**Tests (Core).** Header encode/decode; `WriteDevice` → `ParseDevice`
round-trip for devices with several zones; version negotiation; `DeviceType`
mapping table; handoff state machine (own / release on silence) as a pure
class with an injected clock.

---

## 8. CS2 Game State Integration

**Goal.** Real in-game state drives the lights, the legitimate way: Valve's
Game State Integration, where the game POSTs JSON to a local URL you register
with a config file. No memory reading, no fake vendor registry keys.

**Facts to verify against Valve's GSI documentation (developer.valvesoftware.com,
"Counter-Strike: Global Offensive Game State Integration").** Config file
`gamestate_integration_unifiedrgb.cfg` in the game's `csgo/cfg` folder
(CS2: `<library>/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/`)
containing `uri`, `timeout`, `buffer`, `throttle`, `heartbeat`, an `auth`
token and a `data` block selecting sections (`provider`, `map`, `round`,
`player_id`, `player_state`, `player_weapons`, `player_match_stats`). Steam
path from `HKCU\Software\Valve\Steam\SteamPath`; libraries from
`steamapps/libraryfolders.vdf`; CS2 is app `730`, Dota 2 is `570`.

**Design.**
1. `Core/Games/GsiServer.cs`: `HttpListener` on `127.0.0.1:<port>` (default
   27180, configurable; step to the next port if taken). Reject POSTs whose
   `auth.token` does not match ours. Parse with `System.Text.Json` into a
   `GameState` snapshot (health, armor, ammo clip/reserve, money, round phase,
   bomb state and plant time, flashed, burning, team, round kills, map phase).
   Publish as a volatile record; record the last heartbeat time.
2. `Cs2Effect : IEffect` (`LiveInput => true`, `Bakeable => false`, in a new
   "Game" category): health as a green→red gradient; low ammo pulse; bomb
   planted → red pulse tightening over 40 s; flashed → white flash; round end
   → team color flash; no heartbeat for 10 s → a quiet idle color. Reads the
   snapshot only; no allocation per frame.
3. Setup UX in Settings → "Games": CS2 toggle; "Install CS2 config" writes the
   cfg into every detected library that has app 730 and shows the path; status
   "waiting for game" / "connected, heartbeat 2 s ago". If writing fails,
   show the exact path and the file contents to copy by hand.
4. Dota 2 as a stretch with the same server and a second effect.

**Acceptance.** With CS2 running and the cfg installed, taking damage changes
the lights within ~200 ms; a bomb plant starts the pulse; closing the game
returns the effect to idle after the heartbeat timeout; the installed cfg is
accepted by the game (visible in its console as GSI connecting).

**Tests (Core).** Payload parsing from representative GSI JSON fixtures
(freezetime, live with bomb planted, player dead, spectating); auth
rejection; bomb timer state machine.

---

## 9. Whole-desk canvas

**Goal.** Place devices on a 2D surface that represents the desk and render
effects across it as one image: a wave rolls off the keyboard, across the
mouse, up the case fans, in order. Also lets a user draw where an unusual
device's LEDs physically are (the GPU ribbon problem, generalized).

**Current state.** `EffectEngine.Channel.Pos = ZonePositions(dev, offset, count)`
(`EffectEngine.cs:338`): per-zone coordinates normalized to 0..1 from the
device's `LedPositions` (2D where the driver knows the layout: Strafe key grid,
Gigabyte ring or strip via `BuildPositions`) or a 1D spread by index. Effects
consume only `pos` through `Geo` caches (`Diag/Angle/Radius/YRange/IsFlat`),
so nothing in the 58 effects knows about devices. All channels share one
clock. `ApplyModeToAll` starts the same effect on every device.
`LianLayoutWindow` is the existing small layout editor (drag-to-reorder,
saves `lianli-layout.json`). `Views/LcdDesignerPane.xaml.cs` has snap-to-align
guides (`SnapToGuides`, `Nearest`, `BoundsOf`, `GuideLayer`).

**Design.**
1. Layout store `canvas.json` (Core model `CanvasLayout`):
   ```
   { enabled, width: 1600, height: 900,
     items: [ { device: "<Name>", x, y, w, h, rotation: 0|90|180|270, flipX, flipY,
                ledLayout?: { shape: "strip"|"ring"|"grid", cols, rows, serpentine } } ] }
   ```
   Devices missing from the file are auto-placed by type (keyboard bottom
   center, mouse to its right, motherboard/GPU/DRAM/fans stacked to the side)
   so enabling the canvas is never blank.
2. Coordinate mapping in Core (`Core/Effects/CanvasMapper.cs`):
   `Map(localPos, item, canvas) -> LedPos` applies flip, rotation, then
   `((x + local.X * w) / width, (y + local.Y * h) / height)`. Computed once
   per channel at `Start`, never per frame. Because effects read only `pos`,
   every existing effect becomes desk-wide with zero effect changes; `Geo`
   caches key on the array instance, so a new array is a new cache.
3. `ledLayout` override: generates `LedPositions` for devices whose driver
   fallback is wrong (a 30-LED strip on an ARGB header, a ring, a serpentine
   grid). Applied inside `ZonePositions` when present. This replaces the
   Strip/Ring special-casing idea for Gigabyte headers with something general.
4. Target scope: add "Whole desk" beside "Entire device" and zone targets.
   Whole desk = `ApplyModeToAll` with canvas mapping on; persists via a new
   `EffectAssignment.Canvas: bool` on each channel in `profiles.json`
   (additive; older builds ignore it). Shared clock keeps devices phase-locked.
5. Editor `CanvasWindow`: a scaled 2D surface; each device is a rectangle
   with its LED dots drawn from `LedPositions`/`LedGeometry`, live-tinted from
   `LightingController.ComposedFrame`. Drag to move, corner grip to resize,
   rotate and flip buttons, auto-arrange, and snap guides: **extract the
   guide code from `LcdDesignerPane` into a shared helper** (`SnapGuides`)
   and use it in both. Undo via `UndoStack<T>` from feature 3.
6. Off switch: with `enabled = false`, behavior is byte-identical to today.

**Edge cases.** Device renamed or missing: item ignored, new devices
auto-placed. A device with zone effects and a desk effect: the desk effect
replaces overlapping channels (existing `StopRange` semantics). Preview
panels (`LedPreview`) should show device-local coordinates as today; the
canvas editor shows the desk view.

**Acceptance.** Canvas on, keyboard placed left of the fans, Rainbow Wave on
Whole desk: the wave visibly continues from the keyboard into the fans
without restarting. Canvas off: per-device behavior identical to today.
Profiles save and reload desk assignments. Editor drags snap and show guides.
An ARGB strip given `strip 30x1` runs Matrix down its length correctly.
No new per-frame allocation.

**Tests (Core).** `CanvasMapper.Map` for every rotation/flip against known
points; `ledLayout` generators (strip, ring, serpentine grid) produce the
expected coordinates and counts; auto-arrange is deterministic; JSON
round-trip with missing/unknown devices.

---

## When the set is done

- Update `README.md` and `docs/index.html`: the feature tour, and the
  comparison table gains rows for third-party control (OpenRGB SDK API) and
  game state integration; hardware persistence and the canvas change several
  existing cells. Keep the "Where the others win" section honest; some items
  there stop being true.
- Release with `.\release.ps1 -Version 1.1.0`; the script stamps the site.
- Record anything durable (protocol facts, gotchas) in the session memory.

## Tracker

- [x] 1. Sensor rules (+ `AutomationDecision.Resolve` extracted to Core)
- [x] 2. Scheduler (Night mode migrated)
- [x] 3. Undo/redo in the LCD designer (`UndoStack<T>`)
- [x] 4. Battery for wireless gear (Razer; sensor source for rules)
      NOT verified against hardware: no Razer device has been attached to this
      machine since the spec was written, so the percentage has never been
      compared with Synapse and charging has never been seen to flip. The bytes
      match openrazer and the decode is covered by fixture tests. Plug the
      HyperFlux pad back in and confirm before 1.1.0 ships.
- [x] 5. Now-playing LCD widget (+ album art)
- [x] 6. Hardware persistence (Gigabyte, ENE, Lian Li wired, Strafe handback)
      Commands verified against primary sources and covered by tests, but the
      visual hold is NOT confirmed: the app runs elevated and a non-elevated
      shell cannot close it gracefully, so the exit path has never been watched
      on real hardware. Use Settings > When the app is closed > Apply now, then
      Exit from the tray, and check the rig before 1.1.0 ships.
      Note: the spec's ENE effect-colour register (0x8160) was WRONG. It is
      0x8010, 15 bytes, and REG_DIRECT/REG_MODE sit immediately after it.
- [x] 7. OpenRGB SDK server (+ control handoff)
      Verified against the live app: an external client listed all 7 devices with
      correct types, LED counts and zones, and a write claimed a device and was
      released on disconnect. Not yet tried with OpenRGB's own GUI (its exe is
      GUI-subsystem, so a CLI run prints nothing to a pipe).
      Note: the spec's UNKNOWN device type was 19; it is 21 in current OpenRGB.
      Protocol v0 also has NO vendor field, so the blob omits it when a client
      negotiates down, or every string after the name shifts by one.
- [x] 8. CS2 Game State Integration
      Verified as far as it can be without playing: Steam detection, both
      libraries, the config written to the real CS2 folders and read back,
      and POSTs to the live app accepted (valid token) and rejected (forged).
      NOT played: nobody has watched the lights react to real damage.
- [x] 9. Whole-desk canvas (+ LED layout overrides, shared snap guides)
      Auto-arrange verified against the real rig: all 7 devices placed sensibly
      and inside the desk. The mapping, the LED shape generators and the
      continuity claim (a wave crossing two devices instead of restarting) are
      covered by tests. NOT seen: nobody has watched a wave roll off the
      keyboard onto the fans on real hardware.
- [x] Website/README updated
- [ ] 1.1.0 released  (deliberately NOT done: the hardware-facing parts of
      features 4, 6, 8 and 9 have not been watched on real hardware yet)
