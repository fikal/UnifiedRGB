namespace UnifiedRgb.Core.Devices;

/// <summary>One selectable light part within a single Lian Li fan (its offset
/// and length inside that fan's LED slice).</summary>
public readonly record struct LianFanPart(string Name, int Offset, int Count);

/// <summary>A Lian Li fan device (wireless SL-INF or the wired SL-Infinity hub)
/// that the app's fan editor can drive generically: N identical fans, each a
/// slice of LedsPerFan LEDs split into the same named parts (center/inner,
/// outer ring, optional side glow). Lets one clickable editor serve both.</summary>
public interface ILianFanDevice
{
    int LianFanCount { get; }
    int LianLedsPerFan { get; }
    /// <summary>The parts within one fan, in LED order (offsets relative to the
    /// fan's slice). Wireless: Center(0,8) Outer(8,20) Side(28,16). Wired:
    /// Inner(0,8) Outer(8,12).</summary>
    IReadOnlyList<LianFanPart> LianFanParts { get; }
    /// <summary>Display names for each fan, in stack order (top first).</summary>
    IReadOnlyList<string> LianFanNames { get; }
}
