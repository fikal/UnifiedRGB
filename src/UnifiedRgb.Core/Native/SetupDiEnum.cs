using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>SetupAPI device-interface enumeration, shared by the HID and WinUSB
/// transports. It used to exist as two verbatim copies (struct, four imports,
/// the cbSize quirk) in <see cref="HidNative"/> and <see cref="WinUsbDevice"/>.</summary>
static class SetupDiEnum
{
    /// <summary>Device paths of every PRESENT interface of a class GUID. With a
    /// fragment (e.g. "vid_0416&amp;pid_8040") only paths containing it,
    /// case-insensitively, are returned.</summary>
    public static List<string> InterfacePaths(Guid ifaceGuid, string? fragment = null)
    {
        var results = new List<string>();
        Guid g = ifaceGuid;
        IntPtr devs = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) return results;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref g, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(devs, ref ifData, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed == 0) continue;   // size query failed: never WriteInt32 into a 0-byte block
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize is 8 on x64 (DWORD +
                    // one padded WCHAR) and 6 on x86 - the documented quirk.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devs, ref ifData, detail, needed, out _, IntPtr.Zero))
                        continue;
                    string? path = Marshal.PtrToStringAuto(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (fragment != null && !path.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return results;
    }

    const int DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid gClass, string? enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid gClass, int idx, ref SP_DEVICE_INTERFACE_DATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int size, out int needed, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);
}
