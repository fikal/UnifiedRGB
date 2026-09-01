using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| "It manages itself": a tiny state machine over the lights.  |
|                                                              |
|   Locked   — session locked: lights off, restore on unlock.  |
|   Night    — inside the nightly off-window: lights off.      |
|   App      — a foreground process matches a rule: apply      |
|              that rule's profile.                            |
|   Base     — whatever the user last had (captured the        |
|              moment we leave Base, restored — frames AND     |
|              running effects — when we come back).           |
|                                                              |
| Priority: Locked > Night > App > Base. Transitions only on   |
| state CHANGE — steady states never re-apply. All on the      |
| dispatcher; session events come from SystemEvents.           |
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

    enum Mode { Base, App, Night, Locked }

    readonly MainViewModel _vm;
    readonly DispatcherTimer _timer;
    bool _locked;
    Mode _mode = Mode.Base;
    string? _activeRuleProfile;
    MainViewModel.LightState? _returnPoint;
    // The user acted during the night window: stay awake until the window
    // ends, then re-arm for the next night.
    bool _nightOverride;

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
        if (_selfApplying || _mode == Mode.Base) return;
        if (_mode == Mode.Night)
        {
            // Someone changing colors at 11 PM clearly wants lights.
            _nightOverride = true;
            Log.Info("auto", "user changed lighting during night mode - staying awake until tomorrow night");
        }
        _returnPoint = null;
        Log.Info("auto", "lighting changed during an override — keeping it as the new baseline");
    }

    /// <summary>Wake lights button: leave night-off and restore what the user
    /// had before the window started; re-arms at the next night window.</summary>
    public void Wake()
    {
        if (_mode != Mode.Night) return;
        _nightOverride = true;
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
            bool inNight = s.NightMode && InNightWindow(s);
            if (!inNight) _nightOverride = false;   // re-arm for the next night
            bool nightArmed = inNight && !_nightOverride;
            // "Only when I'm away": inside the window, hold the lights until you've
            // been idle 10 min - so an evening session isn't cut off, and idle time
            // carried in from before the start counts (lights drop right at start
            // if you were already away). Active again → lights come back.
            bool nightOff = nightArmed && (!s.NightIdleOnly || IdleSeconds() >= NightIdleSeconds);
            string? proc = s.AppSwitchEnabled ? ForegroundProcessName() : null;
            (Mode mode, string? profile) desired =
                _locked && s.LockLightsOff ? (Mode.Locked, null)
                : nightOff ? (Mode.Night, null)
                : s.AppSwitchEnabled && MatchRule(s, proc) is string p ? (Mode.App, p)
                : (Mode.Base, null);

            // Live feedback: without this the feature is a black box
            // ("i dont know how this works").
            bool self = proc != null &&
                proc.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
            _vm.AutomationStatus =
                desired.mode == Mode.Night ? $"Night mode: lights off until {s.NightEnd}"
                : inNight && _nightOverride ? "Night mode paused (you woke the lights). Resumes tomorrow night."
                : nightArmed && s.NightIdleOnly ? "Night mode armed — lights turn off after 10 min idle"
                : !s.AppSwitchEnabled ? ""
                : proc == null ? "Watching for your listed programs…"
                : self ? "This window is focused. Switch to another program to test your rules."
                : desired.profile != null ? $"Foreground app: {proc} → applying profile '{desired.profile}'"
                : $"Foreground app: {proc} (no matching rule)";

            if (desired.mode == _mode && desired.profile == _activeRuleProfile) return;
            Transition(desired.mode, desired.profile);
        }
        catch (Exception ex) { Log.Occasional("automation", "auto", $"tick failed: {ex.Message}"); }
    }

    void Transition(Mode next, string? profile)
    {
        // Leaving Base: remember exactly what the user had.
        if (_mode == Mode.Base && next != Mode.Base)
            _returnPoint = _vm.CaptureState();

        _selfApplying = true;
        try
        {
        switch (next)
        {
            case Mode.Locked:
            case Mode.Night:
                _vm.LightsOff();
                Log.Info("auto", next == Mode.Locked ? "session locked — lights off" : "night window — lights off");
                break;
            case Mode.App:
                _vm.SetPumpLcdOn(true);   // unlocking straight into an app rule must relight the LCD
                if (profile != null && _vm.ApplyProfileByName(profile))
                    Log.Info("auto", $"foreground app rule — profile '{profile}'");
                break;
            case Mode.Base:
                if (_returnPoint != null)
                {
                    _vm.RestoreState(_returnPoint);
                    Log.Info("auto", "restored your lighting");
                }
                break;
        }
        }
        finally { _selfApplying = false; }
        _mode = next;
        _activeRuleProfile = next == Mode.App ? profile : null;
        _vm.NightLightsOff = next == Mode.Night;   // drives the wake banner
    }

    static bool InNightWindow(SettingsData s)
    {
        if (!TimeSpan.TryParse(s.NightStart, out var start) ||
            !TimeSpan.TryParse(s.NightEnd, out var end)) return false;
        var now = DateTime.Now.TimeOfDay;
        return start <= end
            ? now >= start && now < end
            : now >= start || now < end;   // wraps midnight (23:00 → 07:00)
    }

    static string? MatchRule(SettingsData s, string? proc)
    {
        var rules = s.AutomationRules;
        if (rules == null || rules.Count == 0 || string.IsNullOrEmpty(proc)) return null;
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Process) || string.IsNullOrWhiteSpace(r.Profile)) continue;
            string want = r.Process.Trim();
            if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want = want[..^4];
            if (proc.Contains(want, StringComparison.OrdinalIgnoreCase)) return r.Profile;
        }
        return null;
    }

    static string? ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return null;
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
            return name;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _vm.LightingApplied -= OnUserLighting;
        _vm.WakeLightsHook = null;   // was left dangling after dispose
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Never leave the lights off because we're exiting mid-state.
        if (_mode is Mode.Locked or Mode.Night && _returnPoint != null)
            try { _vm.RestoreState(_returnPoint); } catch { }
    }
}
