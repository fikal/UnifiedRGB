# UnifiedRGB — Performance & Refactoring Review

**Date:** 2026-08-31 · **Scope:** full solution (~22k lines) · **Method:** manual review of every file in
Core/Effects+Engine, Core/Devices+Native+Sensors+Audio+Net+Input, and the App project.
Every claim carries a `file:line` reference verified against the code on this date (line numbers will
drift as fixes land — search for the symbol if a line moved).

**Goal: fix ALL of it.** Check items off as they ship. Batches are ordered by real-world payoff ÷ risk.

Legend: 🔴 major (measurable 24/7 cost or real leak) · 🟠 significant · 🟡 worthwhile · 🧹 cleanup

---

## Fix-session record — 2026-08-31

**Shipped:** all of Batch 1 · Batch 2 (except B2.10/B2.13-event, deferred with reasons) · Batch 3's
B3.2 + B3.6-lite + B3.8 · the high-frequency half of Batch 4.
**Verified after every checkpoint:** clean build (App + CLI), **79/79 codec round-trip tests**, app
launch with all devices detected (Strafe, Gigabyte, G403, MSI 5090, UniHub, 2× ENE DRAM, LCD),
zero WARN/ERR in the session log.
**MEASURED BEFORE/AFTER — 2026-08-31.** Controlled A/B: published 1.0.16 exe vs today's build, same
machine, same settings, same "All White" static startup profile, both tray-hidden (`--autostart`),
60 s of `dotnet-counters` (System.Runtime) each, elevated attach:

| Metric (static state, 60 s)   | BEFORE 1.0.16 | AFTER today | Δ |
|---|---|---|---|
| CPU (share of one core)       | 5.35 %  | 3.97 %  | **−26 %** |
| Allocation rate               | 6.23 MB/s | **0.04 MB/s** | **−99.4 % (156×)** |
| Gen 0 collections             | 5       | 0       | −100 % |
| **Gen 2 collections**         | **20**  | **2**   | **−90 %** |
| LOH size                      | 6.6 MB  | 1.2 MB  | −82 % |
| Working set                   | 100 MB  | 51 MB   | **−49 %** |

