using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>Restart the USB device behind a HID interface: Windows stops its
/// driver stack, the hub re-enumerates it, and the device's firmware gets a
/// fresh USB session. This is what clears a device whose firmware has wedged
/// while staying on the bus (the Thermalright pump LCD, 2026-10-14: still
/// enumerated, refusing every write, not answering its identify query) - no
/// amount of closing and reopening our own handle reaches it.
///
/// Done through the system's own pnputil (/restart-device, Windows 10 2004+),
/// the exact command that was verified to clear that wedge, rather than a
/// re-implementation of it. Needs administrator rights, which the app has.</summary>
public static class UsbDeviceRestart
{
    /// <summary>The USB device instance that owns a HID interface path: walk up
    /// from the HID collection past the per-interface node ("...&amp;MI_00") to
    /// the device itself, e.g. USB\VID_0416&amp;PID_5302\USBDISPLAY. Null when
    /// the path is not on USB or no longer present.</summary>
    public static string? UsbInstanceFor(string hidInterfacePath)
    {
        string? id = InstanceIdOfInterface(hidInterfacePath);
        if (id == null || CM_Locate_DevNodeW(out uint node, id, 0) != 0) return null;
        for (int depth = 0; depth < 6; depth++)
        {
            if (CM_Get_Parent(out uint parent, node, 0) != 0) return null;
            node = parent;
            string? pid = DeviceId(node);
            if (pid == null) return null;
            if (pid.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                && !pid.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                return pid;
        }
        return null;
    }

    /// <summary>Restart one device instance. True only when Windows reports it
    /// done (pnputil exit 0); the detail says what happened either way.</summary>
    public static bool Restart(string instanceId, out string detail)
    {
        int? code = Pnputil(out string output, "/restart-device", instanceId);
        switch (code)
        {
            case 0:
                detail = "restarted";
                return true;
            case 3010:
                // ERROR_SUCCESS_REBOOT_REQUIRED: something held the device, and the
                // restart only takes effect after a reboot - nothing changed now.
                detail = "Windows needs a reboot to finish restarting it";
                return false;
            case null:
                // Killed part way: the device may be left removed. Ask Windows to
                // re-enumerate so it does not stay gone until the next replug.
                Pnputil(out _, "/scan-devices");
                detail = output;
                return false;
            default:
                detail = $"pnputil exit {code}: {output}";
                return false;
        }
    }

    /// <summary>Run the system pnputil with a 15 s budget. Returns the exit code,
    /// or null when it could not run or did not finish (output then says why).</summary>
    static int? Pnputil(out string output, params string[] args)
    {
        // Absolute path: this process is elevated, and a pnputil.exe found on
        // PATH (or beside the app) is not one to hand admin rights to.
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
        if (!File.Exists(exe)) { output = "pnputil.exe is not on this system"; return null; }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) { output = "pnputil did not start"; return null; }
            // Both pipes drained concurrently, or a full one stalls the child.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { /* it exited on its own between the wait and the kill */ }
                output = $"pnputil {args[0]} did not finish in 15 s";
                return null;
            }
            output = ((stdout.Wait(1000) ? stdout.Result : "") + (stderr.Wait(1000) ? stderr.Result : ""))
                .Trim().Replace(Environment.NewLine, " ");
            return p.ExitCode;
        }
        catch (Exception ex) { output = ex.Message; return null; }   // the caller logs the detail
    }

    static string? InstanceIdOfInterface(string path)
    {
        var key = DEVPKEY_Device_InstanceId;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    static string? DeviceId(uint node)
    {
        var buf = new char[400];   // MAX_DEVICE_ID_LEN is 200
        return CM_Get_Device_IDW(node, buf, buf.Length, 0) == 0 ? new string(buf).TrimEnd('\0') : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
        new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint node, char[] buffer, int length, uint flags);
}
