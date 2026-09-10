using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The two things Windows will tell us for free, and nothing    |
| else.                                                        |
|                                                              |
| Before this class, a device that was unplugged and plugged   |
| back in stayed dark until the user noticed and pressed        |
| Rescan, and so did every device on the rig after a sleep and |
| a wake. Not because either event is hard to hear - one is a  |
| window message and the other is a SystemEvents callback -    |
| but because nothing was listening.                           |
|                                                              |
| Everything here is deliberately thin. It turns an OS event   |
| into RecoveryPolicy.Note and a timer tick into                |
| RecoveryPolicy.Claim, and it owns no decisions of its own:   |
| the debouncing, the settle times and the "is this a good     |
| moment" rules all live in Core where they can be tested at   |
| speed without a window or a device. What is left here is the |
| part that genuinely needs Win32.                             |
|                                                              |
| IDLE COST, which for a tray app that runs for weeks is the   |
| thing that decides whether a feature is allowed to exist:    |
|                                                              |
|   - no polling thread and no periodic timer. Both triggers   |
|     are push. On a machine nobody touches, this class costs  |
|     exactly nothing: no wakeups, no allocations, no I/O.     |
|   - the DispatcherTimer runs ONLY between an event and the   |
|     rescan it causes, which is a couple of seconds at a      |
|     time, a few times a week.                                |
|   - device notifications are registered for the two          |
|     interface classes we actually drive (HID and USB device) |
|     rather than DEVICE_NOTIFY_ALL_INTERFACE_CLASSES, and     |
|     each message is filtered by vendor id before it is even  |
|     counted. Mounting a USB stick, docking a monitor or      |
|     opening a webcam wakes this for a few microseconds and   |
|     is then dropped. That filter is what stops the feature   |
|     from turning every USB event on the machine into a full  |
|     teardown and reopen of the user's lighting.              |
|                                                              |
| The window is message-only (HWND_MESSAGE). It is not the     |
| main window on purpose: this has to work while the app is in |
| the tray with no window realised at all, which is how it     |
| runs for most of its life.                                   |
\*-----------------------------------------------------------*/
public sealed class DeviceWatchdog : IDisposable
{
    /*---------------- Win32 ----------------*/

    const int WM_DEVICECHANGE = 0x0219;
    const int DBT_DEVICEARRIVAL = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int DBT_DEVTYP_DEVICEINTERFACE = 0x05;
    const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    const int HWND_MESSAGE = -3;

    /// <summary>GUID_DEVINTERFACE_HID: every keyboard, mouse, pad and most of
    /// the RGB controllers in this app.</summary>
    static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE: the WinUSB devices (the Lian Li
    /// wireless dongle and its receiver) that expose no HID interface.</summary>
    static readonly Guid UsbInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        // The device path follows in memory. Declared as one char so the
        // struct has the layout RegisterDeviceNotification expects; the
        // incoming path is read by hand from the message's lParam instead,
        // because marshalling a variable-length trailing string as a struct
        // field is exactly the sort of thing that reads one byte too far.
        public char Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient, IntPtr filter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterDeviceNotification(IntPtr handle);

    /*---------------- what counts as ours ----------------*/

    /// <summary>USB vendor ids the app has a driver for, lower case, as they
    /// appear inside a device interface path ("...\HID#VID_1B1C&PID_1B48#...").
    ///
    /// This is a FILTER, not a device list: getting it wrong in the generous
    /// direction costs one unnecessary rescan, and getting it wrong in the
    /// mean direction costs a device that never recovers. So it is the vendor
    /// rather than the vendor-and-product, and a new driver's vendor belongs
    /// here the same way its factory belongs in DeviceManager. The SMBus and
    /// I2C families (motherboard, DRAM, GPU) are absent on purpose: they are
    /// soldered to the machine and cannot be hot-plugged, so no arrival
    /// message will ever be about them.</summary>
    static readonly string[] OurVendors =
    {
        "vid_1b1c",   // Corsair
        "vid_1038",   // SteelSeries
        "vid_048d",   // ITE, the Gigabyte board controller
        "vid_046d",   // Logitech
        "vid_1532",   // Razer
        "vid_8089",   // Sayo
        "vid_0cf2",   // ENE, the Lian Li Uni Hub
        "vid_0416",   // Nuvoton, the Lian Li wireless dongle and the pump LCD
    };

    /*---------------- state ----------------*/

    readonly RecoveryPolicy _policy = new();
    readonly Action<RecoveryPlan> _recover;
    readonly Func<RecoveryConditions> _conditions;
    readonly DispatcherTimer _timer;
    readonly Dispatcher _ui;

    HwndSource? _window;
    IntPtr _hidNotify, _usbNotify;
    bool _disposed;

    /// <param name="recover">Runs on the UI thread when it is time. This is
    /// the view model's redetect-and-restore; the watchdog deliberately knows
    /// nothing about how that is done.</param>
    /// <param name="conditions">Asked at the moment of the decision, not when
    /// the event arrived: whether an SDK client holds a device and whether the
    /// lights are meant to be off can both change during the settle.</param>
    public DeviceWatchdog(Action<RecoveryPlan> recover, Func<RecoveryConditions> conditions)
    {
        _recover = recover;
        _conditions = conditions;
        _ui = Dispatcher.CurrentDispatcher;
        // Interval is set every time the timer is armed; this is only the
        // initial value. Stopped until something is pending, which is the
        // whole idle story of this class.
        _timer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(RecoveryPolicy.DeviceSettleMs),
        };
        _timer.Tick += (_, _) => Fire();
    }

