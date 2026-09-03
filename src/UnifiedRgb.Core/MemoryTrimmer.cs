using System.Runtime.InteropServices;

namespace UnifiedRgb.Core;

/// <summary>Working-set trimming for the tray-resident lifestyle: when the
/// window hides, compact the GC heap and hand the process's idle pages back
/// to the OS standby list. They fault back in lazily (cheap soft faults) the
/// next time the UI opens; meanwhile the memory is genuinely reclaimable and
/// Task Manager tells the truth about what the app is using.</summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>The pseudo-handle (-1): full access to ourselves, nothing to
    /// close. Process.GetCurrentProcess().Handle opened a real handle that was
    /// only released by a later finalizer pass.</summary>
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    public static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (EmptyWorkingSet(GetCurrentProcess()))
                Log.Info("memory", "working set trimmed (tray idle)");
            else
            {
                int err = Marshal.GetLastWin32Error();
                Log.Warn("memory", $"EmptyWorkingSet failed (win32 {err})");
            }
        }
        catch (Exception ex) { Log.Warn("memory", $"trim failed: {ex.Message}"); }
    }
}
