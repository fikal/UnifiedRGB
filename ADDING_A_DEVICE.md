# Adding a device to UnifiedRGB

This is the contract a driver has to meet. Most of it used to exist only as
post-mortem comments scattered across the thirteen drivers, each written after
the bug it describes had shipped. It is written down here so the next driver
does not have to rediscover any of it, and so a reviewer has something to
check a PR against.

The rules are not stylistic. Every one of them corresponds to a way a driver
has actually failed in the field.

## Where things go

- The driver is one file in `src/UnifiedRgb.Core/Devices/`. It implements
  `IRgbDevice` and, only if the hardware really supports it, one or more of
  the optional interfaces below.
- Transport: HID drivers take an `IHidTransport` (never `HidNative.HidHandle`
  directly) and open it through `HidNative.OpenFirst`. That interface is what
  lets the harness drive your driver against a fake (see Testing, below).
- Registration is in `DeviceManager.cs`: add `YourDriver.TryOpen` to
  `Factories` (one device per family) or `YourDriver.DetectAll` to
  `MultiFactories` (several).
- If your device shares a physical bus with a sibling (two DRAM sticks on one
  SMBus, several devices behind one socket), add it to
  `LightingController.LaneOf` so their writes cannot interleave on the wire.

### The registration rule that is easy to break

The family name - what the per-device disable list, the detection log and the
"seen but not usable" notes all key on - is derived by reflection from the
factory delegate's declaring type. So the factory **must be a static method on
the driver class itself**. Register through a lambda or a helper type and the
device works, but can never be disabled and is logged under a compiler
generated name. A test in the harness enforces this.

## The contract

### Identity

- `Name` is the primary key of the entire app. Profiles, static frames, exit
  behaviours, disabled-device entries and desk layouts are all stored by it.
  It must be stable across restarts and unique on the machine. Two of the same
  device need a suffix; see `OpenRgbDevice` for the precedent.
- `LedCount` and `Zones` are **immutable for the object's lifetime**. The
  engine sizes its frame buffers once when a channel starts; a device that
  changes its count later is written stale frames. If a layout can change,
  that is a rescan, not a property update.
- `Zones` must be contiguous, non-overlapping, and sum exactly to `LedCount`,
  and the property must return the **same instance** every time. Anything
  else silently collapses to a single "All" zone over the SDK, and the preview
  geometry cache keys on reference equality. Per-zone color calibration leans
  on the same promise: `Calibration` resolves a device's trimmed zone ranges
  once and caches the result against the device INSTANCE, rebuilding only when
  a trim changes, so a device whose zones moved under it would be written
  frames trimmed by the layout it used to have.
- `LedPositions` and `LedGeometry` are either exactly `LedCount` long or
  `null`. A wrong length is silently discarded and your effects render as a
  straight line, with no message anywhere.

### Threading

`SetColors` (and `SetZone`) will be called **concurrently from several
threads at once**: one effect worker thread per channel, the applier's lane
workers, the SDK server's socket threads, and any timer or thread your driver
started itself. The applier's lane does **not** serialize you against the
effect engine - they are two independent write paths.

So: take your own lock around every hardware transaction, and re-enter it
consistently (C# `lock` is re-entrant; do not swap it for something that is
not). Every existing driver does this; none of them says why.

### How long a write may take

There is no formal budget, but you are inside three deadlines you cannot see:

- the engine gives a stopped channel **300 ms** to finish its current write
  before warning that it is stuck;
- the applier drains for **1500 ms** before devices are disposed on a rescan;
- exit behaviours get **2000 ms total, for every device combined**.

A full-device frame that takes more than a few tens of milliseconds is a
problem. If your protocol needs settle delays, keep them small and outside
the lock where you can, and never poll telemetry from inside the color path.
The HID write timeout is 400 ms per report; a wedged device holding your lock
for that long per packet is why it was cut from 2000.

### Frame length

