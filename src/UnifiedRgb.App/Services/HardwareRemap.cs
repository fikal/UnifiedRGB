using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>Puts a saved profile back onto a device whose LED numbering has
/// changed since it was saved.
///
/// A profile stores LED POSITIONS, and positions move. Putting a 30-LED strip on
/// a spare board header renumbers every LED after it: the GPU ribbon that used to
/// start at LED 8 now starts at 38, so a saved "Matrix on 0..49" keeps painting
/// the LEDs the ribbon used to occupy while the ribbon itself sits outside the
/// range, dark. That is exactly what happened on my desk, and the only way out
/// was to reassign every effect by hand.
///
/// Zone NAMES are stable where indices are not - "GPU Ribbon" is the same ribbon
/// before and after - so a saved range is re-anchored through the name and lands
/// back on the same physical LEDs.
///
/// Nothing here guesses. A range that cannot be placed is reported rather than
/// moved somewhere plausible: silently lighting the wrong LEDs is the bug this
/// is fixing, and doing it confidently would be worse.</summary>
public sealed class HardwareRemap
{
    readonly IReadOnlyList<RgbZone> _live;
    readonly ZoneSpan[] _saved;
    readonly int _savedLeds, _liveLeds;

    /// <param name="savedZones">The layout at save time, or null for a profile
    /// written before layouts were recorded. Without it only the whole-device
    /// case can be re-anchored, which is still the common one.</param>
    /// <param name="savedLeds">The device's LED count at save time - the saved
    /// frame's length. Zero when the profile has no frame for this device.</param>
    public HardwareRemap(IRgbDevice dev, ZoneSpan[]? savedZones, int savedLeds)
    {
        _live = dev.Zones ?? Array.Empty<RgbZone>();
        _saved = savedZones ?? Array.Empty<ZoneSpan>();
        _savedLeds = savedLeds;
        _liveLeds = dev.LedCount;
    }

    /// <summary>The device is laid out differently than when this was saved.
    /// False is the overwhelmingly common case and every caller short-circuits
    /// on it, so an unchanged desk behaves exactly as it did before.</summary>
    public bool LayoutChanged
        => _savedLeds != 0 && _savedLeds != _liveLeds
        || _saved.Length != 0 && (_saved.Length != _live.Count
            || _saved.Where((z, i) => z.Name != _live[i].Name
                                   || z.Offset != _live[i].Offset
                                   || z.Count != _live[i].Count).Any());

    /// <summary>Live zones this profile has nothing to say about - hardware
    /// added since it was saved. Their LEDs keep whatever colour they already
    /// had, and the user is told so they can save the profile again.</summary>
    public IEnumerable<string> UnknownZones
        => _saved.Length == 0 ? Array.Empty<string>()
         : _live.Where(z => !_saved.Any(s => s.Name == z.Name)).Select(z => z.Name);

    /// <summary>Where a saved LED range lives now, or null when it cannot be
    /// placed and the effect has to be dropped.</summary>
    public (int Offset, int Count)? Map(int offset, int count)
    {
        if (offset < 0 || count <= 0) return null;
        if (!LayoutChanged) return offset + count <= _liveLeds ? (offset, count) : null;

        // The range IS a zone. The ordinary case: every effect the UI assigns
        // covers either a zone or the whole device, so this is what a real
        // profile's ranges look like.
        foreach (var s in _saved)
        {
            if (s.Offset != offset || s.Count != count) continue;
            var live = _live.FirstOrDefault(z => z.Name == s.Name);
            if (live != null) return (live.Offset, live.Count);
        }

        // Part of a zone, at the same place inside it - the Lian Li fan-out
        // splits a hub into per-fan ranges this way. Only when the zone is still
        // big enough to hold it; a shrunken zone is a genuine mismatch.
        foreach (var s in _saved)
        {
            if (offset < s.Offset || offset + count > s.Offset + s.Count) continue;
            var live = _live.FirstOrDefault(z => z.Name == s.Name);
            if (live != null && offset - s.Offset + count <= live.Count)
                return (live.Offset + (offset - s.Offset), count);
        }

        // The whole device, which needs no zone layout to recognise. This is the
        // one that rescues profiles saved before layouts were written down: the
        // three on my desk were all "this effect, everywhere".
        if (offset == 0 && count == _savedLeds) return (0, _liveLeds);

        return offset + count <= _liveLeds ? (offset, count) : null;
    }

    /// <summary>Copy a saved frame onto the live one zone by zone. LEDs with no
    /// saved counterpart keep the colour they already have, which is what the
    /// straight truncating copy did for extra LEDs before.</summary>
    public void MapFrame(Rgb[] saved, Rgb[] into)
    {
        if (!LayoutChanged || _saved.Length == 0 || _live.Count == 0)
        {
            Array.Copy(saved, into, Math.Min(saved.Length, into.Length));
            return;
        }
        foreach (var live in _live)
        {
            var src = _saved.FirstOrDefault(s => s.Name == live.Name);
            if (src == null) continue;
            int n = Math.Min(Math.Min(src.Count, live.Count),
                             Math.Min(saved.Length - src.Offset, into.Length - live.Offset));
            if (n > 0) Array.Copy(saved, src.Offset, into, live.Offset, n);
        }
    }
}
