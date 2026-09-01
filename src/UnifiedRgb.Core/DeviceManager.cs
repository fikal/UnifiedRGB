using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Core;

/// <summary>Central registry: detects every supported device across all
/// transports and exposes them as a single list for the UI. New device types
/// are added by registering a factory here.</summary>
public sealed class DeviceManager : IDisposable
{
    readonly List<IRgbDevice> _devices = new();
    readonly Dictionary<IRgbDevice, string> _family = new();

    public IReadOnlyList<IRgbDevice> Devices => _devices;

    /// <summary>Driver-family name (factory type) that produced each device —
    /// the unit the disable feature skips so hardware is never even opened.</summary>
    public IReadOnlyDictionary<IRgbDevice, string> FamilyOf => _family;

    /// <summary>Factories for each device family. Each returns a device if its
    /// hardware is present, or null. Add new devices (mobo, LNP, mouse, fans,
    /// RAM, GPU) here as they are implemented.</summary>
    static readonly Func<IRgbDevice?>[] Factories =
    {
        CorsairStrafeMk2.TryOpen,
        SteelSeriesApex.TryOpen,
        GigabyteIt5711.TryOpen,
        LogitechG403.TryOpen,
        MsiGpu.TryOpen,
        SayoDevice.TryOpen,
        LianLiWireless.TryOpen,
        LianLiUniHub.TryOpen,
    };

    /// <summary>Families that can yield several devices at once (DRAM sticks,
    /// the OpenRGB bridge).</summary>
    static readonly Func<List<IRgbDevice>>[] MultiFactories =
    {
        EneDram.DetectAll,
        Net.OpenRgbLink.DetectAll,
    };

    /// <summary>Detect everything. skipFamily (by factory type name) lets the
    /// app honor user-disabled devices WITHOUT ever opening the hardware —
    /// some inits write packets, and the whole point of disabling is to leave
    /// the device to other software.</summary>
    public void DetectAll(Func<string, bool>? skipFamily = null)
    {
        foreach (var factory in Factories)
        {
            string name = factory.Method.DeclaringType?.Name ?? "?";
            if (skipFamily?.Invoke(name) == true) { Log.Info("detect", $"{name}: skipped (disabled)"); continue; }
            try
            {
                var dev = factory();
                if (dev != null)
                {
                    _devices.Add(dev);
                    _family[dev] = name;
                    Log.Info("detect", $"{name}: FOUND '{dev.Name}' ({dev.LedCount} LEDs)");
                }
                else Log.Info("detect", $"{name}: not present");
            }
            catch (Exception ex)
            {
                Log.Error("detect", $"{name} threw: {ex}");
                Console.Error.WriteLine($"[DeviceManager] {name} failed: {ex.Message}");
            }
        }
        foreach (var factory in MultiFactories)
        {
            string name = factory.Method.DeclaringType?.Name ?? "?";
            if (skipFamily?.Invoke(name) == true) { Log.Info("detect", $"{name}: skipped (disabled)"); continue; }
            try
            {
                var found = factory();
                _devices.AddRange(found);
                // OpenRGB proxies get per-device families so one can be
                // disabled without disabling the whole bridge.
                foreach (var d in found)
                    _family[d] = d is OpenRgbDevice ? $"OpenRgb:{d.Name}" : name;
                Log.Info("detect", $"{name}: {found.Count} device(s)" +
                    (found.Count > 0 ? " - " + string.Join(", ", found.Select(d => d.Name)) : ""));
            }
            catch (Exception ex)
            {
                Log.Error("detect", $"{name} threw: {ex}");
                Console.Error.WriteLine($"[DeviceManager] {name} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var d in _devices) d.Dispose();
        _devices.Clear();
        _family.Clear();
    }
}
