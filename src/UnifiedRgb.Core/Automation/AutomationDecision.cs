namespace UnifiedRgb.Core.Automation;

/// <summary>What the lights should be doing right now.</summary>
public enum AutomationMode
{
    /// <summary>Whatever the user last set. The state every override returns to.</summary>
    Base,
    /// <summary>A foreground-app rule matched.</summary>
    App,
    /// <summary>A sensor threshold rule is firing.</summary>
    Sensor,
    /// <summary>Inside the nightly off window.</summary>
    Night,
    /// <summary>Session locked.</summary>
    Locked,
}

/// <summary>Everything the decision reads, gathered by the caller. A struct of
/// facts, so the decision itself touches no clock, no Win32 and no settings
/// file and the test harness can drive every combination.</summary>
public readonly struct AutomationInputs
{
    public bool Locked { get; init; }
    public bool LockLightsOff { get; init; }

    /// <summary>Inside the configured night window (the caller owns the clock).</summary>
    public bool InNightWindow { get; init; }
    /// <summary>The user relit things during the window, so night is paused
    /// until the window ends.</summary>
    public bool NightOverride { get; init; }
    /// <summary>Night waits for the machine to go idle instead of firing at the
    /// start time.</summary>
    public bool NightIdleOnly { get; init; }
    public double IdleSeconds { get; init; }
    public double NightIdleThreshold { get; init; }
    /// <summary>End of the window, for the status line only.</summary>
    public string NightEnd { get; init; }

    public bool AppSwitchEnabled { get; init; }
    public string? ForegroundProcess { get; init; }
    /// <summary>The focused window is ours: worth saying so, because testing a
    /// rule while staring at this app never matches anything.</summary>
    public bool ForegroundIsSelf { get; init; }
    public IReadOnlyList<AutomationRule>? AppRules { get; init; }

    /// <summary>The firing sensor rule, already evaluated by the caller
    /// (it owns the per-rule state across ticks).</summary>
    public SensorHit? Sensor { get; init; }
    /// <summary>A source some enabled rule wants but cannot read, for the
    /// status line ("needs PawnIO"). Null when everything is readable.</summary>
    public string? SensorUnavailable { get; init; }
}

public readonly record struct AutomationOutcome(AutomationMode Mode, string? Profile, string Status);

/// <summary>The whole "it manages itself" decision, as one pure function.
///
/// Priority: Locked > Night > Sensor > App > Base. A thermal alert outranks
/// "you are in a game", because the alert is the machine telling you something;
/// it does not outrank lights the user deliberately put out.</summary>
public static class AutomationDecision
{
    public static AutomationOutcome Resolve(in AutomationInputs x)
    {
        bool inNight = x.InNightWindow;
        bool nightArmed = inNight && !x.NightOverride;
        // "Only when I'm away": hold the lights until the machine has been idle,
        // so an evening session is never cut off mid-use. Idle time carried in
        // from before the window counts, so an already-away machine drops right
        // at the start time.
        bool nightOff = nightArmed && (!x.NightIdleOnly || x.IdleSeconds >= x.NightIdleThreshold);

        string? appProfile = x.AppSwitchEnabled
            ? AutomationRule.Match(x.AppRules, x.ForegroundProcess)
            : null;

        AutomationMode mode;
        string? profile;
        if (x.Locked && x.LockLightsOff) { mode = AutomationMode.Locked; profile = null; }
        else if (nightOff) { mode = AutomationMode.Night; profile = null; }
        else if (x.Sensor is SensorHit hit) { mode = AutomationMode.Sensor; profile = hit.Profile; }
        else if (appProfile != null) { mode = AutomationMode.App; profile = appProfile; }
        else { mode = AutomationMode.Base; profile = null; }

        return new AutomationOutcome(mode, profile, Status(in x, mode, appProfile, nightArmed, inNight));
    }

    /// <summary>Live feedback. Without it the whole feature is a black box the
    /// user cannot tell from a bug.</summary>
    static string Status(in AutomationInputs x, AutomationMode mode, string? appProfile,
                         bool nightArmed, bool inNight)
    {
        if (mode == AutomationMode.Night) return $"Night mode: lights off until {x.NightEnd}";
        if (inNight && x.NightOverride) return "Night mode paused (you woke the lights). Resumes tomorrow night.";
        if (nightArmed && x.NightIdleOnly) return "Night mode armed, lights turn off after 10 min idle";
        if (x.Sensor is SensorHit hit) return $"{hit.Describe()} → profile '{hit.Profile}'";
        if (x.SensorUnavailable is string missing)
            return $"Sensor rule paused: no reading for {SensorSources.Label(missing)}. PawnIO may not be installed.";
        if (!x.AppSwitchEnabled) return "";
        if (x.ForegroundProcess == null) return "Watching for your listed programs…";
        if (x.ForegroundIsSelf) return "This window is focused. Switch to another program to test your rules.";
        return appProfile != null
            ? $"Foreground app: {x.ForegroundProcess} → applying profile '{appProfile}'"
            : $"Foreground app: {x.ForegroundProcess} (no matching rule)";
    }
}
