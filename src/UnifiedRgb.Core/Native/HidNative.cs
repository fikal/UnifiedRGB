using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Core.Native;

/// <summary>Dependency-free HID access (SetupAPI + hid.dll + kernel32),
/// ported from the proven StrafeInit probe. Enumerates HID interfaces by
/// VID/PID and opens a specific collection for read/write.</summary>
public static class HidNative
{
    public sealed record HidInfo(
        string Path, ushort UsagePage, ushort Usage, int OutputLength, int InputLength,
        ushort VendorId = 0, ushort ProductId = 0, int FeatureLength = 0,
        string Product = "", string Manufacturer = "");

    /// <summary>Find + open the first interface a driver wants — THE shared
    /// open pattern (it existed as six hand-rolled copies, four of which let a
    /// transient open failure propagate out of TryOpen so the device silently
    /// vanished for the session). Exceptions are logged and become null.</summary>
    public static (HidHandle Handle, HidInfo Info)? OpenFirst(
        string tag, ushort vid, ushort pid,
        Func<HidInfo, bool> pick, Func<HidInfo, bool>? fallbackPick = null)
    {
        try
        {
            var ifaces = Find(vid, pid);
            var iface = ifaces.FirstOrDefault(pick)
                     ?? (fallbackPick != null ? ifaces.FirstOrDefault(fallbackPick) : null);
            if (iface == null) return null;
            return (Open(iface.Path), iface);
        }
        catch (Exception ex)
        {
            Log.Error(tag, ex);
            return null;
        }
    }

    /// <summary>Enumerate every HID collection on the system (diagnostics).</summary>
    public static List<HidInfo> FindAll() => Find(0, 0, all: true);

    /// <summary>Find all HID collections for a VID/PID, with their caps.</summary>
    public static List<HidInfo> Find(ushort vid, ushort pid) => Find(vid, pid, all: false);

