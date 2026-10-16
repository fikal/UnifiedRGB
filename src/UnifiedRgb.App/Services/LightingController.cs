using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/// <summary>The device-write side of the view model: the effect engine, the
/// per-device static frames and the coalescing applier, plus the ONE copy of
/// "snapshot the frame, scale it, post it" that used to be pasted at nine call
/// sites (and its zone-slice variant at three). The view model keeps the
/// bindable state and decides WHAT to write; this decides HOW it reaches the
/// hardware.</summary>
public sealed class LightingController
{
    public EffectEngine Engine { get; } = new();
    public CoalescingApplier Applier { get; } = new();
    /// <summary>Concurrent because an SDK client's write reaches FrameFor from
    /// a socket thread, while the UI thread inserts into it constantly and a
    /// rescan clears it. A plain Dictionary here was a torn-read waiting to
    /// happen: concurrent insert and read is the classic spin-forever bug.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, Rgb[]> _frames = new();

    /// <summary>Which applier lane a device writes on. Parallel lanes keep
    /// every device changing at the same moment on profile flips; devices
    /// that share a transport must share a lane so their transactions can't
    /// interleave on the wire.</summary>
    public static object LaneOf(IRgbDevice d) => d switch
    {
        EneDram => "lane:smbus",          // both DRAM sticks ride one SMBus
        OpenRgbDevice => "lane:openrgb",  // all remote devices share one socket
        _ => d,                           // native devices: a lane each
    };

    /// <summary>What an SDK client has painted on each device it claimed,
    /// UNSCALED and kept separate from the user's statics.
    ///
    /// Partial writes have to accumulate somewhere. They used to be merged over
    /// the user's STORED frame, which never contains the client's own earlier
    /// writes, so "LED 0 red" followed by "LED 1 blue" ended up black+blue: the
    /// second write rebuilt LED 0 from the static underneath it. This is the
    /// frame the merge composes over instead. The user's statics stay untouched,
    /// because they are the thing restored when the client goes away.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, Rgb[]> _external = new();

    /*-----------------------------------------------------*\
    | "The lights are off" is enforced HERE, at the write    |
    | boundary, and nowhere else has to remember it.          |
    |                                                         |
    | It used to be a flag each caller checked for itself:    |
    | the SDK path did, and PushFrame, PushZone, the uncovered |
    | restore, the calibration patch and every effect start   |
    | did not. So a hand edit, an identify put-back, the desk |
    | editor or a calibration slider each relit some part of  |
    | a locked desk, one path at a time, and every new path   |
    | was one more place to forget it.                        |
    |                                                         |
    | While suppressed, every static push RECORDS - the stored |
    | frame is the caller's to change and is kept - and does  |
    | not write. PushBlack is the only thing that reaches the |
    | hardware. Coming out of the dark is a repaint from the  |
    | stored state (MainViewModel.RelightLeftovers and the    |
    | automation's restore), so nothing recorded is lost.     |
    \*-----------------------------------------------------*/
    volatile bool _suppressed;

    /// <summary>Which dark span this is: bumped every time the lights go off
    /// AND every time they come back, so a write decided in one span (a
    /// calibration patch the user asked for inside it, a lights-off retry)
    /// can tell that it is stale in the next.</summary>
    int _darkEpoch;
    public int DarkEpoch => Volatile.Read(ref _darkEpoch);

