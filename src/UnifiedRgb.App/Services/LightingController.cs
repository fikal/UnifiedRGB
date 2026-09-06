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

    /// <summary>The device's stored static frame (created black on first use).</summary>
    public Rgb[] FrameFor(IRgbDevice d) => _frames.GetOrAdd(d, static k => new Rgb[k.LedCount]);

    /// <summary>Drop every stored frame and idle applier lane (device instances
    /// are being replaced; both are keyed by instance). Call after StopAndDrain.</summary>
    public void ForgetFrames()
    {
        _frames.Clear();
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

        var slice = new Rgb[count];
        for (int i = 0; i < count; i++) slice[i] = colors[i];
        Master.Scale(slice);

        // A partial write goes out as a zone where the device can do that;
        // otherwise the slice is merged over what the device is showing, so a
        // zone write does not black everything outside it.
        if (offset == 0 && count == dev.LedCount)
        {
            Applier.Post(LaneOf(dev), (dev, "ext"), () => dev.SetColors(slice));
            return;
        }
        if (dev is IZoneWritable zw)
        {
            Applier.Post(LaneOf(dev), (dev, "ext", offset), () => zw.SetZone(offset, slice));
            return;
        }
        var whole = (Rgb[])FrameFor(dev).Clone();
        Master.Scale(whole);
        for (int i = 0; i < count && offset + i < whole.Length; i++) whole[offset + i] = slice[i];
        Applier.Post(LaneOf(dev), (dev, "ext"), () => dev.SetColors(whole));
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
