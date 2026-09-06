using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedRgb.Core.Effects;

/// <summary>How an unusual device's LEDs are actually arranged, when the
/// driver's guess is wrong. A 30-LED strip taped along a GPU is a line, not
/// whatever its header reports.</summary>
public sealed class LedLayoutOverride
{
    /// <summary>"strip", "ring" or "grid".</summary>
    public string Shape { get; set; } = "strip";
    public int Cols { get; set; } = 1;
    public int Rows { get; set; } = 1;

    /// <summary>Grid only: every other row runs backwards, which is how most
    /// matrices are actually wired.</summary>
    public bool Serpentine { get; set; }
}

/// <summary>One device's place on the desk. Coordinates are in canvas units,
/// with the rectangle being the space the device occupies; rotation orients the
/// device's own layout inside that rectangle rather than changing it, so what
/// you drag is what it covers.</summary>
public sealed class CanvasItem
{
    public string Device { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 200;
    public double H { get; set; } = 100;

    /// <summary>0, 90, 180 or 270. Anything else is treated as 0.</summary>
    public int Rotation { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }

    public LedLayoutOverride? LedLayout { get; set; }

    public CanvasItem Clone() => new()
    {
        Device = Device, X = X, Y = Y, W = W, H = H,
        Rotation = Rotation, FlipX = FlipX, FlipY = FlipY,
        LedLayout = LedLayout is null ? null : new LedLayoutOverride
        {
            Shape = LedLayout.Shape, Cols = LedLayout.Cols,
            Rows = LedLayout.Rows, Serpentine = LedLayout.Serpentine,
        },
    };
}

/// <summary>The desk: where each device physically sits, so one effect can run
/// across all of them as a single image instead of restarting on each.
///
/// Off by default and byte-identical to the old behaviour when off, because
/// this changes the coordinates every effect renders against and that is not a
/// change to make for someone who did not ask for it.</summary>
public sealed class CanvasLayout
{
    public bool Enabled { get; set; }
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public List<CanvasItem> Items { get; set; } = new();

    /// <summary>The layout the engine reads. Set by the app once at startup and
    /// on every edit. Static because ZonePositions is static and is called from
    /// effect workers, the preview and the app alike; a null here is simply
    /// "no canvas", which is the default state.</summary>
    public static CanvasLayout? Current;

    static string Path => AppPaths.Config("canvas.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static CanvasLayout Load()
    {
        try
        {
            if (!File.Exists(Path)) return new CanvasLayout();
            var loaded = JsonSerializer.Deserialize<CanvasLayout>(File.ReadAllText(Path));
            return loaded ?? new CanvasLayout();
        }
        catch (Exception ex)
        {
            Log.Warn("canvas", $"canvas.json could not be read: {ex.Message}");
            return new CanvasLayout();
        }
    }

    public void Save()
    {
        try { SafeFile.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Warn("canvas", $"canvas.json could not be written: {ex.Message}"); }
    }

    public CanvasItem? ItemFor(string deviceName)
    {
        for (int i = 0; i < Items.Count; i++)
            if (string.Equals(Items[i].Device, deviceName, StringComparison.Ordinal))
                return Items[i];
        return null;
    }

    /// <summary>A device's LED layout override, wherever the canvas is on or
    /// off: fixing where a strip's LEDs actually are is useful on its own, and
    /// should not require turning the desk view on.</summary>
    public static LedLayoutOverride? LedLayoutFor(string deviceName)
        => Current?.ItemFor(deviceName)?.LedLayout;

    public CanvasLayout Clone() => new()
    {
        Enabled = Enabled, Width = Width, Height = Height,
        Items = Items.Select(i => i.Clone()).ToList(),
    };

    /// <summary>Give every device that has no place yet a sensible one, so
    /// turning the canvas on is never a blank rectangle. Devices already placed
    /// are left exactly where they are, and one that has gone away keeps its
    /// entry in case it comes back.
    ///
    /// Deliberately deterministic: the same devices in the same order always
    /// land in the same spots, so it can be tested and so re-running it does
    /// not shuffle a desk the user has already arranged.</summary>
    public void AutoArrange(IEnumerable<IRgbDevice> devices)
    {
        // Rough desk: keyboard along the bottom with the mouse beside it, the
        // case standing to the left with its board, RAM and GPU inside, fans up
        // the right edge where a radiator usually sits.
        var perType = new Dictionary<DeviceType, int>();

        foreach (var device in devices)
        {
            if (ItemFor(device.Name) != null) continue;
            int n = perType.TryGetValue(device.Type, out int c) ? c : 0;
            perType[device.Type] = n + 1;
            Items.Add(SlotFor(device, n));
        }
    }

    /// <summary>Where the n-th device of a type goes. Public for the tests: the
    /// arrangement is the feature, so it is worth pinning.</summary>
    public CanvasItem SlotFor(IRgbDevice device, int indexWithinType)
    {
        double w = Width, h = Height;
        int n = indexWithinType;

        return device.Type switch
        {
            DeviceType.Keyboard => Place(w * 0.22, h * 0.72 + n * h * 0.14, w * 0.46, h * 0.16),
            DeviceType.Mouse => Place(w * 0.72 + n * w * 0.10, h * 0.74, w * 0.07, h * 0.12),
            DeviceType.Motherboard => Place(w * 0.06, h * 0.16 + n * h * 0.30, w * 0.30, h * 0.28),
            DeviceType.Gpu => Place(w * 0.08, h * 0.48 + n * h * 0.10, w * 0.26, h * 0.07),
            DeviceType.Dram => Place(w * 0.22, h * 0.06 + n * h * 0.05, w * 0.12, h * 0.04),
            DeviceType.Fan or DeviceType.Cooler => Place(w * 0.42 + n * w * 0.13, h * 0.10, w * 0.11, h * 0.42),
            _ => Place(w * 0.80, h * 0.10 + n * h * 0.12, w * 0.16, h * 0.10),
        };

        CanvasItem Place(double x, double y, double iw, double ih) => new()
        {
            Device = device.Name,
            // Everything stays inside the desk even when a lot of one type
            // turns up: a device parked off-canvas would render nothing and
            // look like a bug.
            X = Math.Clamp(x, 0, Math.Max(0, w - iw)),
            Y = Math.Clamp(y, 0, Math.Max(0, h - ih)),
            W = iw, H = ih,
        };
    }
}
