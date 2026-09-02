namespace UnifiedRgb.Core.Effects;

/// <summary>An animated effect. Given elapsed time and each LED's physical
/// position it fills a per-LED buffer. The same effect drives every device off
/// one shared clock, so all devices stay phase-locked; using real (x,y) makes
/// waves flow diagonally across the actual keyboard/board/fan geometry.</summary>
public interface IEffect
{
    string Name { get; }

    /// <summary>True if the effect tints a user-chosen base color (breathing,
    /// wave) rather than generating its own hues (rainbow).</summary>
    bool UsesBaseColor { get; }

    /// <summary>True if the effect is periodic and self-contained (no live
    /// input), so it can be baked into a fixed frame loop and uploaded to
    /// hardware that loops it (Lian Li wireless fans). Effects driven by live
    /// data (audio, sensors, screen) are NOT bakeable and must stream.</summary>
    bool Bakeable => true;

    /// <summary>False when the speed slider does nothing for this effect - live
    /// mirrors (screen / wallpaper / chroma) and the clock-driven warmth shift.
    /// The UI hides the speed + direction controls for these.</summary>
    bool HasSpeed => true;

    /// <summary>The natural loop period in seconds at the given speed - the
    /// bake window. Default is one pattern rotation; a small seam crossfade in
    /// the baker hides any imperfect periodicity.</summary>
    double LoopSeconds(double speed) => 4.0 / Math.Max(0.1, Math.Abs(speed));

    /// <summary>pos[i] is LED i's normalized position (0..1). Length == buffer.</summary>
    void Render(Rgb[] buffer, LedPos[] pos, double seconds, double speed, Rgb baseColor);

    /// <summary>Device-aware render: the engine always calls this. Effects that
    /// need the device (key-to-LED mapping, true aspect ratio) override it;
    /// everything else falls through to the plain Render.</summary>
    void Render(IRgbDevice device, int offset, Rgb[] buffer, LedPos[] pos,
                double seconds, double speed, Rgb baseColor)
        => Render(buffer, pos, seconds, speed, baseColor);
}

/// <summary>An effect that paints from a user-chosen list of colors rather than
/// a single base color (e.g. Taichi's two halves). The app shows the palette
/// editor and gives each target its own palette instance.</summary>
/// <summary>Render-thread-safe read view over an editable palette. The UI
/// mutates an ObservableCollection (Clear + Add per color); render threads used
/// to read Count then [i] straight off that list and threw mid-rebuild (one
/// dropped frame + a WARN per palette edit, per running channel). This keeps an
/// immutable array snapshot refreshed on every change; the indexer clamps so a
/// Count read from one snapshot can never index out of a newer, shorter one.</summary>
public sealed class LivePalette : IReadOnlyList<Rgb>
{
    volatile Rgb[] _items;

    public LivePalette(System.Collections.ObjectModel.ObservableCollection<Rgb> source)
    {
        _items = source.ToArray();
        source.CollectionChanged += (_, _) => _items = source.ToArray();
    }

    /// <summary>The current snapshot - read it ONCE per frame for consistent Count/[i].</summary>
    public Rgb[] Snapshot => _items;
    public int Count => _items.Length;
    public Rgb this[int index]
    {
        get
        {
            var a = _items;
            if (a.Length == 0) return new Rgb(255, 255, 255);
            return a[(uint)index < (uint)a.Length ? index : a.Length - 1];
        }
    }
    public IEnumerator<Rgb> GetEnumerator() => ((IEnumerable<Rgb>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

public interface IPaletteEffect
{
    IReadOnlyList<Rgb> Palette { get; set; }
}

public sealed class RainbowWave : IEffect
{
    public string Name => "Rainbow Wave";
    public bool UsesBaseColor => false;
    // Hue advances 60 deg/s at speed 1, so one full 360 turn = 6s: bake exactly
    // one turn so the loop closes on the same hue (no crossfade color flash).
    public double LoopSeconds(double speed) => Fx.Loop(6.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            buf[i] = ColorUtil.HsvToRgb(t * speed * 60.0 - d * 540.0, 1.0, 1.0);
        }
    }
}

public sealed class RainbowCycle : IEffect
{
    public string Name => "Rainbow Cycle";
    public bool UsesBaseColor => false;
    // 40 deg/s -> a full 360 hue turn every 9s: bake one whole turn so the baked
    // loop starts and ends on the same color (was 4s = 160 deg, a big hue jump
    // at the seam that the crossfade smeared into a flash of every color).
    public double LoopSeconds(double speed) => Fx.Loop(9.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        var c = ColorUtil.HsvToRgb(t * speed * 40.0, 1.0, 1.0);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

public sealed class Breathing : IEffect
{
    public string Name => "Breathing";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(Math.PI, speed);   // one breath
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double v = 0.5 + 0.5 * Math.Sin(t * speed * 2.0);
        var c = ColorUtil.Scale(baseColor, v);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

public sealed class Wave : IEffect
{
    public string Name => "Wave";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI / 3.0), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double phase = t * speed * 3.0 - d * Math.PI * 4.0;
            double v = 0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(phase));
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

public sealed class Spiral : IEffect
{
    public string Name => "Spiral";
    public bool UsesBaseColor => false;
    public double LoopSeconds(double speed) => Fx.Loop(4.5, speed);   // 80 deg/s -> 360 in 4.5s
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        // angle/radius from device center are position-only — cached per channel.
        var ang = Geo.Angle(pos);
        var rad = Geo.Radius(pos);
        for (int i = 0; i < buf.Length; i++)
        {
            double hue = t * speed * 80.0 + ang[i] * 180.0 / Math.PI + rad[i] * 360.0;
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, 1.0);
        }
    }
}
