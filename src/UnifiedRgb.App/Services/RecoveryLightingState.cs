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

    public void ApplyProfile(Profile profile)
    {
        foreach (var (name, hex) in profile.DeviceFrames)
        {
            if (hex == null) continue;
            var frame = Frames.TryGetValue(name, out var old)
                ? (Rgb[])old.Clone() : new Rgb[hex.Length];
            for (int i = 0; i < Math.Min(frame.Length, hex.Length); i++)
                if (Rgb.TryFromHex(hex[i], out var color)) frame[i] = color;
            Frames[name] = frame;
        }
        Effects.Clear();
        Effects.AddRange((profile.Effects ?? new()).Where(e => e != null));
    }
}
