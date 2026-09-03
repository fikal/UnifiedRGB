using System.Runtime.CompilerServices;

namespace UnifiedRgb.Core.Effects;

/// <summary>Per-channel geometry cache (B3.5). A channel's LedPos[] never
/// changes after ZonePositions builds it, yet effects re-derived the same
/// position-only math per LED per frame forever — the diagonal coordinate in
/// ~14 render loops, Atan2/radius in the radial effects, min/max scans.
///
/// Keyed WEAKLY on the position array itself (ConditionalWeakTable), so no
/// IEffect API change was needed: an effect calls Geo.Diag(pos) once per
/// frame and indexes the cached array. Entries die with their channel.
/// GetValue is thread-safe; a rare duplicate build on a race is harmless.</summary>
static class Geo
{
    sealed class Cache
    {
        public double[] Diag = Array.Empty<double>();     // (X + Y) / 2
        public double[] Angle = Array.Empty<double>();    // Atan2(Y-.5, X-.5)
        public double[] Radius = Array.Empty<double>();   // distance from center
        public double MinY, MaxY;
    }

    static readonly ConditionalWeakTable<LedPos[], Cache> _cache = new();

    static Cache Build(LedPos[] pos)
    {
        var c = new Cache
        {
            Diag = new double[pos.Length],
            Angle = new double[pos.Length],
            Radius = new double[pos.Length],
            MinY = double.MaxValue,
            MaxY = double.MinValue,
        };
        for (int i = 0; i < pos.Length; i++)
        {
            double x = pos[i].X, y = pos[i].Y;
            c.Diag[i] = (x + y) * 0.5;
            double dx = x - 0.5, dy = y - 0.5;
            c.Angle[i] = Math.Atan2(dy, dx);
            c.Radius[i] = Math.Sqrt(dx * dx + dy * dy);
            if (y < c.MinY) c.MinY = y;
            if (y > c.MaxY) c.MaxY = y;
        }
        if (pos.Length == 0) { c.MinY = 0; c.MaxY = 1; }
        return c;
    }

    public static double[] Diag(LedPos[] pos) => _cache.GetValue(pos, Build).Diag;
    public static double[] Angle(LedPos[] pos) => _cache.GetValue(pos, Build).Angle;
    public static double[] Radius(LedPos[] pos) => _cache.GetValue(pos, Build).Radius;

    /// <summary>True when the channel has no vertical extent - a strip (GPU
    /// ribbon, light bar, a single fan ring) where every LED shares one Y.
    /// Effects that animate along Y collapse there: whole regions light in
    /// lockstep because their fall coordinate is constant. Those effects use
    /// this to fall along the strip's own axis instead.</summary>
    public static bool IsFlat(LedPos[] pos)
    {
        var c = _cache.GetValue(pos, Build);
        return c.MaxY - c.MinY <= 0.3;
    }

    public static (double Min, double Max) YRange(LedPos[] pos)
    {
        var c = _cache.GetValue(pos, Build);
        return (c.MinY, c.MaxY);
    }
}
