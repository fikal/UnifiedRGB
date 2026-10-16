using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Core.Native;

/// <summary>One command byte out, a fixed-size reply back: all a WireView poll
/// needs from its port. SerialLink is the real one; the harness fakes it (a
/// silent device, a slow one, one that answers as something else).</summary>
internal interface ISerialQuery : IDisposable
{
    bool Query(byte command, Span<byte> reply);
}

/// <summary>Minimal Win32 serial port for USB CDC devices that speak a
/// one-command-byte, fixed-size-reply protocol (WireView Pro II). Opened
/// EXCLUSIVELY, as Windows does for every COM port, so a second program
/// (the vendor app, HWiNFO) gets access-denied while we hold it. Synchronous
/// I/O with bounded timeouts: a silent device costs one timeout, never a hang.
/// Not thread-safe; the owner serialises calls.</summary>
internal sealed class SerialLink : ISerialQuery
{
    readonly SafeFileHandle _h;
    SerialLink(SafeFileHandle h) => _h = h;

    /// <summary>Open "COMn" at 8N1 with DTR/RTS asserted (some CDC firmwares
    /// only send while DTR is up; Linux asserts it on open, so the device's
    /// own tooling expects it). Returns null with the Win32 error on failure:
    /// 5 (access denied) = another program has the port.</summary>
    public static SerialLink? Open(string port, int baud, int timeoutMs, out int error)
    {
        var h = CreateFile(@"\\.\" + port, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }

        var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
        var to = new COMMTIMEOUTS
        {
            // Interval 0 + a total constant: ReadFile returns when the buffer
            // is full or the constant elapses, whichever comes first.
            ReadTotalTimeoutConstant = (uint)timeoutMs,
            WriteTotalTimeoutConstant = (uint)timeoutMs,
        };
        if (!GetCommState(h, ref dcb)) { error = Marshal.GetLastWin32Error(); h.Dispose(); return null; }
        dcb.BaudRate = (uint)baud;
        dcb.ByteSize = 8;
        dcb.Parity = 0;      // NOPARITY
        dcb.StopBits = 0;    // ONESTOPBIT
        dcb.Flags = F_BINARY | F_DTR_CONTROL_ENABLE | F_RTS_CONTROL_ENABLE;
        if (!SetCommState(h, ref dcb) || !SetCommTimeouts(h, ref to))
        {
            error = Marshal.GetLastWin32Error(); h.Dispose(); return null;
        }
        PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR);
        error = 0;
        return new SerialLink(h);
    }

    /// <summary>Send one command byte and read exactly reply.Length bytes.
    /// The receive buffer is purged first: the protocol has no framing, so a
    /// late tail from an earlier timed-out reply would otherwise shift every
    /// field of this one.</summary>
    public bool Query(byte command, Span<byte> reply)
    {
        PurgeComm(_h, PURGE_RXCLEAR);
        Span<byte> cmd = stackalloc byte[] { command };
        if (!WriteFile(_h, ref MemoryMarshal.GetReference(cmd), 1, out uint wrote, IntPtr.Zero) || wrote != 1)
            return false;
        int got = 0;
        while (got < reply.Length)
        {
            var rest = reply[got..];
            if (!ReadFile(_h, ref MemoryMarshal.GetReference(rest), (uint)rest.Length, out uint n, IntPtr.Zero))
                return false;
            if (n == 0) return false;   // the total timeout elapsed
            got += (int)n;
        }
        return true;
    }

    public void Dispose() => _h.Dispose();

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, OPEN_EXISTING = 3;
    const uint PURGE_TXCLEAR = 0x0004, PURGE_RXCLEAR = 0x0008;
    // DCB bitfield: fBinary bit 0, fDtrControl bits 4-5, fRtsControl bits 12-13.
    const uint F_BINARY = 1u << 0, F_DTR_CONTROL_ENABLE = 1u << 4, F_RTS_CONTROL_ENABLE = 1u << 12;

    [StructLayout(LayoutKind.Sequential)]
    struct DCB
    {
        public uint DCBlength, BaudRate, Flags;
        public ushort wReserved, XonLim, XoffLim;
        public byte ByteSize, Parity, StopBits;
        public byte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant,
                    WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, ref byte buf, uint len, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, ref byte buf, uint len, out uint read, IntPtr overlapped);
}
