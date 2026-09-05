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
    /// Implementations should no-op if the frame is unchanged.</summary>
    void SetColors(IReadOnlyList<Rgb> colors);

    /// <summary>Convenience: set every LED to one color.</summary>
    void SetAll(Rgb color)
        => SetColors(Enumerable.Repeat(color, LedCount).ToArray());
}

/// <summary>Devices that can update a sub-range of LEDs in isolation, without
/// disturbing the rest. Lets a zone effect and a per-zone static color coexist
/// on the same physical device (e.g. fans 1+2 on a rainbow while fan 3 is a
/// fixed color) instead of full-frame writers fighting each other.</summary>
public interface IZoneWritable
{
    /// <summary>Update only LEDs [offset, offset+colors.Count); leave the rest
    /// of the device exactly as it is.</summary>
    void SetZone(int offset, IReadOnlyList<Rgb> colors);
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
