namespace UnifiedRgb.Core;

/*-----------------------------------------------------------*\
| "Is my keyboard still there?"                                |
|                                                              |
| Until now the answer was: look at it. A device that stopped  |
| taking writes - a yanked dongle, a hub that browned out, a   |
| vendor tool that grabbed the handle while we held it - just  |
| stopped lighting up. Nothing in the app changed, nothing in  |
| the UI said anything, and the only recovery was for the user |
| to notice and press Rescan. The information existed the      |
| whole time: every driver knew each refused packet. It simply |
| had nowhere to go.                                           |
|                                                              |
| This is where it goes. It is deliberately a CONSUMER of      |
| signals that already exist, not a new probe:                 |
|                                                              |
|   - the write verdict. Since IRgbDevice.SetColors returns a  |
|     bool, a refusal is a fact rather than a guess. A run of  |
|     them is the whole "not responding" signal, and it costs  |
|     nothing because the writes were happening anyway.        |
|   - detection. DetectionNotes already says what the last     |
|     scan could see but not drive, and HeldByOtherSoftware is |
|     already exactly "controlled by another app".             |
|   - the SDK bridge. A device an OpenRGB client has claimed   |
|     is also controlled by another app, just a friendly one.  |
|                                                              |
| What it must never be is a prober. ADDING_A_DEVICE.md and    |
| WritePolicy are explicit that health is inferred from the    |
| writes the app was making anyway; a test write to "check"    |
| a device would put packets on a bus the user's other         |
| software may be sharing, and on some of these transports a   |
| speculative write is how you wedge an SMBus.                 |
|                                                              |
| THREADING. Written from every effect worker and every        |
| applier lane (one thread per device, up to 60 fps each) and  |
| from the SDK socket threads; read from the UI thread. So the |
| write path takes one uncontended per-device lock, allocates  |
| nothing at all, and builds no strings unless the state       |
| actually changes. Changed is raised OUTSIDE the lock and     |
| only on a real transition, so a healthy idle rig raises      |
| nothing and a device streaming at 60 fps costs a dictionary  |
| probe and four integer updates per frame.                    |
|                                                              |
| HYSTERESIS. Both directions, and for different reasons. One  |
| dropped packet is ordinary: a busy receiver, an SMBus held   |
| by another tool, a keyboard mid-firmware-housekeeping. The   |
| next frame fixes it, and a status line that blinked "not     |
| responding" every time would be worse than no status line.   |
| Equally, one lucky packet out of a dying device is not       |
| health, so coming back needs a run of good writes too.       |
\*-----------------------------------------------------------*/

/// <summary>What the app can honestly say about one device right now.
///
/// Ordered by how bad it is, which is what makes "the worst state on the rig"
/// a Max over the list rather than a switch.</summary>
public enum DeviceHealthState
{
    /// <summary>Open, and taking the frames we send it.</summary>
    Connected = 0,

    /// <summary>It refused the last frame or two. Expected to fix itself: on
    /// the streaming path the retry IS the next frame, so this is nearly
    /// always a state the user never sees.</summary>
    Retrying = 1,

    /// <summary>Another program has it. Not a fault - the lighting is somebody
    /// else's right now, and ours comes back when they let go.</summary>
    ControlledElsewhere = 2,

    /// <summary>It has refused everything for long enough that the next frame
    /// is plainly not going to fix it. This is the state worth acting on: the
    /// watchdog's recovery, and the one line in the UI that tells the user
    /// their dongle is out.</summary>
    NotResponding = 3,
}

/// <summary>One transition, for the UI and the activity history. A struct so
/// raising it allocates nothing beyond the delegate call.
///
/// Device is null for the wholesale change Reset raises, which means "throw
/// away whatever you were showing and read the list again".</summary>
public readonly record struct DeviceHealthChange(
    IRgbDevice? Device, DeviceHealthState From, DeviceHealthState To, string? Detail);

/// <summary>A device and what we can say about it, for a UI rebuild.</summary>
public readonly record struct DeviceHealthReading(
    IRgbDevice Device, DeviceHealthState State, string? Detail, int ConsecutiveRefusals);

/// <summary>Per-device health, fed by the write verdict and by detection.</summary>
public sealed class DeviceHealth
{
    /*-----------------------------------------------------*\
    | The thresholds, and why each one is the number it is.  |
    \*-----------------------------------------------------*/

