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
        // Device is normalised at load, but the item may have been built by
        // hand (or deserialised elsewhere) since: a null name must not turn a
        // clone into an NRE in the desk editor's undo snapshot.
        Device = Device ?? "", X = X, Y = Y, W = W, H = H,
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

    /// <summary>Read canvas.json, or the defaults when there is none. The
    /// file is hand-editable, and a deserialised object is not yet a usable
    /// one: an explicit `"Items": null` defeats the property initializer and
    /// reached the app's SyncCanvas (`Items.Count`) during startup, which
    /// threw before the window was up. Everything the engine and the editor
    /// index into is normalised here, at the load boundary, so nothing
    /// downstream has to defend against a shape the file can produce.
    ///
    /// A file that cannot be parsed, or that needed repairing, is copied aside
    /// first (`canvas.json.corrupt-&lt;stamp&gt;`, the HardwareConfig /
    /// ProfileStore convention): the next Save() writes whatever is current
    /// over the original, and a user who hand-edited their desk deserves to
    /// get their text back rather than a silently regenerated file.</summary>
    public static CanvasLayout Load()
    {
        if (!File.Exists(Path)) return new CanvasLayout();

        // Read and parse are split: a read failure is a locked or vanishing
        // file, not a corrupt one - there is nothing to copy aside.
        string text;
        try { text = File.ReadAllText(Path); }
        catch (Exception ex)
        {
            Log.Warn("canvas", $"canvas.json could not be read: {ex.Message}");
            return new CanvasLayout();
        }

        CanvasLayout? loaded;
        try { loaded = JsonSerializer.Deserialize<CanvasLayout>(text); }
        catch (Exception ex)
        {
            KeepCorruptCopy($"unreadable ({ex.Message})");
            return new CanvasLayout();
        }
        if (loaded is null)
        {
            // The literal text `null` parses without throwing and is just as
            // useless as a syntax error.
            KeepCorruptCopy("the file is a JSON null");
            return new CanvasLayout();
        }
        if (loaded.Normalize())
            KeepCorruptCopy("structurally invalid; repaired in memory");
        return loaded;
    }

    /// <summary>Copy the on-disk file beside itself before the defaults (or a
    /// repaired copy) take its place. The stamp keeps successive launches from
    /// overwriting each other's evidence; a copy that fails is only logged,
    /// since refusing to start over a backup problem would be worse than the
    /// corrupt file was.</summary>
    static void KeepCorruptCopy(string why)
    {
        string backup = Path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Copy(Path, backup, overwrite: true); }
        catch (Exception ex) { backup = $"(copy failed: {ex.Message})"; }
        Log.Warn("canvas", $"canvas.json {why} - using defaults for what could not be read; original kept at {backup}");
    }

    /// <summary>Bring a deserialised layout into the shape the rest of the
    /// code assumes. Returns true when anything had to change, which is the
    /// signal that the file did not say what it should have. Each rule pins a
    /// value that would otherwise throw or render nothing: a null list or
    /// entry NREs in ItemFor; a zero/negative/NaN size makes CanvasMapper
    /// divide by it; a canvas of width 0 clamps every device to X=0; a grid
    /// of 0 columns is a modulo by zero.</summary>
    bool Normalize()
    {
        bool changed = false;

        if (Items is null) { Items = new(); changed = true; }
        if (Items.RemoveAll(i => i is null) > 0) changed = true;

        if (Width <= 0) { Width = 1600; changed = true; }
        if (Height <= 0) { Height = 900; changed = true; }

        foreach (var item in Items)
        {
            if (item.Device is null) { item.Device = ""; changed = true; }
            if (!double.IsFinite(item.X)) { item.X = 0; changed = true; }
            if (!double.IsFinite(item.Y)) { item.Y = 0; changed = true; }
            if (!(item.W > 0) || !double.IsFinite(item.W)) { item.W = 200; changed = true; }
            if (!(item.H > 0) || !double.IsFinite(item.H)) { item.H = 100; changed = true; }
            if (item.Rotation is not (0 or 90 or 180 or 270)) { item.Rotation = 0; changed = true; }
            if (item.LedLayout is { } led)
            {
                if (led.Cols < 1) { led.Cols = 1; changed = true; }
                if (led.Rows < 1) { led.Rows = 1; changed = true; }
            }
        }
        return changed;
    }

    public void Save()
    {
        try { SafeFile.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Warn("canvas", $"canvas.json could not be written: {ex.Message}"); }
    }

    public CanvasItem? ItemFor(string deviceName)
    {
        // Load normalises the list, but Items is a settable property and this
        // runs on effect workers: a layout assigned by hand (tests, a future
        // import) must degrade to "not placed", never to an NRE mid-render.
        var items = Items;
        if (items is null) return null;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is { } item && string.Equals(item.Device, deviceName, StringComparison.Ordinal))
                return item;
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
        // Same defence as ItemFor: a clone of an un-normalised layout is a
        // normalised one, not an exception.
        Items = (Items ?? new()).Where(i => i is not null).Select(i => i.Clone()).ToList(),
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
        Items ??= new();   // see ItemFor: never assume the list survived intact

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
