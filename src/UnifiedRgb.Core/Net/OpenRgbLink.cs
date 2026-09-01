using System.Text.RegularExpressions;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| Detection bridge: if an OpenRGB server is reachable, wrap   |
| its devices as IRgbDevices — EXCEPT hardware our native     |
| drivers cover. Native always wins: proxying a natively-     |
| driven device would put two writers on one wire (the iCUE   |
| problem). Skips are logged and exposed so the app can also  |
| turn the corresponding OpenRGB detectors off in the bundled |
| instance's config.                                          |
\*-----------------------------------------------------------*/
public static class OpenRgbLink
{
    static OpenRgbClient? _client;

    /// <summary>VID:PID pairs our native drivers claim; any OpenRGB device at
    /// one of these locations is skipped even if the native device is absent
    /// (the native driver is the one that should pick it up).</summary>
    static readonly (int Vid, int Pid)[] NativeHardware =
    {
        (0x1B1C, 0x1B48),          // Corsair Strafe MK.2
        (0x1038, 0x1642),          // Apex Pro TKL Gen 3
        (0x1038, 0x1614),          // Apex Pro TKL 2023
        (0x048D, 0x5711),          // Gigabyte RGB Fusion (X870E)
        (0x048D, 0x8297),          // Gigabyte RGB Fusion (IT8297)
        (0x0416, 0x5302),          // Thermalright pump LCD
    };

    /// <summary>Names of remote devices skipped during the last detect —
    /// candidates for detector-level disabling in the bundled instance.</summary>
    public static List<string> LastSkipped { get; } = new();

    public static List<IRgbDevice> DetectAll()
    {
        LastSkipped.Clear();
        _client?.Dispose();
        _client = null;

        var list = new List<IRgbDevice>();
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!OpenRgbClient.IsServerUp(port: OpenRgbManager.Port)) return list;
        try
        {
            _client = OpenRgbClient.Connect(port: OpenRgbManager.Port);
            int count = _client.GetControllerCount();
            for (int i = 0; i < count; i++)
            {
                OpenRgbClient.DeviceInfo info;
                try { info = _client.GetControllerData(i); }
                catch (Exception ex) { Log.Warn("openrgb", $"device {i}: read failed: {ex.Message}"); continue; }

                if (IsNativelyCovered(info))
                {
                    LastSkipped.Add(info.Name);
                    Log.Info("openrgb", $"skipping '{info.Name}' (natively driven)");
                    continue;
                }
                if (Math.Max(info.LedCount, info.Colors.Length) == 0)
                {
                    Log.Info("openrgb", $"skipping '{info.Name}' (no LEDs)");
                    continue;
                }
                nameCounts.TryGetValue(info.Name, out int dup);
                nameCounts[info.Name] = dup + 1;
                list.Add(new OpenRgbDevice(_client, info, dup));
            }
            Log.Info("openrgb", $"bridged {list.Count} device(s), skipped {LastSkipped.Count} natively-covered");
        }
        catch (Exception ex)
        {
            Log.Warn("openrgb", $"bridge failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
        }
        return list;
    }

    static bool IsNativelyCovered(OpenRgbClient.DeviceInfo info)
    {
        var m = Regex.Match(info.Location, @"vid[_&#]?([0-9a-fA-F]{4}).{0,4}pid[_&#]?([0-9a-fA-F]{4})",
                            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            int vid = Convert.ToInt32(m.Groups[1].Value, 16);
            int pid = Convert.ToInt32(m.Groups[2].Value, 16);
            if (NativeHardware.Any(h => h.Vid == vid && h.Pid == pid)) return true;
            if (vid == 0x046D) return true;                 // any Logitech: native HID++ driver
        }
        // Non-HID natives, matched by identity rather than location:
        if (info.Type == 1 && (info.Name.Contains("ENE", StringComparison.OrdinalIgnoreCase)
                            || info.Name.Contains("Aura", StringComparison.OrdinalIgnoreCase)))
            return true;                                    // ENE/Aura SMBus DRAM
        if (info.Type == 2 && info.Name.Contains("MSI", StringComparison.OrdinalIgnoreCase))
            return true;                                    // MSI GPU via NvAPI I2C
        return false;
    }

    public static void Shutdown()
    {
        _client?.Dispose();
        _client = null;
    }
}
