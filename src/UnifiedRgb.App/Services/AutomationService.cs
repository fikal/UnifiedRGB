using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Sensors;
// System.Diagnostics also defines an ActivityKind (distributed tracing), and
// this file needs Stopwatch from that namespace, so name the one we mean.
using ActivityKind = UnifiedRgb.Core.Automation.ActivityKind;

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

    /*--- Temporary pause. Per session and never persisted: see AutomationPause,
          which owns the reasoning and the copy. The held mode and profile are
          the lighting the pause froze, so a lock that happens mid-pause can be
          undone exactly rather than approximately. ---*/
    bool _paused;
    AutomationMode _pauseHoldMode = AutomationMode.Base;
    string? _pauseHoldProfile;
    MainViewModel.LightState? _pauseLighting;

    // Edge detection for the fan failsafe. It fires on the sensor hub's own
    // thread and is only visible as a flag, so the tick is where it becomes a
    // sentence. Watched even while paused: it is a hardware event, not a
    // lighting decision, and it is the single most alarming thing this app can
    // do without being asked.
    bool _failsafeSeen;

    /// <summary>The recent-decisions history the UI shows. Exposed here purely
    /// so the wiring is obvious from the service that writes most of it; it is
    /// the one shared instance either way.</summary>
    public static ActivityLog Activity => ActivityLog.Shared;

    /// <summary>The live service, for the view model's pause binding.
    ///
    /// The window owns the instance in a private field and the view model has
    /// no route to it, so the alternative was another hook property in the
    /// style of WakeLightsHook. There is exactly one of these per process and
    /// it lives for the whole session, so a plain reference says the same
    /// thing with less ceremony. Null before the window is built and after
    /// dispose, hence the nullable.</summary>
    public static AutomationService? Current { get; private set; }

    /// <summary>Stop rules changing the lighting until this is turned off
    /// again. Deliberately temporary: it lives for the session only, so a
    /// forgotten pause cannot quietly kill someone's schedules next week.
    ///
    /// Setting it ticks immediately rather than waiting up to two seconds for
    /// the timer, because a pause button that takes a visible moment to do
    /// anything reads as a broken pause button.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value)
            {
                // Freeze what is on now. Freeze() is what decides that "lights
                // currently off" is the one state not worth freezing.
                _pauseHoldMode = AutomationPause.Freeze(_mode);
                _pauseHoldProfile = _pauseHoldMode == _mode ? _activeRuleProfile : null;
                _pauseLighting = _mode is AutomationMode.Locked or AutomationMode.ScheduleOff
                    ? _returnPoint : _vm.CaptureState();
                ActivityLog.Note(ActivityKind.Paused, AutomationPause.Began);
                Log.Info("auto", "automation paused by the user");
            }
            else
            {
                ActivityLog.Note(ActivityKind.Paused, AutomationPause.Ended);
                Log.Info("auto", "automation resumed by the user");
            }
            Tick();
        }
    }

    public AutomationService(MainViewModel vm) : this(vm, monitor: true) { }

    internal AutomationService(MainViewModel vm, bool monitor)
    {
        _vm = vm;
        _vm.WakeLightsHook = Wake;
        _vm.LightingApplied += OnUserLighting;
        if (monitor) SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        if (monitor) _timer.Start();
        Current = this;
    }

    /// <summary>The user (UI, hotkey, or a scene sequence) changed the
    /// lighting while an override was active: their choice is the new
    /// baseline. Drop the stale snapshot so leaving the override doesn't
    /// stomp it — the field bug: return-to-Base applied an old profile.</summary>
    void OnUserLighting()
    {
        if (_paused && !_selfApplying)
        {
            _pauseLighting = _vm.CaptureState();
            _pauseHoldMode = AutomationMode.Base;
            _pauseHoldProfile = null;
            _returnPoint = _pauseLighting;
            return;
        }
        if (_selfApplying || _mode == AutomationMode.Base) return;
        if (_mode == AutomationMode.ScheduleOff)
        {
            // Someone changing colors at 11 PM clearly wants lights.
            _scheduleOverride = true;
            Log.Info("auto", "user changed lighting during a scheduled dark window, staying awake until it ends");
            ActivityLog.Note(ActivityKind.UserOverride,
                "You lit things up during a scheduled dark window, so that schedule stays paused until the window ends.");
        }
        _returnPoint = null;
        Log.Info("auto", "lighting changed during an override, keeping it as the new baseline");
        ActivityLog.Note(ActivityKind.UserOverride,
            "You changed the lighting while a rule was running, so that is your baseline from now on.");
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

            // Before anything else, and regardless of the pause: the fans going
            // back to automatic is the user's business whatever the lights are
            // doing.
            NoteFailsafe();

            if (_paused)
            {
                // Nothing is gathered while paused: no schedule sweep, no
                // foreground lookup, no sensor step. Held is a function of the
                // frozen state and the lock alone, so a paused app is also the
                // quietest the automation ever is.
                var held = AutomationPause.Resolve(_pauseHoldMode, _pauseHoldProfile, _locked, s.LockLightsOff);
                _vm.SetAutomationStatus(held.Status, held.Topic);
                if (held.Mode == _mode && held.Profile == _activeRuleProfile) return;
                if (held.Mode == AutomationMode.Locked)
                {
                    // Capture immediately before blackout, including hand edits
                    // which do not raise the profile-applied event.
                    if (_mode is not (AutomationMode.Locked or AutomationMode.ScheduleOff))
                        _pauseLighting = _vm.CaptureState();
                }
                else if (_pauseLighting != null)
                {
                    _vm.NightLightsOff = false;
                    _vm.LightsSuppressed = false;
                    _selfApplying = true;
                    try { _vm.RestoreState(_pauseLighting); }
                    finally { _selfApplying = false; }
                    _mode = held.Mode;
                    _activeRuleProfile = held.Profile;
                    return;
                }
                Transition(held.Mode, held.Profile, null);
                return;
            }

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

            _vm.SetAutomationStatus(decision.Status, decision.Topic);
            // Resuming is not itself a transition. This used to be forced
            // open by a _resumePending flag, which sent an un-paused desk
            // straight back to the startup profile - wiping the lighting the
            // user had set BY HAND during the pause, and filing it as "no
            // rule matches any more". The flag was never needed: the paused
            // branch above keeps _mode and _activeRuleProfile level with what
            // is actually lit, so a decision that differs from them is
            // already the only thing a resume has to re-assert.
            if (decision.Mode == _mode && decision.Profile == _activeRuleProfile) return;

            // Only now, on an actual transition, is it worth asking what ELSE
            // wanted the lights. Re-running the app match costs one substring
            // sweep and buys the one thing the user cannot see today: which
            // source outranked which. Doing it per tick would allocate nothing
            // either, but it would say the same thing over and over.
            if (s.AppSwitchEnabled)
            {
                string? alsoWanted = AutomationRule.Match(s.AutomationRules, proc);
                if (alsoWanted != null && PrecedenceNote(decision.Mode, decision.Profile, alsoWanted, proc) is string note)
                    ActivityLog.Note(ActivityKind.Precedence, note);
            }

            Transition(decision.Mode, decision.Profile, decision.Status);
        }
        catch (Exception ex) { Log.Occasional("automation", "auto", $"tick failed: {ex.Message}"); }
    }

    /// <summary>An app rule matched and lost. Precedence is completely
    /// invisible in the UI today: the schedule simply wins and the app rule
    /// looks broken, which is exactly the "complex setup is hard to
    /// understand" complaint. Returns null when there is nothing worth saying
    /// (the app rule won, or the session is locked, where the reason is not
    /// exactly subtle).</summary>
    static string? PrecedenceNote(AutomationMode mode, string? profile, string appProfile, string? proc)
    {
        string who = proc ?? "the foreground app";
        return mode switch
        {
            AutomationMode.ScheduleOff =>
                $"A scheduled lights off window won over your app rule for {who}, so profile '{appProfile}' is not being applied.",
            AutomationMode.ScheduleProfile when profile != null && profile != appProfile =>
                $"A schedule won over your app rule for {who}: profile '{profile}' is on instead of '{appProfile}'.",
            AutomationMode.Sensor when profile != null && profile != appProfile =>
                $"A sensor rule won over your app rule for {who}: profile '{profile}' is on instead of '{appProfile}'.",
            _ => null,
        };
    }

    /// <summary>Turn the fan failsafe flag into history the moment it changes.
    /// Reading the flag touches nothing and never wakes the hub, so this is
    /// free on the ticks where nothing has happened.</summary>
    void NoteFailsafe()
    {
        bool tripped = SensorHub.FailsafeTripped;
        if (tripped == _failsafeSeen) return;
        _failsafeSeen = tripped;
        ActivityLog.Note(ActivityKind.Failsafe, tripped
            ? "The thermal failsafe fired: the CPU or GPU got too hot, so every fan went back to the board's own control. Your curves are kept."
            : "The thermal failsafe was cleared by a new fan-control setting. Other fans may still be on automatic control.");
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
                // IdleOnly is deliberately ignored here: it means "do not cut my
                // session short", which only makes sense for going dark. The
                // editor hides it for a profile window, so an unread flag on one
                // is either migrated or hand-edited and must not silently stop
                // the rule from ever firing.
                profile = new ScheduleHit(r.End, r.Profile);
            }
        }

        // The override pauses the dark until every lights-off window has closed,
        // then re-arms for the next one. It silences the profile windows with it:
        // waking the lights is the user saying they want THEIR lighting, and
        // handing them a scheduled profile a second later is not that. Scheduling
        // resumes once the dark window they overrode has passed.
        if (!anyOffWindow) _scheduleOverride = false;
        if (_scheduleOverride) { off = null; profile = null; waitingIdle = false; }

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
        bool anyEnabled = false, needHub = false, needFull = false;
        for (int i = 0; i < rules.Count; i++)
        {
            if (!rules[i].Enabled) continue;
            anyEnabled = true;
            // Battery arrives from its own poller, so a battery rule wakes
            // nothing here at all.
            if (!SensorSources.NeedsHub(rules[i].Source)) continue;
            needHub = true;
            if (SensorSources.NeedsFullSweep(rules[i].Source)) { needFull = true; break; }
        }
        if (!anyEnabled) return (null, null);
        if (needFull) SensorHub.Touch();
        else if (needHub) SensorHub.TouchTemps();

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

    /// <summary>Perform the switch, and say why in the activity history.
    ///
    /// <paramref name="status"/> is the line AutomationDecision.Resolve already
    /// wrote for this decision. It is reused verbatim rather than re-worded,
    /// because that copy is the one place the wording of "what the automation
    /// is doing" is maintained, and a second set of nearly-identical sentences
    /// would drift from it within a release. It is null when the transition did
    /// not come from a decision (the pause holding a mode), and the fallbacks
    /// below cover that.</summary>
    void Transition(AutomationMode next, string? profile, string? status)
    {
        // Leaving Base: remember exactly what the user had, and SAY what that
        // was. "restored your lighting" on the way back could not be told apart
        // from doing nothing, which cost a debugging session: the baseline had
        // quietly become the rule's own profile.
        if (_mode == AutomationMode.Base && next != AutomationMode.Base)
        {
            _returnPoint = _vm.CaptureState();
            Log.Info("auto", $"baseline saved: {Describe(_returnPoint)}");
        }

        _selfApplying = true;
        try
        {
        switch (next)
        {
            case AutomationMode.Locked:
            case AutomationMode.ScheduleOff:
                _vm.LightsOff();
                Log.Info("auto", next == AutomationMode.Locked ? "session locked, lights off" : "scheduled window, lights off");
                ActivityLog.Note(ActivityKind.LightsOff, next == AutomationMode.Locked
                    ? "Your session locked, so the lights went off. They come back when you sign in."
                    // The scheduled case has a status that already names the
                    // window's end time, which is the fact people actually want.
                    : status ?? "A scheduled window turned the lights off.");
                break;
            case AutomationMode.Sensor:
            case AutomationMode.ScheduleProfile:
            case AutomationMode.App:
                _vm.SetPumpLcdOn(true);   // unlocking straight into a rule must relight the LCD
                if (profile == null) break;
                if (_vm.ApplyProfileByName(profile))
                {
                    Log.Info("auto", next switch
                    {
                        AutomationMode.Sensor => $"sensor rule fired, applied profile '{profile}'",
                        AutomationMode.ScheduleProfile => $"scheduled window, applied profile '{profile}'",
                        _ => $"foreground app rule matched, applied profile '{profile}'",
                    });
                    // The status already says which rule and which profile, so
                    // the entry's category ("Rule activated") is the only thing
                    // this needs to add. The fallback is for the pause putting
                    // a frozen rule back after an unlock, where there is no
                    // fresh decision to quote.
                    ActivityLog.Note(ActivityKind.RuleActivated,
                        status ?? $"Back from the lock screen, profile '{profile}' is on again.");
                }
                // A rule naming a profile that no longer exists used to log
                // nothing at all, so the lights not changing had no explanation.
                else
                {
                    Log.Warn("auto", $"a rule wanted profile '{profile}', which no longer exists");
                    ActivityLog.Note(ActivityKind.RuleActivated,
                        $"A rule asked for profile '{profile}', which no longer exists, so your lighting was left alone.");
                }
                break;
            case AutomationMode.Base:
                // Relight the pump LCD FIRST, whichever branch below wins. Lock
                // and the scheduled dark window blank it (LightsOff), and only
                // the RestoreState branch used to turn it back on: with a
                // startup profile set (the default return policy) an ordinary
                // unlock restored the RGB and left the panel black.
                _vm.SetPumpLcdOn(true);
                // Back to the startup profile, if there is one and the user has
                // not asked otherwise. The saved baseline is whatever happened
                // to be on screen when the rule fired, which after an evening of
                // building a profile IS that profile: the rule then "returns" to
                // the thing it was overriding with, and nothing appears to end.
                string? startup = _vm.ReturnProfile;

                // Why we are back at Base. Blaming the rules while automation
                // is paused would send someone hunting through a rule list
                // that was never involved.
                string why = _paused ? "Automation is paused" : "No rule matches any more";

                if (!string.IsNullOrWhiteSpace(startup) && _vm.ApplyProfileByName(startup))
                {
                    Log.Info("auto", $"no rule matches, back to your startup profile '{startup}'");
                    ActivityLog.Note(ActivityKind.BackToBase,
                        $"{why}, so your startup profile '{startup}' is back on.");
                }
                else if (_returnPoint != null)
                {
                    _vm.RestoreState(_returnPoint);
                    Log.Info("auto", $"no rule matches, restored {Describe(_returnPoint)}");
                    // Naming what was restored is the whole point: "restored
                    // your lighting" cannot be told apart from doing nothing.
                    ActivityLog.Note(ActivityKind.BackToBase,
                        $"{why}, so {Describe(_returnPoint)} is back on.");
                }
                // No return point (the user relit things mid-override): the LCD
                // still has to come back - RestoreState was the only path that did it.
                else
                {
                    _vm.SetPumpLcdOn(true);
                    Log.Info("auto", "no rule matches, keeping the lighting you set during it");
                    ActivityLog.Note(ActivityKind.BackToBase,
                        $"{why}, and you changed the lighting while a rule was running, so that is what you keep.");
                }
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

    /// <summary>What a saved baseline actually is, for the log. A profile name
    /// when one was selected, otherwise how much hand-set lighting it holds:
    /// either way the line says enough to tell a real restore from a no-op.</summary>
    static string Describe(MainViewModel.LightState s) =>
        s.ProfileName is { Length: > 0 } name
            ? $"profile '{name}'"
            : $"hand-set lighting ({s.Effects.Count} effect(s) on {s.Frames.Count} device(s))";

    // Resolved once: Process.GetCurrentProcess().ProcessName per 2 s tick took
    // a full process-table snapshot (100-500 KB) and leaked the Process object.
    static readonly string SelfName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");

    // Foreground WINDOW + pid -> name, so an unchanged foreground window costs
    // no GetProcessById (another full process-table snapshot) per tick.
    //
    // The window has to be part of the key. Keyed on the pid alone, every
    // Store/UWP app looked identical: they all share one ApplicationFrameHost
    // process, so switching from one hosted app to another hit the cache and
    // returned the FIRST one's name for as long as the user stayed inside
    // hosted apps - the wrong profile, stickily. The timestamp covers the
    // other half: pids get reused, and a cached name otherwise outlived the
    // process it named.
    static IntPtr _lastHwnd; static uint _lastPid; static string? _lastName; static long _lastAt;
    const long ForegroundCacheMs = 5_000;

    static string? ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return null;
            long now = Environment.TickCount64;
            if (h == _lastHwnd && pid == _lastPid && _lastName != null && now - _lastAt < ForegroundCacheMs)
                return _lastName;
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
            _lastHwnd = h; _lastPid = pid; _lastName = name; _lastAt = now;
            return name;
        }
        catch { _lastHwnd = IntPtr.Zero; _lastPid = 0; _lastName = null; return null; }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _timer.Stop();
        _vm.LightingApplied -= OnUserLighting;
        _vm.WakeLightsHook = null;   // was left dangling after dispose
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Never leave the lights off because we're exiting mid-state.
        if (_mode is AutomationMode.Locked or AutomationMode.ScheduleOff && _returnPoint != null)
            try { _vm.RestoreState(_returnPoint); } catch { }
    }
}
