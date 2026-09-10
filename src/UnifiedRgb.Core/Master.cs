namespace UnifiedRgb.Core;

/// <summary>Global master brightness (0.05–1.0) applied at the last moment
/// before colors reach hardware — the engine scales every animated frame and
/// the app scales every static push, so saved profiles/frames stay unscaled
/// and brightness is non-destructive.
///
/// Finish() below is the complete version of that boundary: per-device
/// calibration and then this dimmer, in that order. New call sites should use
/// Finish; Scale stays public because the range/caching dance in the effect
/// engine and a few device-less callers still need the dimmer on its own.</summary>
public static class Master
{
    static double _brightness = 1.0;

    public static double Brightness
    {
        get => Volatile.Read(ref _brightness);
        set => Volatile.Write(ref _brightness, Math.Clamp(value, 0.05, 1.0));
    }

    /// <summary>Scale a buffer in place (call only on frames/clones about to
    /// be written to hardware, never on stored state).</summary>
    public static void Scale(Rgb[] buf) => Scale(buf, 0, buf.Length);

    /// <summary>Scale one range in place. The engine composes a whole non-zone
    /// device from a pre-scaled static base plus each channel's unscaled slice,
    /// so it needs to scale the slices it just laid down without re-scaling
    /// (and re-rounding) the base underneath them.</summary>
    public static void Scale(Rgb[] buf, int offset, int count)
    {
        double b = Brightness;
        if (b >= 0.999) return;
        int end = Math.Min(buf.Length, offset + count);
        for (int i = Math.Max(0, offset); i < end; i++)
        {
            var c = buf[i];
            buf[i] = new Rgb((byte)(c.R * b + 0.5), (byte)(c.G * b + 0.5), (byte)(c.B * b + 0.5));
        }
    }

    /// <summary>THE write-boundary transform, whole: this device's calibration
    /// trim (the zone's own where a zone has one), then the master dimmer.
    /// Every place that used to call Scale on a frame it knows the device for
    /// calls this instead, so there is exactly one answer to "what happens to
    /// a color between the picker and the wire" and it lives in one method.
    ///
    /// The order is calibration FIRST, master brightness SECOND, and it is not
    /// arbitrary. Calibration describes the device's own transfer curve, so it
    /// has to see the color the user actually picked; run it on an
    /// already-dimmed value and the white balance would shift every time the
    /// master slider moved, which is exactly the inconsistency it exists to
    /// remove. The per-device brightness CAP survives being second, because
    /// master brightness only ever reduces: a value already pinned under the
    /// ceiling stays under it. The full argument is in Calibration.cs.
    ///
    /// Same rule as Scale, and it matters more here, not less: call this only
    /// on frames or clones about to be written to hardware. Applying it to
    /// stored state would bake a hardware trim into the user's saved colors.
    ///
    /// Cost when nothing is calibrated (the default install) is one volatile
    /// read of a null reference, so this is not merely mathematically identity
    /// with the old Scale-only path - it never touches a byte differently.</summary>
    /// <param name="deviceOffset">The DEVICE LED index that buf[bufOffset]
    /// corresponds to. This is what lets the trim know which ZONE of the
    /// device each pixel belongs to, and it is why this takes a device object
    /// rather than the device NAME it used to take: two call sites hand over a
    /// single zone's slice, whose own index 0 is somewhere in the middle of
    /// the device, and a name cannot say where. Pass 0 for a whole-device
    /// frame.</param>
    public static void Finish(IRgbDevice device, Rgb[] buf, int bufOffset, int count, int deviceOffset)
    {
        if (buf == null) return;
        Calibration.Apply(device, buf, bufOffset, count, deviceOffset);
        Scale(buf, bufOffset, count);
    }

    /// <summary>The whole-buffer form. <paramref name="deviceOffset"/> is
    /// still explicit because a whole BUFFER is not always a whole DEVICE: a
    /// zone slice is finished with this overload and its own device offset.</summary>
    public static void Finish(IRgbDevice device, Rgb[] buf, int deviceOffset = 0)
        => Finish(device, buf, 0, buf?.Length ?? 0, deviceOffset);
}
