using System.Reflection;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>The two native libraries this assembly loads that are not part of
/// Windows - PawnIOLib (the PawnIO driver's user-mode API) and nvapi64 (the
/// NVIDIA driver's) - loaded by ABSOLUTE path only.
///
/// This process runs as administrator. Both used to be found by search: PawnIO
/// tried two hard-coded C:\Program Files paths and then handed the name back to
/// the default search, and NvAPI was a bare DllImport. On a PC without the
/// NVIDIA driver or PawnIO that search walked PATH, which includes
/// %LOCALAPPDATA%\Microsoft\WindowsApps - writable by any process of the user,
/// elevated or not - so a DLL planted there under either name was loaded by
/// the next rescan as administrator. Now each name resolves to the one place
/// its installer puts it, and when it is not there the load FAILS instead of
/// searching anywhere else.
///
/// One resolver for the whole assembly, installed once: the runtime allows a
/// single resolver per assembly and throws on a second, so both users register
/// through here rather than each setting its own.
///
/// Deliberately NOT done: a process-wide SetDefaultDllDirectories(System32).
/// WPF's own native parts (wpfgfx loading D3DCompiler_47_cor3 by name, PenImc),
/// LibreHardwareMonitor and Vortice load DLLs by name from the app or
/// single-file extraction folder, and confining the whole process could break
/// rendering or startup in a way only a real launch would show. Our own
/// P/Invokes are covered by the assembly attribute and this resolver; the
/// single-file extraction folder itself (%TEMP%\.net, user-writable) is a
/// packaging question, not something code here can fix.</summary>
internal static class NativeLibraries
{
    internal const string PawnIoName = "PawnIOLib";
    const string NvApiFile = "nvapi64.dll";

    static readonly object _registerLock = new();
    static bool _registered;

    /// <summary>Install the resolver (idempotent). Called from the static
    /// constructors of PawnIO and NvApi, so it is in place before either
    /// type's first P/Invoke whichever of them is touched first.</summary>
    internal static void Register()
    {
        // Under a lock, and marked done only once installed: with a flag set
        // first, the second of two racing static constructors returned before
        // the first had installed anything, and its P/Invoke fell through to
        // the default search (one transient "PawnIO unavailable").
        lock (_registerLock)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>The resolver itself: a handle, or DllNotFoundException, for our two
    /// libraries - never IntPtr.Zero, which means "search" - and Zero (System32-only
    /// default probing) for everything else.</summary>
    internal static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name.Equals(PawnIoName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PawnIoName + ".dll", StringComparison.OrdinalIgnoreCase))
            return LoadFirst(PawnIoCandidates(), PawnIoName);
        if (name.Equals(NvApiFile, StringComparison.OrdinalIgnoreCase)
            || name.Equals("nvapi64", StringComparison.OrdinalIgnoreCase))
            return LoadFirst(new[] { NvApiPath() }, NvApiFile);
        // Everything else is a Windows DLL. Default probing, which this
        // assembly's DefaultDllImportSearchPaths confines to System32.
        return IntPtr.Zero;
    }

    /// <summary>Where the PawnIO installer puts its library, built from the
    /// real Program Files folders rather than assuming drive C: (which also
    /// missed every install on another drive). A folder Windows reports as
    /// empty - Program Files (x86) on a 32-bit OS - is dropped, because a
    /// relative path would resolve against the working directory.</summary>
    internal static string[] PawnIoCandidates() => new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        }
        .Select(Environment.GetFolderPath)
        .Where(dir => !string.IsNullOrEmpty(dir))
        .Select(dir => Path.Combine(dir, "PawnIO", PawnIoName + ".dll"))
        .Where(Path.IsPathFullyQualified)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>The NVIDIA driver installs nvapi64.dll into System32.</summary>
    internal static string NvApiPath() => Path.Combine(Environment.SystemDirectory, NvApiFile);

    /// <summary>The first candidate that exists and loads. Never IntPtr.Zero:
    /// zero tells the runtime to fall back to its own search, which is exactly
    /// the search this class exists to avoid. A missing library is a
    /// DllNotFoundException, which every caller already treats as "not
    /// installed".</summary>
    internal static IntPtr LoadFirst(IReadOnlyList<string> candidates, string name)
    {
        foreach (var path in candidates)
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
        throw new DllNotFoundException(
            $"{name} was not found where its installer puts it ({string.Join("; ", candidates)}), and is not searched for anywhere else");
    }
}