    /// <summary>The lights are deliberately off (a locked session, a dark
    /// window, the PC going to sleep). See the box above for what that means
    /// at this boundary. Clearing it replays the SDK clients' pictures; the
    /// USER's lighting comes back through the view model's relight.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            bool wasSuppressed = _suppressed;
            if (wasSuppressed == value) return;
            Interlocked.Increment(ref _darkEpoch);
            _suppressed = value;
            Engine.LightsOff = value;   // the workers' own floor (see EffectEngine.LightsOff)
            if (value) return;
            // Clients may send a final static/partial frame while locked. Keep
            // it in the shadow and replay the latest complete picture on wake.
            foreach (var pair in _external)
                lock (pair.Value) QueueExternal(pair.Key, pair.Value);
        }
    }

    /// <summary>A write may light this device now: the lights are on, or
    /// the write belongs to the dark span it was allowed in (a calibration
    /// the user opened inside the dark window; see PushReference).</summary>
    bool MayLight(int allowedEpoch) => !_suppressed || (allowedEpoch != 0 && allowedEpoch == Volatile.Read(ref _darkEpoch));

    void HeldDark(IRgbDevice dev) =>
        Log.Occasional($"dark:{dev.Name}", "lighting",
            $"{dev.Name}: the lights are off - the colour is kept for when they come back, not written");

    /*--- What each device was last shown by THIS boundary, for the settled-colour save ---*/

    /// <summary>The finished frame the last static push landed on the device,
    /// kept only while nothing else has written it since: the lights-off black,
    /// a calibration patch, a client's picture, an identify blink, an exit
    /// preview or an effect each forget it. The settled-colour save stores a
    /// colour in the device's flash only when the device is known to be showing
    /// exactly the user's stored colour (PersistStoredColor) - it used to store
    /// whatever the driver had last been sent, which was the lock's black.
    ///
    /// Set, and its array updated, only on the device's own applier lane, so
    /// a picture is never mutated from two threads at once; anything else
    /// only ever removes the entry.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, Rgb[]> _landed = new();

    /// <summary>Devices whose last write from here was the lights-off black,
    /// i.e. still dark from a lock or a dark window. Whatever brings the lights
    /// back repaints exactly these (MainViewModel.RelightLeftovers), so a path
    /// that lifts the dark without repainting cannot leave one black.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, byte> _blackedOut = new();

    /// <summary>Still showing the lights-off black.</summary>
    public bool IsBlackedOut(IRgbDevice dev) => _blackedOut.ContainsKey(dev);

    /// <summary>Something outside this class wrote the device (identify, an
    /// exit preview, a header test, an effect start): it is no longer known
    /// to be showing its stored colour.</summary>
    public void NoteOtherWrite(IRgbDevice dev) => _landed.TryRemove(dev, out _);

    /// <summary>Bumped when the device instances are replaced or the writes
    /// are stopped (rescan, shutdown), so a lights-off retry still waiting
    /// cannot land on an instance that is gone.</summary>
    int _instanceEpoch;

    /// <summary>The device's stored static frame (created black on first use).</summary>
    public Rgb[] FrameFor(IRgbDevice d) => _frames.GetOrAdd(d, static k => new Rgb[k.LedCount]);

    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, bool> _written = new();

    /// <summary>Whether this session has ever actually written to a device.
    ///
    /// FrameFor hands back a ZEROED array for a device nothing has set, and zero is
    /// black - so a device we have never touched reads as "showing black" in the
    /// bundle, which is not what it is showing at all. It is whatever its firmware
    /// or its vendor software left on it. The two are worth telling apart when
    /// somebody is looking at a report trying to work out why a device looks
    /// wrong.</summary>
    public bool HasBeenWritten(IRgbDevice d) => _written.ContainsKey(d);

    void NoteWritten(IRgbDevice d) => _written[d] = true;

    /// <summary>Raised when the USER's own static lighting is pushed - not an SDK
    /// client's, and not an effect frame, which never comes through here. One place
    /// rather than a call beside each of the push sites, because the one that gets
    /// missed is the one somebody reports.</summary>
    public event Action? StaticPushed;

    /// <summary>An SDK client gave a device back. The next client starts from
    /// the user's lighting again instead of inheriting the last one's pixels.</summary>
    public void ForgetExternal(IRgbDevice dev) => _external.TryRemove(dev, out _);

    /// <summary>True while an SDK client is painting this device. Its writes
    /// go out whole from the client's picture, so an effect started on the
    /// device meanwhile would be repainted over at the client's write rate:
    /// the view model refuses to start one until the client lets go.</summary>
    public bool IsClaimed(IRgbDevice dev) => _external.ContainsKey(dev);

    /// <summary>Devices the SDK bridge has told us it took, as opposed to ones
    /// it has painted yet. IsClaimed only becomes true on a client's FIRST
    /// frame, and the takeover (StopEffectsOn) comes before that frame: asked
    /// alone, IsClaimed let a static push land in that gap, over a device a
    /// client had just taken, and raised the "controlled by another app" badge
    /// only to drop it again. Concurrent: the applier lanes re-check it when a
    /// queued write runs.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, byte> _held = new();

    /// <summary>A client took the device (before its first frame).</summary>
    public void Hold(IRgbDevice dev) => _held[dev] = 0;

    /// <summary>That client let go; false when it was not held.</summary>
    public bool Unhold(IRgbDevice dev) => _held.TryRemove(dev, out _);

    /// <summary>Every hold dropped (a rescan replaces the instances). What keeps
    /// it that way is upstream: a takeover still in flight from the server the
    /// rescan stopped is dropped by its retired host (OpenRgbHost.Retire), and
    /// one naming a replaced instance by the view model (StopEffectsOn).</summary>
    public void ReleaseAllHolds() => _held.Clear();

    /// <summary>THE question "does an SDK client own this device?": from the
    /// takeover until it lets go - held, or already painting. The write
    /// boundary below asks exactly this, and so does every caller that has to
    /// leave a client's device alone; ten hand-written copies of the pair used
    /// to live in the view model while this boundary asked only half of it.</summary>
    public bool IsClientOwned(IRgbDevice dev) => _held.ContainsKey(dev) || IsClaimed(dev);

    /// <summary>Every claim dropped at once (a rescan replaces the instances
    /// these are keyed by, and shutdown wants nothing held).</summary>
    public void ForgetExternalAll() => _external.Clear();

    /// <summary>Drop every stored frame, written mark and idle applier lane
    /// (device instances are being replaced, and all of them are keyed by
    /// instance). Call after StopAndDrain. The written marks used to be left
    /// out, so every rescan pinned each replaced device object - handles and
    /// all - in _written for the life of the process.</summary>
    public void ForgetFrames()
    {
        Interlocked.Increment(ref _instanceEpoch);
        _frames.Clear();
        _external.Clear();
        _written.Clear();
        _landed.Clear();
        _blackedOut.Clear();
        Applier.PruneIdle();
    }

    /// <summary>Every hardware write posted from here runs under the device's
    /// write gate - the one boundary shared with the effect workers, which
    /// write their devices directly from their own threads and never touch
    /// the applier.
    ///
    /// The lane alone was not enough. Stopping an effect gives its worker
    /// 300 ms to leave the write it is in; a slow driver (HID timeout, the
    /// wireless receiver's paced transmit) can take longer, the engine gives
    /// up, and the static replacement posted right after would race the
    /// worker's last frame to the hardware - and lose often enough that "I
    /// picked a static and the device kept the effect's color" was a real
    /// report. Under the gate the straggler finishes first, THEN the
    /// replacement writes, and the replacement is what stays.
    ///
    /// Any other write to a device that bypasses this class (the view model's
    /// identify blink and exit-behaviour preview) must take the same gate.</summary>
    static object GateOf(IRgbDevice dev) => EffectEngine.WriteGateFor(dev);

    /// <summary>Write the device's whole stored frame: snapshot (the frame keeps
    /// changing on the UI thread), scale by master brightness on the worker,
    /// post latest-wins per device.
    ///
    /// This is a static apply, which is TERMINAL: unlike an effect frame there
    /// is no frame behind it to correct a refusal, so a dropped packet here
    /// leaves the device on the color the user just replaced, with nothing
    /// anywhere to say why. It goes through WritePolicy.MustLand, which
    /// bypasses the driver's dedup - the device may have slept, reset or been
    /// handed back to its firmware since the cache was filled - and retries to
    /// a bounded deadline before saying so loudly.</summary>
    public void PushFrame(IRgbDevice dev)
    {
        StaticPushed?.Invoke();   // the STORED frame changed, whether or not it can go out now
        if (IsClientOwned(dev)) { HeldByClient(dev); return; }
        // Lights off: the stored frame IS the record (the caller has already
        // changed it), and the relight paints it. Running non-zone channels
        // are told the statics moved, exactly as for a write.
        if (_suppressed) { Engine.InvalidateBase(dev); HeldDark(dev); return; }
        NoteWritten(dev);
        _blackedOut.TryRemove(dev, out _);
        var snap = (Rgb[])FrameFor(dev).Clone();
        Engine.InvalidateBase(dev);   // running non-zone channels re-snapshot the edited statics
        Applier.Post(LaneOf(dev), dev, () =>
        {
            // Asked again at execution: this write may have been queued before
            // a client claimed the device, and landing it now would paint over
            // the client's picture - the thing the claim exists to prevent.
            if (IsClientOwned(dev)) return;
            // ...or before the lights went off. The black is queued behind
            // this under the same key, so it normally replaces it; this is for
            // the one already taken off the queue.
            if (_suppressed) return;
            // Calibration then master brightness, on the CLONE only. The stored
            // frame keeps the color the user actually picked, so trimming a
            // device never rewrites their saved colors.
            Master.Finish(dev, snap);
            // The gate is held across the retries, not just the first attempt:
            // it is the boundary shared with the effect workers, and a
            // straggler frame landing between two of our tries would be the
            // thing left showing.
            lock (GateOf(dev))
            {
                bool landed = WritePolicy.MustLand(dev.Name, "static apply", () =>
                {
                    dev.InvalidateCache();
                    // No flash write here. A mouse that can store its colour
                    // (IPersistableLighting) is saved once the lighting has
                    // settled for 30 s (MainViewModel.PersistSettledLighting):
                    // this path used to commit the G403 on every static apply -
                    // each colour-wheel step, each brightness tick, each show step.
                    return DeviceHealth.Attempt(dev, () => dev.SetColors(snap));
                });
                if (landed) _landed[dev] = snap;
                else _landed.TryRemove(dev, out _);
            }
        }, moveToEnd: true);
    }

    /// <summary>Write one zone of a zone-writable device from its stored frame,
    /// so setting one zone never disturbs an effect running on another. Keyed
    /// per (device, offset) so zones coalesce independently.</summary>
    public void PushZone(IZoneWritable zw, IRgbDevice dev, int off, int count) => PushZone(zw, dev, off, count, (dev, off));

    /// <summary>PushZone under a caller-chosen applier key, for a write that
    /// must not be replaced by a later zone push that merely starts at the
    /// same LED (see RestoreStatics).</summary>
    void PushZone(IZoneWritable zw, IRgbDevice dev, int off, int count, object key)
    {
        StaticPushed?.Invoke();
        if (IsClientOwned(dev)) { HeldByClient(dev); return; }
        if (_suppressed) { Engine.InvalidateBase(dev); HeldDark(dev); return; }   // recorded, not written: see PushFrame
        NoteWritten(dev);
        var frame = FrameFor(dev);
        var slice = new Rgb[count];
        for (int i = 0; i < count; i++) slice[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;
        Applier.Post(LaneOf(dev), key, () =>
        {
            if (IsClientOwned(dev) || _suppressed) return;   // queued before the takeover or the black: see PushFrame
            // The slice is a copy of the stored range, so finishing it here
            // trims the hardware without touching what was stored.
            // The slice starts at device LED `off`, so the trim is told
            // where it sits: a zone with its own trim gets that one rather
            // than whatever governs the top of the device.
            Master.Finish(dev, slice, off);
            // Terminal for the same reason PushFrame is: a static zone has no
            // next frame behind it.
            lock (GateOf(dev))
                LandedSlice(dev, off, slice, WritePolicy.MustLand(dev, zw, off, slice, "static zone apply"));
        }, moveToEnd: true);
    }

    /// <summary>A zone write that landed keeps the device's known picture up
    /// to date for that range; one that did not makes it unknown. Only a
    /// picture that began as a whole static frame is kept: a zone alone says
    /// nothing about the rest of the device.</summary>
    void LandedSlice(IRgbDevice dev, int off, Rgb[] slice, bool landed)
    {
        if (!landed) { _landed.TryRemove(dev, out _); return; }
        if (!_landed.TryGetValue(dev, out var picture)) return;
        for (int i = 0; i < slice.Length && off + i < picture.Length; i++) picture[off + i] = slice[i];
    }

    /// <summary>The write boundary's half of "an SDK client holds this device".
    /// A static push arriving while a client paints the device is kept as the
    /// user's intent in the stored frame - the client's release restores from
    /// it - and not written, whoever asked: the colour picker used to reach
    /// the hardware straight through here while every profile apply and effect
    /// start was refused, and the device flickered between the two writers.</summary>
    void HeldByClient(IRgbDevice dev) =>
        Log.Occasional($"held:{dev.Name}", "lighting",
            $"{dev.Name}: an SDK client holds this device - static colours are kept for when it lets go, not written");

    /// <summary>Repaint a range with its stored static colors: the zone alone
    /// when the device can address zones, else the whole frame.
    ///
    /// Keyed by the exact range: under PushZone's (device, offset) key, a colour
    /// picked on the first zone right after replacing a whole-device effect
    /// replaced this restore in the queue (latest wins per key), and the rest of
    /// the old range kept the effect's last frame - the very thing it repairs.</summary>
    public void RestoreStatics(IRgbDevice dev, int off, int count)
    {
        if (dev is IZoneWritable zw) PushZone(zw, dev, off, count, (dev, "statics", off, count));
        else PushFrame(dev);
    }

    /// <summary>Put the stored statics back on whatever part of [off, off+count)
    /// of a zone device no effect channel is animating when the write runs.
    ///
    /// This is the cleanup after an effect is replaced by a smaller one. A zone
    /// device writes each channel's range alone, so when a whole-device effect
    /// gave way to one on a single zone, the rest of the device was left on the
    /// old effect's last frame with nothing ever writing it again. Which LEDs
    /// are free is decided under the gate at execution, not when this is
    /// posted: "All fans + part" starts one channel per fan right after the
    /// first one stops the whole-device effect, and a restore computed up front
    /// would paint statics over fans whose channels started a moment later.
    /// A channel started after this runs writes after it, behind the gate, so
    /// either way the effect is what is left showing.
    ///
    /// Not a user colour change (the stored frame is untouched), so it raises
    /// no StaticPushed. Keyed by the exact range, so it neither replaces nor
    /// is replaced by a static zone push that merely starts at the same LED.</summary>
    public void RestoreUncovered(IZoneWritable zw, IRgbDevice dev, int off, int count)
    {
        if (count <= 0) return;
        if (IsClientOwned(dev)) { HeldByClient(dev); return; }
        if (_suppressed) { HeldDark(dev); return; }   // nothing to record: the stored frame did not change
        NoteWritten(dev);
        // Snapshotted here, on the thread that edits the frame, like PushZone.
        var frame = FrameFor(dev);
        var slice = new Rgb[count];
        for (int i = 0; i < count; i++) slice[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;
        Applier.Post(LaneOf(dev), (dev, "uncovered", off, count), () =>
        {
            if (IsClientOwned(dev) || _suppressed) return;   // queued before the takeover or the black: see PushFrame
            Master.Finish(dev, slice, off);
            lock (GateOf(dev))
            {
                // The engine's lock is taken inside the gate here. Nothing
                // takes the pair the other way round: the workers only ever
                // take the engine lock outside their gate.
                var running = Engine.ChannelsFor(dev);
                int end = off + count, at = off;
                while (at < end)
                {
                    int next = end;
                    bool covered = false;
                    foreach (var c in running)
                    {
                        if (at >= c.Offset && at < c.Offset + c.Count) { at = c.Offset + c.Count; covered = true; break; }
                        if (c.Offset > at && c.Offset < next) next = c.Offset;
                    }
                    if (covered) continue;
                    var part = slice[(at - off)..(next - off)];
                    LandedSlice(dev, at, part, WritePolicy.MustLand(dev, zw, at, part, "static zone apply"));
                    at = next;
                }
            }
        }, moveToEnd: true);
    }

    /// <summary>Paint a frame an SDK client sent us, without disturbing the
    /// user's stored colors: what they had is coming back when the client goes
    /// away, so this must not overwrite the thing we restore FROM.
    ///
    /// Master brightness still applies. A client asking for full white on a rig
    /// the user has dimmed to 20% should not be the one thing that ignores the
    /// slider.</summary>
    public void PushExternalFrame(IRgbDevice dev, int offset, IReadOnlyList<Rgb> colors)
    {
        NoteWritten(dev);
        int count = Math.Min(colors.Count, Math.Max(0, dev.LedCount - offset));
        if (count <= 0) return;

        // Record it in this client's working picture of the device first, so
        // every later partial write composes over it. Seeded from the user's
        // statics: a client that only ever paints one zone leaves the rest of
        // the device looking the way it found it.
        var live = _external.GetOrAdd(dev, d => (Rgb[])FrameFor(d).Clone());

        // Always written WHOLE, from this client's accumulated picture, under
        // ONE applier key per device. Zone-capable devices used to take a
        // partial as a SetZone under its own (dev, "ext", offset) key, and the
        // two key shapes could not be ordered against each other: the applier
        // replaces a re-posted key in place, so behind a busy lane "full red,
        // zone blue, full green" ran green then blue, and "zone blue, full
        // red, zone green" ran green then red - either way the LED ended on
        // the older write. One key means one queue position and the newest
        // post always carries every earlier one's pixels. It costs nothing
        // real: a claimed device has no effect running on it (BeginExternal
        // stops them), so nothing outside the client's range is disturbed, and
        // the zone drivers dedup per zone, so an unchanged zone is not
        // re-sent.
        //
        // Mutate, snapshot AND queue under the one lock. Two clients on one
        // device (ownership allows it) could otherwise interleave as
        // A-mutates, A-snapshots, B-mutates, B-snapshots, B-queues, A-queues -
        // and A's older snapshot, which does not contain B's pixels, replaces
        // B's in the queue. B's write is then simply never sent.
        lock (live)
        {
            for (int i = 0; i < count && offset + i < live.Length; i++) live[offset + i] = colors[i];
            if (!_suppressed) QueueExternal(dev, live);
        }
    }

    // Caller holds the client's shadow lock so capture and queue preserve order.
    void QueueExternal(IRgbDevice dev, Rgb[] live)
    {
        var whole = (Rgb[])live.Clone();
        Applier.Post(LaneOf(dev), (dev, "ext"), () =>
        {
            lock (GateOf(dev))
            {
                // Check at execution too: a frame queued before the blackout
                // must not land after it, nor after this claim was released.
                if (_suppressed || !_external.TryGetValue(dev, out var current)
                    || !ReferenceEquals(current, live)) return;
                Master.Finish(dev, whole);
                // The client's picture, not the user's colour: the settled
                // save must not store it, and the device is lit again.
                _landed.TryRemove(dev, out _);
                _blackedOut.TryRemove(dev, out _);
                DeviceHealth.Attempt(dev, () => dev.SetColors(whole));
            }
        }, moveToEnd: true);
    }

    /// <summary>Put a client's latest picture on the device again (after an
    /// exit preview or an identify blink drew over it). False when the
    /// client has not painted it yet, so there is no picture to give back.</summary>
    public bool ReplayExternal(IRgbDevice dev)
    {
        if (!_external.TryGetValue(dev, out var live)) return false;
        lock (live) if (!_suppressed) QueueExternal(dev, live);
        return true;
    }

    /// <summary>The client's latest picture, finished for the hardware, or
    /// null when no client has painted this device.</summary>
    Rgb[]? ClientPicture(IRgbDevice dev)
    {
        if (!_external.TryGetValue(dev, out var live)) return null;
        Rgb[] copy;
        lock (live) copy = (Rgb[])live.Clone();
        Master.Finish(dev, copy);
        return copy;
    }

    /// <summary>Black the device WITHOUT touching its stored frame (lights-off).
    ///
    /// The most terminal write there is, and the one whose failure users
    /// actually see: nothing follows a lights-off, so a refused packet leaves
    /// the machine sleeping with a color still lit. Must-land, therefore -
    /// dedup bypassed (a device that reverted to its firmware's boot rainbow
    /// still has "black" in the driver's cache) and retried to a deadline.
    ///
    /// And past that deadline, while the lights are still meant to be off.
    /// One 400 ms attempt was all a black ever got: a DRAM stick behind
    /// another tool's two-second SMBus hold, or a device busy with something
    /// else at the moment of the lock, refused it and then stayed lit all
    /// night, because nothing comes after a lights-off. A refused black is
    /// tried again a few times with a growing wait (BlackRetryMs, doubled),
    /// and the retry gives up the moment the dark span it belongs to ends or
    /// the device instance is replaced.</summary>
    public void PushBlack(IRgbDevice dev) => PushBlack(dev, 0);

    /// <summary>Ask every device that can drop our lighting on its own
    /// (ISelfRevertingLighting) whether it still shows it, and put it back if
    /// not: the black while the lights are off, the stored colour otherwise.
    /// A device running an effect needs no write - its driver has forgotten what
    /// it showed, so the effect's next frame goes out. Each check runs on the
    /// device's lane under its write gate, so it never reads the controller
    /// between two halves of someone else's write.</summary>
    /// <param name="likelyReset">Something that usually means a reset just
    /// happened (a display change): a device that cannot report its state
    /// (CanTell false) is written again without asking. Otherwise such a
    /// device is skipped - asking it says nothing.</param>
    public void RepairReverts(IEnumerable<IRgbDevice> devices, bool likelyReset = false)
    {
        foreach (var dev in devices)
        {
            if (dev is not ISelfRevertingLighting r || !HasBeenWritten(dev)) continue;
            if (!likelyReset && !r.CanTell) continue;
            Applier.Post(LaneOf(dev), (dev, "revert-check"), () =>
            {
                if (IsClientOwned(dev)) return;   // the client's picture: its next frame is the repair
                lock (GateOf(dev))
                {
                    bool blind = !r.CanTell;
                    if (!blind && r.ShowingOurs() != false) return;
                    if (blind) dev.InvalidateCache();   // so a running effect's next frame goes out too
                    else Log.Warn("lighting", $"{dev.Name}: its controller dropped our lighting on its own (a driver reset?) - putting it back");
                    _landed.TryRemove(dev, out _);
                    if (!_suppressed && Engine.ChannelsFor(dev).Count > 0) return;
                    var frame = _suppressed ? new Rgb[dev.LedCount] : (Rgb[])FrameFor(dev).Clone();
                    if (!_suppressed) Master.Finish(dev, frame);
                    bool landed = WritePolicy.MustLand(dev.Name, "revert repair", () =>
                    {
                        dev.InvalidateCache();
                        return DeviceHealth.Attempt(dev, () => dev.SetColors(frame));
                    });
                    if (landed && !_suppressed) _landed[dev] = frame;
                }
            });
        }
    }

    /// <summary>Retries after the first refused black, and the first wait.
    /// Settable only for the harness.</summary>
    internal const int BlackRetries = 3;
    internal static int BlackRetryMs = 1000;

    void PushBlack(IRgbDevice dev, int retry)
    {
        var black = new Rgb[dev.LedCount];
        // Written by this app, even if nothing else ever was: the device is
        // showing OUR black now, not whatever its firmware had (see
        // HasBeenWritten, which an exit preview's put-back consults).
        NoteWritten(dev);
        _blackedOut[dev] = 0;
        int epoch = Volatile.Read(ref _darkEpoch), instances = Volatile.Read(ref _instanceEpoch);
        // No Master.Finish here, and that is deliberate rather than an
        // oversight: black is a fixed point of the whole write-boundary
        // transform. Gamma of zero is zero, any gain times zero is zero, and a
        // ceiling never raises a value - just as master brightness has never
        // been applied here, because scaling zero is still zero. Running the
        // transform would be provably identical and only invite the question
        // of whether a trimmed device can fail to turn off.
        Applier.Post(LaneOf(dev), dev, () =>
        {
            bool landed;
            lock (GateOf(dev))
            {
                _landed.TryRemove(dev, out _);
                landed = WritePolicy.MustLand(dev, black, "lights off");
            }
            if (landed || retry >= BlackRetries || !_suppressed) return;
            int wait = BlackRetryMs << retry;
            Log.Info("lighting", $"{dev.Name}: the lights-off black was refused - trying again in {wait} ms");
            _ = Task.Delay(wait).ContinueWith(_ =>
            {
                // Still the same dark, and still the same device instance.
                // A wake in between owns the device now, and a rescan has
                // replaced it (its own suppressed rescan blacks the new one).
                if (!_suppressed || epoch != Volatile.Read(ref _darkEpoch)
                    || instances != Volatile.Read(ref _instanceEpoch)) return;
                PushBlack(dev, retry + 1);
            }, TaskScheduler.Default);
        }, moveToEnd: true);
    }

    /// <summary>Drive a device to one flat reference color for the CALIBRATION
    /// AID, without touching its stored frame. See CalibrationAid for the
    /// session around this; this is just the write.
    ///
    /// The color goes through Master.Finish exactly like every other frame, and
    /// that is the entire point of the aid: the user is looking at the trimmed
    /// output while they move the sliders, so the patch has to be what the
    /// hardware would really show, not the raw request. A raw patch would let
    /// them "match" devices that are still mismatched the moment the aid
    /// closes.
    ///
    /// Terminal, so must-land: the aid puts a color up and then nothing else
    /// writes until the user moves something. A dropped packet would leave one
    /// device showing the previous patch, which is the worst possible outcome
    /// for a screen whose entire job is comparing devices side by side.
    ///
    /// Lights off is honoured like everywhere else, with one exception the aid
    /// asks for by passing <paramref name="darkEpoch"/>: a calibration the user
    /// OPENED inside a dark window is them asking for the patches, so it may
    /// light them for that dark span (DarkEpoch at its start). A dark that
    /// began under an open calibration wins - its black stays, and a slider
    /// moved afterwards no longer relights the patch for the rest of the
    /// night. The aid's restore puts the black back either way.</summary>
    public void PushReference(IRgbDevice dev, Rgb color, int darkEpoch = 0)
    {
        if (!MayLight(darkEpoch)) { HeldDark(dev); return; }
        var frame = new Rgb[dev.LedCount];
        for (int i = 0; i < frame.Length; i++) frame[i] = color;
        Applier.Post(LaneOf(dev), (dev, "calref"), () =>
        {
            if (!MayLight(darkEpoch)) return;   // the lights went off after this was queued
            Master.Finish(dev, frame);
            lock (GateOf(dev))
            {
                _landed.TryRemove(dev, out _);
                WritePolicy.MustLand(dev, frame, "calibration reference");
            }
        }, moveToEnd: true);
    }

    /// <summary>Put back what the device should be showing NOW, after
    /// something drew over it (an identify blink, an exit preview, a header
    /// test). The CALLER holds the device's write gate on its lane: the answer
    /// is decided here, at the moment of writing, not when the job was posted.
    ///
    /// Three answers, in order: the lights-off black while the lights are off;
    /// an SDK client's own picture while one owns the device (nothing at all
    /// if it has not painted yet - its first frame is on the way); otherwise
    /// the user's lighting as composed now (a running effect paints over it the
    /// moment the gate is released). Each of the three callers used to write
    /// the user's colours unconditionally, captured when they were posted:
    /// an identify at 3 AM lit the device for the rest of the night, and one
    /// on a device a client held put the user's static under a claim that
    /// refuses every static write.</summary>
    internal void WriteWhatItShows(IRgbDevice dev, string what)
    {
        if (_suppressed)
        {
            _blackedOut[dev] = 0;
            _landed.TryRemove(dev, out _);
            WritePolicy.MustLand(dev, new Rgb[dev.LedCount], what);
            return;
        }
        if (IsClientOwned(dev))
        {
            _landed.TryRemove(dev, out _);
            if (ClientPicture(dev) is { } picture) WritePolicy.MustLand(dev, picture, what);
            return;
        }
        // Still a static picture only while no effect paints over it, so it
        // counts as the stored colour only then.
        bool animated = Engine.ChannelsFor(dev).Count > 0;
        var frame = ComposedFrame(dev);
        Master.Finish(dev, frame);
        bool landed = WritePolicy.MustLand(dev, frame, what);
        _blackedOut.TryRemove(dev, out _);
        if (landed && !animated) _landed[dev] = frame;
        else _landed.TryRemove(dev, out _);
    }

    /// <summary>The same, posted on the device's lane under its gate, keyed
    /// like a static push so the newest of the two wins.</summary>
    public void PutBack(IRgbDevice dev, string what)
    {
        NoteWritten(dev);
        Applier.Post(LaneOf(dev), dev, () =>
        {
            lock (GateOf(dev)) WriteWhatItShows(dev, what);
        }, moveToEnd: true);
    }

    /// <summary>Store the device's colour in its own memory - the user's
    /// STORED colour, finished for the hardware, and only while the device is
    /// known to be showing exactly that.
    ///
    /// The save used to ask the driver to commit whatever it had last been
    /// sent. That was the lock's black after a hand edit in a dark window
    /// left the mouse unrelit, and the G403 then powered up dark for good;
    /// it could as well have been a calibration patch, an identify white or a
    /// frame frozen by the effect breaker. Now the colour is the stored one,
    /// the boundary must have landed exactly it on the device (_landed), and
    /// the driver commits it only if it is also what it last sent
    /// (IPersistableLighting.PersistColor). Any of the three disagreeing
    /// means no write to flash.
    ///
    /// On the device's lane, under its write gate, like every other write: it
    /// ran on the UI thread with neither, and could interleave with an
    /// identify blink between two of its packets.</summary>
    public void PersistStoredColor(IRgbDevice dev, IPersistableLighting persist)
    {
        var want = (Rgb[])FrameFor(dev).Clone();
        Master.Finish(dev, want);
        Applier.Post(LaneOf(dev), (dev, "persist"), () =>
        {
            lock (GateOf(dev))
            {
                // Asked again where it counts: any of these may have started
                // since the timer fired.
                if (_suppressed || IsClientOwned(dev) || CalibrationAid.AnyActive
                    || Engine.ChannelsFor(dev).Count > 0) return;
                if (!_landed.TryGetValue(dev, out var shown) || !shown.AsSpan().SequenceEqual(want))
                {
                    Log.Occasional($"persist-skip:{dev.Name}", "lighting",
                        $"{dev.Name}: not storing the colour onboard - the device is not showing your stored colour right now");
                    return;
                }
                try
                {
                    if (!persist.PersistColor(want))
                        Log.Occasional($"persist-refused:{dev.Name}", "lighting", $"{dev.Name}: the colour was not stored onboard");
                }
                catch (Exception ex) { Log.Warn("lighting", $"{dev.Name}: storing the colour failed: {ex.Message}"); }
            }
        }, moveToEnd: true);
    }

    /// <summary>Full device frame = static colors with every running channel
    /// composited in (what the hardware is actually showing). Each channel's
    /// slice is the worker's last render, copied - the effect is not rendered
    /// a second time on the UI thread per preview pull; the on-demand render
    /// remains only for a channel with no frame yet (just started) or one
    /// idle in baked Lian mode, where the worker renders nothing.</summary>
    public Rgb[] ComposedFrame(IRgbDevice dev) => ComposedFrame(dev, null, null);

    /// <summary>The same frame, reusing buffers the CALLER owns.
    ///
    /// For the desk preview, which pulls this for every device 30 times a second:
    /// the frame clone and the channel list were an allocation per device per
    /// pull, which is most of what an idle app with the preview open was making.
    /// Both buffers are grown as needed and returned, so the caller keeps whatever
    /// it was handed and passes it back next time.
    ///
    /// Caller-owned rather than fields on this controller ON PURPOSE: the support
    /// bundle and the SDK host also compose frames, and shared scratch here would
    /// be shared across threads. Pass null from anywhere that is not a hot loop.</summary>
    public Rgb[] ComposedFrame(IRgbDevice dev, Rgb[]? into, List<EffectEngine.Channel>? channels)
    {
        var src = FrameFor(dev);
        var frame = into != null && into.Length == src.Length ? into : new Rgb[src.Length];
        Array.Copy(src, frame, src.Length);
        if (channels != null) Engine.ChannelsFor(dev, channels);
        else channels = Engine.ChannelsFor(dev);
        Rgb[]? buf = null;
        foreach (var ch in channels)
        {
            if (Engine.TryCopyLastFrame(ch, frame, ch.Offset)) continue;
            // RenderChannel wants a buffer of exactly ch.Count, so this is reused
            // only between channels of the same size. It is the rare path anyway
            // (a channel with no frame yet, or idle in baked Lian mode).
            if (buf == null || buf.Length != ch.Count) buf = new Rgb[ch.Count];
            if (Engine.RenderChannel(ch, buf))
                for (int i = 0; i < buf.Length && ch.Offset + i < frame.Length; i++)
                    frame[ch.Offset + i] = buf[i];
        }
        return frame;
    }

    /// <summary>Stop every channel, then wait for queued writes to land - the
    /// order Rescan and shutdown need before device handles are disposed.</summary>
    public void StopAndDrain(int timeoutMs = 1500)
    {
        // A lights-off retry still waiting must not post after this: the
        // handles may be about to close (see PushBlack).
        Interlocked.Increment(ref _instanceEpoch);
        Engine.StopAll();
        Applier.Drain(timeoutMs);
    }
}
