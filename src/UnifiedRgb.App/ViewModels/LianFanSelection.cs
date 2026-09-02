using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.App;

/// <summary>The Lian Li fan editor's selection state and its geometry math,
/// with no UI or hardware in it: which fan(s) and which part are selected,
/// which LED ranges an apply covers, which zone represents the selection, the
/// part buttons and the drawing counts for the fan model. The main view model
/// keeps the thin bindable surface and the hardware side.
///
/// Two independent dimensions: FAN scope - <see cref="Fan"/> (-1 = all fans) -
/// and PART - <see cref="Part"/> (0 = whole fan, 1..N = the device's parts).
/// "All fans" combines with the current part (All + Outer = every fan's outer
/// ring); it is NOT a part that collapses to the whole device.</summary>
public sealed class LianFanSelection
{
    public int Fan { get; set; }
    public int Part { get; set; }

    /// <summary>The mode the user picked most recently, waiting to be carried
    /// onto the next part they click. One-shot; cleared on device/fan switches.
    /// This is what makes BOTH orders work: part-then-mode (the app's native
    /// flow) and mode-then-part (how people actually think).</summary>
    public EffectChoice? PendingChoice { get; set; }

    /// <summary>Device switch: back to fan 1, whole fan, nothing pending.</summary>
    public void Reset() { Fan = 0; Part = 0; PendingChoice = null; }

    /// <summary>True when an apply must fan out to a specific part on EVERY fan
    /// (All fans + inner/outer) - those zones aren't contiguous, so the single
    /// selected zone can't cover them. "All fans + whole" is contiguous and rides
    /// the whole-device zone, so it's not a fan-out.</summary>
    public bool FanOut => Fan < 0 && Part > 0;

    /// <summary>The LED range of the current part on one fan (the whole fan when
    /// Part is 0 or out of range for this device).</summary>
    public (int Off, int Count) RangeOn(ILianFanDevice dev, int fan)
    {
        int per = dev.LianLedsPerFan;
        return Part >= 1 && Part <= dev.LianFanParts.Count
            ? (fan * per + dev.LianFanParts[Part - 1].Offset, dev.LianFanParts[Part - 1].Count)
            : (fan * per, per);
    }

    /// <summary>The device ranges an apply (color/effect) writes: one range per
    /// fan (all fans, or just the selected one), covering the whole fan or just
    /// the chosen part.</summary>
    public List<(int off, int cnt)> ApplyRanges(ILianFanDevice dev)
    {
        var fans = Fan < 0 ? Enumerable.Range(0, dev.LianFanCount) : Enumerable.Range(Fan, 1);
        return fans.Select(f => RangeOn(dev, f)).ToList();
    }

    /// <summary>The zone that stands for the selection in the wheel/mode UI:
    /// null = the whole device (all fans + whole fan), else the current part on
    /// the selected fan (fan 0 stands in when the scope is "all").</summary>
    public (int Off, int Count)? RepresentativeRange(ILianFanDevice dev)
    {
        if (Fan < 0 && Part == 0) return null;
        return RangeOn(dev, Fan < 0 ? 0 : Fan);
    }

    /// <summary>Human label for the current fan-scope x part (status line).</summary>
    public string Label(ILianFanDevice? d)
    {
        if (d == null) return "";
        string fan = Fan < 0 ? "All fans" : $"Fan {Fan + 1}";
        string part = Part == 0 ? "Whole fan"
            : Part - 1 < d.LianFanParts.Count ? d.LianFanParts[Part - 1].Name : "";
        return $"{fan}  ·  {part}";
    }

    /// <summary>Part buttons for the fan editor: All fans, Whole fan, then each
    /// of the device's parts (center/inner, outer, and side if it has one).
    /// Data-driven so both the 3-part wireless and 2-part wired fans work.</summary>
    public static IReadOnlyList<LianPartButton> Parts(ILianFanDevice? d)
    {
        var list = new List<LianPartButton> { new("All fans", -1), new("Whole fan", 0) };
        if (d != null)
            for (int i = 0; i < d.LianFanParts.Count; i++) list.Add(new(d.LianFanParts[i].Name, i + 1));
        return list;
    }

    /// <summary>Part LED counts for the fan-view drawing: (center/inner, outer,
    /// side, sideInOuter). Side 0 = no side strips. sideInOuter = the side
    /// rectangles are cosmetic and mirror the outer ring (wired SL-Infinity).</summary>
    public static (int Center, int Outer, int Side, bool SideInOuter) PartCounts(ILianFanDevice? d)
    {
        var p = d?.LianFanParts;
        if (p == null || p.Count == 0) return (8, 20, 16, false);
        int center = p[0].Count;
        int outer = p.Count > 1 ? p[1].Count : 0;
        if (p.Count > 2) return (center, outer, p[2].Count, false);   // wireless: real side part
        // Wired SL-Infinity has one outer group (12) that lights the ring AND
        // the side glow (L-Connect's SLInfinityOuter). Draw cosmetic side
        // rectangles that mirror the outer ring so it reads like the wireless.
        return (center, outer, 8, true);
    }
}
