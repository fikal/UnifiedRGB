using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Core.Effects;

/// <summary>Temperature-reactive lighting: green when cool through amber to
/// red when hot, riding the hottest of CPU/GPU. The glow "beats" faster as
/// the machine works harder — an AIO that shows the load. Speed = pulse
/// depth/urgency. Falls back to a slow dim blue breath when no sensor is
/// available (non-elevated / unsupported), so it's visibly alive but
/// obviously not reading a temperature.</summary>
public sealed class TempGlow : IEffect
{
    public string Name => "Temp Glow";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;

    const double CoolC = 35, HotC = 85;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        SensorHub.TouchTemps();   // temp only: don't arm the Cooling-pane sweep
        double? temp = SensorHub.HottestC;

        Rgb color;
        double pulse;
        if (temp is double c)
        {
            double x = Math.Clamp((c - CoolC) / (HotC - CoolC), 0, 1);
            double hue = 120.0 * (1.0 - x);                    // green -> red
            // Heartbeat: rate 0.4Hz cool -> 2.2Hz hot; depth grows with heat.
            double rate = 0.4 + 1.8 * x;
            // Speed is pulse depth, not direction: Reverse (negative speed)
            // used to clamp it to 0 and silently stop the heartbeat.
            double depth = (0.10 + 0.35 * x) * Math.Clamp(Math.Abs(speed), 0, 2);
            pulse = 1.0 - depth * (0.5 + 0.5 * Math.Sin(t * rate * Math.PI * 2));
            color = ColorUtil.HsvToRgb(hue, 1.0, 1.0);
        }
        else
        {
            // No sensor: unmistakably "no data", not "cool".
            pulse = 0.25 + 0.15 * Math.Sin(t * 0.8);
            color = ColorUtil.HsvToRgb(220, 0.9, 1.0);
        }

        var final = ColorUtil.Scale(color, pulse);
        for (int i = 0; i < buf.Length; i++) buf[i] = final;
    }
}
