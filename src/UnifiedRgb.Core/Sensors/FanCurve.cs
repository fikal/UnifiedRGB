namespace UnifiedRgb.Core.Sensors;

/// <summary>Which temperature a fan curve follows.</summary>
public enum TempSource { Cpu, Gpu, Hottest }

/// <summary>One point on a fan curve: at TempC, run at DutyPct.</summary>
public readonly record struct CurvePoint(int TempC, int DutyPct);

/*-----------------------------------------------------------*\
| A fan curve: temperature -> duty %, with a named preset or  |
| custom points. DutyAt linearly interpolates between points  |
| and clamps beyond the ends. The engine (SensorHub) samples  |
| the chosen temperature source each tick and applies the     |
| result through LHM, floored at the safety minimum.          |
\*-----------------------------------------------------------*/
public sealed class FanCurve
{
    public string Preset { get; set; } = "Custom";
    public TempSource Source { get; set; } = TempSource.Hottest;
    /// <summary>The lowest duty this fan may run (30 for board headers — the
    /// pump-safe floor; 0 for the GPU, whose coolers are fan-stop capable).</summary>
    public int Floor { get; set; } = 30;
    /// <summary>Ordered low->high by temperature; kept sorted by Set/ctor.</summary>
    public List<CurvePoint> Points { get; set; } = new();

    public FanCurve() { }

    public FanCurve(string preset, TempSource source, IEnumerable<CurvePoint> points, int floor = 30)
    {
        Preset = preset;
        Source = source;
        Floor = floor;
        Points = points.OrderBy(p => p.TempC).ToList();
    }

    /// <summary>Duty percent for a temperature (linear interpolation between
    /// the two surrounding points; flat beyond the first/last point).</summary>
    public int DutyAt(double tempC)
    {
        if (Points.Count == 0) return 0;
        if (tempC <= Points[0].TempC) return Points[0].DutyPct;
        if (tempC >= Points[^1].TempC) return Points[^1].DutyPct;
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            if (tempC <= b.TempC)
            {
                double span = b.TempC - a.TempC;
                double f = span <= 0 ? 0 : (tempC - a.TempC) / span;
                return (int)Math.Round(a.DutyPct + f * (b.DutyPct - a.DutyPct));
            }
        }
        return Points[^1].DutyPct;
    }

    public FanCurve Clone() => new(Preset, Source, Points, Floor);

    /*------------------------ presets ------------------------*\
    | Board headers keep the 30% pump-safe minimum. With a      |
    | floor of 0 (GPU), the idle end of each preset maps down   |
    | to 0% — a proper fan-stop curve: silent until warm.       |
    \*---------------------------------------------------------*/
    public static readonly string[] PresetNames = { "Quiet", "Standard", "High", "Full" };

    public static FanCurve Preset_(string name, TempSource source = TempSource.Hottest, int floor = 30)
    {
        FanCurve c = name switch
        {
            "Quiet" => new("Quiet", source, new CurvePoint[]
                { new(20, 30), new(45, 30), new(60, 40), new(72, 60), new(82, 85), new(90, 100) }, floor),
            "Standard" => new("Standard", source, new CurvePoint[]
                { new(20, 30), new(40, 35), new(55, 50), new(68, 70), new(80, 90), new(90, 100) }, floor),
            "High" => new("High", source, new CurvePoint[]
                { new(20, 45), new(40, 60), new(55, 75), new(68, 90), new(80, 100) }, floor),
            "Full" => new("Full", source, new CurvePoint[]
                { new(20, 100), new(100, 100) }, floor),
            _ => new("Standard", source, new CurvePoint[]
                { new(20, 30), new(40, 35), new(55, 50), new(68, 70), new(80, 90), new(90, 100) }, floor),
        };
        // Fan-stop-capable fans: the preset's idle plateau (30%) drops to the
        // floor so the fan is OFF until the curve actually rises.
        if (floor < 30)
            for (int i = 0; i < c.Points.Count; i++)
                if (c.Points[i].DutyPct <= 30)
                    c.Points[i] = c.Points[i] with { DutyPct = floor };
        return c;
    }

    /// <summary>True if this curve's points still match its named preset
    /// (so the UI can show the preset as selected until the user edits it).</summary>
    public bool MatchesPreset()
    {
        if (Preset == "Custom") return false;
        var p = Preset_(Preset, Source, Floor);
        return p.Points.Count == Points.Count &&
               p.Points.Zip(Points).All(z => z.First == z.Second);
    }
}
