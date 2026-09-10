namespace UnifiedRgb.Core.Automation;

/*-----------------------------------------------------------*\
| "Leave my lights alone for a bit."                           |
|                                                              |
| Everything about the pause that is a DECISION lives here,    |
| pure, for the same reason AutomationDecision.Resolve does:   |
| the service that uses it needs a view model, a dispatcher    |
| and a real clock, so anything left inside it is untestable.  |
|                                                              |
| The pause is deliberately temporary and per session. It is   |
| never written to settings.json: a user who pauses to build a |
| profile at 9 PM and then forgets is far better served by the |
| pause dying with the process than by their schedules being   |
| silently dead a week later with no memory of why.            |
|                                                              |
| WHAT THE PAUSE DOES: it FREEZES. The lighting that is on     |
| when you pause stays on, and no rule may replace it. It does |
| not snap back to your base lighting, because "stop changing  |
| my lights" that immediately changes your lights is a joke at |
| the user's expense - if a game rule is on and you like it,   |
| pausing is how you keep it.                                  |
|                                                              |
| TWO EXCEPTIONS, both about not stranding the user in a dark  |
| room:                                                        |
|                                                              |
| 1. Pausing while the lights are OUT (session lock, or a      |
|    scheduled dark window) freezes to Base instead. Freezing  |
|    "off" would mean the pause button appeared to do nothing  |
|    at all, on the one occasion the user can see least.       |
|                                                              |
| 2. The session lock still wins WHILE paused. The pause is    |
|    for someone sitting at the machine wanting the rules to   |
|    stop fighting them; locking says they got up and left,    |
|    and leaving a rig blazing all night because automation    |
|    was paused an hour earlier is a surprise nobody asked     |
|    for. LockLightsOff is itself an explicit user setting.    |
|    On unlock the frozen state is restored exactly, which is  |
|    why Hold carries the profile as well as the mode.         |
\*-----------------------------------------------------------*/
public static class AutomationPause
{
    /// <summary>The status line while paused. Plain, in the same voice as the
    /// rest of the automation copy, and it says what will happen next: a
    /// status that just went blank would read as a bug.</summary>
    public const string Status = "Automation paused. Rules will not change your lighting until you resume.";

    /// <summary>Paused, but the session is locked, so the lights are out for a
    /// reason that has nothing to do with the rules. Saying so stops the
    /// pause from getting the blame at the next unlock.</summary>
    public const string LockedStatus = "Automation paused, and the session is locked, so the lights are off until you sign back in.";

    /// <summary>History sentence when the pause starts.</summary>
    public const string Began = "You paused automation. Rules and schedules will not touch your lighting until you resume.";

    /// <summary>History sentence when it ends.</summary>
    public const string Ended = "You resumed automation. Rules and schedules are back in charge.";

    /// <summary>What the service should hold at the moment the pause starts.
    ///
    /// Everything freezes as it is, except lights that are deliberately off:
    /// see exception 1 above. Locked and ScheduleOff both mean "dark", and
    /// neither is something a user can usefully stare at while they tinker.</summary>
    public static AutomationMode Freeze(AutomationMode current)
        => current is AutomationMode.Locked or AutomationMode.ScheduleOff
            ? AutomationMode.Base
            : current;

    /// <summary>The whole decision for one tick while paused: the frozen mode
    /// and profile are held, unless the session is locked and the user asked
    /// for lights out on lock, which still wins.
    ///
    /// Note what is NOT here: no schedule, no foreground process, no sensor.
    /// That is the point. While paused the caller does not even gather them,
    /// so a pause is also the cheapest the automation ever gets.</summary>
    /// <remarks>Topic All: a pause stops schedules, sensors and app rules
    /// alike, so every screen that shows automation status has to say so. A
    /// window about schedules that stayed silent while automation was paused
    /// would be the most misleading thing on it.</remarks>
    public static AutomationOutcome Resolve(AutomationMode held, string? heldProfile, bool locked, bool lockLightsOff)
        => locked && lockLightsOff
            ? new AutomationOutcome(AutomationMode.Locked, null, LockedStatus, AutomationTopic.All)
            : new AutomationOutcome(held, heldProfile, Status, AutomationTopic.All);
}
