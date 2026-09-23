using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>Desired lighting survives scans while a device is absent. Present
/// devices replace their previous entry, including an empty effect list when
/// the user has switched back to static lighting.</summary>
public sealed class RecoveryLightingState
{
    public Dictionary<string, Rgb[]> Frames { get; } = new(StringComparer.Ordinal);
    public List<EffectAssignment> Effects { get; } = new();

    public void Remember(IEnumerable<IRgbDevice> devices, Func<IRgbDevice, Rgb[]> frameFor,
                         IEnumerable<EffectAssignment> effects, bool replaceEffects = true)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            present.Add(device.Name);
            Frames[device.Name] = (Rgb[])frameFor(device).Clone();
        }
        if (replaceEffects)
        {
            Effects.RemoveAll(e => present.Contains(e.Device));
            Effects.AddRange(effects.Where(e => present.Contains(e.Device)));
        }
    }

    public void Restore(Dictionary<string, Rgb[]> frames, IEnumerable<EffectAssignment> effects)
    {
        foreach (var (name, frame) in frames) Frames[name] = (Rgb[])frame.Clone();
        Effects.Clear();
        Effects.AddRange(effects);
    }

    /// <summary>A profile applied while a device is absent: its colours become
    /// what the device shows when it is back. The remembered frame GROWS to the
    /// profile's length when the profile is longer - a profile made for a
    /// device that gained LEDs, or a layout changed while it was unplugged,
    /// used to have its extra LEDs cut off at the old count, and the device
    /// came back with an incomplete picture. A shorter profile leaves the tail
    /// it does not mention as it was, and a colour that will not parse keeps
    /// whatever that LED had (black on an LED nothing has set).</summary>
    public void ApplyProfile(Profile profile)
    {
        foreach (var (name, hex) in profile.DeviceFrames)
        {
            if (hex == null) continue;
            Frames.TryGetValue(name, out var old);
            var frame = new Rgb[Math.Max(old?.Length ?? 0, hex.Length)];
            if (old != null) Array.Copy(old, frame, old.Length);
            for (int i = 0; i < hex.Length; i++)
                if (Rgb.TryFromHex(hex[i], out var color)) frame[i] = color;
            Frames[name] = frame;
        }
        Effects.Clear();
        Effects.AddRange((profile.Effects ?? new()).Where(e => e != null));
    }
}