    /// <summary>Refusals in a row before we admit to "retrying". Two, not one:
    /// a single refused packet is the most ordinary event on these buses and
    /// the frame behind it is already the retry. One would make the label
    /// flicker on a perfectly healthy rig.</summary>
    public const int RetryingAfterRefusals = 2;

    /// <summary>Refusals in a row before we call it dead. Ten is well past any
    /// transient: a Logitech receiver that is merely busy refuses one or two,
    /// never ten.</summary>
    public const int NotRespondingAfterRefusals = 10;

    /// <summary>...AND this long since the run of refusals started. The count
    /// alone is not enough because the two write paths run at wildly different
    /// rates: an effect streams at up to 60 fps, so ten refusals is 170 ms -
    /// far too quick to declare a device dead - while a device sitting on a
    /// static only writes on the once-a-second keepalive, where ten refusals
    /// is ten seconds. Requiring both means "dead" always means at least two
    /// seconds of nothing landing, whichever path the device is on.</summary>
    public const int NotRespondingAfterMs = 2000;

    /// <summary>Good writes in a row before a device is called healthy again.
    /// Three, because the failure mode this exists for is a device answering
    /// intermittently: one accepted packet out of a flapping dongle is not
    /// recovery, and bouncing the label back and forth is exactly the flapping
    /// the hysteresis is here to stop.</summary>
    public const int ConnectedAfterWrites = 3;

    /// <summary>The app's health. Static for the same reason ActivityLog is:
    /// the writers are effect workers and applier lanes scattered across two
    /// projects with no shared owner to hand an instance to, and there is
    /// exactly one lighting stack per process. The type is instantiable so
    /// tests drive an isolated one with their own clock.</summary>
    public static DeviceHealth Shared { get; } = new();

    readonly Func<long> _clock;

    /// <summary>Keyed by the device INSTANCE, not by its name. Two devices can
    /// legitimately share a name (a Razer mouse and its own dongle both report
    /// the model), and a shared slot would have them overwrite each other's
    /// health exactly as it once did their battery readings. The cost is that
    /// health does not survive a rescan, which is correct: a rescan replaces
    /// every instance, and what the OLD handle was doing says nothing about
    /// the new one.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<IRgbDevice, Entry> _entries = new();

    /// <param name="clock">Milliseconds from any monotonic source. Injected so
    /// the state machine can be tested at speed rather than in real time.</param>
    public DeviceHealth(Func<long>? clock = null) => _clock = clock ?? DefaultClock;

    /// <summary>Monotonic and cheap: TickCount64 is a read of a kernel page,
    /// not a syscall, which matters because this is consulted on the first
    /// refusal of every run of them.</summary>
    static long DefaultClock() => Environment.TickCount64;

    /// <summary>Raised on a real transition only, on whichever thread wrote,
    /// and outside the entry's lock. A UI subscriber must marshal, and must
    /// not assume it is cheap to be called from a write thread.</summary>
    public event Action<DeviceHealthChange>? Changed;

    sealed class Entry
    {
        public readonly object Gate = new();
        public DeviceHealthState State = DeviceHealthState.Connected;
        public int OkStreak;
        public int RefusalStreak;
        public long FirstRefusalAt;
        public string? Detail;
        /// <summary>Set independently of the write streaks: a claim outranks
        /// them while it lasts, and when it ends the streaks are still there to
        /// say what the device was doing underneath.</summary>
        public bool Held;
    }

    static Entry NewEntry(IRgbDevice _) => new();

    /*-----------------------------------------------------*\
    | The write path. Everything here is per-frame hot.      |
    \*-----------------------------------------------------*/

