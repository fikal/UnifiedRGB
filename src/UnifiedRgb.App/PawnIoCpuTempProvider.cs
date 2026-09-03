using UnifiedRgb.Core.Native;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App;

/// <summary>CPU-temperature source for the LCD, read through SensorHub's
/// PawnIO AMDFamily17 reader. The panel used to open its OWN PawnIO module
/// handle and issue the SMN index/data ioctl pair from the UI thread on every
/// render tick, unserialised against the hub's timer-thread reads of the same
/// register; one reader, one cadence, and the hub's value is the one the fan
/// curves act on. ReadCelsius returns null when unavailable - including the
/// first reads before the hub's first sample lands - so the display simply
/// shows "--" instead of failing.</summary>
public sealed class PawnIoCpuTempProvider : ICpuTempProvider, IDisposable
{
    /// <summary>The PawnIO driver answers (the hub's reader can open); the
    /// module itself still needs elevation, which the manifest guarantees.
    /// Evaluated live (one cheap pawnio_version call): a construction-time
    /// snapshot kept the LCD's "needs PawnIO" banner up after an in-app
    /// install even though the readings had started.</summary>
    public bool Available => PawnIO.IsAvailable;

    public double? ReadCelsius()
    {
        SensorHub.TouchTemps();   // temp only: don't arm the Cooling-pane sweep
        return SensorHub.CpuTempC;   // null until the hub's timer has sampled once
    }

    /// <summary>Nothing owned: the hub's reader is process-wide.</summary>
    public void Dispose() { }
}
