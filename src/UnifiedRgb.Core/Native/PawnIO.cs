using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>Thin P/Invoke wrapper over PawnIOLib.dll (the user-mode API for the
/// signed PawnIO kernel driver). A PawnIO "module" is a small signed blob that
/// exposes a few safe hardware primitives (e.g. ioctl_read_smn); all the actual
/// sensor logic lives in our code.</summary>
public sealed class PawnIO : IDisposable
{
    IntPtr _handle;

    static PawnIO()
    {
        // PawnIOLib.dll installs to Program Files, which is not on the DLL search
        // path, so resolve it explicitly.
        NativeLibrary.SetDllImportResolver(typeof(PawnIO).Assembly, (name, _, _) =>
        {
            if (name == "PawnIOLib")
            {
                foreach (var p in new[]
                {
                    @"C:\Program Files\PawnIO\PawnIOLib.dll",
                    @"C:\Program Files (x86)\PawnIO\PawnIOLib.dll",
                })
                    if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    public static bool IsAvailable
    {
        get { try { return pawnio_version(out _) == 0; } catch { return false; } }
    }

    /// <summary>A signed module blob embedded in this assembly (Modules\*.bin),
    /// matched by file-name suffix. The one copy of what used to be three.</summary>
    public static byte[]? ReadEmbeddedModule(string file)
    {
        var asm = typeof(PawnIO).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Open the driver and load a module blob. Returns null on failure
    /// (driver missing, blob rejected, etc.).</summary>
    public static PawnIO? LoadModule(byte[] blob)
    {
        try
        {
            if (pawnio_open(out IntPtr h) != 0 || h == IntPtr.Zero) return null;
            if (pawnio_load(h, blob, (UIntPtr)blob.Length) != 0) { pawnio_close(h); return null; }
            return new PawnIO { _handle = h };
        }
        catch { return null; }
    }

    /// <summary>Execute a module function. Returns the number of output values
    /// written, or -1 on failure.</summary>
    public int Execute(string name, ulong[] input, ulong[] output)
    {
        // The sensor hub reads its sources through captured locals and closes
        // them from another thread after a bounded drain, so a tick that
        // overran the drain can arrive here while Dispose is running. Handing
        // a freed context to pawnio_execute is a native dereference inside
        // PawnIOLib - an access violation that no catch here can contain,
        // because it is not a managed exception. The read lock makes close
        // wait for an in-flight call instead, and the disposed flag makes
        // every later call a cheap no-op.
        _lock.EnterReadLock();
        try
        {
            if (_disposed || _handle == IntPtr.Zero) return -1;
            int hr = pawnio_execute(_handle, name, input, (UIntPtr)input.Length,
                output, (UIntPtr)output.Length, out UIntPtr ret);
            return hr == 0 ? (int)ret : -1;
        }
        catch { return -1; }
        finally { _lock.ExitReadLock(); }
    }

    readonly System.Threading.ReaderWriterLockSlim _lock = new();
    volatile bool _disposed;

    public void Dispose()
    {
        // The write lock waits out any Execute already inside the driver. It
        // is bounded in practice: a PawnIO ioctl is a register read, not an
        // operation that can park for seconds.
        _lock.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero) { try { pawnio_close(_handle); } catch { } _handle = IntPtr.Zero; }
        }
        finally { _lock.ExitWriteLock(); }
    }

    [DllImport("PawnIOLib")] static extern int pawnio_version(out uint version);
    [DllImport("PawnIOLib")] static extern int pawnio_open(out IntPtr handle);
    [DllImport("PawnIOLib")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport("PawnIOLib")]
    static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr retSize);
    [DllImport("PawnIOLib")] static extern int pawnio_close(IntPtr handle);
}
