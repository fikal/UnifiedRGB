namespace UnifiedRgb.Core;

/// <summary>Alignment snapping, the way a forms designer does it: while you
/// drag, edges and centres pull into line with the surface and with everything
/// already placed, and a guide line shows what you lined up with.
///
/// Pure geometry with no view types, so the LCD designer and the desk canvas
/// share one implementation and it can be tested without a window.</summary>
public static class SnapGuides
{
    /// <summary>How close, in surface units, before it pulls. Small enough to
    /// place something a few pixels off an edge on purpose, large enough that
    /// a deliberate alignment does not need a steady hand.</summary>
    public const double Threshold = 6;

    /// <summary>Every line worth snapping to on one axis: the surface's two
    /// edges and its centre, plus each other item's two edges and centre.</summary>
    public static List<double> Lines(double surfaceSize, IEnumerable<(double Start, double Size)> others)
    {
        var lines = new List<double> { 0, surfaceSize / 2, surfaceSize };
        foreach (var (start, size) in others)
        {
            lines.Add(start);
            lines.Add(start + size / 2);
            lines.Add(start + size);
        }
        return lines;
    }

    /// <summary>The nearest line to any of the anchors, and how far the item
    /// has to move to meet it. Null when nothing is within the threshold.</summary>
    public static double? Nearest(IReadOnlyList<double> lines, ReadOnlySpan<double> anchors,
                                  out double delta, double threshold = Threshold)
    {
        double best = threshold, chosen = 0;
        bool found = false;
        delta = 0;
        for (int i = 0; i < lines.Count; i++)
            foreach (double anchor in anchors)
            {
                double d = Math.Abs(lines[i] - anchor);
                if (d > best) continue;
                best = d; chosen = lines[i]; delta = lines[i] - anchor; found = true;
            }
        return found ? chosen : null;
    }

    /// <summary>Snap one axis. An item can line up by its leading edge, its
    /// centre or its trailing edge, so all three are offered and the closest
    /// wins. Returns where the item's origin ends up and which line it met, so
    /// the caller can draw it.</summary>
    public static (double Value, double? Line) Snap(double origin, double size,
                                                    IReadOnlyList<double> lines,
                                                    double threshold = Threshold)
    {
        Span<double> anchors = stackalloc double[3];
        anchors[0] = origin;
        anchors[1] = origin + size / 2;
        anchors[2] = origin + size;

        double? line = Nearest(lines, anchors, out double delta, threshold);
        return line == null ? (origin, null) : (origin + delta, line);
    }
}
