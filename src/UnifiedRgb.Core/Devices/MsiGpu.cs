using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>MSI GeForce RTX (Gaming Trio et al.) board RGB — ITE9 controller at
/// I2C address 0x68 behind the GPU's I2C bus, reached through NvAPI (port 1).
/// Protocol ported from OpenRGB's MSIGPUv2: program colors while the mode
/// register is set to IDLE, then select STATIC; each register write is padded
/// with the controller's required settle delay. One logical LED (the card's
/// lighting runs as a single zone in this mode). No elevation required.</summary>
public sealed class MsiGpu : IRgbDevice
{
    const byte ADDR = 0x68;
    const byte REG_MODE = 0x22;
    const byte REG_R1 = 0x30, REG_G1 = 0x31, REG_B1 = 0x32;
    const byte REG_BRIGHTNESS = 0x36;
    const byte MODE_IDLE = 0x1C;
    const byte MODE_STATIC = 0x13;
    // Protocol generations: RTX 20/30 cards use v1 (no idle dance, unknown reg
    // 0x26); RTX 40/50 use v2 (idle mode + unknown reg 0x2E).
    const byte REG_UNKNOWN_V1 = 0x26;
    const byte REG_UNKNOWN_V2 = 0x2E;

    readonly IntPtr _gpu;
    readonly bool _v2;
    readonly object _writeLock = new();
    Rgb? _last;

    public string Name { get; }
    public string Vendor => "MSI";
    public DeviceType Type => DeviceType.Gpu;
    public int LedCount => 1;
    public IReadOnlyList<RgbZone> Zones { get; } =
        new[] { new RgbZone { Name = "GPU", Offset = 0, Count = 1 } };

    MsiGpu(IntPtr gpu, string name, bool v2) { _gpu = gpu; Name = name; _v2 = v2; }

    /// <summary>RTX 40/50-series names use the v2 protocol; 20/30 use v1.</summary>
    static bool IsV2(string gpuName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gpuName, @"RTX\s*(\d)");
        return !m.Success || m.Groups[1].Value is "4" or "5";
    }

    public static MsiGpu? TryOpen()
    {
        Span<byte> probe = stackalloc byte[1];   // outside the loop: stackallocs only free at method exit
        foreach (var (handle, name) in NvApi.EnumGpus())
        {
            // Probe the ITE controller: a register read at 0x68 must succeed.
            if (NvApi.I2CRead(handle, ADDR, REG_MODE, probe))
            {
                bool v2 = IsV2(name);
                Log.Info("MsiGpu", $"'{name}' at 0x68, protocol {(v2 ? "v2" : "v1")}");
                return new MsiGpu(handle, name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    ? name.Replace("NVIDIA ", "MSI ") : $"MSI {name}", v2);
            }
        }
        return null;
    }

    // OpenRGB settles 20 ms after EVERY register (7 writes = 140 ms per color
    // change - the GPU visibly trailed the other devices on profile flips).
    // Data registers latch much faster; only mode switches keep a long settle.
    const int SettleData = 5, SettleMode = 15;

    void Write(byte reg, byte val, int settleMs)
    {
        Span<byte> b = stackalloc byte[1];
        b[0] = val;
        NvApi.I2CWrite(_gpu, ADDR, reg, b);
        Thread.Sleep(settleMs);
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        lock (_writeLock)
        {
            var c = colors[0];
            if (_last == c) return;
            _last = c;

            if (_v2)
            {
                Write(REG_UNKNOWN_V2, 0x00, SettleData);
                Write(REG_MODE, MODE_IDLE, SettleMode);
                Write(REG_R1, c.R, SettleData);
                Write(REG_G1, c.G, SettleData);
                Write(REG_B1, c.B, SettleData);
                Write(REG_BRIGHTNESS, 100, SettleData);   // max (20 * 5)
                Write(REG_MODE, MODE_STATIC, SettleMode);
            }
            else
            {
                // v1 (RTX 20/30): no idle dance; write colors, brightness, mode.
                Write(REG_UNKNOWN_V1, 0x00, SettleData);
                Write(REG_R1, c.R, SettleData);
                Write(REG_G1, c.G, SettleData);
                Write(REG_B1, c.B, SettleData);
                Write(REG_BRIGHTNESS, 100, SettleData);
                Write(REG_MODE, MODE_STATIC, SettleMode);
            }
        }
    }

    public void Dispose() { }
}
