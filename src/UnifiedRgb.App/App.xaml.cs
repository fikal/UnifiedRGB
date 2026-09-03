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
    static bool _errorBoxOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new System.Threading.Mutex(true, @"Local\UnifiedRgb.App.Instance", out bool isFirst);
        _activate = new System.Threading.EventWaitHandle(false,
            System.Threading.EventResetMode.AutoReset, @"Local\UnifiedRgb.App.Activate");
        if (!isFirst)
        {
            // Deliberately no Log call here: Log's first touch rotates a >1 MB
            // log by moving it aside, which would split the RUNNING instance's
            // session log. The first instance logs the signal instead.
            _activate.Set();
            Environment.Exit(0);
        }
        // First instance: surface the window whenever a later launch signals.
        var listener = new System.Threading.Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    Log.Info("app", "another instance was launched - surfacing this one");
                    (MainWindow as MainWindow)?.SurfaceFromSecondLaunch();
                });
        })
        { IsBackground = true, Name = "single-instance" };
        listener.Start();

        Log.Info("app", $"UnifiedRGB starting  elevated={DiagnosticReport.IsAdmin()}  args={string.Join(' ', e.Args)}");

        // Crashes land in the log so remote users can send it back.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("app", ex.Exception);
            ex.Handled = true;
            // MessageBox pumps the dispatcher, so a timer that throws on every
            // tick re-enters here while the box is still up and would stack a
            // fresh modal on top of it each tick. One box at a time; every
            // occurrence is in the log regardless.
            if (_errorBoxOpen) return;
            _errorBoxOpen = true;
            try
            {
                MessageBox.Show($"UnifiedRGB hit an error (details in {Log.FilePath}):\n\n{ex.Exception.Message}",
                    "UnifiedRGB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _errorBoxOpen = false; }
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
            // Then close the sensor sources: only LHM's Computer.Close() unloads
            // its ring0 driver service and releases the ISA-bus mutex, and
            // process teardown does neither. Best-effort, after the restore.
            try { UnifiedRgb.Core.Sensors.SensorHub.Shutdown(); } catch { }
        };

        base.OnStartup(e);
    }
}
