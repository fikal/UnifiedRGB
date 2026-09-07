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

    /// <summary>What a device is being told to show, rate limited per device.
    /// The whole lighting path used to log nothing whatsoever, so a bundle
    /// could say which hardware existed and nothing about what was sent to
    /// it.</summary>
    static void Trace(IRgbDevice dev, string what) =>
        Log.Occasional($"light:{dev.Name}", "lighting", $"{dev.Name}: {what}");

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

    /// <summary>The device's stored static frame (created black on first use).</summary>
    public Rgb[] FrameFor(IRgbDevice d) => _frames.GetOrAdd(d, static k => new Rgb[k.LedCount]);

    /// <summary>An SDK client gave a device back. The next client starts from
    /// the user's lighting again instead of inheriting the last one's pixels.</summary>
    public void ForgetExternal(IRgbDevice dev) => _external.TryRemove(dev, out _);

    /// <summary>Every claim dropped at once (a rescan replaces the instances
    /// these are keyed by, and shutdown wants nothing held).</summary>
    public void ForgetExternalAll() => _external.Clear();

    /// <summary>Drop every stored frame and idle applier lane (device instances
    /// are being replaced; both are keyed by instance). Call after StopAndDrain.</summary>
    public void ForgetFrames()
    {
        _frames.Clear();
        _external.Clear();
        Applier.PruneIdle();
    }

    /// <summary>Write the device's whole stored frame: snapshot (the frame keeps
    /// changing on the UI thread), scale by master brightness on the worker,
    /// post latest-wins per device.</summary>
    public void PushFrame(IRgbDevice dev)
    {
        var snap = (Rgb[])FrameFor(dev).Clone();
        Engine.InvalidateBase(dev);   // running non-zone channels re-snapshot the edited statics
        Applier.Post(LaneOf(dev), dev, () =>
        {
            Master.Scale(snap);
            // A static colour on the mouse is committed to its onboard memory
            // in the same write (the engine streams effect frames without the
            // persist byte; a one-shot static apply would otherwise sit
            // uncommitted until Dispose).
            if (dev is LogitechG403 g) g.SetColors(snap, persist: true);
            else dev.SetColors(snap);
        });
    }

    /// <summary>Write one zone of a zone-writable device from its stored frame,
    /// so setting one zone never disturbs an effect running on another. Keyed
    /// per (device, offset) so zones coalesce independently.</summary>
    public void PushZone(IZoneWritable zw, IRgbDevice dev, int off, int count)
    {
        var frame = FrameFor(dev);
        var slice = new Rgb[count];
        for (int i = 0; i < count; i++) slice[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;
        Applier.Post(LaneOf(dev), (dev, off), () => { Master.Scale(slice); zw.SetZone(off, slice); });
    }

    /// <summary>Repaint a range with its stored static colors: the zone alone
    /// when the device can address zones, else the whole frame.</summary>
    public void RestoreStatics(IRgbDevice dev, int off, int count)
    {
        if (dev is IZoneWritable zw) PushZone(zw, dev, off, count);
        else PushFrame(dev);
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
        int count = Math.Min(colors.Count, Math.Max(0, dev.LedCount - offset));
        if (count <= 0) return;

        // Record it in this client's working picture of the device first, so
        // every later partial write composes over it. Seeded from the user's
        // statics: a client that only ever paints one zone leaves the rest of
        // the device looking the way it found it.
        var live = _external.GetOrAdd(dev, d => (Rgb[])FrameFor(d).Clone());

        // A whole-device write needs no merge, and a device that can address
        // zones takes the slice directly - neither disturbs anything outside it.
        bool wholeDevice = offset == 0 && count == dev.LedCount;
        if (wholeDevice || dev is IZoneWritable)
        {
            var slice = new Rgb[count];
            for (int i = 0; i < count; i++) slice[i] = colors[i];
            lock (live)
                for (int i = 0; i < count && offset + i < live.Length; i++) live[offset + i] = colors[i];
            Master.Scale(slice);
            if (wholeDevice) Applier.Post(LaneOf(dev), (dev, "ext"), () => dev.SetColors(slice));
            else Applier.Post(LaneOf(dev), (dev, "ext", offset),
                              () => ((IZoneWritable)dev).SetZone(offset, slice));
            return;
        }

        // Written whole, so what goes out is this client's accumulated picture,
        // scaled once at the boundary.
        //
        // Mutate, snapshot AND queue under the one lock. The applier coalesces
        // latest-wins per key and both clients here use the same key, so with
        // the queue outside the lock two clients could interleave as
        // A-mutates, A-snapshots, B-mutates, B-snapshots, B-queues, A-queues -
        // and A's older snapshot, which does not contain B's pixels, replaces
        // B's in the queue. B's write is then simply never sent. Ownership
        // explicitly allows two clients on one device, so this is reachable.
        lock (live)
        {
            for (int i = 0; i < count && offset + i < live.Length; i++) live[offset + i] = colors[i];
            var whole = (Rgb[])live.Clone();
            Master.Scale(whole);
            Applier.Post(LaneOf(dev), (dev, "ext"), () => dev.SetColors(whole));
        }
    }

    /// <summary>Black the device WITHOUT touching its stored frame (lights-off).</summary>
    public void PushBlack(IRgbDevice dev)
    {
        var black = new Rgb[dev.LedCount];
        Applier.Post(LaneOf(dev), dev, () => dev.SetColors(black));
    }

    /// <summary>Full device frame = static colors with every running channel
    /// composited in (what the hardware is actually showing). Each channel's
    /// slice is the worker's last render, copied - the effect is not rendered
    /// a second time on the UI thread per preview pull; the on-demand render
    /// remains only for a channel with no frame yet (just started) or one
    /// idle in baked Lian mode, where the worker renders nothing.</summary>
    public Rgb[] ComposedFrame(IRgbDevice dev)
    {
        var frame = (Rgb[])FrameFor(dev).Clone();
        foreach (var ch in Engine.ChannelsFor(dev))
        {
            if (Engine.TryCopyLastFrame(ch, frame, ch.Offset)) continue;
            var buf = new Rgb[ch.Count];
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
        Engine.StopAll();
        Applier.Drain(timeoutMs);
    }
}
