using UnifiedRgb.Core.Net;

namespace UnifiedRgb.Core.Devices;

/*-----------------------------------------------------------*\
| A device exposed by a running OpenRGB server, proxied into  |
| the normal IRgbDevice contract so every effect / profile /  |
| preview feature works on it unchanged. Bridge tier: gets a  |
| friend's exotic hardware lit today; native drivers remain   |
| the promotion path.                                         |
\*-----------------------------------------------------------*/
public sealed class OpenRgbDevice : IRgbDevice, IZoneWritable
{
    readonly OpenRgbClient _client;
    readonly int _index;
    readonly Rgb[] _shadow;
    readonly RgbZone[] _zones;
    readonly object _writeLock = new();
    Rgb[]? _last;

    public string Name { get; }
    public string Vendor { get; }
    public DeviceType Type { get; }
    public int LedCount { get; }
    public IReadOnlyList<RgbZone> Zones => _zones;
    public IReadOnlyList<LedPos>? LedPositions { get; }
    public IReadOnlyList<LedRect>? LedGeometry => null;
    public float? PreviewAspect { get; }

    /// <summary>HID/I2C location string from the server (VID/PID lives here).</summary>
    public string Location { get; }

    public OpenRgbDevice(OpenRgbClient client, OpenRgbClient.DeviceInfo info, int duplicateIndex = 0)
    {
        _client = client;
        _index = info.Index;
        // Effect state, profiles, and disable entries key by name: identical
        // devices (two same-model Razer mice) must not collide.
        Name = (duplicateIndex > 0 ? $"{info.Name} ({duplicateIndex + 1})" : info.Name) + " (OpenRGB)";
        Vendor = string.IsNullOrWhiteSpace(info.Vendor) ? "via OpenRGB" : info.Vendor;
        Type = MapType(info.Type);
        LedCount = Math.Max(info.LedCount, info.Colors.Length);
        Location = info.Location;
        _shadow = new Rgb[LedCount];
        for (int i = 0; i < Math.Min(LedCount, info.Colors.Length); i++)
        {
            uint c = info.Colors[i];
            _shadow[i] = new Rgb((byte)(c & 0xFF), (byte)(c >> 8 & 0xFF), (byte)(c >> 16 & 0xFF));
        }

        int offset = 0;
        var zones = new List<RgbZone>();
        foreach (var z in info.Zones)
        {
            zones.Add(new RgbZone { Name = z.Name, Offset = offset, Count = z.LedCount });
            offset += z.LedCount;
        }
        _zones = zones.Count > 0
            ? zones.ToArray()
            : new[] { new RgbZone { Name = "Device", Offset = 0, Count = LedCount } };

        (LedPositions, PreviewAspect) = BuildPositions(info);

        try { client.SetCustomMode(_index); }
        catch (Exception ex) { Log.Warn("openrgb", $"{Name}: custom mode failed: {ex.Message}"); }
    }

    /// <summary>Positions from matrix zones when the server provides them
    /// (keyboards), else a simple line so wave effects still flow.</summary>
    static (LedPos[]?, float?) BuildPositions(OpenRgbClient.DeviceInfo info)
    {
        int total = Math.Max(info.LedCount, info.Colors.Length);
        if (total == 0) return (null, null);
        var pos = new LedPos[total];
        bool anyMatrix = false;
        int offset = 0;
        foreach (var z in info.Zones)
        {
            if (z.Matrix != null && z.MatrixW > 1 && z.MatrixH > 1)
            {
                anyMatrix = true;
                for (int y = 0; y < z.MatrixH; y++)
                    for (int x = 0; x < z.MatrixW; x++)
                    {
                        uint led = z.Matrix[y * z.MatrixW + x];
                        if (led == 0xFFFFFFFF) continue;
                        int idx = offset + (int)led;
                        if (idx < total)
                            pos[idx] = new LedPos(
                                z.MatrixW > 1 ? (float)x / (z.MatrixW - 1) : 0.5f,
                                z.MatrixH > 1 ? (float)y / (z.MatrixH - 1) : 0.5f);
                    }
            }
            else
            {
                for (int i = 0; i < z.LedCount && offset + i < total; i++)
                    pos[offset + i] = new LedPos(z.LedCount > 1 ? (float)i / (z.LedCount - 1) : 0.5f, 0.5f);
            }
            offset += z.LedCount;
        }
        var first = info.Zones.FirstOrDefault(z => z.Matrix != null);
        float? aspect = anyMatrix && first != null && first.MatrixH > 0
            ? (float)first.MatrixW / first.MatrixH : null;
        return (pos, aspect);
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            int n = Math.Min(colors.Count, LedCount);
            for (int i = 0; i < n; i++) _shadow[i] = colors[i];
            if (_last != null && _last.AsSpan(0, n).SequenceEqual(_shadow.AsSpan(0, n))) return;
            _client.UpdateLeds(_index, _shadow);
            (_last ??= new Rgb[LedCount]).AsSpan().Clear();
            _shadow.CopyTo(_last, 0);
        }
    }

    public void SetZone(int offset, IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            for (int i = 0; i < colors.Count && offset + i < LedCount; i++)
                _shadow[offset + i] = colors[i];

            for (int zi = 0; zi < _zones.Length; zi++)
            {
                if (_zones[zi].Offset == offset && _zones[zi].Count == colors.Count)
                {
                    _client.UpdateZoneLeds(_index, zi, _shadow.AsSpan(offset, colors.Count));
                    _last = null;
                    return;
                }
            }
            _client.UpdateLeds(_index, _shadow);            // arbitrary range: full frame
            _last = null;
        }
    }

    static DeviceType MapType(int t) => t switch
    {
        0 => DeviceType.Motherboard,
        1 => DeviceType.Dram,
        2 => DeviceType.Gpu,
        3 => DeviceType.Cooler,
        4 => DeviceType.LedController,   // LED strip
        5 => DeviceType.Keyboard,
        6 => DeviceType.Mouse,
        _ => DeviceType.Other,
    };

    public void Dispose() { }            // connection is owned by OpenRgbLink
}
