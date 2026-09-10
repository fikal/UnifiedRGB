namespace UnifiedRgb.Core;

/*-----------------------------------------------------------*\
| WHEN to put the lighting back, and when to keep out of the   |
| way.                                                         |
|                                                              |
| The Windows half of automatic recovery is three API calls    |
| and is not the hard part. The hard part is everything        |
| around them, and all of it is decidable without an HWND:     |
|                                                              |
|  - a burst. Plugging in ONE wireless dongle produces a       |
|    WM_DEVICECHANGE per interface it exposes, and a powered   |
|    hub coming back after a resume produces one per port. A   |
|    rescan per event would dispose and reopen every device on |
|    the rig several times in a row, which is visibly worse    |
|    than the dark device it is trying to fix.                 |
|  - readiness. Windows says DBT_DEVICEARRIVAL when the device |
|    object exists, not when its interface can be opened, and  |
|    it says PowerModes.Resume long before the USB stack has   |
|    finished re-enumerating. Redetecting immediately mostly   |
|    finds nothing, which is the worst outcome: the user's     |
|    hardware is quietly dropped from the list.                |
|  - politeness. There are three states where putting the      |
|    lighting back is the WRONG thing to do, and getting that  |
|    wrong means the app lights up a dark bedroom at 3 AM.     |
|                                                              |
| So all of that lives here, as a pure state machine over an   |
| injected clock, and the watchdog in the App project is left  |
| as the thin layer that turns a window message into a Note    |
| and a timer tick into a Claim. That split is what makes the  |
| behaviour testable without hardware, a window, or a wait.    |
|                                                              |
| IDLE COST. Zero. Nothing in here has a thread or a timer;    |
| the watchdog only runs a timer while a recovery is pending,  |
| and both triggers are push-based, so an untouched machine    |
| does not wake for this at all.                               |
\*-----------------------------------------------------------*/

/// <summary>Why a recovery is pending. Carried through so the log line and the
/// activity note can say what actually happened rather than "something".</summary>
public enum RecoveryReason
{
    /// <summary>A device interface appeared (DBT_DEVICEARRIVAL).</summary>
    DeviceArrived,
    /// <summary>A device interface went away (DBT_DEVICEREMOVECOMPLETE).</summary>
    DeviceRemoved,
    /// <summary>The machine woke up (PowerModes.Resume).</summary>
    SystemResumed,
}

/// <summary>What the app decides to do when the debounce expires.</summary>
public enum RecoveryAction
{
    /// <summary>Nothing is pending.</summary>
    None,
    /// <summary>Something is pending but it is not time yet, or the moment is
    /// wrong. Come back when DueAt says.</summary>
    Wait,
    /// <summary>Redetect now.</summary>
    Rescan,
}

/// <summary>The facts the policy cannot see for itself, gathered by the caller
/// at the moment it asks.</summary>
/// <param name="SdkClientHolds">An OpenRGB client currently owns at least one
/// device.</param>
/// <param name="LightsSuppressed">The automation is deliberately keeping the
/// lights off: a scheduled dark window, or a locked session.</param>
public readonly record struct RecoveryConditions(bool SdkClientHolds, bool LightsSuppressed);

/// <summary>The decision, with enough detail to log one honest sentence.</summary>
/// <param name="Relight">Whether the recovery may put the user's lighting back
/// on the devices it finds. False during a dark window or a locked session: we
/// still redetect, so the hardware is known and driveable, but whatever the
/// automation had decided about brightness stays decided.</param>
/// <param name="CollapsedEvents">How many raw OS events this one rescan is
/// standing in for. Purely for the log; a plug that produced eleven is the
/// evidence that the debounce is doing its job.</param>
public readonly record struct RecoveryPlan(
    RecoveryAction Action, RecoveryReason Reason, bool Relight, int CollapsedEvents);

/// <summary>Debounces recovery triggers and decides whether now is the moment.
/// Not thread safe on purpose: every caller is the UI dispatcher (the window
/// message, the SystemEvents callback we marshal, and the timer tick), and a
/// lock here would only hide a threading mistake in the watchdog.</summary>
public sealed class RecoveryPolicy
{
    /*-----------------------------------------------------*\
    | Timings, and why each one is the number it is.         |
    \*-----------------------------------------------------*/

