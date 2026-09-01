using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App;

/// <summary>CPU-temperature source backed by the signed PawnIO AMDFamily17
/// module. Requires the app to run elevated (the PawnIO driver rejects the
/// open otherwise); ReadCelsius returns null when unavailable, so the display
/// simply shows "--" instead of failing.</summary>
public sealed class PawnIoCpuTempProvider : ICpuTempProvider, IDisposable
{
    readonly RyzenCpuTemperature? _reader = RyzenCpuTemperature.TryCreate();

    public bool Available => _reader != null;
    public double? ReadCelsius() => _reader?.ReadCelsius();
    public void Dispose() => _reader?.Dispose();
}
