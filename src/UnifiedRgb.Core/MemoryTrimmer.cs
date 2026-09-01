using System.Runtime.InteropServices;

namespace UnifiedRgb.Core;

/// <summary>Working-set trimming for the tray-resident lifestyle: when the
/// window hides, compact the GC heap and hand the process's idle pages back
/// to the OS standby list. They fault back in lazily (cheap soft faults) the
/// next time the UI opens; meanwhile the memory is genuinely reclaimable and
/// Task Manager tells the truth about what the app is using.</summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            Log.Info("memory", "working set trimmed (tray idle)");
        }
        catch (Exception ex) { Log.Warn("memory", $"trim failed: {ex.Message}"); }
    }
}