    /// <summary>Record what a device did with a frame. TRUE covers both halves
    /// of the contract's success - the frame landed, or was correctly deduped
    /// because the device is already showing it - because both mean the same
    /// thing here: the transport is answering.
    ///
    /// Allocates nothing. Call it from every write site; it is the only feed
    /// that can tell a live device from a dark one without probing.</summary>
    public void Report(IRgbDevice device, bool landed)
    {
        if (device == null) return;
        var e = _entries.GetOrAdd(device, NewEntry);

        DeviceHealthState from, to;
        string? detail;
        lock (e.Gate)
        {
            from = e.State;
            if (landed)
            {
                e.RefusalStreak = 0;
                // Saturate rather than let a device that has been happy for a
                // month overflow the counter.
                if (e.OkStreak < ConnectedAfterWrites) e.OkStreak++;
                if (!e.Held && e.State != DeviceHealthState.Connected && e.OkStreak >= ConnectedAfterWrites)
                {
                    e.State = DeviceHealthState.Connected;
                    e.Detail = null;
                }
            }
            else
            {
                e.OkStreak = 0;
                if (e.RefusalStreak == 0) e.FirstRefusalAt = _clock();
                if (e.RefusalStreak < int.MaxValue) e.RefusalStreak++;

                // A held device stays "controlled by another app" whatever its
                // writes do. Our frames being refused while somebody else owns
                // the handle is the EXPECTED behaviour, not a fault, and
                // relabelling it "not responding" would send the user hunting
                // for a cable that is fine.
                if (!e.Held)
                {
                    if (e.RefusalStreak >= NotRespondingAfterRefusals
                        && _clock() - e.FirstRefusalAt >= NotRespondingAfterMs)
                        e.State = DeviceHealthState.NotResponding;
                    else if (e.RefusalStreak >= RetryingAfterRefusals
                             && e.State == DeviceHealthState.Connected)
                        e.State = DeviceHealthState.Retrying;
                }
            }
            to = e.State;
            detail = e.Detail;
        }

        // Outside the lock, and only on a genuine transition: a device
        // streaming happily raises nothing at all, which is the whole idle
        // cost of this class.
        if (from != to) Changed?.Invoke(new DeviceHealthChange(device, from, to, detail));
    }

    /// <summary>Report a verdict and hand it straight back.
    ///
    /// Exists so a write site can start reporting health without changing
    /// shape: "if (dev.SetColors(f))" becomes
    /// "if (DeviceHealth.Landed(dev, dev.SetColors(f)))", which is one edit
    /// per call site with no new local, no new branch, and nothing that can be
    /// got subtly wrong in the middle of a dedup chain. Both the engine's
    /// streaming paths and the applier's terminal ones are meant to go through
    /// it: the more of the app's writes feed this, the sooner a device that
    /// has quietly stopped answering is noticed.</summary>
    public static bool Landed(IRgbDevice device, bool ok)
    {
        Shared.Report(device, ok);
        return ok;
    }

    /// <summary>Terminal and SDK writes also report exceptions before their
    /// caller retries or logs them. Streaming workers use WriteFrame/WriteZone
    /// to avoid allocating a delegate for each frame.</summary>
    public static bool Attempt(IRgbDevice device, Func<bool> send)
    {
        bool ok;
        try { ok = send(); }
        catch { Shared.Report(device, false); throw; }
        return Landed(device, ok);
    }

    public static bool WriteFrame(IRgbDevice device, IReadOnlyList<Rgb> colors)
    {
        bool ok;
        try { ok = device.SetColors(colors); }
        catch { Shared.Report(device, false); throw; }
        return Landed(device, ok);
    }

    public static bool WriteZone(IRgbDevice device, IZoneWritable zones, int offset, IReadOnlyList<Rgb> colors)
    {
        bool ok;
        try { ok = zones.SetZone(offset, colors); }
        catch { Shared.Report(device, false); throw; }
        return Landed(device, ok);
    }

    /*-----------------------------------------------------*\
    | The detection path. Cold: user actions and rescans.    |
    \*-----------------------------------------------------*/

    /// <summary>Another program has this device, or has given it back. Both
    /// callers are cold: the SDK bridge on a claim/release, and the view model
    /// after a detection pass that saw a vendor tool holding something.
    ///
    /// Releasing does NOT jump straight back to Connected: the streaks decide,
    /// exactly as they would have if the claim had never happened, so a device
    /// that was also failing underneath a claim still reads as failing.</summary>
    public void SetHeldByOther(IRgbDevice device, bool held, string? by = null)
    {
        if (device == null) return;
        var e = _entries.GetOrAdd(device, NewEntry);

        DeviceHealthState from, to;
        string? detail;
        lock (e.Gate)
        {
            from = e.State;
            e.Held = held;
            if (held)
            {
                e.State = DeviceHealthState.ControlledElsewhere;
                e.Detail = by;
            }
            else
            {
                e.Detail = null;
                e.State = Recompute(e);
            }
            to = e.State;
            detail = e.Detail;
        }
        if (from != to) Changed?.Invoke(new DeviceHealthChange(device, from, to, detail));
    }

    /// <summary>What the streaks alone say, used when a claim ends.</summary>
    static DeviceHealthState Recompute(Entry e)
        => e.RefusalStreak >= NotRespondingAfterRefusals ? DeviceHealthState.NotResponding
         : e.RefusalStreak >= RetryingAfterRefusals ? DeviceHealthState.Retrying
         : DeviceHealthState.Connected;