The old build ran a **full Gen 2 GC every 3 seconds** at idle (the LCD/compressor LOH churn);
the new build allocates ~40 KB/s and its working set halved. Remaining static-state CPU is the
LCD's keep-warm streaming + USB stack. Worst-case fully-animated state (Confetti on all devices,
every LED changes every frame): 62% of one core, 35 threads — device-I/O-bound, further reducible
only by B2.10-style bus batching. Raw CSVs + harness: session scratchpad `ab-test\`.
**Behavioral notes:** LCD ping-pong can in theory tear one frame if a USB send outlives two render
ticks (cosmetic-only, accepted) · ChromaFeed keeps no idle-stop by design (passive pipe listener) ·
Strafe/Lian protocol sleeps kept (hardware drops packets without them).

**Visual sweep — 2026-08-31, driven by Claude via an elevated UIA driver + screenshots:** startup
profile restored correctly (Rain, cyan, pill highlighted) · Rain animating in the rewritten
LedPreview (distinct frames) · UniHub: Rainbow Wave animated on the fan rings; Confetti applied via
the All-effects popup with the palette-first panel laid out as designed; Palette Library opened,
Ocean card applied (strip + fan sparkles switched to the 4 blues) · Cooling: CPU 49°/4%/1.37V +
GPU gauges live (UI-gated sensor reads resume on Touch), CPU Fan 2,700 RPM on High curve,
GPU fans "stopped (fan-stop)", **SL-Infinity 1,125 RPM appeared = the gated tach poll wakes when
observed**, no phantom System Fan 4 · LCD designer WYSIWYG live (temp matches Cooling; + Net /
+ Clock / + Weather buttons present) · session log: ZERO warnings/errors through the whole sweep.

---

## Master fix checklist

### Batch 1 — memory churn + always-on waste (high yield, low risk) — ✅ SHIPPED 2026-08-31, build+79 tests+launch verified
- [x] **B1.1** LCD write path: double-buffer frames, cache the blank, one pinned chunk buffer → §MEM-1
      *(ThermalrightLcd: pinned `_payload`/`_report` reused, chunks written by offset, `lock(_report)` vs handshake; LcdController: `_outA/_outB` ping-pong + `_blank`. Note: ping-pong can in theory tear one frame if a USB send outlives two render ticks — cosmetic-only on this panel, accepted.)*
- [x] **B1.2** LianLiTinyuz: cache/downsize the LZ hash table; kill `UnpackLen` List allocs → §MEM-2
      *(thread-static `_headCache` 1<<13 / grow-only `_prevCache` / pooled ev+out lists; UnpackLen → stackalloc. Wire format unchanged — 79 round-trip tests pass.)*
- [x] **B1.3** EffectEngine: frame dedup at the write boundary + ~1s keepalive → §CPU-1
      *(SameFrame compare of scaled output, KeepaliveMs=1000; also added the missing sleep floor and bumped the baked-Lian no-op nap 120→400ms.)*
- [x] **B1.4** LianLiUniHub: add frame dedup; reuse the 3 packet buffers → §CPU-2 *(dedup while mapping into `_chan`, Flush only on change — hub latches; `_pktStart/Col/Commit` reused under `_lock`.)*
- [x] **B1.5** LianLiUniHub: gate the 1.5s timer on a consumer; stop the per-tick `File.ReadAllText` → §CPU-3 *(GroupRpm() marks observed; poll only within 10s of a read; cfg hot-reload via `GetLastWriteTimeUtc` stamp.)*
- [x] **B1.6** Scenes: fix `HookAction` double-subscribe (N× scenes.json writes) → §LEAK-2 *(named handler, remove-before-add.)*
- [x] **B1.7** AppRulesWindow: unsubscribe `CollectionChanged` (leaks a window per open) → §LEAK-1 *(named handler unhooked on Closed. New finding: `RefreshApps` already disposes its Process objects — no fix needed there.)*
- [x] **B1.8** SensorHub.ResetSources: dispose `_cpu` (leaked PawnIO **kernel handle**) → §LEAK-3
- [x] **B1.9** `StartWithWindows`: cache off-thread; never run `schtasks` on the UI thread → §START-2 *(lazy off-thread refresh + optimistic set.)*
- [x] **B1.10** WallpaperCapture: only re-find the WE window when frames stop → §MEM-4 *(healthy-session gate: `IsWindow` + frame-recency + 3s session grace; also shipped §LEAK-5 here — `_winrtDevice` created once, disposed in DisposeDevice.)*
- [x] **B1.11** LcdController: ~2fps blank stream when off; cached blank; no per-tick `File.Exists`; cached DrawingVisual + frozen no-bg brush → §CPU-6

### Batch 2 — locks, stalls, pollers — ✅ SHIPPED 2026-08-31 (except the two flagged), build+tests+launch verified
- [x] **B2.1** LianLiWireless: RF pacing wait moved OUTSIDE `_lock` (`PaceOutsideLock` before SetColors/SetZone; Transmit re-checks under lock, residual ~0); pooled `_rfPkt` fragment buffer. *(The 4×20ms meta-packet spacing and the 30ms standalone double-send are L-Connect protocol timing — kept. `FollowPwmLine`'s exit-time hold documented, left as-is: shutdown-only.)*
- [x] **B2.2** HID write timeout: settable `WriteTimeoutMs`, default **400ms** (was fixed 2000) → worst-case Strafe convoy 24s → ~5s.
- [x] **B2.3** SensorHub tick split: control-essential (CPU/GPU temp + curve apply + failsafe) always; GPU RPM/load/voltage, CpuLoad, BoardFans/CpuVoltage projections and spin-tracking now gated on a recent `Touch()`; board sweep also runs when a curve sources Hottest.
- [x] **B2.4** LedPreview rewritten: 30Hz DispatcherTimer gated on `IsVisibleChanged` (no more `CompositionTarget.Rendering` root), redraw only when sampled colors changed, shared frozen brush cache per color (capped 4096), keycap/exact/dot/fan geometry cached by (positions, style, size) hash. *(VM-side `CurrentTargetView` still allocates per pull — remaining item, Batch 4 note.)*
- [x] **B2.5** Cooling timer: starts on panel entry, stops on leave/self-stops under Settings, restarts when Settings closes with Cooling selected; stopped in Dispose.
- [x] **B2.6** Strafe: pooled `_rCh/_gCh/_bCh` + `_pktBuf`, `_last` reused with index-loop dedup (no more enumerator boxing / ToArray per frame). *(Protocol settle sleeps kept — the keyboard drops packets without them.)*
- [x] **B2.7** ChromaRestServer: `Stop()` added (called from VM Dispose — :54235 released), requests handled on the thread pool, per-thread reusable body buffer + UTF-8 byte parse (no per-frame body string), pre-encoded constant responses, `Response.Abort()` on failure.
- [x] **B2.8** ChromaFeed: head/body buffers reused across messages/connections. **Judgment call:** NO idle-stop — unlike the capture threads this is a *passive listener* (zero CPU while unconnected, `WaitForConnection` blocks); stopping it would break hosts that connect later. Review item amended.
- [x] **B2.9** OpenRgb: timed-out connect disposes the `TcpClient`; conflict-policy enumerates processes ONCE with disposal; client sends are single-write (pooled header+payload buffer, explicit alpha byte).
- [x] **B2.10** EneDram SMBus batching — **SHIPPED + HARDWARE-VERIFIED 2026-08-31** (Ryan watched the sticks: "flowing"). Full-stick 24-byte block writes (select + block = **2 bus transactions/frame instead of 16**, each of which took the machine-wide SMBus mutex + a kernel ioctl). Self-healing: `TryBlock` has no byte fallback, and the first host rejection permanently reverts that stick to the proven 3-byte chunks and repaints the same frame — zero rejections logged across hundreds of animated frames. Also: wire buffer reused (was per-frame), dedup de-boxed to an index loop, `_last` refilled in place.
- [x] **B2.11** WallpaperCapture WinRT device wrapper: created once, reused across sessions, disposed in `DisposeDevice` (was leaked per WE restart). *(Shipped with B1.10.)*
- [x] **B2.12** GigabyteIt5711: per-zone fan-frame dedup (static ARGB header = 0 writes now), wire-order resolved once per stream call (was a string switch per LED per frame), slice copy removed (streams straight from the caller's list).
- [x] **B2.13** Wasapi — **FULLY SHIPPED + LIVE-VERIFIED 2026-08-31** (music playing, Audio Bars reacting). Event-driven capture: `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` + `SetEventHandle`, loop waits on the event with a 30 ms floor; any endpoint refusal rebuilds a fresh client on the proven polling path (log records the mode — this rig: `event-driven`). Engine now sleeps until the sound card signals instead of 100 polls/sec. **Bonus from live testing:** the analyzer's attack/decay was per-Analyze-call (cadence-dependent, read as strobing) — now TIME-BASED (~15 ms rise / ~160 ms fall / 14 s AGC), identical feel on every sample rate; Ryan tuned final reactivity with the Punch slider. COM RCWs released (earlier pass).
- [x] **B2.14** KeyboardTap: hook callback uses `Monitor.TryEnter(2ms)` — it can never stall the system input path past 2ms (drops one lighting event on contention instead of risking the silent OS unhook). Chosen over a lock-free ring for auditability.
- [x] **B2.15** `LianSpeedScale` + `MasterBrightness` saves debounced 700ms (`SaveSettingsDebounced`, flushed on Dispose). Also: `_coolingTimer`/`_lcdSave`/`_lianRebakeTimer` stopped in Dispose. **Bonus:** GigabyteEcio busy-wait now backs off (Yield → 1ms sleep) → §LOCK-6 done.

### Batch 3 — structural refactors — ✅ SHIPPED 2026-08-31 (second pass) except the two flagged
- [x] **B3.1** MainViewModel split into partials — main file **3,535 → 1,411 lines** + seven focused partials: `.Support` (update/upload/admin), `.Cooling`, `.Settings` (automation/night/PawnIO/OpenRGB/disable), `.Lcd` (designer), `.Scenes`, `.Lian` (bake + header config + fan editor), `.Profiles`. Mechanical move, compiler-verified; a couple of members sit in an adjacent partial where the original file interleaved them (noted in file headers). Step 2 (extracting real services) remains optional future work.
- [x] **B3.2** One `ResolveEffect(fx, choice)` + one `LoadPalette(fx, hex)` — all three resolution copies deleted.
- [x] **B3.3-lite** `HidNative.OpenFirst(tag, vid, pid, pick, fallbackPick)` — THE shared find+open path, adopted by all six HID drivers (Strafe, Gigabyte, Sayo, Apex, Thermalright LCD, UniHub). Fixes the inconsistency where four drivers let a transient open failure propagate and silently vanish the device for the session. *(Full `HidRgbDevice` base with shared dedup/buffers: the concrete bugs it would have prevented are already fixed directly; declared done at this level.)*
- [x] **B3.4** Effects consolidation: `Fx.Loop` replaces the loop-period formula at **35 sites**; `Stack44.Frac` + PatternEffect's `Frac`/`Lerp` now forward to `Fx`; **`ColorGrid`** merges the AmbientScreen/WallpaperCapture grid cores WITH ping-pong publishing (their per-tick `Rgb[576]` allocs gone). *(Meteor-kernel×8 unification skipped deliberately — each variant has intentional tail/core differences and the shared-helper version reads worse; ChromaFeed keeps its own grid: different lifecycle.)*
- [x] **B3.5** Per-channel geometry cache — **SHIPPED 2026-08-31** with zero IEffect API change: `Geo` keys a `ConditionalWeakTable` on the channel's stable `LedPos[]` (entries die with the channel; thread-safe). Caches diagonal, center angle, radius, and Y-range. Adopted at **14 diagonal render loops** (script-verified) plus Spiral (Atan2+Sqrt gone), Taichi (Atan2 gone), and AudioBars (per-frame min/max scan gone). Benefits streaming AND the bake (N frames share one build). *(Scope note: PatternEffect's ring coordinate, DoubleArc, and LianStack Orbit's fallback keep live trig — conditional geometry where the cache shape doesn't fit cleanly; costs are minor.)*
- [x] **B3.6-lite** Engine adaptive render rate (10fps idle loop, 60fps on change) — shipped in pass 1.
- [x] **B3.7** Async startup + lazy pane build — **CLOSED: decided won't-do.** Moving `DetectAll` off-thread reorders init guarantees the app depends on (startup profile apply, wizard device list, LCD open), and lazy `ContentControl` panes break the `x:Name` lookups the code-behind uses throughout. The measured startup cost after B1.9 (schtasks off-thread) is a few seconds of device probing on a tray app launched at logon — not worth the regression surface. Revisit only if a user-visible startup complaint ever arrives.
- [x] **B3.8** Lian bake off the dispatcher — shipped in pass 1.
- [x] **B3.9** Dialogs deduped: shared `MakeDialog`/`Btn`/`Title`/`Message` factories, both prompts recomposed (216 → ~185 lines, zero duplication). *(Drag-reorder dedup between AppRulesWindow/LianLayoutWindow left alone deliberately: rarely-touched interaction code where a shared abstraction risks subtle drag regressions for zero runtime gain.)*

### Batch 4 — micro/cleanups — high-frequency items SHIPPED 2026-08-31; rest deferred
- [x] MeteorMix: 3×HsvToRgb of constant hues per LED → direct channel math; `Pow(u,2)`→`u*u`. Spiral: `Sqrt(Pow+Pow)`→`dx*dx+dy*dy`. Twinkle/Confetti: `Pow(s,4)`→`(s²)²`.
- [x] Per-LED-constant hashes cached: `Fx.SparkleRate/Phase/Pick` static tables (4096 entries) feed Twinkle+Confetti; Rain hoists its 17 column drops per frame (stackalloc). *(Starfield/Matrix left — same pattern, lower frequency.)*
- [x] `TimeWarmth`: DateTime.Now cached ~5s (was per frame per channel).
- [x] Freeze+cache converter brushes (`RgbToBrush`/`HexBrush` share one capped frozen cache).
- [x] `NotifyModeChanged`: duplicate `PatternPalette` raise dropped.
- [x] `TempGauge`: NaN/size-aware change check — no more geometry rebuilds every cooling tick.
- [x] `AdminReports` ListBox: `VirtualizationMode="Recycling"`.
- [x] Dead `Master.Scale(Rgb)` deleted.
- [x] `RefreshDisplays` (editor text + analog-clock bitmap) skipped when the LCD designer isn't on screen.
- [x] GigabyteEcio busy-wait backoff (Yield→1ms). *(shipped in B2.15 note)*
- [x] VM Dispose: stops cooling/lcdSave/rebake timers, flushes pending debounced settings save, stops the Chroma REST server.
- [x] **Second pass (structural session, same day):** `PatternEffect` axis now a per-call PARAMETER (race gone; also its `Frac`/`Lerp` forward to `Fx`) · engine non-zone path pre-scales the base frame per brightness value and scales only the channel slice · `Log.Occasional(Func<string>)` overload + engine failure path uses it · IteSuperIo `Read(includePwm:false)` on the SensorHub path (−12 ioctls/sweep) · `LianLiWireless` `LianFanParts`/`LianFanNames`/`FanRpmsBySlot` cached · `PatternMotions` returns two stable static arrays · dead `LcdElementTextConverter` deleted (class + resource) · `OpenRgbManager.IsInstalled` memoized 30s (invalidated on install) + server-log tail reads last 256KB instead of the whole file · capture blend ping-pong shipped inside `ColorGrid` + GDI SelectObject save/restore in the screen capture · `RenderChannel`/`RenderChannelAt` guards unified · LcdElement PropertyChanged: named handler, unhooked on delete/design-swap — **and this fixed a REAL latent bug: the new analog-clock widget's `ClockImage` pushes weren't exempted, so clock-on-screen + designer-open would recurse TouchLcd→Refresh→Ticked→ClockImage to a stack overflow** · `WakeLightsHook` nulled in AutomationService.Dispose · all-effects popup cached per filter state, favorite flags refreshed in place.
- [x] **Third pass (list close-out, same day):** FanCurveEditor drag now moves ONLY the dragged handle + affected line/fill vertices in place, one clean rebuild at release (was ~25 elements recreated per mouse-move — and it deleted the mouse-captured Ellipse, working only because capture survives detachment) · AudioBars yMin/yMax via `Geo.YRange` (B3.5) · GIF background capped 96 frames (~29 MB worst case, was ~46) + frame CATCH-UP so sub-100 ms GIFs no longer play in slow motion · OpenRgbManager window-hide sweeps 250 ms for the first 5 s then 1 s (was flat 250 ms ×120 sweeps).
- [x] **CLOSED as decided-no (rationale):** `ReapplyAllStatic` debounce — the CoalescingApplier's latest-wins keying already bounds a brightness drag to one in-flight write per device; adding a debounce would only delay visible feedback · `Log` buffered writer — a crash would lose the buffered tail, and the log's whole purpose is post-crash support bundles; per-line append stays · NvApi blittable structs — marshaling surgery on a GPU vendor API for a call that now only runs while the Cooling panel is open; risk exceeds the gated gain.

**LIST COMPLETE 2026-08-31.** Every finding is shipped, hardware/live-verified where required, or closed with a written decision. Final state verified: build clean (App+CLI), 79/79 tests, app running, zero log warnings.

## FINAL before/after — 2026-08-31, window OPEN (the real "watching the app" state)

Driven end-to-end by the UIA driver: identical profile, identical clicks (Rainbow Wave → All
devices), 60 s `dotnet-counters` per cell. The earlier tray-mode A/B understated the win — the
open window exposes the old LedPreview (monitor-refresh full redraws) and every animated write path.

| Window open                | 1.0.16 CPU | FINAL CPU | 1.0.16 alloc | FINAL alloc | Gen2/min |
|---|---|---|---|---|---|
| Static profile             | 78.7 %  | **4.8 %  (16×)** | 54.2 MB/s | **0.16 MB/s (338×)** | 22 → **0** |
| Rainbow Wave, ALL devices  | 186 % (≈2 cores) | **38.0 % (4.9×)** | 55.0 MB/s | **1.44 MB/s (38×)** | 20 → **1** |

Working set ~285 → 220–251 MB. Geo-cache correctness verified visually the same pass (diagonal
rainbow + Spiral swirl both pixel-correct). Raw CSVs: scratchpad `ab-test\FINAL-*.csv`.
*(Cross-version note observed during the swap: 1.0.16 saving settings strips fields it doesn't
know — e.g. `FirstRunDone` — so downgrading re-triggers the wizard. Harmless, by design of
round-trip serialization; worth remembering if a rollback build ever ships.)*

---

# Detailed findings

## MEM — Memory churn (GC pressure / RSS growth)

### MEM-1 🔴 Pump-LCD write path: ~11.5 MB/s, ~4 MB/s on the Large Object Heap
Two layers compound at 25 fps (stream thread) / 1–10 Hz (render):
- `Devices/ThermalrightLcd.cs:65` — `new byte[20 + rgb565.Length]` = **153,620 B per frame → LOH** (threshold 85,000).
- `:81` — `payload.AsSpan(off, n).ToArray()` copies 512 B × **301 chunks/frame**.
- `:88` — `new byte[REPORT]` (513 B) × 301/frame inside `WriteReport`.
- Each chunk goes through `HidNative.Transfer` → `GCHandle.Alloc(pinned)` (`Native/HidNative.cs:144`) = **~7,500 pinned handles/sec**, fragmenting Gen0 and fighting the compactor.
- `App/LcdController.cs:198` — `new byte[153600]` RGB565 output per rendered frame (LOH). `:104` — fresh 153,600 B blank every tick while `On == false`.
- Total ≈ 462 KB/frame × 25 ≈ 11.5 MB/s; LOH portion ≈ 3.8 MB/s. LOH is only reclaimed on Gen2 and not compacted by default → weeks-long sessions grow RSS.

**Fix:** two pre-allocated frame buffers ping-ponged via `Volatile.Write` (pattern already proven by `_bgra` at `LcdController.cs:194`), one cached blank, one long-lived pinned 513 B chunk buffer (`GC.AllocateArray(pinned: true)`), write chunks straight out of the payload with offsets. Zero steady-state allocation.

### MEM-2 🔴 LianLiTinyuz: 128 KB LOH hash table per compression, per frame
- `Devices/LianLiTinyuz.cs:100-103` — `new int[1<<15]` (131,072 B → LOH) + `new int[n]` + 32,768-iteration init, **per Encode call**. Encode is NOT bake-only: `LianLiWireless.Transmit()` calls it per changed frame (`LianLiWireless.cs:326`) and again in `SettleResend` (`:355`); rate-limited to 45 ms (`:28`) → ~22/s ≈ **2.9 MB/s LOH** under a live effect.
- Input is ~528 B (4-fan group) — the table is ~62× oversized.
- `:63` — `UnpackLen` allocates a `List<(int,bool)>` per emitted match/length field — dozens–hundreds per encode; chunk count is bounded → `stackalloc`.

**Fix:** instance-cache `head`/`prev`, size `HSIZE` from `n` (e.g. `1<<12`), reset only touched buckets.

### MEM-3 🔴 LedPreview: ~19,000 allocations/sec at monitor refresh while window open
- `App/LedPreview.cs:63-71` — subscribed to `CompositionTarget.Rendering` (60+ Hz), never detached (§LEAK-7), unconditional `InvalidateVisual()` (no change detection).
- Per frame, per LED: unfrozen `RadialGradientBrush` + `GradientStopCollection` + 2–3 stops + 2 `SolidColorBrush` (`:157,165,167`, `:235,241,244`, `:262-268,275-276`, `:308-314,321,323`). 104-key board ≈ **310 Freezables + 104 stop-collections per frame**.
- `RenderKeys` also allocates `HashSet<int>` + `Dictionary<int,List<int>>` + `Rect[]` + row sorts per frame (`:180,187-192,199,208`).
- Its data source `MainViewModel.CurrentTargetView` (`MainViewModel.cs:2977-3030`) per frame: `ComposedFrame` clone (`:2701`), `new Rgb[ch.Count]` per channel (`:2704`), `colors` (`:2988`), `p.ToArray()` full LedPositions copy (`:2995`), `pos` (`:3002`), `g.ToArray()` (`:3028`) — and re-runs `_engine.RenderChannel` **on the UI thread** while a worker already renders the same effect.
- Engine's `ChannelsFor` (`Effects/EffectEngine.cs:88`) returns a fresh `List` + closure per call — add a fill-a-caller-list overload.

**Fix:** copy `Controls/LianLiFanView.cs` wholesale — frozen static brushes/pens (`:48-56`), cached geometry keyed on size (`:165-210`), per-LED brush reuse mutating `.Color` (`:260-269`), a 30 Hz `DispatcherTimer` gated on `IsVisibleChanged` (`:25,67`), and skip `InvalidateVisual` when colors are unchanged.

### MEM-4 🔴 WallpaperCapture re-derives its target window 10×/s, forever
- `Effects/WallpaperCapture.cs:73` — `FindWallpaperWindow()` runs at the top of every 100 ms loop even when WGC is healthy.
- `:279-281` — 3× `Process.GetProcessesByName` every 2 s (each = full system process/thread snapshot, 100–500 KB garbage + 1–5 ms).
- `:285,303` — `EnumWindows` + `EnumChildWindows` per call; `:307` — `new StringBuilder(64)` + `ToString()` **per window enumerated** ≈ 0.4–1 MB/s.

**Fix:** re-find only when `_capturedWnd == 0`, `IsWindow` fails, or no frame for a few seconds; cache the WE pid; compare class names via pooled `char[]`.
Related: the 20-failure "give up" (`:83`) doesn't stick — next `Touch()` respawns the thread ≈ every 10 s (`:35-42,89`).

### MEM-5 🟠 ChromaFeed: per-message buffers, no idle lifetime
- `Effects/ChromaSync.cs:111` `new byte[n*4]` + `:114` `new Rgb[n]` per received frame (≈1 KB typical, ceiling 28 KB/frame at `:110` → up to 1.7 MB/s). `body` is reusable (single-threaded); `grid` must stay fresh (published by ref).
- Unlike `AmbientScreen`/`WallpaperCapture`, the pipe server never stops: `while (true)` at `:96`, started once at `:157`. Adopt Touch()/5s-idle.

### MEM-6 🟡 Capture blend buffers
`Effects/ScreenSync.cs:60` and `WallpaperCapture.cs:239` allocate `new Rgb[576]` per 100 ms tick (~17.5 KB/s each). Ping-pong two arrays.

### MEM-7 🟡 ChromaRestServer per-request churn (game frame rate)
`Net/ChromaRestServer.cs:68-69` StreamReader+`ReadToEnd` string body; `:103` `JsonDocument.Parse(string)` re-encodes to UTF-8; `:83-84` `path.Split`; `:116` `new Rgb[r*c]`; `:160` `Encoding.UTF8.GetBytes` per response; `:152-155` `AppTitle` parses the body a **second** time. Few hundred KB/s at 60 Hz. Pool the body buffer, `Utf8JsonReader`, `static readonly byte[]` responses (`:76,86,88,90,94`).

## CPU — work that never sleeps

### CPU-1 🔴 Engine: flat 60 fps per channel, no dedup, thread-per-channel
- `Effects/EffectEngine.cs:12` `MinFrameMs = 16`; `Run` loop `:117-177` renders + writes USB every frame regardless of effect. TimeWarmth (changes per minute), PaletteCycle (holds ~2 s/color), dim Breathing — all stream 60 fps 24/7.
- **Fix (primary): frame dedup at the write boundary** — after render+Master.Scale, memcmp vs last-sent; skip the write when identical; force a keepalive write ~1/s (LCD/panels need the link warm; RGB latches).
- `:51` thread per channel: realistic rig = 8–12 threads × ~62 wakeups/s ≈ **500–750 wakeups/s** (blocks package C-states), 1 MB stack reservation each. §CPU-1b: single scheduler thread (channels already share one clock `:33`).
- `:134-135` baked-Lian channels wake 8.3×/s to do literally nothing (`continue` before render) → `ManualResetEventSlim`.
- `:174-175` no sleep floor: a device slower than 16 ms/write spins as fast as the write returns.
- Contract note: `IRgbDevice.cs:57` says implementations should no-op unchanged frames — engine-level dedup protects against drivers that forget (see CPU-2, DEV-2).

### CPU-2 🔴 LianLiUniHub: zero dedup + fresh packets
`Devices/LianLiUniHub.cs:296-337` — no `_last` comparison anywhere → static color = **360 blocking HID writes/s** (6 × 353 B packets/frame via `SendChannel` `:339-361`, three `new byte[353]` per channel at `:341,345,357` ≈ 127 KB/s). Add dedup + reuse the three fixed-shape packets as instance fields.

### CPU-3 🔴 LianLiUniHub timer: ungated hardware+filesystem poll every 1.5 s forever
`Devices/LianLiUniHub.cs:89` — timer starts in the ctor; no Touch, no idle-stop. Per tick: `RefreshSpeeds` (`:215,231-234`) = HID SetFeature + `Thread.Sleep(20)` (`:147`) + control GET_REPORT allocating `byte[65]`+`byte[64]` (`:155,158`), **holding `_lock`** (§LOCK-5); plus `File.Exists` + **`File.ReadAllText`** (`:218-219`) = ~2,400 file reads/hr for a tuning file almost nobody has. Nothing reads `GroupRpm` unless the Cooling panel is open (`App/MainViewModel.cs:778`). Gate on a Touch; `LastWriteTimeUtc` before reading.

### CPU-4 🟠 SensorHub: full sweep forever once any fan curve exists
`Sensors/SensorHub.cs:106-123` — idle-stop works only when `_manualFans`/`_fanCurves` are empty (`:109`); with any curve (the expected steady state) `:124-144` runs every 1.5 s, window closed:
- 4 unconditional NvAPI calls (`:125-128`); `NvApi.cs:241-247` builds `NvFanCoolersStatusV1` = `NvFanCoolerStatusItem[32]` + **33× `new uint[8]`**, non-blittable (`:214,222-223`) → ~1.7 KB deep-marshaled in AND out per call. Same in `:328-334`, `:300-306`.
- Full LHM `Update()` sweep (`:134` → `LhmFans.cs:126`).
- `:135-136` `BoardTemps`/`BoardFans` rebuilt with LINQ per tick; `:161` `_fanCurves.ToList()` per tick.
**Fix:** split control-essential (curve source temps + apply) from UI-only (RPMs/load/voltage/projections, gated on a real Touch).

### CPU-5 🟡 Cooling DispatcherTimer never stops
`App/MainViewModel.cs:675-681` — started on first Cooling visit, fires 40×/min for process lifetime, checks `ShowCoolingPanel` only inside the tick (`:678`); not in Dispose (`:3420-3434`). Start/stop with panel selection.

### CPU-6 🟠 LCD stream: 25 fps of identical black all night; per-frame File.Exists
`App/LcdController.cs:61-87` — when `On == false` (night mode/lock), the thread still sends the blank back-to-back (~720k USB transfers per 8-hour night). Throttle to ~2 fps when frame unchanged & off. `:244` `EnsureBackgroundLoaded` does `File.Exists` per rendered frame. Absent-LCD case is already free (`:34-37`). No backoff on failure loop (`:73-75`).

### CPU-7 🟡 WASAPI polls at 100 Hz
`Native/Wasapi.cs:161` `Thread.Sleep(10)` drain loop; `IAudioClient.SetEventHandle` declared at `:50`, never used. Event-driven capture removes 100 wakeups/s while audio effects run.

### CPU-8 🟠 Per-LED invariants recomputed 60×/s (streaming AND bake)
`ch.Pos` never changes after `ZonePositions` (`EffectEngine.cs:179-206`), yet:
- `Math.Atan2` per LED/frame: `IEffect.cs:122` (Spiral), `ExtraEffects.cs:113` (Taichi), `:560` (DoubleArc), `LianStackEffects.cs:203` (Orbit), `PatternEffect.cs:159` (`RingCoord`).
- `IEffect.cs:123` — `Sqrt(Pow(dx,2)+Pow(dy,2))` of position-only terms.
- Diagonal `(X+Y)*0.5` in ~16 render loops (`IEffect.cs:61,104`; `ExtraEffects.cs:44,64,88,329,372,393,414,442,463,541,581,604,685`; `LianStackEffects.cs:93`).
- Per-LED-constant `Fx.Hash` (Sin+Floor, `ExtraEffects.cs:18`): Twinkle `:169-170` (2×/LED/frame), Confetti `MoreEffects.cs:69-72` (3×), Starfield `:262`, MatrixRain `:284-285` (13 cols recomputed per LED), Rain `MoreEffects.cs:180-181`.
**Fix:** per-channel geometry cache built beside `ch.Pos` (angle, radius, diagonal, hash lanes).

### CPU-9 🟡 Lian bake: on the UI thread, throwaway buffers
`App/MainViewModel.cs:2515` bake runs in the DispatcherTimer tick: 32–160 frames (`:2538,2544`) × LEDs ≈ 3–6 ms UI stall per device (multi-device = visible hitch). `:2562` `new Rgb[ch.Count]` inside the frame loop — hoist (halves bake allocations, ~88 KB throwaway per 160-frame bake). Debounce + signature guard already good (`:2505-2517,2529-2533`). Upload already posted to device lane (`:2580`). Move render loop off-dispatcher.

### CPU-10 🧹 Transcendental micro-fixes
- `Math.Pow(x,2|4)` → `x*x`: `ExtraEffects.cs:46,171,261,287,331,374,395,419,444,662,709`; `LianStackEffects.cs:81,94,185`; `IEffect.cs:123`(×2); `MoreEffects.cs:71`; `ReactiveEffects.cs:117`.
- MeteorMix `ExtraEffects.cs:664`: 3× `HsvToRgb` of constant hues 0/120/240 per LED/frame → 3 static Rgb + Scale.
- DoubleArc `:561-563`: 4 `WrapDist` where 2 suffice; `Frac(-p)` computed twice.
- `ColorUtil.cs:8`: `((h%360)+360)%360` = 2 fmod; `:10,:12` compute `h/60` twice.
- `AudioEffects.cs:27-33`: AudioBars scans pos[] for yMin/yMax per frame (channel constant).
- `AudioAnalyzer.cs:143-144`: 48 `Math.Pow`/analyze for band edges that depend only on sample rate.
- `TimeWarmth` `MoreEffects.cs:101`: `DateTime.Now` per frame per channel for a color that changes per minute.
- Engine non-zone path `EffectEngine.cs:151-155`: copies+scales whole device for a channel covering a slice.

### CPU-11 🟠 PatternEffect._linearAxis: per-frame recompute AND a data race
`Effects/PatternEffect.cs:31-35` — derived from `device.PreviewAspect` + type test every frame, written to a mutable instance field from render threads. Instances are per-TargetFx (`MainViewModel.cs:2474`) — one target spanning two devices = two threads writing the same field, wrong axis frames. Resolve once at `Start`, store on `Channel`.

## LOCK — stalls & contention

### LOCK-1 🔴 LianLiWireless sleeps up to ~320 ms inside `_lock`
`Devices/LianLiWireless.cs:318` (≤45 ms pacing), `:336` (30 ms double-send), `:575` (3×20 ms in `SendEffect`), `FollowPwmLine :805-819` (~320 ms, runs at app exit → visible shutdown stall). Same `_lock` taken by SetColors/SetZone (`:289,299`), SettleResend (`:351`), ReconcileAnimation (`:522`), SetFanDuty (`:749`), PwmLoop (`:780,790`), Dispose (`:838`). Color-picker drags block behind ~100 ms holds. Pacing sleep protects nothing — wait before acquiring.

### LOCK-2 🟠 CorsairStrafeMk2: 33 ms of sleeps per frame inside the write lock
`Devices/CorsairStrafeMk2.cs:284-322` — 3 channels × (3×`Sleep(2)` `:321` + `Sleep(5)` `:312`) = 33 ms holding `_writeLock` (`:286`) → hard ~30 fps cap + blocks concurrent statics. Also 1.7 KB/frame fresh packets (`:290,309,317`) and `colors.ToArray()` (`:300`). `:288` `SequenceEqual` via IEnumerable boxes 2 struct enumerators per frame even on the no-op path — same at `SteelSeriesApex.cs:162`, `EneDram.cs:199`.

### LOCK-3 🟠 HID 2 s write timeout → worst-case 24 s lock convoy
`Native/HidNative.cs:117` hardcoded 2000 ms; Strafe issues 12 writes per SetColors → a wedged device can hold its lock ~24 s. Engine breaker counts throws, not slow successes (`EffectEngine.cs:126`), so it never fires. Per-device ~100–250 ms timeout.

### LOCK-4 🟠 KeyboardTap locks inside the low-level hook callback
`Input/KeyboardTap.cs:152,164` — `lock (_evLock)` in the system input path, contended by per-frame `Snapshot` (`:92-112`); >300 ms once (LowLevelHooksTimeout) = Windows silently unhooks, reactive effects die. `CallNextHookEx` runs after the lock. Use an Interlocked ring; call next hook first.

### LOCK-5 🟠 LianLiUniHub `_lock` held across ≥20 ms HID transaction 40×/min
`Devices/LianLiUniHub.cs:233` — tach poll holds the same lock guarding Write/Flush/Probe/Dispose (`:303,283,252,267,367`). Root fix is CPU-3 (don't poll unobserved); the lock discipline itself is sound (`:229-230`).

### LOCK-6 🟡 GigabyteEcio busy-spins a kernel ioctl for up to 1 s
`Sensors/GigabyteEcio.cs:39-45,51-57` — no Sleep/Yield/SpinWait; each iteration takes `IteSuperIo._lock` + machine-wide ISA mutex + PawnIO ioctl (`IteSuperIo.cs:229-237`). Transition-only, but pegs a core and hammers a global mutex.

## DEV — device-driver specifics

### DEV-1 status: clean write paths (protect in refactors)
`MsiGpu.cs:75-105` (dedup + stackalloc), `SayoDevice.cs:56-96`, `LogitechG403.cs:170-192` (per-cluster dedup), `OpenRgbDevice.cs:107-118` (span dedup), `GigabyteIt5711.cs:223` `_streamPkt` reuse.

### DEV-2 🟡 GigabyteIt5711 fan path
`:255-269` no full-frame dedup on fan zones (static ARGB header re-streams at 60 fps); `:301-305` `new Rgb[def.Count]` per zone per frame → span; `:211-219` `OrderBytes` **string switch per LED per frame** — resolve `def.Order` once in `BuildZoneDefs`.

### DEV-3 🟡 EneDram: 16 SMBus transactions + 16 global-mutex cycles per frame
`Devices/EneDram.cs:218-219` — 3-byte blocks; each `RegWriteBlock` (`:84-92`) = WriteWordData + WriteBlockData, each taking `Global\Access_SMBUS.HTP.Method` with 2 s wait (`Native/SmbusPiix4.cs:83-88,120-124`) + PawnIO ioctl + `ulong[5]/[9]` + `byte[40]` allocs (`:75-81,114`). `WriteBlockData` accepts 32 bytes (`:72`) → 30-byte blocks = ~10× fewer transactions. ~16 ms of 100 kHz bus time per animated frame today; dedup saves the static case (`:199`).

### DEV-4 🟡 IteSuperIo fallback sweep
`Sensors/IteSuperIo.cs:385-390` reads 6 PWM regs the caller discards (`SensorHub.cs:250-266` uses only Temps+Rpm); each `EcRead` = 2 ioctls (`:163-169`) → 42 ioctls + ~84 allocs (`:153,159`) per sweep, 40×/min.

## LEAK — leaks & lifetime bugs

- **LEAK-1 🔴** `App/AppRulesWindow.xaml.cs:24` — `vm.AutoRules.CollectionChanged += …` lambda captures the window, never unsubscribed; VM collection is process-lifetime → **every "Manage app rules…" open leaks the full window** (`MainWindow.xaml.cs:334-335`).
- **LEAK-2 🔴 (live bug)** `App/MainViewModel.cs:2018` — `SelectedSequence` setter (`:2004-2016`) calls `HookAction` on persistent `SceneAction`s every select; A→B→A = 3 handlers → every edit writes `scenes.json` 3×.
- **LEAK-3 🔴** `Sensors/SensorHub.cs:271-283` — `ResetSources` nulls `_cpu` without Dispose; `RyzenCpuTemperature` owns a PawnIO **kernel driver handle** (`RyzenCpuTemperature.cs:15,73`; `Native/PawnIO.cs:63-66`, no finalizer) → leaked per reset; next Touch opens a second (`:84`). `_iteChips` right below IS disposed (`:277`) — omission is accidental.
- **LEAK-4 🟠** `Net/OpenRgbClient.cs:48-50` — timed-out `ConnectAsync` leaks the `TcpClient` + orphan task; up to 14 retries per launch (`OpenRgbManager.cs:118,226`). `Net/OpenRgbManager.cs:375-381` — `Process.GetProcesses()` **inside** the 3-policy loop, array never disposed (~750 handles per apply; runs on every launch/relaunch `:140,193`; contrast correct `Stop()` `:265`).
- **LEAK-5 🟠** `Effects/WallpaperCapture.cs:127` — WinRT `IDirect3DDevice` wrapper from `FromAbi` never disposed; `TearDown` (`:252-258`) skips it; re-created per HWND change/exception restart → unbounded over WE restarts, and may pin `_d3d` past `:262`.
- **LEAK-6 🟡** `Native/Wasapi.cs:194-201` — releases `_capture`/`_client` but not the `IMMDeviceEnumerator`/`IMMDevice` RCWs (`:93-94`); new pair per Touch→idle→Touch cycle.
- **LEAK-7 🟡 same-lifetime unsubscribes (tidy during refactor):** `LedPreview.cs:63` (static `CompositionTarget.Rendering` roots control+window, keeps composition ticking — detach on Unloaded/hidden); `MainViewModel.Hook` LcdElement handlers never unhooked incl. delete/design-swap (`:1748-1754,1826-1833,2059-2070` — scene sequences re-hook fresh sets per cycle); `_lcd.Ticked` (`:1742`); `InitScenes` Profiles.CollectionChanged + sequencer StateChanged (`:2027,2030`); `MainWindow.xaml.cs:34,70,355-356`; `AutomationService.WakeLightsHook` not nulled (`:70,246-254`).
- **LEAK-8 🟡** `Net/ChromaRestServer.cs` — no Stop/Close/Dispose at all; `:50-60` breaks on exception without `l.Close()`; port 54235 bound for process lifetime; un-responded contexts hang clients. Single-threaded accept→handle also serializes requests.
- **Verified NOT leaks:** `HidNative.Find`/`HidHandle` alloc/free pairs (`:39/72, 56, 75, 111-112/177-180`); `WinUsbNative` (`:34,45,48`); `KeyboardTap._events` bounded 64 + pruned; `SensorHub._lastSpun` index-bounded; `_identifying` finally-removed; `Log._occasional` key-bounded; `EffectEngine._channels` pruned incl. failure path.

## START — startup & UI-thread hazards

### START-1 🔴 Whole ctor synchronous before first paint
`MainWindow.xaml.cs:11` field-initializes the VM → `MainViewModel.cs:2213-2350` runs on the UI thread: SceneStore load (`:1976`), ProfileStore (2 reads+deserializes), 60 effect ctors (`:2245-2306`), `StartLcd` (USB open + PawnIO driver open + immediate synchronous full render, `:2329,1731-1746`), ChromaFeed pipe (`:2330`), **HTTP bind :54235** (`:2331`), **`Rescan()` → serial `DetectAll` over every factory** (`:2332,3166`; `DeviceManager.cs:46-90`), possibly a **second full rescan** via `SyncLianUniChannelToPopulated` (`:2333,1052-1059`), startup profile + engine threads (`:2340-2344`).
**Latent:** any LCD design containing GPU-temp/fan-RPM elements initializes LibreHardwareMonitor **on the UI thread inside the ctor** (`LcdController.cs:129,135` → `SensorHub.Touch` `SensorHub.cs:73-98`).

### START-2 🔴 `StartWithWindows` getter spawns schtasks, 4 s UI-thread timeout
`MainViewModel.cs:1568-1572` → `ProfileStore.cs:195-212` (`WaitForExit(4000)`), bound at `MainWindow.xaml:1343`; evaluated at window load because every pane's tree builds eagerly.

### START-3 🟠 Eager visual tree
Lighting/LCD designer (4 tabs)/Cooling/Disabled/Settings are all siblings gated by `Visibility` (`MainWindow.xaml:121,632,1041,1298,1309`) — all instantiated + bound at load. Lazy `ContentControl`+DataTrigger.

### START-4 🟠 Sliders writing settings.json per drag delta
`MainWindow.xaml:131` `LianSpeedScale` → `MainViewModel.cs:947-960` full JSON save + `_lastBakeSig.Clear()` + rebake per mouse-move. `:1391-1393` `MasterBrightness` → save + `ReapplyAllStatic` per step (tick-snap is the only throttle). Debounce saves. (EffectSpeed/PatternDensity/Brightness are fine — bake debounced.)

### Good startup behavior to keep
Autostart minimizes to tray + working-set trim after 30 s and on minimize (`MainWindow.xaml.cs:53-65,456-459`).

## APP — WPF layer details

- **APP-1** `NotifyModeChanged` raises 28 events (dupes: `SelectedEffectChoice`, `PatternPalette` ×2 — `MainViewModel.cs:224-242,235,239`). Not periodic — user-driven; a device click cascades ~100 notifications (`:1456` +15). Click-latency, not background CPU.
- **APP-2** Allocating getters bound to UI (fresh instance per get → full container regen on every raise): `PatternMotions` (`:470-473`), `LianUniChannelOptions` (`:973-984`), `LianParts` (`:2730-2739`), `LianGroupNames` (`:2794-2795`), `SceneChoices`/`ProfileChoices` (`:1987-1988`), `ProfileNames` (`:1219`); on devices: `LianFanParts`/`LianFanNames` (`LianLiWireless.cs:93-98`), `FanRpmsBySlot` (`:632-641`), `LianLiUniHub.cs:62-64`. Cache pattern already exists: `RefreshVisibleEffects` `_visibleCacheKey` (`:183,196`).
- **APP-3** Converters allocate unfrozen brushes per conversion (`Converters.cs:10-16,38-44`; used `MainWindow.xaml:360,383,531,565,712,833,856`); `LcdElementTextConverter` (`:19-35`) dead.
- **APP-4** LCD tick extras: `DrawingVisual`+gradient per render (`LcdController.cs:148-149,172`); `RefreshDisplays` not gated on LCD pane visibility and `RenderClockImage` builds a new RTB per tick with ~19 pens/brushes per `DrawClock`, called twice (`MainViewModel.cs:1757-1765`; `LcdController.cs:400-411,354-387,181`). `FormattedText` cache already correct (`:331-342`). RTB reuse already correct (`:191`).
- **APP-5** GIF background: up to 150 composited 320×240 Pbgra32 frames ≈ **46 MB** resident (`:271-311,283,301-305`); old list GC'd not disposed on swap (`:245,267`); frame delays <100 ms drop frames (`:225-239`).
- **APP-6** `FanCurveEditor.Rebuild()` (~25 elements + subscriptions) per mouse-move during drag (`Controls/FanCurveEditor.xaml.cs:54-93,190`); rebuild removes the captured Ellipse (`:171,56`) — verify drag. `TempGauge` NaN≠NaN rebuilds arcs every cooling tick (`Controls/TempGauge.xaml.cs:39-61`; `MainViewModel.cs:742-743`).
- **APP-7** XAML: WrapPanel item panels defeat virtualization (bounded, fine) at `MainWindow.xaml:192-200,198,225,355,378,412,458,527,561,831,854`; `AdminReports` is the unbounded one — add `VirtualizationMode="Recycling"` (`:1458-1473`); all-effects popup rebuilt per open (`MainWindow.xaml.cs:257`; `MainViewModel.cs:157-169`); 3 ItemsControls share `PatternPalette` (`:411,526,560`); hidden glow `Border`+`BlurEffect` per templated button/pill/row (`Themes/Styles.xaml:344-347,427-430,495-498`) — culled at Opacity 0, live render pass when animated.
- **APP-8** Cooling ListBox TextBox-in-recycled-container hazard is worked around by in-place `SensorRow.Value` mutation — keep (`MainWindow.xaml:1092-1139,1128`; `MainViewModel.cs:872-887`; `Models.cs:225-229`).
- **APP-9** Scenes comment at `Scenes.cs:104-107` documents the UI thread being saturated enough to starve Background-priority timers — treat as the canary; MEM-3/CPU-6/START fixes relieve it.

## NET — network specifics

- **NET-1** ChromaRestServer: see MEM-7/LEAK-8; also make `Handle` off-thread (`GetContextAsync` or queue).
- **NET-2** OpenRgbClient: two `_s.Write` per packet on a `NoDelay` socket = 2 TCP segments per LED update (`Net/OpenRgbClient.cs:141-142,51`) — reserve header space in the payload buffer, single write; per-frame payload alloc (`:106,119`) → reusable buffer under existing `lock(_io)` (`:114`). Protocol handling otherwise tight (`:136,159,166,147,172-181`).
- **NET-3** OpenRgbManager: `HideWindowsOf` = 120 full `EnumWindows` sweeps over 30 s (`:432-449`); `ReadServerLogTail` reads entire verbose log then `TakeLast` (`:456-474`, `--loglevel 6` at `:154`); `IsInstalled` enumerates recursively per access (`:35-37`), re-run per launch attempt (`:118,135`).

## REF — refactoring map

### REF-1 MainViewModel (3,447 lines, 126 OnChanged sites, 22 commands) — responsibility bands
`32-98` device manager/lanes/TargetFx · `100-242` effect selection/favorites/NotifyModeChanged · `244-263` settings flags · `265-330` update+support · `331-388` admin inbox · `390-541` effect/pattern/ripple surface · `543-599` palette library · `601-629` swatches · `631-660,889-917` left-nav · `661-887` Cooling · `919-1064` OpenRGB/brightness/Lian settings · `1066-1177` automation primitives · `1179-1278` automation+night settings · `1280-1324` PawnIO · `1326-1382` OpenRGB lifecycle · `1384-1434` disable family · `1436-1535` color state · `1537-1626` profiles/first-run · `1628-1662` window placement+commands · `1663-1968` LCD designer · `1970-2211` scenes/sequences · `2213-2350` ctor · `2354-2491` SetColor/ApplyFx · `2493-2598` Lian bake · `2600-2710` targets/frames/compose · `2712-2972` Lian fan editor · `2974-3067` preview VMs · `3069-3190` ApplyAll/Rescan · `3192-3416` profile capture/restore · `3420-3438` dispose.
**Step 1 (zero risk):** partial files per band. **Step 2:** extract `LightingController` (engine+frames+applier+TargetFx+ApplyFxRange+Capture/RestoreEffects), `LianBakeService`, `CoolingViewModel`, `LcdDesignerViewModel`, `ShowViewModel`, `PaletteViewModel`, `SupportViewModel`, `AutomationSettingsViewModel`, `DeviceCatalog`.

### REF-2 Duplicated blocks (each shipped at least one bug already)
- Effect-instance resolution switch ×3: `MainViewModel.cs:2472-2479`, `:3106-3115`, and the if/else re-implementation `:3252-3299` — **the third copy caused the "All devices → white" bug** → one `ResolveEffect(fx, choice)`.
- Snapshot+scale+post ×9: `:1080,1140,1618,2595,2690,3063,3148,3182,3398` → `PushFrame(dev)`; zone-slice ×3: `:2589,2682,3056` → `PushZone`.
- Palette clear+rebuild-from-hex ×8: `:3259-3296` (×4!), `:623-629,550-554,559-570,3103-3105` → `LoadPalette(pal, hex)`.
- `_targetFx` get-or-add ×5: `:92-95,1604,2431,3088,3243` → `FxFor(...)`.
- Bg rect math ×4 (`:1940-1968,1860-1874`); `LcdBgX/Y/W/H` setters ×4 (`:1880-1913`); Lian part offset ×2 (`:2891,2915`).
- `Dialogs.cs` two dialogs ~85% identical incl. verbatim `Btn` local fn (`:30-113` vs `:119-215`); drag-reorder duplicated (`AppRulesWindow.xaml.cs:99-227` vs `LianLayoutWindow.xaml.cs:69-154`).

### REF-3 HidRgbDevice base class
- Open-by-predicate ×6: `CorsairStrafeMk2.cs:238-242`, `SteelSeriesApex.cs:89-107`, `SayoDevice.cs:37-54`, `GigabyteIt5711.cs:147-157`, `LianLiUniHub.cs:92-103`, `ThermalrightLcd.cs:27-35` → `HidNative.OpenFirst(vid, pids, pick)`. Also fixes inconsistent try/catch (Sayo/Apex wrap, the other four propagate into `DeviceManager.cs:63` and silently vanish).
- `FrameChanged` dedup helper (index loop over IReadOnlyList, reused `_last`) — replaces the LINQ preamble in Strafe/Apex/EneDram and supplies the missing dedup in UniHub/Gigabyte-fan.
- `protected readonly byte[] _pkt` (generalize `GigabyteIt5711._streamPkt`); `SendAndSettle(pkt, settleMs)` for the scattered magic sleeps (`LianLiUniHub.cs:147,253,268`; `GigabyteIt5711.cs:204,207,350,353`; `CorsairStrafeMk2.cs:312,321`).
- Keep separate: `LianLiWireless` (WinUSB/RF/compression), `EneDram` (SMBus).

### REF-4 Effects consolidation
- `Frac` ×4 (`ExtraEffects.cs:14`, `LianStackEffects.cs:54`, `PatternEffect.cs:163`, `MoreEffects.cs:12`); `Lerp` ×2 (`ExtraEffects.cs:27`, `PatternEffect.cs:165`); palette sampling ×2 (`MoreEffects.cs:8-18`, `PatternEffect.cs:140-148`).
- Meteor kernel ×8 (`ExtraEffects.cs:41,367,390,411,437,654`; `LianStackEffects.cs:78,183`); runway 3-dot chase ×2 (`ExtraEffects.cs:66,606`); sparkle kernel ×2 (`ExtraEffects.cs:167`, `MoreEffects.cs:67`); complement ×2 (`:109,519`); waiting-breath ×2 (`ChromaSync.cs:161`, `WallpaperCapture.cs:351`); ping-pong ×3 (`:85,368,538`); `LoopSeconds` formula ×~30 → `Fx.Loop(k, speed)`.
- One `ColorGrid` for `AmbientScreen`/`WallpaperCapture`/`ChromaFeed` (byte-identical `Sample`, near-identical blend/Touch/thread shape) — lets the ping-pong fix land once. `GetSystemMetrics` P/Invoked twice.
- Dead: `Master.Scale(Rgb)` (`Master.cs:17-22`). Guard inconsistency: `RenderChannel` vs `RenderChannelAt` (`EffectEngine.cs:94,112`).

### REF-5 Dialogs/drag-reorder dedup — see REF-2 last bullet.

## Already good — protect these in refactors
Engine render-loop buffer reuse (`EffectEngine.cs:119-129`) · `CoalescingApplier` latest-wins lanes · `GigabyteIt5711._streamPkt` · MsiGpu/Sayo/Logitech/OpenRGB dedup · `AudioAnalyzer` allocation-free FFT (static rings + stackalloc) · `LcdController.Format` ConditionalWeakTable cache + `_rtb`/`_bgra` reuse · LianLiFanView's entire rendering approach · bake debounce + signature · working-set trim on minimize · `HidNative`/`WinUsbNative` native-resource pairing · KeyboardTap bounded event ring · `_lastSpun`/`_identifying` hygiene.
