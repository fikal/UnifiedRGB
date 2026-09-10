namespace UnifiedRgb.Core.Automation;

/// <summary>What the lights should be doing right now.</summary>
public enum AutomationMode
{
    /// <summary>Whatever the user last set. The state every override returns to.</summary>
    Base,
    /// <summary>A foreground-app rule matched.</summary>
    App,
    /// <summary>A schedule is open and wants a profile applied.</summary>
    ScheduleProfile,
    /// <summary>A sensor threshold rule is firing.</summary>
    Sensor,
    /// <summary>A schedule is open and wants the lights out. This is what
    /// Night mode became; it keeps Night's place in the order.</summary>
    ScheduleOff,
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

    /*--- schedules: the caller owns the clock, so it decides which windows
          are open and hands the results in. ---*/

    /// <summary>An open lights-off schedule, already past its idle wait and not
    /// overridden. Null when no window wants the lights out.</summary>
    public ScheduleHit? ScheduleOff { get; init; }
    /// <summary>An open profile schedule.</summary>
    public ScheduleHit? ScheduleProfile { get; init; }
    /// <summary>A lights-off window is open but the user relit things, so it is
    /// paused until the window ends. Status only.</summary>
    public bool SchedulePaused { get; init; }
    /// <summary>A lights-off window is open and waiting for the machine to go
    /// idle before acting. Status only.</summary>
    public bool ScheduleWaitingIdle { get; init; }
    /// <summary>End time of whichever window the status is talking about.</summary>
    public string ScheduleEnd { get; init; }

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

/// <summary>An open schedule: what it wants, and when it closes.</summary>
public readonly record struct ScheduleHit(string End, string? Profile);

/// <summary>What a status sentence is ABOUT, so a screen can show only the
/// lines that belong to it.
///
/// There is one status line for the whole feature, and it used to be shown
/// verbatim in four places. Because the app-rule sentences sit at the bottom
/// of the priority chain, they are what shows whenever no schedule and no
/// sensor rule is active, which is most of the time. The schedules window
/// therefore spent its life explaining foreground apps, and "this window is
/// focused, switch to another program to test your rules" turned up while the
/// user was editing a temperature threshold. Every sentence now says which
/// feature it belongs to, and each screen asks for its own.</summary>
public enum AutomationTopic
{
    /// <summary>Nothing to say.</summary>
    None,
    /// <summary>True wherever automation is discussed: a pause stops schedules,
    /// sensors and app rules alike, so every screen needs to hear about it.</summary>
    All,
    Schedule,
    Sensor,
    App,
}

public readonly record struct AutomationOutcome(
    AutomationMode Mode, string? Profile, string Status, AutomationTopic Topic = AutomationTopic.None)
{
    /// <summary>The status line, or empty when it belongs to another screen.
    /// `All` passes every filter by design.</summary>
    public string StatusFor(AutomationTopic topic)
        => Topic == topic || Topic == AutomationTopic.All ? Status : "";
}

/// <summary>The whole "it manages itself" decision, as one pure function.
///
/// Priority: Locked > Schedule(lights off) > Sensor > Schedule(profile) >
/// App > Base.
///
/// A thermal alert outranks "you are in a game", because the alert is the
/// machine telling you something; it does not outrank lights that are
/// deliberately out, whether the user locked the machine or scheduled the
/// dark. A scheduled PROFILE is the mildest override of the lot, so it sits
/// just above the app rules it would otherwise fight.</summary>
public static class AutomationDecision
{
    public static AutomationOutcome Resolve(in AutomationInputs x)
    {
        string? appProfile = x.AppSwitchEnabled
            ? AutomationRule.Match(x.AppRules, x.ForegroundProcess)
            : null;

        AutomationMode mode;
        string? profile;
        if (x.Locked && x.LockLightsOff) { mode = AutomationMode.Locked; profile = null; }
        else if (x.ScheduleOff != null) { mode = AutomationMode.ScheduleOff; profile = null; }
        else if (x.Sensor is SensorHit hit) { mode = AutomationMode.Sensor; profile = hit.Profile; }
        else if (x.ScheduleProfile is ScheduleHit sp) { mode = AutomationMode.ScheduleProfile; profile = sp.Profile; }
        else if (appProfile != null) { mode = AutomationMode.App; profile = appProfile; }
        else { mode = AutomationMode.Base; profile = null; }

        var (status, topic) = Status(in x, mode, appProfile);
        return new AutomationOutcome(mode, profile, status, topic);
    }

    /// <summary>Live feedback. Without it the whole feature is a black box the
    /// user cannot tell from a bug.
    ///
    /// Every sentence carries the feature it is about, because one line is
    /// shared by four screens and only one of them wants any given sentence.
    /// The order here is the priority order, so the app lines are the fallback:
    /// that is exactly why they used to leak into windows about schedules and
    /// sensors, and why the topic exists.</summary>
    static (string Text, AutomationTopic Topic) Status(in AutomationInputs x, AutomationMode mode, string? appProfile)
    {
        if (mode == AutomationMode.ScheduleOff)
            return ($"Scheduled: lights off until {x.ScheduleEnd}", AutomationTopic.Schedule);
        if (x.SchedulePaused)
            return ("Schedule paused (you woke the lights). It runs again next time.", AutomationTopic.Schedule);
        if (x.ScheduleWaitingIdle)
            return ("Schedule armed, lights turn off after 10 min idle", AutomationTopic.Schedule);
        if (mode == AutomationMode.ScheduleProfile && x.ScheduleProfile is ScheduleHit ps)
            return ($"Scheduled until {ps.End}: profile '{ps.Profile}'", AutomationTopic.Schedule);
        if (x.Sensor is SensorHit hit)
            return ($"{hit.Describe()} → profile '{hit.Profile}'", AutomationTopic.Sensor);
        if (x.SensorUnavailable is string missing)
            return ($"Sensor rule paused: no reading for {SensorSources.Label(missing)}. PawnIO may not be installed.",
                    AutomationTopic.Sensor);
        if (!x.AppSwitchEnabled) return ("", AutomationTopic.None);
        if (x.ForegroundProcess == null)
            return ("Watching for your listed programs…", AutomationTopic.App);
        if (x.ForegroundIsSelf)
            return ("This window is focused. Switch to another program to test your rules.", AutomationTopic.App);
        return appProfile != null
            ? ($"Foreground app: {x.ForegroundProcess} → applying profile '{appProfile}'", AutomationTopic.App)
            : ($"Foreground app: {x.ForegroundProcess} (no matching rule)", AutomationTopic.App);
    }
}
