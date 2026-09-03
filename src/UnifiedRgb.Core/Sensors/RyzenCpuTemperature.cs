using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/// <summary>Reads the AMD Zen (family 17h-1Ah) CPU temperature (Tctl) via the
/// signed PawnIO AMDFamily17 module's ioctl_read_smn primitive. The decode of
/// the SMU::THM::THM_TCON_CUR_TMP register is ours.</summary>
public sealed class RyzenCpuTemperature : IDisposable
{
    // SMU thermal control register holding the current Tctl temperature.
    const uint THM_CUR_TEMP = 0x00059800;
    const uint RANGE_SEL = 1u << 19;   // when set, range is shifted down by 49 C

    readonly PawnIO _io;

    RyzenCpuTemperature(PawnIO io) => _io = io;

    /// <summary>Load the embedded signed module and open the driver. Returns null
    /// if PawnIO is unavailable or the module is rejected (non-AMD, etc.).</summary>
    public static RyzenCpuTemperature? TryCreate()
    {
        if (!PawnIO.IsAvailable) return null;
        var blob = PawnIO.ReadEmbeddedModule("AMDFamily17.bin");
        if (blob == null) return null;
        var io = PawnIO.LoadModule(blob);
        if (io == null) return null;
        var t = new RyzenCpuTemperature(io);
        if (t.ReadCelsius() is > 0 and < 130) return t;   // sanity-gate on a real reading
        t.Dispose();   // PawnIO has no finalizer: a rejected reader leaked its kernel handle
        return null;
    }

    /// <summary>Current Tctl in degrees Celsius, or null if the read failed.</summary>
    public double? ReadCelsius()
    {
        var outv = new ulong[1];
        if (_io.Execute("ioctl_read_smn", new ulong[] { THM_CUR_TEMP }, outv) < 1) return null;
        uint raw = (uint)outv[0];
        double t = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & RANGE_SEL) != 0) t -= 49.0;
        return t;
    }

    /// <summary>Step-by-step diagnostic string for troubleshooting.</summary>
    public static string Diagnose()
    {
        if (!PawnIO.IsAvailable) return "PawnIO not available";
        var blob = PawnIO.ReadEmbeddedModule("AMDFamily17.bin");
        if (blob == null) return "embedded module not found";
        var io = PawnIO.LoadModule(blob);
        if (io == null) return $"open/load failed (blob {blob.Length}B) - likely needs elevation";
        var outv = new ulong[1];
        int n = io.Execute("ioctl_read_smn", new ulong[] { THM_CUR_TEMP }, outv);
        if (n < 1) { io.Dispose(); return "loaded OK but ioctl_read_smn failed"; }
        uint raw = (uint)outv[0];
        double t = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & RANGE_SEL) != 0) t -= 49.0;
        io.Dispose();
        return $"OK raw=0x{raw:X8} temp={t:0.0}C";
    }

    public void Dispose() => _io.Dispose();
}
