using System.Text.Json.Serialization;

namespace UnifiedRgb.Core;

/// <summary>What a device can be left doing once UnifiedRGB stops driving it.
/// Static implies Off, which is the same command with black.</summary>
[Flags]
public enum HardwareExitCaps
{
    None = 0,
    Static = 1,
    Effects = 2,
    ReturnToHardware = 4,
}

/// <summary>A device that can be handed back to its own firmware rather than
/// simply abandoned mid-frame.
///
/// Everything in this app streams, so closing it leaves whatever the last
/// frame happened to be, and a device that resets leaves the firmware's boot
/// rainbow. This is the way out of that: a static colour, an onboard effect,
/// or the device's own saved profile.</summary>
public interface IHardwareModes
{
    HardwareExitCaps ExitCaps { get; }

    /// <summary>Onboard effects this device can be left playing, by name.
    /// Empty unless ExitCaps has Effects.</summary>
    IReadOnlyList<string> HardwareEffects { get; }

    /// <summary>Leave the device showing one colour, with no host talking to
    /// it. Black is "off".</summary>
    void SetHardwareStatic(Rgb color);

    /// <summary>Leave the device playing one of HardwareEffects. The colour is
    /// used by effects that take one and ignored by the rest.</summary>
    void SetHardwareEffect(string name, Rgb? color);

    /// <summary>Tell the device to resume its own saved profile.</summary>
    void ReturnToHardware();
}

// Persisted by NAME, like LcdElementKind: reordering the members must never
// silently remap what someone saved.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExitMode { KeepLast, Static, Effect, Off, ReturnToHardware }

/// <summary>One device's "when the app is closed" choice, stored in
/// hardware.json under the device's name.</summary>
public sealed class ExitBehavior
{
    public ExitMode Mode { get; set; } = ExitMode.KeepLast;
    public string ColorHex { get; set; } = "FFFFFF";
    public string? Effect { get; set; }
}

/// <summary>Turning a stored choice into the call that carries it out.
///
/// Separate from the exit path so the decision is testable without hardware,
/// and deliberately forgiving: a device that has lost the effect named in an
/// old config, or was swapped for one that cannot do what was asked, leaves
/// its last colours rather than throwing on the way out of the process.</summary>
public static class HardwareExit
{
    /// <summary>Carry out one device's choice. Returns a line worth logging,
    /// or null when nothing was sent.</summary>
    public static string? Apply(IRgbDevice device, ExitBehavior? behavior)
    {
        if (behavior == null || behavior.Mode == ExitMode.KeepLast) return null;
        if (device is not IHardwareModes hw) return null;

        var caps = hw.ExitCaps;
        switch (behavior.Mode)
        {
            case ExitMode.Off:
                if (!caps.HasFlag(HardwareExitCaps.Static)) return null;
                hw.SetHardwareStatic(Rgb.Black);
                return "off";

            case ExitMode.Static:
            {
                if (!caps.HasFlag(HardwareExitCaps.Static)) return null;
                var c = Rgb.FromHex(behavior.ColorHex);
                hw.SetHardwareStatic(c);
                return $"static #{c.ToHex()}";
            }

            case ExitMode.Effect:
            {
                if (!caps.HasFlag(HardwareExitCaps.Effects)) return null;
                string? name = Resolve(hw.HardwareEffects, behavior.Effect);
                if (name == null) return null;      // renamed or gone: leave it alone
                hw.SetHardwareEffect(name, Rgb.TryFromHex(behavior.ColorHex, out var ec) ? ec : null);
                return $"effect {name}";
            }

            case ExitMode.ReturnToHardware:
                if (!caps.HasFlag(HardwareExitCaps.ReturnToHardware)) return null;
                hw.ReturnToHardware();
                return "onboard profile";
        }
        return null;
    }

    /// <summary>Match a stored effect name against what the device offers now,
    /// ignoring case so a hand-edited hardware.json still works.</summary>
    public static string? Resolve(IReadOnlyList<string> effects, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        for (int i = 0; i < effects.Count; i++)
            if (string.Equals(effects[i], wanted, StringComparison.OrdinalIgnoreCase))
                return effects[i];
        return null;
    }

    /// <summary>The choices to offer for a device, in the order they should be
    /// listed. Always starts with KeepLast, which is what every device does
    /// today and what an unconfigured device keeps doing.</summary>
    public static List<ExitBehavior> Choices(IRgbDevice device)
    {
        var list = new List<ExitBehavior> { new() { Mode = ExitMode.KeepLast } };
        if (device is not IHardwareModes hw) return list;

        if (hw.ExitCaps.HasFlag(HardwareExitCaps.Static))
        {
            list.Add(new ExitBehavior { Mode = ExitMode.Static });
            list.Add(new ExitBehavior { Mode = ExitMode.Off });
        }
        if (hw.ExitCaps.HasFlag(HardwareExitCaps.Effects))
            foreach (var e in hw.HardwareEffects)
                list.Add(new ExitBehavior { Mode = ExitMode.Effect, Effect = e });
        if (hw.ExitCaps.HasFlag(HardwareExitCaps.ReturnToHardware))
            list.Add(new ExitBehavior { Mode = ExitMode.ReturnToHardware });
        return list;
    }

    /// <summary>How a choice reads in the dropdown.</summary>
    public static string Label(ExitBehavior b) => b.Mode switch
    {
        ExitMode.KeepLast => "Keeps its last colors",
        ExitMode.Static => "Static color",
        ExitMode.Off => "Off",
        ExitMode.Effect => b.Effect ?? "Effect",
        ExitMode.ReturnToHardware => "Its own saved profile",
        _ => b.Mode.ToString(),
    };

    /// <summary>True when the choice needs the colour picker next to it.</summary>
    public static bool NeedsColor(ExitBehavior b) => b.Mode == ExitMode.Static;
}
