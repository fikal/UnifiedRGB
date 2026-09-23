using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App;

/// <summary>The geometry cache behind the LED preview.
///
/// Positions, footprints, style and aspect depend only on the selected
/// (device, zone), so the 30 Hz pull hands back the SAME instances until the
/// selection changes. That is not a tidiness point, it is the contract:
/// LedPreview compares by ReferenceEquals and skips its per-tick array compares
/// when the arrays are the ones it already had. Hand back fresh arrays every
/// pull and nothing breaks, nothing logs, and the preview quietly does thirty
/// times the work a second - which is the failure mode this codebase keeps
/// finding the hard way, so PreviewCache in the harness pins it.
///
/// Only the colour buffer is refilled per pull, in place; the preview copies
/// what it shows into its own array.
///
/// Extracted from MainViewModel because it is genuinely one thing: seven fields
/// that are only ever read and written together, and nothing else in the
/// view-model touched them.</summary>
sealed class TargetPreview
{
    IRgbDevice? _dev;
    RgbZone? _zone;
    IReadOnlyList<LedPos>? _srcPos;
    IReadOnlyList<LedRect>? _srcGeo;
    Rgb[] _colors = Array.Empty<Rgb>();
    LedPos[] _pos = Array.Empty<LedPos>();
    LedRect[]? _rects;
    PreviewStyle _style;
    double _aspect;

    /// <summary>What nothing selected looks like. A field rather than a fresh
    /// tuple so even this path keeps handing back the same arrays.</summary>
    public static readonly (Rgb[] Colors, LedPos[] Pos, PreviewStyle Style, double Aspect, LedRect[]? Rects) Empty
        = (Array.Empty<Rgb>(), Array.Empty<LedPos>(), PreviewStyle.Dots, 1.6, null);

    /// <summary>The live view of one target. <paramref name="frame"/> is the
    /// composed frame for the whole device; the zone's slice is copied out of
    /// it into the reused colour buffer.</summary>
    public (Rgb[] Colors, LedPos[] Pos, PreviewStyle Style, double Aspect, LedRect[]? Rects) Of(
        IRgbDevice dev, RgbZone? zone, Rgb[] frame)
    {
        int off = zone?.Offset ?? 0;
        int count = zone?.Count ?? dev.LedCount;

        // Rebuild on anything that changes the shape - including the device
        // handing out NEW position or geometry lists, which a rescan does even
        // when the device and zone objects look the same.
        if (!ReferenceEquals(dev, _dev) || !ReferenceEquals(zone, _zone) || _pos.Length != count
            || !ReferenceEquals(dev.LedPositions, _srcPos) || !ReferenceEquals(dev.LedGeometry, _srcGeo))
            Rebuild(dev, zone, off, count);

        var colors = _colors;
        for (int i = 0; i < count; i++)
            colors[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;
        return (colors, _pos, _style, _aspect, _rects);
    }

    void Rebuild(IRgbDevice dev, RgbZone? zone, int off, int count)
    {
        _dev = dev; _zone = zone;
        _srcPos = dev.LedPositions; _srcGeo = dev.LedGeometry;
        _colors = new Rgb[count];

        // Positions normalized to the target's bounding box - the engine's own
        // math, so the preview geometry can never drift from what effects render.
        _pos = EffectEngine.ZonePositions(dev, off, count);

        _style = zone?.IsFan == true ? PreviewStyle.Fan
               : dev.Type == DeviceType.Keyboard ? PreviewStyle.Keys
               : PreviewStyle.Dots;
        _aspect = _style == PreviewStyle.Fan ? 1.0
                : dev.PreviewAspect ?? (_style == PreviewStyle.Keys ? 3.2 : 1.6);

        // Exact per-LED footprints, only when the target is the whole device
        // (zone slices fall back to the position-based heuristic).
        _rects = zone == null && dev.LedGeometry is { } g && g.Count == dev.LedCount ? g.ToArray() : null;
    }
}