    static List<HidInfo> Find(ushort vid, ushort pid, bool all)
    {
        var results = new List<HidInfo>();
        string idFragment = $"vid_{vid:x4}&pid_{pid:x4}";

        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devs = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) return results;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(devs, ref ifData, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devs, ref ifData, detail, needed, out _, IntPtr.Zero))
                        continue;
                    string path = Marshal.PtrToStringAuto(detail + 4) ?? "";
                    if (!all && !path.Contains(idFragment, StringComparison.OrdinalIgnoreCase)) continue;

                    // Metadata-only open (no R/W access) works even for devices
                    // another driver holds exclusively — right for enumeration.
                    using var h = CreateFile(path, 0,
                        FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    if (!HidD_GetPreparsedData(h, out IntPtr ppd)) continue;
                    HidP_GetCaps(ppd, out HIDP_CAPS caps);
                    HidD_FreePreparsedData(ppd);

                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    HidD_GetAttributes(h, ref attrs);
                    // Fresh buffer per string + cut at the FIRST null: a reused
                    // buffer keeps the tail of the previous (longer) string
                    // past the terminator, producing mangled device names.
                    var buf = new byte[256];
                    string product = HidD_GetProductString(h, buf, buf.Length) ? CleanString(buf) : "";
                    Array.Clear(buf);
                    string vendor = HidD_GetManufacturerString(h, buf, buf.Length) ? CleanString(buf) : "";

                    results.Add(new HidInfo(path, caps.UsagePage, caps.Usage,
                        caps.OutputReportByteLength, caps.InputReportByteLength,
                        attrs.VendorID, attrs.ProductID, caps.FeatureReportByteLength,
                        product, vendor));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return results;
    }

    static string CleanString(byte[] unicodeBuf)
    {
        string s = System.Text.Encoding.Unicode.GetString(unicodeBuf);
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Trim();
    }

    public static HidHandle Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"Failed to open HID device (err {Marshal.GetLastWin32Error()})");
        return new HidHandle(h);
    }

    /// <summary>Overlapped I/O with event-based waits. The previous
    /// implementation spawned a NEW THREAD per read to get a timeout — at
    /// request/reply protocols under 60fps effects that was hundreds of
    /// thread creations a minute.</summary>
    public sealed class HidHandle : IDisposable
    {
        readonly SafeFileHandle _handle;
        readonly ManualResetEvent _rdEvent = new(false), _wrEvent = new(false);
        readonly IntPtr _rdOv, _wrOv;
        readonly object _rdLock = new(), _wrLock = new();
        static readonly int OvSize = 2 * IntPtr.Size + 8 + IntPtr.Size;   // OVERLAPPED
        const int ERROR_IO_PENDING = 997;

        public HidHandle(SafeFileHandle handle)
        {
            _handle = handle;
            _rdOv = Marshal.AllocHGlobal(OvSize);
            _wrOv = Marshal.AllocHGlobal(OvSize);
        }

        /// <summary>Write timeout. A HID interrupt OUT that hasn't completed in
        /// a few hundred ms isn't going to — the old fixed 2000 ms let one
        /// wedged device hold its driver's write lock for seconds per packet
        /// (~24 s worst case for a 12-packet keyboard frame).</summary>
        public int WriteTimeoutMs { get; set; } = 400;

        public bool Write(byte[] report)
        {
            lock (_wrLock) return Transfer(write: true, report, WriteTimeoutMs, out _);
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            lock (_rdLock) return Transfer(write: false, buffer, timeoutMs, out int got) ? got : 0;
        }

        bool Transfer(bool write, byte[] buf, int timeoutMs, out int transferred)
        {
            transferred = 0;
            if (_handle.IsClosed) return false;
            var evt = write ? _wrEvent : _rdEvent;
            var ov = write ? _wrOv : _rdOv;

            evt.Reset();
            // OVERLAPPED = { Internal, InternalHigh, Offset+OffsetHigh(8), hEvent }
            Marshal.WriteIntPtr(ov, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(ov, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt64(ov, 2 * IntPtr.Size, 0);
            Marshal.WriteIntPtr(ov, 2 * IntPtr.Size + 8, evt.SafeWaitHandle.DangerousGetHandle());

            // Overlapped I/O keeps writing to `buf` AFTER WriteFile returns
            // IO_PENDING, but the CLR only pins a managed array for the duration
            // of the P/Invoke call. Pin it ourselves for the whole operation, or
            // the GC can move it mid-write and corrupt the heap (ExecutionEngine-
            // Exception under GC pressure, e.g. LCD streaming while fans re-bake).
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                bool started = write
                    ? WriteFile(_handle, buf, buf.Length, out _, ov)
                    : ReadFile(_handle, buf, buf.Length, out _, ov);
                if (!started && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;

                if (!evt.WaitOne(timeoutMs))
                {
                    CancelIoEx(_handle, ov);
                    evt.WaitOne(1000);          // let the cancellation complete before unpinning
                }
                return GetOverlappedResult(_handle, ov, out transferred, false) && transferred >= 0;
            }
            finally { pin.Free(); }
        }

        /// <summary>Send a HID feature report (report[0] = report ID).</summary>
        public bool SetFeature(byte[] report) => HidD_SetFeature(_handle, report, report.Length);

        /// <summary>Read a HID feature report (report[0] = report ID on entry).</summary>
        public bool GetFeature(byte[] report) => HidD_GetFeature(_handle, report, report.Length);

        /// <summary>Read a HID INPUT report via control GET_REPORT (report[0] =
        /// report ID on entry). Used for command→response devices that answer on
        /// the control pipe rather than pushing on the interrupt IN pipe.</summary>
        public bool GetInputReport(byte[] report) => HidD_GetInputReport(_handle, report, report.Length);

        public void Dispose()
        {
            try { CancelIoEx(_handle, IntPtr.Zero); } catch { }
            _handle.Dispose();
            _rdEvent.Dispose();
            _wrEvent.Dispose();
            Marshal.FreeHGlobal(_rdOv);
            Marshal.FreeHGlobal(_wrOv);
        }
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid gClass, string? enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid gClass, int idx, ref SP_DEVICE_INTERFACE_DATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int size, out int needed, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
}
