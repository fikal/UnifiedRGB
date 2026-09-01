using UnifiedRgb.Core.Audio;

namespace UnifiedRgb.Core.Effects;

/*-----------------------------------------------------------*\
| Audio-reactive effects. Both read the shared AudioAnalyzer  |
| (WASAPI loopback + FFT); Touch() in Render keeps the        |
| capture alive only while an audio effect is actually shown. |
| Effects are stateless (instances are shared across          |
| channels), so all smoothing lives in the analyzer.          |
\*-----------------------------------------------------------*/

/// <summary>Spectrum bars: X position picks the frequency band. Devices with
/// real vertical spread (keyboard) fill bottom-up like an equalizer; flat
/// devices (fan rings, strips, single zones) pulse per-band brightness.</summary>
public sealed class AudioBars : IEffect
{
    public string Name => "Audio Bars";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        AudioAnalyzer.Touch();

        // Vertical spread decides equalizer-fill vs pure brightness mode —
        // a per-channel constant, cached (was a full pos[] scan every frame).
        var (yMin, yMax) = Geo.YRange(pos);
        bool fill = yMax - yMin > 0.3;
        double punch = Math.Clamp(speed, 0.25, 4.0);

        for (int i = 0; i < buf.Length; i++)
        {
            float band = AudioAnalyzer.BandAt(pos[i].X);
            double level = Math.Pow(band, 1.0 / punch);
            double hue = 230.0 - pos[i].X * 230.0;          // bass blue -> treble red

            double v;
            if (fill)
            {
                // y: 0 = top row, 1 = bottom; bars rise from the bottom.
                double height = (1.0 - (pos[i].Y - yMin) / (yMax - yMin));
                v = Math.Clamp((level - height) * 6.0 + 0.5, 0, 1) * (0.25 + 0.75 * level);
            }
            else
            {
                v = 0.06 + 0.94 * level;
            }
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, v);
        }
    }
}

/// <summary>Whole target breathes with the music: brightness rides the overall
/// level with an extra kick from the bass. Uses the chosen base color.</summary>
public sealed class AudioPulse : IEffect
{
    public string Name => "Audio Pulse";
    public bool UsesBaseColor => true;
    public bool Bakeable => false;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        AudioAnalyzer.Touch();
        double punch = Math.Clamp(speed, 0.25, 4.0);
        double level = Math.Pow(0.6 * AudioAnalyzer.Level + 0.4 * AudioAnalyzer.Bass, 1.0 / punch);
        var c = ColorUtil.Scale(baseColor, 0.04 + 0.96 * level);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}