    /// <summary>Start listening. Safe to call when either half fails: a
    /// machine where the notification cannot be registered still recovers on
    /// resume, and one where SystemEvents is unavailable still recovers on a
    /// replug. Neither failure is allowed to take the app down with it.</summary>
    public void Start()
    {
        try
        {
            // A message-only window, because the app spends most of its life
            // in the tray with no main window realised, and a notification
            // registered on a window that does not exist yet is a notification
            // that never arrives.
            _window = new HwndSource(new HwndSourceParameters("UnifiedRgb device watchdog")
            {
                ParentWindow = new IntPtr(HWND_MESSAGE),
                WindowStyle = 0,
            });
            _window.AddHook(WndProc);
            _hidNotify = Register(_window.Handle, HidInterface);
            _usbNotify = Register(_window.Handle, UsbInterface);
            if (_hidNotify == IntPtr.Zero && _usbNotify == IntPtr.Zero)
                Log.Warn("watchdog", "no device notifications registered: replug recovery is off, resume recovery still works");
        }
        catch (Exception ex)
        {
            // Worth a warning rather than a throw: half the feature still
            // works, and a tray app that will not start because a window class
            // could not be created is strictly worse than one that recovers
            // only on resume.
            Log.Warn("watchdog", $"device notifications unavailable: {ex.Message}");
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Log.Info("watchdog", "listening for device changes and for wake");
    }

    static IntPtr Register(IntPtr hwnd, Guid iface)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            Size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            DeviceType = DBT_DEVTYP_DEVICEINTERFACE,
            ClassGuid = iface,
        };
        IntPtr buf = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, buf, false);
            IntPtr h = RegisterDeviceNotificationW(hwnd, buf, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (h == IntPtr.Zero)
                Log.Warn("watchdog", $"RegisterDeviceNotification({iface}) failed: {Marshal.GetLastWin32Error()}");
            return h;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /*---------------- the two triggers ----------------*/

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE) return IntPtr.Zero;
        int evt = wParam.ToInt32();
        if (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE) return IntPtr.Zero;
        // Everything else Windows sends on this message (query-remove,
        // config-changed, the volume events) is either advisory or about
        // storage, and acting on it would just be more rescans.

        if (!IsOurs(lParam)) return IntPtr.Zero;

        Note(evt == DBT_DEVICEARRIVAL ? RecoveryReason.DeviceArrived : RecoveryReason.DeviceRemoved);
        return IntPtr.Zero;
    }

    /// <summary>Read the device interface path out of the broadcast and decide
    /// whether it is one of ours.
    ///
    /// Fails OPEN: anything we cannot read or cannot classify counts as ours.
    /// The cost of a false yes is one rescan the user will not notice; the
    /// cost of a false no is a device that stays dark, which is the entire bug
    /// this class exists to fix.</summary>
    static bool IsOurs(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return true;
        try
        {
            int type = Marshal.ReadInt32(lParam, sizeof(int));   // second field
            if (type != DBT_DEVTYP_DEVICEINTERFACE) return true;
            // The path starts where the fixed fields end.
            int offset = Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE>(nameof(DEV_BROADCAST_DEVICEINTERFACE.Name)).ToInt32();
            string? path = Marshal.PtrToStringUni(lParam + offset);
            if (string.IsNullOrEmpty(path)) return true;
            // ToLowerInvariant allocates one short string per USB event on the
            // machine. That is a few dozen bytes a day, not a per-frame cost,
            // and it is what keeps the comparison free of culture surprises.
            path = path.ToLowerInvariant();
            foreach (var vid in OurVendors)
                if (path.Contains(vid, StringComparison.Ordinal)) return true;
            return false;
        }
        catch
        {
            return true;   // unreadable broadcast: assume it matters
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Resume only. Suspend needs nothing from us: the exit-behaviour and
        // lights-off paths already run on the way down, and a device we write
        // to while Windows is tearing the USB stack apart is a device that
        // refuses the write for a reason that is not its fault.
        if (e.Mode != PowerModes.Resume) return;
        // SystemEvents raises on its own thread, and everything downstream
        // (the timer, the view model, the device list) is the UI's.
        if (_ui.CheckAccess()) Note(RecoveryReason.SystemResumed);
        else _ui.BeginInvoke(new Action(() => Note(RecoveryReason.SystemResumed)));
    }

    /*---------------- the debounce ----------------*/

    void Note(RecoveryReason reason)
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        _policy.Note(reason, now);
        Arm(now);
    }

    /// <summary>Point the timer at the policy's next decision moment. Restart
    /// rather than adjust: a DispatcherTimer whose Interval is set while it
    /// runs keeps counting from the old start, and a debounce that fires early
    /// is a debounce that does not work.</summary>
    void Arm(long now)
    {
        if (!_policy.Pending) { _timer.Stop(); _armedFor = long.MinValue; return; }
        // Leave a RUNNING timer alone unless the policy has actually pushed the
        // moment further out. Restarting unconditionally is what let a flapping
        // port starve the timer forever: once MaxDeferMs caps the deferral
        // DelayFrom returns 0, the interval floors at 50 ms, and any event
        // inside that 50 ms began the countdown again - so the ceiling that
        // exists precisely for a flapping port was the one thing that could
        // never fire.
        long due = _policy.DueAt;
        if (_timer.IsEnabled && due <= _armedFor) return;
        _timer.Stop();
        _armedFor = due;
        // Never zero: a DispatcherTimer with a zero interval fires on every
        // dispatcher pass, which is a busy loop dressed as a timer.
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _policy.DelayFrom(now)));
        _timer.Start();
    }

    /// <summary>Absolute tick the running timer is counting towards, so Arm can
    /// tell a genuine deferral from a restart that would reset the debounce.</summary>
    long _armedFor = long.MinValue;

    void Fire()
    {
        _timer.Stop();
        _armedFor = long.MinValue;
        if (_disposed) return;
        long now = Environment.TickCount64;
        // Remembered before the claim consumes it, so a failed recovery below
        // can put the same request back.
        var reason = _policy.PendingReason;

        RecoveryPlan plan;
        // The conditions lambda walks the device list and asks the lighting
        // controller about every claim; it is exactly as trustworthy as the
        // recovery the catch below guards, and it used to run OUTSIDE any try.
        // A throw here escapes a DispatcherTimer tick handler, which does not
        // log an error - it ends the process.
        try { plan = _policy.Claim(now, _conditions()); }
        catch (Exception ex)
        {
            Log.Error("watchdog", $"could not read the recovery conditions: {ex}");
            Arm(Environment.TickCount64);
            return;
        }

        switch (plan.Action)
        {
            case RecoveryAction.Rescan:
                try { _recover(plan); }
                catch (Exception ex)
                {
                    // Claim has already dropped the pending request and stamped
                    // the clock, so without this a rescan that threw halfway was
                    // the end of it: a torn-down device list, no lights, and
                    // nothing scheduled to try again. Re-note it and let the
                    // ordinary debounce have another go.
                    Log.Error("watchdog", $"recovery failed, will try again: {ex}");
                    long after = Environment.TickCount64;
                    _policy.Note(reason, after);
                    Arm(after);
                }
                break;
            case RecoveryAction.Wait:
                Arm(now);
                break;
        }
    }

    /// <summary>A rescan the user asked for, or one anything else performed,
    /// counts as the recovery: dropping the pending one avoids doing the whole
    /// teardown twice for one gesture.</summary>
    public void NoteRescanHappened() { _policy.Cancel(Environment.TickCount64); _timer.Stop(); _armedFor = long.MinValue; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        // Unregister BEFORE the window goes, or the notification outlives the
        // HWND it delivers to.
        if (_hidNotify != IntPtr.Zero) { UnregisterDeviceNotification(_hidNotify); _hidNotify = IntPtr.Zero; }
        if (_usbNotify != IntPtr.Zero) { UnregisterDeviceNotification(_usbNotify); _usbNotify = IntPtr.Zero; }
        if (_window != null)
        {
            _window.RemoveHook(WndProc);
            _window.Dispose();
            _window = null;
        }
    }
}