    /// <summary>Quiet time after the LAST device event before redetecting.
    ///
    /// 1500 ms. Two jobs. It collapses the burst - a Lian Li hub enumerates
    /// two interfaces, a wireless receiver three or four, and they land within
    /// tens of milliseconds of each other, so anything above ~200 ms already
    /// gets them all. And it waits for readiness: DBT_DEVICEARRIVAL fires when
    /// the device object exists, and opening the HID interface immediately
    /// after it fails often enough that the device would be dropped from the
    /// list until the user pressed Rescan by hand - the exact bug this feature
    /// exists to remove. 1500 ms is comfortably past both while still being
    /// quick enough that plugging a keyboard in feels immediate.</summary>
    public const int DeviceSettleMs = 1500;

    /// <summary>Quiet time after a resume before redetecting.
    ///
    /// 6000 ms, four times the device figure, because Resume is delivered
    /// early and generously: Windows raises it while the USB host controllers
    /// are still being restarted, so an immediate redetect can genuinely find
    /// zero devices on a rig that has all of them. USB re-enumeration is
    /// typically done within 2-4 s of the notification; vendor services that
    /// grab handles on wake (iCUE, Synapse, the Lian Li tool) settle a little
    /// after that, and starting our detection before them is how a device ends
    /// up "held by another program" for the rest of the session. 6 s clears
    /// both. It is deliberately not longer: the lights are dark for this whole
    /// window, and every extra second is a second of the user wondering
    /// whether the app survived the sleep.</summary>
    public const int ResumeSettleMs = 6000;

    /// <summary>How long a continuous stream of events may keep postponing the
    /// rescan. A trailing debounce with no ceiling never fires while events
    /// keep arriving, and a flapping USB port (a bad cable, a hub browning
    /// out) does exactly that - it would hold recovery off forever, which is
    /// the one machine that needs it most. Ten seconds after the FIRST event
    /// of a burst, we go regardless.</summary>
    public const int MaxDeferMs = 10_000;

    /// <summary>Never two rescans closer together than this. A rescan disposes
    /// and reopens every device on the rig; unplug-then-replug is one gesture
    /// to the user and produces a removal and an arrival seconds apart, and
    /// doing the whole teardown twice for it is pure disruption.</summary>
    public const int MinGapMs = 3000;

    /// <summary>How long to stand off when the moment is wrong (an SDK client
    /// is holding a device). Short, because the whole point is to go the
    /// instant it lets go.</summary>
    public const int BusyRetryMs = 2000;

    /// <summary>How many times a busy rig may push a pending recovery back
    /// before we do it anyway. Thirty at 2 s is about a minute. A client that
    /// holds a device forever is a normal, supported thing (that is what the
    /// SDK bridge is FOR), so waiting forever would mean a rig with OpenRGB
    /// attached never recovers a replugged device at all - and a permanently
    /// dark device is worse than one interrupted client frame.</summary>
    public const int MaxBusyDeferrals = 30;

    readonly Func<long>? _clock;

    /// <param name="clock">Optional monotonic millisecond source, so a caller
    /// that would rather not pass `now` at every call site does not have to.
    /// The explicit-now overloads are what the tests use.</param>
    public RecoveryPolicy(Func<long>? clock = null) => _clock = clock;

    long Now() => _clock?.Invoke() ?? Environment.TickCount64;

    bool _pending;
    long _dueAt;
    long _cap;            // the burst ceiling: we fire by here whatever else arrives
    long _lastRunAt = long.MinValue / 4;   // room to subtract without overflowing
    RecoveryReason _reason;
    int _events;
    int _busyDeferrals;

    /// <summary>True while a recovery is waiting to happen. The watchdog runs
    /// its timer only while this is true, which is what keeps the idle cost of
    /// the whole feature at nothing.</summary>
    public bool Pending => _pending;

    /// <summary>When the watchdog should next ask. Meaningless unless Pending.</summary>
    public long DueAt => _dueAt;

    /// <summary>Milliseconds from now until the next ask, floored at zero, for
    /// setting a timer interval.</summary>
    public int DelayFrom(long now) => (int)Math.Clamp(_dueAt - now, 0, MaxDeferMs + ResumeSettleMs);

    /// <summary>How many OS events the pending recovery is standing in for.</summary>
    public int PendingEvents => _events;

    /// <summary>Why the pending recovery is pending.</summary>
    public RecoveryReason PendingReason => _reason;

