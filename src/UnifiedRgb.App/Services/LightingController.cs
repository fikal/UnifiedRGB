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
    readonly Dictionary<IRgbDevice, Rgb[]> _frames = new();

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
    public Rgb[] FrameFor(IRgbDevice d)
    {
        if (!_frames.TryGetValue(d, out var f))
        {
            f = new Rgb[d.LedCount];
            _frames[d] = f;
        }
        return f;
    }

    /// <summary>Drop every stored frame (device instances are being replaced).</summary>
    public void ForgetFrames() => _frames.Clear();

    /// <summary>Write the device's whole stored frame: snapshot (the frame keeps
    /// changing on the UI thread), scale by master brightness on the worker,
    /// post latest-wins per device.</summary>
    public void PushFrame(IRgbDevice dev)
    {
        var snap = (Rgb[])FrameFor(dev).Clone();
        Applier.Post(LaneOf(dev), dev, () => { Master.Scale(snap); dev.SetColors(snap); });
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

    /// <summary>Black the device WITHOUT touching its stored frame (lights-off).</summary>
    public void PushBlack(IRgbDevice dev)
    {
        var black = new Rgb[dev.LedCount];
        Applier.Post(LaneOf(dev), dev, () => dev.SetColors(black));
    }

    /// <summary>Full device frame = static colors with every running channel
    /// composited in (what the hardware is actually showing).</summary>
    public Rgb[] ComposedFrame(IRgbDevice dev)
    {
        var frame = (Rgb[])FrameFor(dev).Clone();
        foreach (var ch in Engine.ChannelsFor(dev))
        {
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
