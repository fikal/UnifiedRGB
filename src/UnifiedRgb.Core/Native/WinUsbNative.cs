using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Core.Native;

/// <summary>Minimal WinUSB transport: enumerate a vendor device interface by
/// GUID (SetupAPI), open it, and write bulk pipes. Used by devices that ship
/// with winusb.sys + a vendor interface GUID instead of HID (Lian Li SLV3
/// wireless transmitter). One open handle at a time — WinUSB is exclusive.</summary>
public sealed class WinUsbDevice : IDisposable
{
    readonly SafeFileHandle _file;
    readonly IntPtr _iface;
    volatile bool _disposed;

    public byte BulkOutPipe { get; }
    public byte BulkInPipe { get; }   // 0 = none found

    WinUsbDevice(SafeFileHandle file, IntPtr iface, byte pipeOut, byte pipeIn)
    { _file = file; _iface = iface; BulkOutPipe = pipeOut; BulkInPipe = pipeIn; }

    /// <summary>Find the device path for an interface GUID whose path contains
    /// the given VID/PID fragment (e.g. "vid_0416&amp;pid_8040"), or null.</summary>
    public static string? FindPath(Guid ifaceGuid, string vidPidFragment)
        => SetupDiEnum.InterfacePaths(ifaceGuid, vidPidFragment).FirstOrDefault();

    /// <summary>Open the device and locate its bulk OUT pipe. Returns null if
    /// the path can't be opened (another program owns it) or has no bulk OUT.</summary>
    public static WinUsbDevice? Open(string path)
    {
        var fh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (fh.IsInvalid) return null;
        if (!WinUsb_Initialize(fh, out var iface)) { fh.Dispose(); return null; }

        byte pipeOut = 0, pipeIn = 0;
        if (WinUsb_QueryInterfaceSettings(iface, 0, out var ifd))
            for (byte i = 0; i < ifd.bNumEndpoints; i++)
                if (WinUsb_QueryPipe(iface, 0, i, out var pi) && pi.PipeType == 2)
                {
                    if ((pi.PipeId & 0x80) == 0) pipeOut = pi.PipeId;
                    else pipeIn = pi.PipeId;
                }
        if (pipeOut == 0) { WinUsb_Free(iface); fh.Dispose(); return null; }
        if (pipeIn != 0)
        {
            // Bounded reads: a silent device must never hang a caller.
            uint timeoutMs = 2000;
            WinUsb_SetPipePolicy(iface, pipeIn, 3 /* PIPE_TRANSFER_TIMEOUT */, 4, ref timeoutMs);
        }
        // Bounded writes too: the default OUT timeout is infinite, and a
        // transmitter that stops ACKing would park the caller (holding its
        // driver lock) forever. A timed-out write returns false like any other
        // failure, which every caller already handles.
        uint outTimeoutMs = 1000;
        WinUsb_SetPipePolicy(iface, pipeOut, 3 /* PIPE_TRANSFER_TIMEOUT */, 4, ref outTimeoutMs);
        return new WinUsbDevice(fh, iface, pipeOut, pipeIn);
    }

    public bool Write(byte pipe, byte[] buf)
        => !_disposed && WinUsb_WritePipe(_iface, pipe, buf, (uint)buf.Length, out _, IntPtr.Zero);

    /// <summary>Read up to buf.Length bytes from the bulk IN pipe in 64-byte
    /// chunks; returns bytes read (0 on timeout/no pipe).</summary>
    public int Read(byte[] buf)
    {
        if (BulkInPipe == 0 || _disposed) return 0;
        int got = 0;
        var chunk = new byte[64];
        while (got < buf.Length)
        {
            if (!WinUsb_ReadPipe(_iface, BulkInPipe, chunk, 64, out uint n, IntPtr.Zero) || n == 0) break;
            int copy = Math.Min((int)n, buf.Length - got);
            Array.Copy(chunk, 0, buf, got, copy);
            got += copy;
        }
        return got;
    }

    public void Dispose()
    {
        if (_disposed) return;   // WinUsb_Free twice on one interface handle is a double free
        _disposed = true;
        WinUsb_Free(_iface);
        _file.Dispose();
    }

    /*--------------------------- P/Invoke ---------------------------*/
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints,
            bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle h, out IntPtr iface);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Free(IntPtr iface);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte alt, out USB_INTERFACE_DESCRIPTOR ifd);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_QueryPipe(IntPtr iface, byte alt, byte idx, out WINUSB_PIPE_INFORMATION pipe);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr iface, byte pipe, byte[] buf, uint len, out uint sent, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipe, byte[] buf, uint len, out uint read, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipe, uint policy, uint valueLen, ref uint value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
}