    // There is deliberately no Declare(state) here. One existed, documented for
    // "a device the app holds a handle to that detection has found is no longer
    // on the bus" - a caller that cannot exist: by the time a scan notices a
    // device is gone, its instance has already been disposed and replaced, so
    // there is nothing left to declare anything about. That signal travels by
    // NAME, through DetectionNotes.WentAway, and shows up in the blocked-device
    // rows. Health stays inferred from writes rather than asserted.

    /*-----------------------------------------------------*\
    | The read path. UI thread.                              |
    \*-----------------------------------------------------*/

    /// <summary>What we can say about one device. A device nobody has written
    /// to yet reads as Connected: detection opened it and it answered its
    /// initialisation, which is the only evidence available and is the same
    /// evidence the device list is built from.</summary>
    public DeviceHealthState StateOf(IRgbDevice device)
    {
        // Under the entry lock rather than a volatile read: the field is an
        // enum, which Volatile.Read has no overload for, and this is the UI
        // thread taking an uncontended lock a handful of times per repaint.
        if (!_entries.TryGetValue(device, out var e)) return DeviceHealthState.Connected;
        lock (e.Gate) return e.State;
    }

    /// <summary>The extra sentence, when there is one ("Corsair iCUE has it").</summary>
    public string? DetailOf(IRgbDevice device)
    {
        if (!_entries.TryGetValue(device, out var e)) return null;
        lock (e.Gate) return e.Detail;
    }

    /// <summary>Consecutive refused frames. The support bundle prints this
    /// beside each device: "connected" with a refusal streak behind it is a
    /// different problem from "connected" with none.</summary>
    public int RefusalsOf(IRgbDevice device)
    {
        if (!_entries.TryGetValue(device, out var e)) return 0;
        lock (e.Gate) return e.RefusalStreak;
    }

    /// <summary>Everything known, as a copy. The support bundle uses it to
    /// catch the devices health has an opinion about that are no longer in the
    /// device list at all, which is the interesting half of a "it vanished"
    /// report.</summary>
    public DeviceHealthReading[] Snapshot()
    {
        var pairs = _entries.ToArray();
        var outp = new DeviceHealthReading[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
        {
            var e = pairs[i].Value;
            lock (e.Gate) outp[i] = new DeviceHealthReading(pairs[i].Key, e.State, e.Detail, e.RefusalStreak);
        }
        return outp;
    }

    /// <summary>Forget everything. Called by a rescan, which replaces every
    /// device instance: without it the dictionary would hold the disposed ones
    /// alive for the life of the process, and the old entries could never be
    /// reached again anyway.</summary>
    public void Reset()
    {
        _entries.Clear();
        Changed?.Invoke(default);
    }

    /// <summary>Drop one device (it has gone, or is being disposed).</summary>
    public void Forget(IRgbDevice device) => _entries.TryRemove(device, out _);

    /*-----------------------------------------------------*\
    | The words. One place, so the UI, the log and the       |
    | support bundle cannot drift apart.                     |
    \*-----------------------------------------------------*/

    /// <summary>The short label, in the app's plain voice. These are the exact
    /// four phrases the feature was asked for.</summary>
    public static string Describe(DeviceHealthState state) => state switch
    {
        DeviceHealthState.Connected => "connected",
        DeviceHealthState.Retrying => "retrying",
        DeviceHealthState.NotResponding => "not responding",
        DeviceHealthState.ControlledElsewhere => "controlled by another app",
        _ => "",
    };

    /// <summary>What to do about it, or null when there is nothing to do. The
    /// same shape as BlockedDevice.Remedy, and for the same reason: a status
    /// with no next step is just an accusation.</summary>
    public static string? Remedy(DeviceHealthState state) => state switch
    {
        DeviceHealthState.NotResponding =>
            "check the cable or the dongle; the app retries on its own when it comes back",
        DeviceHealthState.ControlledElsewhere =>
            "close the other program, or leave it - your lighting returns when it lets go",
        _ => null,
    };

    /// <summary>True for a state the user should be told about. Retrying is
    /// deliberately NOT worth a badge: it resolves on the next frame, and a
    /// warning that clears itself before it can be read is noise.</summary>
    public static bool IsWorthShowing(DeviceHealthState state)
        => state is DeviceHealthState.NotResponding or DeviceHealthState.ControlledElsewhere;
}