    /// <summary>An OS event arrived. Cheap enough to call from a window
    /// procedure: it is a handful of comparisons and no allocation.</summary>
    public void Note(RecoveryReason reason, long now)
    {
        int settle = reason == RecoveryReason.SystemResumed ? ResumeSettleMs : DeviceSettleMs;

        if (!_pending)
        {
            _pending = true;
            _events = 0;
            _busyDeferrals = 0;
            _dueAt = now + settle;
            _cap = now + MaxDeferMs;
            _reason = reason;
        }
        else
        {
            // Trailing debounce: each event pushes the moment out, so the
            // rescan happens once the plugging has stopped rather than in the
            // middle of it. Never PULLS it in - a 1.5 s device event arriving
            // during a 6 s resume wait must not shorten the resume wait, which
            // is the case Math.Max is guarding.
            _dueAt = Math.Max(_dueAt, now + settle);
            // ...and never past the ceiling, except that a resume genuinely
            // needs its full settle even if the burst started earlier, so a
            // resume raises the ceiling to fit.
            if (reason == RecoveryReason.SystemResumed) _cap = Math.Max(_cap, now + settle);
            _dueAt = Math.Min(_dueAt, _cap);

            // A resume is the more interesting reason and outranks a device
            // event for the purpose of the log line: "your machine woke up" is
            // what the user will recognise, and the hub replugging itself is a
            // consequence of it rather than a separate story.
            if (reason == RecoveryReason.SystemResumed) _reason = reason;
        }
        _events++;
    }

    /// <summary>Convenience overload for callers that let the policy read the
    /// clock itself.</summary>
    public void Note(RecoveryReason reason) => Note(reason, Now());

    /// <summary>Ask what to do right now. On Rescan the pending recovery is
    /// CONSUMED, so a caller that gets Rescan must actually rescan; a caller
    /// that gets Wait should come back at DueAt.</summary>
    public RecoveryPlan Claim(long now, in RecoveryConditions conditions)
    {
        if (!_pending) return new RecoveryPlan(RecoveryAction.None, _reason, false, 0);

        bool relight = !conditions.LightsSuppressed;

        if (now < _dueAt)
            return new RecoveryPlan(RecoveryAction.Wait, _reason, relight, _events);

        // The floor between two rescans. Checked here rather than in Note
        // because it is about when the LAST rescan happened, which Note has no
        // reason to care about.
        if (now - _lastRunAt < MinGapMs)
        {
            _dueAt = _lastRunAt + MinGapMs;
            return new RecoveryPlan(RecoveryAction.Wait, _reason, relight, _events);
        }

        // An SDK client holding a device is the one caller we defer to rather
        // than override. A rescan drops every claim at once (the device
        // instances it holds are being replaced), so recovering underneath a
        // client would yank the lighting out of whatever is driving it and
        // hand the user their own profile back mid-scene. Waiting is nearly
        // always free: clients let go in seconds.
        if (conditions.SdkClientHolds && _busyDeferrals < MaxBusyDeferrals)
        {
            _busyDeferrals++;
            _dueAt = now + BusyRetryMs;
            return new RecoveryPlan(RecoveryAction.Wait, _reason, relight, _events);
        }

        var plan = new RecoveryPlan(RecoveryAction.Rescan, _reason, relight, _events);
        _pending = false;
        _events = 0;
        _busyDeferrals = 0;
        _lastRunAt = now;
        return plan;
    }

    /// <summary>Convenience overload.</summary>
    public RecoveryPlan Claim(in RecoveryConditions conditions) => Claim(Now(), conditions);

    /// <summary>Forget a pending recovery without running it (the app is going
    /// down, or a manual rescan has just done the work for us).</summary>
    public void Cancel(long now)
    {
        _pending = false;
        _events = 0;
        _busyDeferrals = 0;
        _lastRunAt = now;
    }

    /// <summary>One plain sentence for the activity history, in the app's
    /// voice: what happened, and what the app did about it.</summary>
    public static string Sentence(RecoveryReason reason, int devices, bool relight) => reason switch
    {
        RecoveryReason.SystemResumed => relight
            ? $"Your PC woke up, so the lighting was put back on {devices} device(s)."
            : $"Your PC woke up and {devices} device(s) came back, but the lights stay off for now.",
        RecoveryReason.DeviceRemoved => relight
            ? $"A device was unplugged, so the lighting was rebuilt across {devices} device(s)."
            : $"A device was unplugged; {devices} device(s) are still here and the lights stay off for now.",
        _ => relight
            ? $"A device was plugged in, so the lighting was put back on {devices} device(s)."
            : $"A device was plugged in; it is ready, and the lights stay off for now.",
    };
}
