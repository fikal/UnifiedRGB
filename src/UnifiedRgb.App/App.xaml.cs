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
        UnifiedRgb.App.Services.AppInstall.LogStartup();
        // Off the UI thread: it talks to Task Scheduler and may copy the exe.
        _ = System.Threading.Tasks.Task.Run(UnifiedRgb.App.Services.AppInstall.ReconcileAutostart);

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
            // Wireless fans have no BIOS: they follow the board's PWM line when
            // the user chose that for exit, and otherwise the ones we were
            // driving go to 100%. Both used to be skipped here, leaving them
            // on whatever duty the curve last wrote, with nothing running it.
            bool wirelessToBoard = CoolingViewModel.HandOffWirelessOnCrash;
            try
            {
                // HandbackOwed: an Identify burst or a queued handback is owed
                // one as much as a fan with a mode (see MainViewModel.Dispose).
                if (UnifiedRgb.Core.Sensors.SensorHub.HandbackOwed)
                    UnifiedRgb.Core.Sensors.SensorHub.RestoreAllFans(UnifiedRgb.Core.Sensors.FanHandback.Crash, wirelessHandoff: wirelessToBoard);
            }
            catch (Exception fanEx) { Log.Warn("app", $"crash: handing the fans back failed: {fanEx.Message}"); }
            if (wirelessToBoard)
            {
                // Same as a clean exit (MainViewModel.Dispose), which does it
                // whether or not any fan had a mode.
                try { UnifiedRgb.Core.Devices.LianLiWireless.Instance?.FollowPwmLine(); }
                catch (Exception lianEx) { Log.Warn("app", $"crash: handing the wireless fans to the board failed: {lianEx.Message}"); }
            }
            // Then close the sensor sources: only LHM's Computer.Close() unloads
            // its ring0 driver service and releases the ISA-bus mutex, and
            // process teardown does neither. Best-effort, after the restore.
            try { UnifiedRgb.Core.Sensors.SensorHub.Shutdown(); } catch { }
        };

        // A binding whose path stops resolving fails silently in a shipped build:
        // the control goes blank and nothing anywhere says why. WPF does report
        // it, on a trace source nobody listens to by default. Listen, and put
        // each distinct message in the session log once, so a support bundle
        // can answer "why is that box empty".
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingTraceListener());

        // An import that brought in fan-config.json has the running fan hub
        // re-read it, rather than the hub overwriting it before the restart.
        // Wired here, not in the view model, so harness-built view models
        // never reach the real hub.
        MainViewModel.AfterFanConfigImport = UnifiedRgb.Core.Sensors.SensorHub.ReloadFanConfig;

        base.OnStartup(e);
    }

    /// <summary>Routes WPF data-binding trace output into the app log, once per
    /// distinct message, with a cap so a binding failing on every frame cannot
    /// grow the set for the life of the process.</summary>
    sealed class BindingTraceListener : System.Diagnostics.TraceListener
    {
        readonly HashSet<string> _seen = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(System.Diagnostics.TraceEventCache? eventCache, string source,
            System.Diagnostics.TraceEventType eventType, int id, string? message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_seen)
            {
                if (_seen.Count >= 200 || !_seen.Add(message)) return;
            }
            Log.Warn("binding", message);
        }
        public override void TraceEvent(System.Diagnostics.TraceEventCache? eventCache, string source,
            System.Diagnostics.TraceEventType eventType, int id, string? format, params object?[]? args)
            => TraceEvent(eventCache, source, eventType, id,
                          args is { Length: > 0 } && format != null ? string.Format(format, args) : format);
    }
}