Callers always pass exactly `LedCount` colors (or a zone's count to
`SetZone`). Do not rely on it: never index past `colors.Count`. The convention
for a short frame is **repeat the last color** - `WritePolicy.ColorAt` is
that one line, already written. Since `SetColors` returns a verdict a driver
may instead refuse a short frame outright and return `false`, which is honest
and visible. What is banned is dropping it **silently** and padding the tail
with **black** - both have been done, and both looked like a dead device.
Every driver in the tree follows this now. `EneDram` and `CorsairStrafeMk2`
were the last two black-padding; `SteelSeriesApex` was the last to address
only the first `colors.Count` keys and leave the tail on its previous colour -
which is the *silent* half of the same ban, and worse for carrying a `true`
verdict and caching the short frame as "what the device is showing".
`Suites/Devices.cs` pins the keyboard behaviour so none of it can come back.

### The verdict: what `SetColors` returns

`SetColors` and `SetZone` return a `bool`, and it means exactly one thing:

- **true** - the frame reached the device, **or** was correctly skipped
  because the device is already showing precisely it. A deduped frame is a
  success: the hardware is in the state the caller asked for.
- **false** - the device **refused** it and is still showing something else.

That is the only signal anything above a driver has. It used to be `void`, so
a refusal was invisible outside the driver that saw it: the engine cached
frames it had not delivered, and a lights-off that never went out looked
exactly like one that did.

Return honestly, and do not guess. A driver on a one-way bus that genuinely
cannot observe delivery (`LianLiWireless` past the dongle, `OpenRgbDevice`
past the socket) returns true for the part it *can* see and says so in a
comment. "Probably fine" is not a verdict.

Two cases people get wrong:

- **Backing off is not delivering.** A driver that skips a cluster because it
  is inside a retry window knows that cluster is on the wrong color. Return
  false.
- **Writing bytes nobody displays is not delivering.** `EneDram` returns false
  when the color registers were accepted but direct mode is still off: the
  stick took the bytes and is showing its onboard effect.

#### Returning false has a cost: pace anything expensive behind it

The engine latches its once-a-second keepalive clock (`shared.LastWrite`) only
on a **successful** write. A driver that keeps returning false is therefore
called back on **every frame** - up to 60 Hz - not once a second, and each call
runs inside `lock (gate) lock (shared)`, so it holds up every other channel on
that device.

That is fine for a cheap refusal. It is not fine for a blocking HID read, a
sleep, or a mode-init sequence. If the work behind a `false` is expensive, put
it behind a retry clock and refuse immediately while the window is open:
`LogitechG403.RetryAfterFailMs`, `RazerHid._nextRetryTick`, and both keyboards'
`InitRetryAfterFailMs` are the shape. The refusal stays honest and immediate -
only the expensive retry is paced.

**Clear the clock in `InvalidateCache`.** A caller that has decided a write
MUST land is not served by a driver still waiting out a sulk from an earlier
refusal, and `MustLand` calls `InvalidateCache` on every attempt.

### Failure: return, log, do not throw

The engine's breaker counts **thrown exceptions**: after 300 consecutive ones
it stops the channel **permanently** and never restarts it. A `false` return
is invisible to it. Those are not interchangeable, so the rule is:

- a transient failure (a refused report, a timed-out write, a device that is
  asleep) **returns false**. Log it with `Log.Occasional`, do not commit the
  dedup cache (next section), and if the device keeps refusing, back off - a
  mouse that does not answer must not be sent a claim and a color on every
  frame, each blocking on a read timeout;
- throw only when the device is gone for good and stopping the channel is the
  right outcome.

`WritePolicy.Refused` in `Devices/WritePolicy.cs` does the invalidate, the
rate-limited log line and the `false` in one call, so a driver cannot return
the failure and forget the invalidation.

### Retry: the next frame, not this one

