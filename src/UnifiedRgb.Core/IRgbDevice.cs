namespace UnifiedRgb.Core;

/// <summary>A named, addressable region of LEDs within a device (a fan ring, a
/// keyboard block, an ARGB header, etc.).</summary>
public sealed class RgbZone
{
    public required string Name { get; init; }

    /// <summary>Index of this zone's first LED in the device-wide LED array.</summary>
    public required int Offset { get; init; }

    /// <summary>Number of LEDs in this zone.</summary>
    public required int Count { get; init; }

    /// <summary>True for an addressable fan ring — surfaces the fan visualizer.</summary>
    public bool IsFan { get; init; }
}

/// <summary>Normalized physical position of an LED within its device
/// (0..1 in both axes, origin top-left). Lets effects run over real
/// geometry (diagonal waves across a keyboard, rings on a fan).</summary>
public readonly record struct LedPos(float X, float Y);

/// <summary>Normalized physical footprint of an LED (center + size, all as
/// fractions of the device's bounding box). W == 0 marks a hidden LED (e.g.
/// ISO-only keys on an ANSI board).</summary>
public readonly record struct LedRect(float X, float Y, float W, float H);

/// <summary>Everything the UI needs to show and drive one physical device,
/// regardless of transport (HID, WinUSB, SMBus, I2C).</summary>
public interface IRgbDevice : IDisposable
{
    string Name    { get; }
    string Vendor  { get; }
    DeviceType Type { get; }

    /// <summary>Total number of individually addressable LEDs.</summary>
    int LedCount { get; }

    /// <summary>Named zones mapping into the device-wide LED array.</summary>
    IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>Physical position of each LED (length == LedCount), or null
    /// if the device has no meaningful 2-D layout (effects fall back to a
    /// 1-D spread by index).</summary>
    IReadOnlyList<LedPos>? LedPositions => null;

    /// <summary>Width/height ratio of the device's physical layout, for
    /// aspect-correct previews (null = let the UI guess).</summary>
    float? PreviewAspect => null;

    /// <summary>Exact per-LED footprints (length == LedCount) for faithful
    /// previews — keycap widths, logo bars, etc. Null = position dots only.</summary>
    IReadOnlyList<LedRect>? LedGeometry => null;

    /// <summary>Push a full frame of per-LED colors (length == LedCount).
    /// Implementations should no-op if the frame is unchanged.
    ///
    /// TRUE means the frame reached the device, OR was correctly skipped
    /// because the device is already showing exactly it. FALSE means the
    /// device REFUSED it and is still showing something else.
    ///
    /// This return is the whole reason anything above a driver can tell a
    /// landed frame from a dropped one. It used to be void, so a refusal was
    /// invisible outside the driver that saw it: the engine cached frames it
    /// had not delivered, and a lights-off command that never went out looked
    /// exactly like one that did. Drivers that genuinely cannot tell (a
    /// one-way bus with no acknowledgement) say so in a comment and return
    /// true rather than guessing.
    ///
    /// A refusal RETURNS. Throwing is reserved for a device that is gone for
    /// good, because the engine's breaker counts throws and stops the channel
    /// permanently after 300 of them. See Devices/WritePolicy.cs for the rest
    /// of the contract - dedup after success, backoff, and the must-land path
    /// for writes with no next frame behind them.</summary>
    bool SetColors(IReadOnlyList<Rgb> colors);

    /// <summary>Forget what this device is believed to be showing, so the next
    /// SetColors goes out even if it is identical to the last one.
    ///
    /// Two callers. A driver that changes the hardware's mode invalidates its
    /// own cache, because the device no longer shows what the cache says. And
    /// WritePolicy.MustLand calls this before every attempt at a terminal
    /// write - a static apply, a lights-off, an exit behaviour - so a stale
    /// cache can never turn "the device already has this" into a success for a
    /// frame the hardware never received.
    ///
    /// The default is correct for a device that caches nothing.</summary>
    void InvalidateCache() { }

    /// <summary>Convenience: set every LED to one color. Returns what
    /// SetColors returned, so a caller that cares can still tell.</summary>
    bool SetAll(Rgb color)
        => SetColors(Enumerable.Repeat(color, LedCount).ToArray());
}

/// <summary>Devices that can update a sub-range of LEDs in isolation, without
/// disturbing the rest. Lets a zone effect and a per-zone static color coexist
/// on the same physical device (e.g. fans 1+2 on a rainbow while fan 3 is a
/// fixed color) instead of full-frame writers fighting each other.</summary>
public interface IZoneWritable
{
    /// <summary>Update only LEDs [offset, offset+colors.Count); leave the rest
    /// of the device exactly as it is. Same verdict as SetColors: true when
    /// the range reached the device or was correctly deduped, false when the
    /// device refused it.</summary>
    bool SetZone(int offset, IReadOnlyList<Rgb> colors);
}

public enum DeviceType
{
    Motherboard,
    Gpu,
    Dram,
    Keyboard,
    Mouse,
    Cooler,
    Fan,
    LedController,
    Other,
}

/// <summary>Keyboards that can resolve a Windows virtual-key code to the LED
/// under that physical key — powers the reactive typing effects.</summary>
public interface IKeyMappedDevice
{
    /// <summary>Device-wide LED index for a VK code, or -1 if it has no LED.</summary>
    int LedForVk(int vk);
}

/// <summary>A device that can report its own charge. Wireless gear only:
/// wired devices simply do not implement it, which is what keeps the poller
/// off them entirely rather than asking and discarding the answer.</summary>
public interface IBatteryDevice
{
    /// <summary>Charge and whether it is on the charger, or null when the
    /// device did not answer: asleep, out of range, or firmware without a
    /// battery. Null is not zero. A mouse that is merely idle must not read
    /// as flat, or a low-battery rule would fire every night.</summary>
    BatteryReading? ReadBattery();
}

/// <param name="Percent">0..100.</param>
public readonly record struct BatteryReading(int Percent, bool Charging);
