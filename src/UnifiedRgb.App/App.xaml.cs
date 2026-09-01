using System.Windows;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

public partial class App : Application
{
    // Two live instances fight over every device (interleaved HID writes can
    // wedge firmware) — same-session single instance, with a signal so the
    // second launch surfaces the first one's window instead.
    static System.Threading.Mutex? _single;
    static System.Threading.EventWaitHandle? _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless helper mode: the non-elevated app relaunches itself this
        // way (elevated) so the support diagnostic includes the admin-only
        // SMBus/RAM scan. Collect, write, exit — no window, no devices, and
        // deliberately exempt from the single-instance rule.
        int diagArg = Array.IndexOf(e.Args, "--collect-diag");
        if (diagArg >= 0 && diagArg + 1 < e.Args.Length)
        {
            try { System.IO.File.WriteAllText(e.Args[diagArg + 1], DiagnosticReport.Collect()); }
            catch (Exception ex) { Log.Error("diag-helper", ex); }
            Environment.Exit(0);
        }

        _single = new System.Threading.Mutex(true, @"Local\UnifiedRgb.App.Instance", out bool isFirst);
        _activate = new System.Threading.EventWaitHandle(false,
            System.Threading.EventResetMode.AutoReset, @"Local\UnifiedRgb.App.Activate");
        if (!isFirst)
        {
            Log.Info("app", "another instance is running - signaling it and exiting");
            _activate.Set();
            Environment.Exit(0);
        }
        // First instance: surface the window whenever a later launch signals.
        var listener = new System.Threading.Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.SurfaceFromSecondLaunch());
        })
        { IsBackground = true, Name = "single-instance" };
        listener.Start();

        Log.Info("app", $"UnifiedRGB starting  elevated={IsElevated()}  args={string.Join(' ', e.Args)}");

        // Crashes land in the log so remote users can send it back.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("app", ex.Exception);
            MessageBox.Show($"UnifiedRGB hit an error (details in {Log.FilePath}):\n\n{ex.Exception.Message}",
                "UnifiedRGB", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("app", ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));
            // Fatal path: hand any manually-driven fans back to the BIOS
            // before the process dies (the marker file covers a hard kill).
            try
            {
                if (UnifiedRgb.Core.Sensors.SensorHub.AnyControlledFan)
                    UnifiedRgb.Core.Sensors.SensorHub.RestoreAllFans("crash", keepConfig: true);
            }
            catch { }
        };

        base.OnStartup(e);
    }

    static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