On the streaming path **the retry is the next frame**, and that is deliberate.
A driver that re-sends a refused packet immediately doubles its traffic to a
device that has already stopped answering, and each of those attempts can wait
out the 400 ms write timeout while holding the driver's lock. So: return
false, drop the cache, back off if the refusals keep coming, and let the
engine's once-a-second keepalive or the user's next apply be the retry.

The bounded *immediate* retry exists in exactly one place, for the writes that
have no next frame behind them.

### Dedup, and the half of the rule that keeps going wrong

The write path runs at up to 60 fps for as long as the app is running, so a
driver **must** skip a frame identical to the last one it sent. That much is
in `IRgbDevice`. The half that is not:

1. **Commit the cache only after the write landed.** Recording a frame as
   sent before sending it means a dropped packet is cached, and every
   identical frame after it - including the engine's own once-per-second
   keepalive re-send, which exists precisely to cover a lost packet - is
   skipped. The zone stays on its old color until something changes. This
   exact bug has shipped in four drivers. `WritePolicy.Unchanged` and
   `WritePolicy.Cache` are the two halves, in the right order, without
   allocating.
2. **Invalidate the cache after any mode change.** Handing the device back
   to its own firmware, toggling direct mode, re-initialising after a
   resume: after any of these the hardware no longer shows what your cache
   says it does, and the next frame must go out even if it is "the same".
   Expose that as `InvalidateCache()` - it is on `IRgbDevice` with a do
   nothing default, and the must-land path below calls it.
3. **Never write to flash at frame rate.** If your device can persist a
   color to onboard memory, that is a separate, rare operation, gated by a
   long quiet period. Streaming frames do not carry the persist flag.

### The writes that must land

A static apply, a lights-off and an exit behaviour are **terminal**: there is
no next frame to correct a refusal. "It will be fixed on the next frame" is
false for exactly the writes a user notices - the machine sleeping with a
color still lit because the off command was dropped.

Those go through `WritePolicy.MustLand`, which is the only place in the app
that retries a write immediately. It:

- calls `InvalidateCache()` before **every** attempt - the two device-typed
  overloads do, so a stale "it already shows this" cannot turn into a success
  for a frame the hardware never got. The `Func<bool>` overload cannot see a
  device and does not: its callers invalidate inside their own lambda;
