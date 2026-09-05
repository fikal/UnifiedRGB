using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| "It manages itself": a tiny state machine over the lights.  |
|                                                              |
|   Locked   session locked: lights off, restore on unlock.    |
|   Schedule a timed window is open: lights off, or apply its  |
|            profile. Night mode is one of these now.          |
|   Sensor   a threshold rule is firing (CPU hit 85): apply    |
|            that rule's profile.                              |
|   App      a foreground process matches a rule: apply        |
|            that rule's profile.                              |
|   Base     whatever the user last had (captured the moment   |
|            we leave Base, restored, frames AND running       |
|            effects, when we come back).                      |
|                                                              |
| Priority: Locked > Schedule(off) > Sensor > Schedule(profile) |
| > App > Base. Transitions                                    |
| only on state CHANGE, steady states never re-apply. The      |
| decision itself is pure and lives in Core                    |
| (AutomationDecision.Resolve); this class gathers the facts,  |
| owns the per-rule sensor state, and performs the switch. All |
| on the dispatcher; session events come from SystemEvents.    |
\*-----------------------------------------------------------*/
public sealed class AutomationService : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Seconds since the last keyboard/mouse input anywhere in the
    /// session. dwTime shares GetTickCount's 32-bit domain, so the unsigned
    /// subtraction wraps correctly.</summary>
    static double IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((uint)Environment.TickCount - lii.dwTime) / 1000.0;
    }

    // How long you must be idle before night mode turns the lights off (when the
    // "only when I'm away" option is on). Being idle for this long BEFORE the
    // window starts means the lights drop right at the start time.
    const double NightIdleSeconds = 600;   // 10 minutes

    readonly MainViewModel _vm;
    readonly DispatcherTimer _timer;
    bool _locked;
    AutomationMode _mode = AutomationMode.Base;
    string? _activeRuleProfile;

    /*--- sensor rules: the hysteresis state lives here, one entry per rule,
          reused across ticks so a tick allocates nothing. ---*/
    readonly Stopwatch _clock = Stopwatch.StartNew();
    SensorRuleState[] _sensorStates = Array.Empty<SensorRuleState>();
    double?[] _sensorValues = Array.Empty<double?>();
    object? _sensorStatesFor;   // the rule list those states belong to
    Func<string, bool>? _profileExists;   // cached: a closure per tick is a closure too many
    MainViewModel.LightState? _returnPoint;
    // The user acted during a lights-off window: stay awake until every such
    // window has closed, then re-arm for the next one.
    bool _scheduleOverride;

    bool _selfApplying;   // our own profile applies must not clear the return point

    public AutomationService(MainViewModel vm)
    {
        _vm = vm;
        _vm.WakeLightsHook = Wake;
        _vm.LightingApplied += OnUserLighting;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>The user (UI, hotkey, or a scene sequence) changed the
    /// lighting while an override was active: their choice is the new
    /// baseline. Drop the stale snapshot so leaving the override doesn't
    /// stomp it — the field bug: return-to-Base applied an old profile.</summary>
    void OnUserLighting()
    {
        if (_selfApplying || _mode == AutomationMode.Base) return;
        if (_mode == AutomationMode.ScheduleOff)
        {
            // Someone changing colors at 11 PM clearly wants lights.
            _scheduleOverride = true;
            Log.Info("auto", "user changed lighting during a scheduled dark window, staying awake until it ends");
        }
        _returnPoint = null;
        Log.Info("auto", "lighting changed during an override, keeping it as the new baseline");
    }

    /// <summary>Wake lights button: leave night-off and restore what the user
    /// had before the window started; re-arms at the next night window.</summary>
    public void Wake()
    {
        if (_mode != AutomationMode.ScheduleOff) return;
        _scheduleOverride = true;
        Tick();
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { _locked = true; Tick(); }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { _locked = false; Tick(); }
    }

    void Tick()
    {
        try
        {
            var s = _vm.SettingsData;
            var sched = EvaluateSchedules(s);
            string? proc = s.AppSwitchEnabled ? ForegroundProcessName() : null;
            var (sensor, unavailable) = StepSensorRules(s);

            var decision = AutomationDecision.Resolve(new AutomationInputs
            {
                Locked = _locked,
                LockLightsOff = s.LockLightsOff,
                ScheduleOff = sched.Off,
                ScheduleProfile = sched.Profile,
                SchedulePaused = sched.Paused,
                ScheduleWaitingIdle = sched.WaitingIdle,
                ScheduleEnd = sched.End,
                AppSwitchEnabled = s.AppSwitchEnabled,
                ForegroundProcess = proc,
                ForegroundIsSelf = proc != null && proc.Equals(SelfName, StringComparison.OrdinalIgnoreCase),
                AppRules = s.AutomationRules,
                Sensor = sensor,
                SensorUnavailable = unavailable,
            });

            _vm.AutomationStatus = decision.Status;
            if (decision.Mode == _mode && decision.Profile == _activeRuleProfile) return;
            Transition(decision.Mode, decision.Profile);
        }
        catch (Exception ex) { Log.Occasional("automation", "auto", $"tick failed: {ex.Message}"); }
    }

    /// <summary>Which timed windows are open right now.
    ///
    /// The idle clock is only read when some open window actually asks for it,
    /// so the usual tick still costs one Win32 call for the foreground window
    /// and nothing else.</summary>
    (ScheduleHit? Off, ScheduleHit? Profile, bool Paused, bool WaitingIdle, string End)
        EvaluateSchedules(SettingsData s)
    {
        var rules = s.Schedules;
        if (rules == null || rules.Count == 0)
        {
            _scheduleOverride = false;
            return (null, null, false, false, "");
        }

        var now = DateTime.Now;
        ScheduleHit? off = null, profile = null;
        bool anyOffWindow = false, waitingIdle = false;
        string end = "";

        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (!r.Enabled || !ScheduleRule.InWindow(r, now)) continue;

            if (r.Action == ScheduleAction.LightsOff)
            {
                anyOffWindow = true;
                if (end.Length == 0) end = r.End;
                // "Only when I'm away": hold the lights until the machine has
                // been idle, so an evening session is never cut off. Idle time
                // carried in from before the window counts, so an already-away
                // machine drops right at the start time.
                if (r.IdleOnly && IdleSeconds() < NightIdleSeconds) { waitingIdle = true; continue; }
                if (off == null) off = new ScheduleHit(r.End, null);
            }
            else if (profile == null && !string.IsNullOrWhiteSpace(r.Profile) && _vm.HasProfile(r.Profile!))
            {
                if (r.IdleOnly && IdleSeconds() < NightIdleSeconds) continue;
                profile = new ScheduleHit(r.End, r.Profile);
            }
        }

        // The override pauses the dark until every lights-off window has closed,
        // then re-arms for the next one.
        if (!anyOffWindow) _scheduleOverride = false;
        if (_scheduleOverride) { off = null; waitingIdle = false; }

        return (off, profile, anyOffWindow && _scheduleOverride, off == null && waitingIdle, end);
    }

    /// <summary>Advance every sensor rule one tick and report the winner.
    /// Also the gate on the sensor poller: with no enabled rules nothing here
    /// touches SensorHub, so the hub stays asleep exactly as before.</summary>
    (SensorHit? Hit, string? Unavailable) StepSensorRules(SettingsData s)
    {
        var rules = s.SensorRulesEnabled ? s.SensorRules : null;
        if (rules == null || rules.Count == 0) return (null, null);

        // Wake only as much of the hub as the rules actually read: a rule on
        // CPU temperature must not drag in the GPU/board sweep.
        bool anyEnabled = false, needFull = false;
        for (int i = 0; i < rules.Count; i++)
        {
            if (!rules[i].Enabled) continue;
            anyEnabled = true;
            if (SensorSources.NeedsFullSweep(rules[i].Source)) { needFull = true; break; }
        }
        if (!anyEnabled) return (null, null);
        if (needFull) SensorHub.Touch(); else SensorHub.TouchTemps();

        // One state slot per rule. Reset when the list itself changes (added,
        // removed, reordered) so state never lands on the wrong rule.
        if (!ReferenceEquals(_sensorStatesFor, rules) || _sensorStates.Length != rules.Count)
        {
            _sensorStates = new SensorRuleState[rules.Count];
            _sensorValues = new double?[rules.Count];
            _sensorStatesFor = rules;
        }

        _profileExists ??= _vm.HasProfile;
        double now = _clock.Elapsed.TotalSeconds;
        string? unavailable = null;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            double? v = r.Enabled ? SensorSources.Read(r.Source) : null;
            _sensorValues[i] = v;
            _sensorStates[i] = SensorRuleEvaluator.Step(r, v, _sensorStates[i], now);
            if (r.Enabled && v == null && unavailable == null) unavailable = r.Source;
        }
        var hit = SensorRuleEvaluator.FirstActive(rules, _sensorStates, _sensorValues, _profileExists);
        // A firing rule outranks the "no reading" note.
        return (hit, hit == null ? unavailable : null);
    }

    void Transition(AutomationMode next, string? profile)
    {
        // Leaving Base: remember exactly what the user had.
        if (_mode == AutomationMode.Base && next != AutomationMode.Base)
            _returnPoint = _vm.CaptureState();

        _selfApplying = true;
        try
        {
        switch (next)
        {
            case AutomationMode.Locked:
            case AutomationMode.ScheduleOff:
                _vm.LightsOff();
                Log.Info("auto", next == AutomationMode.Locked ? "session locked, lights off" : "scheduled window, lights off");
                break;
            case AutomationMode.Sensor:
            case AutomationMode.ScheduleProfile:
            case AutomationMode.App:
                _vm.SetPumpLcdOn(true);   // unlocking straight into a rule must relight the LCD
                if (profile != null && _vm.ApplyProfileByName(profile))
                    Log.Info("auto", next switch
                    {
                        AutomationMode.Sensor => $"sensor rule fired, profile '{profile}'",
                        AutomationMode.ScheduleProfile => $"scheduled window, profile '{profile}'",
                        _ => $"foreground app rule, profile '{profile}'",
                    });
                break;
            case AutomationMode.Base:
                if (_returnPoint != null)
                {
                    _vm.RestoreState(_returnPoint);
                    Log.Info("auto", "restored your lighting");
                }
                // No return point (the user relit things mid-override): the LCD
                // still has to come back - RestoreState was the only path that did it.
                else _vm.SetPumpLcdOn(true);
                break;
        }
        }
        finally { _selfApplying = false; }
        _mode = next;
        _activeRuleProfile = next is AutomationMode.App or AutomationMode.Sensor
            or AutomationMode.ScheduleProfile ? profile : null;
        _vm.NightLightsOff = next == AutomationMode.ScheduleOff;   // drives the wake banner
        _vm.LightsSuppressed = next is AutomationMode.Locked or AutomationMode.ScheduleOff;   // scene sequences hold while off
    }

    // Resolved once: Process.GetCurrentProcess().ProcessName per 2 s tick took
    // a full process-table snapshot (100-500 KB) and leaked the Process object.
    static readonly string SelfName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");

    // Foreground pid -> name, so an unchanged foreground window costs no
    // GetProcessById (another full process-table snapshot) per tick.
    static uint _lastPid; static string? _lastName;

    static string? ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return null;
            if (pid == _lastPid && _lastName != null) return _lastName;
            string name;
            using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;

            // Store/UWP apps (Calculator etc.): the foreground window belongs
            // to ApplicationFrameHost; the real app owns a child window
            // inside the frame — resolve to that one, or rules never match.
            if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint framePid = pid;
                string? hosted = null;
                EnumChildWindows(h, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out uint cpid);
                    if (cpid != 0 && cpid != framePid)
                    {
                        try { using var cp = Process.GetProcessById((int)cpid); hosted = cp.ProcessName; }
                        catch { }
                        return hosted == null;   // stop once resolved
                    }
                    return true;
                }, IntPtr.Zero);
                if (hosted != null) name = hosted;
            }
            _lastPid = pid; _lastName = name;
            return name;
        }
        catch { _lastPid = 0; _lastName = null; return null; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _vm.LightingApplied -= OnUserLighting;
        _vm.WakeLightsHook = null;   // was left dangling after dispose
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Never leave the lights off because we're exiting mid-state.
        if (_mode is AutomationMode.Locked or AutomationMode.ScheduleOff && _returnPoint != null)
            try { _vm.RestoreState(_returnPoint); } catch { }
    }
}
