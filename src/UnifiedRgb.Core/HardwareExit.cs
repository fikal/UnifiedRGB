using System.Text.Json.Serialization;
using UnifiedRgb.Core.Devices;

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
/// rainbow. This is the way out of that: a static color, an onboard effect,
/// or the device's own saved profile.</summary>
public interface IHardwareModes
{
    HardwareExitCaps ExitCaps { get; }

    /// <summary>Onboard effects this device can be left playing, by name.
    /// Empty unless ExitCaps has Effects.</summary>
    IReadOnlyList<string> HardwareEffects { get; }

    /// <summary>Leave the device showing one color, with no host talking to
    /// it. Black is "off".
    ///
    /// All three of these return whether the hardware took the command, for
    /// the same reason SetColors does: they are the LAST thing sent to the
    /// device, so there is no next frame to correct a refusal. HardwareExit
    /// retries them to a bounded deadline on that answer, and a driver that
    /// does not implement a mode returns false rather than pretending.</summary>
    bool SetHardwareStatic(Rgb color);

    /// <summary>Leave the device playing one of HardwareEffects. The color is
    /// used by effects that take one and ignored by the rest.</summary>
    bool SetHardwareEffect(string name, Rgb? color);

    /// <summary>Tell the device to resume its own saved profile.</summary>
    bool ReturnToHardware();
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
/// its last colors rather than throwing on the way out of the process.</summary>
public static class HardwareExit
{
    /// <summary>Carry out one device's choice. Returns a line worth logging,
    /// or null when nothing was sent.</summary>
    public static string? Apply(IRgbDevice device, ExitBehavior? behavior)
        => Apply(device, behavior, out _);

    /// <summary>The same, with the answer callers on the exit path need: did
    /// it actually land?
    ///
    /// This is the most terminal write in the app. The process is going away,
    /// so nothing follows this to correct a refusal, and a dropped packet here
    /// is the bug users describe as "it went to sleep with the lights still
    /// on". So every command goes through WritePolicy.MustLand: dedup bypassed,
    /// retried until the budget runs out, and logged loudly if it never lands.
    ///
    /// <paramref name="delivered"/> is false both when nothing was sent (an
    /// unsupported mode, a bad color) and when what was sent was refused; the
    /// returned description separates them, being null only in the first case.
    /// The budget is per device and the caller owns it, because the whole exit
    /// path shares one 2000 ms window across every device on the machine.</summary>
    public static string? Apply(IRgbDevice device, ExitBehavior? behavior, out bool delivered,
                                int budgetMs = WritePolicy.MustLandBudgetMs)
    {
        delivered = false;
        if (behavior == null || behavior.Mode == ExitMode.KeepLast) return null;
        if (device is not IHardwareModes hw) return null;

        var caps = hw.ExitCaps;
        switch (behavior.Mode)
        {
            case ExitMode.Off:
                if (!caps.HasFlag(HardwareExitCaps.Static)) return null;
                delivered = Deliver(device, "hardware off", () => hw.SetHardwareStatic(Rgb.Black), budgetMs);
                return "off";

            case ExitMode.Static:
            {
                if (!caps.HasFlag(HardwareExitCaps.Static)) return null;
                // TryFromHex, not FromHex: this class promises to be forgiving,
                // and a hand-edited hardware.json with a bad color should leave
                // the device alone rather than throw on the way out.
                if (!Rgb.TryFromHex(behavior.ColorHex, out var c)) return null;
                delivered = Deliver(device, $"hardware static #{c.ToHex()}", () => hw.SetHardwareStatic(c), budgetMs);
                return $"static #{c.ToHex()}";
            }

            case ExitMode.Effect:
            {
                if (!caps.HasFlag(HardwareExitCaps.Effects)) return null;
                string? name = Resolve(hw.HardwareEffects, behavior.Effect);
                if (name == null) return null;      // renamed or gone: leave it alone
                var ec = Rgb.TryFromHex(behavior.ColorHex, out var parsed) ? parsed : (Rgb?)null;
                delivered = Deliver(device, $"hardware effect {name}", () => hw.SetHardwareEffect(name, ec), budgetMs);
                return $"effect {name}";
            }

            case ExitMode.ReturnToHardware:
                if (!caps.HasFlag(HardwareExitCaps.ReturnToHardware)) return null;
                delivered = Deliver(device, "handback to firmware", () => hw.ReturnToHardware(), budgetMs);
                return "onboard profile";
        }
        return null;
    }

    /// <summary>One mode change, made to land, with the cache invalidation the
    /// contract requires around it. Before, because a stale "it already shows
    /// this" must not turn into a success for a command the hardware never
    /// got; after, because whatever the device is showing now, it is not the
    /// frame the driver last streamed at it.</summary>
    static bool Deliver(IRgbDevice device, string what, Func<bool> send, int budgetMs)
    {
        bool ok = WritePolicy.MustLand(device.Name, what,
            () => DeviceHealth.Attempt(device, () => { device.InvalidateCache(); return send(); }), budgetMs);
        device.InvalidateCache();
        return ok;
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

    /// <summary>True when the choice needs the color picker next to it.</summary>
    public static bool NeedsColor(ExitBehavior b) => b.Mode == ExitMode.Static;
}