- retries to a bounded deadline (`MustLandBudgetMs`, spent per device out of
  the exit path's single 2000 ms window);
- treats a throw as one refused attempt rather than an escape route, because
  the process is on its way out and there is nothing left to stop;
- logs at **error** level, naming the device and the attempt count, when it
  still could not deliver.

Callers: `LightingController.PushFrame` / `PushZone` / `PushBlack` /
`PushReference` (the calibration reference patch), `HardwareExit.Apply`, and
the identify blink's restore. A streaming caller
must **not** use it: an SDK client's frames retry themselves, and a retry loop
on an applier lane at a client's frame rate is a stutter, not a fix.

`IHardwareModes.SetHardwareStatic` / `SetHardwareEffect` / `ReturnToHardware`
return `bool` for the same reason, and a driver that does not implement a
mode returns false rather than pretending.

### Detection

- Return `null` (or an empty list) when the hardware is not there.
- When the hardware **is** there and you cannot use it - another program
  holds it, it needs a driver that is not installed, it needs elevation - say
  so through `DetectionNotes.Report`. That is what turns "my mouse is not
  detected" into one sentence with a fix attached, in the log, the support
  bundle and Settings > Devices. `HidNative.OpenFirst` already reports the
  common HID cases for you.
- **Never probe hardware you have not positively identified with a write.** A
  receiver was soft-bricked once by an exploratory packet. Initialisation
  writes are fine, but only to a VID/PID you matched exactly.

### The optional interfaces

- `IZoneWritable` - implement only if the hardware can genuinely update a
  range without disturbing the rest. Be aware you are opting into a different
  engine path: each channel writes its own zone from its own thread, and the
  engine does **not** lock between them. Your lock is the only one.
- `IHardwareModes` (exit behaviour) - called on a worker thread with the 2 s
  shared budget above, after the engine has stopped and before disposal.
  Be quick, and invalidate your dedup cache afterwards. If your device hands
  itself back to firmware, do it here rather than in `Dispose`, so the user
  gets the choice.
- `IBatteryDevice` - polled through the applier on your lane; a read that
  shares the transport with writes still needs your lock. Null means unknown,
  never zero.
- `IKeyMappedDevice` - called on the render thread at frame rate; must be a
  cheap lookup.

### Dispose

Writes **can** arrive during and after `Dispose`: the applier drains on a
timeout, and your own timers are not covered by it. Set a disposed flag under
your lock, check it at every entry point, and return quietly. Never throw from
`Dispose` - the manager tolerates it, but a throw skips whatever you meant to
do after.

### Performance

Nothing on the per-frame path may allocate in steady state. Keep one wire
buffer per report shape and reuse it. The app runs 24/7 in the tray and is
held to idling at a whisper; a driver that allocates per frame or re-sends
unchanged frames is the usual reason it does not.

## Testing without the hardware

The harness (`src/UnifiedRgb.Tests`) has `FakeHid`, an `IHidTransport` made
of lists: it records every report a driver writes, answers reads from a
queue, and refuses whichever writes a test says to. It lives in
`Support/Fakes.cs`. Every HID driver has an `internal` constructor that takes
the transport, and the harness can see it.

A driver PR should come with a section in `Suites/Devices.cs` alongside the
ones already there: build the driver over a `FakeHid`, send a known color,
and pin the bytes that come out - report id, magic, checksum, where the color
sits. Then refuse a write and prove the frame is sent again rather than
cached, **and that the refused call returned false while the deduped one
returned true**. The Sayo section is the smallest example; the Logitech and
Thermalright sections show the failure paths, and `Suites/WriteContract.cs`
pins the contract itself - the three verdicts, and the must-land path - if you
want the shape before you write your own. Run that one suite while you
iterate, rather than the whole set:

```
dotnet run --project src/UnifiedRgb.Tests -- Devices
```

Those fixtures are what let a protocol decoded from a USB capture survive a
refactor. Without them, the only test is plugging the device in.

## Protocol notes

Every magic number gets a comment saying where it came from: a USB capture,
observed vendor-tool behaviour, a cited open-source implementation. No
decompiled code, ever - behavioural reimplementation only. If a value is
genuinely unknown, name it `UNKNOWN`, as `MsiGpu` does, rather than inventing
a meaning.

## Checklist

Before opening the PR:

- [ ] `Name` is stable and unique; `LedCount` and `Zones` never change after
      construction; `Zones` are contiguous and sum to `LedCount`
- [ ] Every hardware transaction is under the driver's own lock
- [ ] A full frame completes in well under 100 ms; no telemetry on the color
      path
- [ ] Short frames repeat the last color; nothing indexes past
      `colors.Count`
- [ ] `SetColors`/`SetZone` return true for delivered AND for correctly
      deduped, false only for refused; a bus that cannot tell says so in a
      comment rather than guessing
- [ ] Transient failures return false (logged, rate-limited, backed off) and
      leave the retry to the next frame; only a dead device throws
- [ ] Dedup commits only after a successful write, and is invalidated after
      any mode change; `InvalidateCache()` drops it
- [ ] `IHardwareModes` methods, if implemented, return whether the command
      landed
- [ ] Persist-to-flash, if any, is rare and never per frame
- [ ] Detection never writes to unidentified hardware; blocked hardware is
      reported through `DetectionNotes`
- [ ] Registered as a static method on the driver class; added to `LaneOf`
      if it shares a bus
- [ ] Safe to call after `Dispose`; `Dispose` never throws
- [ ] No per-frame allocation
- [ ] A `FakeHid` fixture pins the wire format and the refused-write path
- [ ] Every magic number has a provenance comment
