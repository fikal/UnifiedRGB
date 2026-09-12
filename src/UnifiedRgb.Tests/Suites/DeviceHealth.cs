using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Device health and automatic recovery: the decisions, not the |
| plumbing.                                                    |
|                                                              |
| The feature is two halves. One is three Win32 calls -        |
| RegisterDeviceNotification, a window procedure, and          |
| SystemEvents.PowerModeChanged - and there is nothing to test |
| about them that does not need a real machine, a real window  |
| and a real dongle to pull out.                               |
|                                                              |
| The other half is where all the behaviour actually lives,    |
| and none of it needs any of that:                            |
|                                                              |
|  - the state machine. When does a refusal become "retrying", |
|    when does retrying become "not responding", and what does |
|    it take to be believed healthy again. This is the part    |
|    that is on screen, so it is the part that must not flap.  |
|  - the debounce. Plugging in one dongle produces a burst of  |
|    events; the burst must collapse to exactly one rescan, a  |
|    resume must wait longer than a replug, and a port that    |
|    flaps forever must not be able to postpone recovery       |
|    forever.                                                  |
|  - the politeness rules. An SDK client is deferred to, and   |
|    a scheduled dark window is never relit.                   |
|                                                              |
| That split is deliberate and it is why RecoveryPolicy and    |
| DeviceHealth live in Core with an injected clock while       |
| DeviceWatchdog in the App project owns nothing but the       |
| Win32 seam. Every test below runs in microseconds with no    |
| hardware and no waiting.                                     |
\*-----------------------------------------------------------*/
static class DeviceHealthSuite
{
    public static void Run(Harness t)
    {
        Transitions(t);
        Hysteresis(t);
        HeldByAnotherApp(t);
        Words(t);
        DetectionBridge(t);
        Debounce(t);
        ResumeSettles(t);
        BurstCeiling(t);
        MinimumGap(t);
        DefersToSdkClient(t);
        NeverRelightsADarkWindow(t);
        TransitionDuringRowRefresh(t);
    }

    static void TransitionDuringRowRefresh(Harness t)
    {
        t.Section("health: a transition during row refresh receives a follow-up pass");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            UnifiedRgb.App.MainViewModel? vm = null;
            Action<DeviceHealthChange>? subscription = null;
            try
            {
                vm = new UnifiedRgb.App.MainViewModel(startServices: false);
                var dev = new FakeDevice { Name = "Health refresh race" };
                vm.Devices.Add(dev);
                // Capture only this VM's subscription so a stopped test dispatcher
                // cannot remain subscribed to the process-wide health service.
                var changed = typeof(DeviceHealth).GetField("Changed",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var before = ((Delegate?)changed.GetValue(DeviceHealth.Shared))?.GetInvocationList() ?? [];
                typeof(UnifiedRgb.App.MainViewModel).GetMethod("HookDeviceHealth",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                var after = ((Delegate)changed.GetValue(DeviceHealth.Shared)!).GetInvocationList();
                subscription = (Action<DeviceHealthChange>)after.Except(before).Single();
                bool transitioned = false;
                int passes = 0;
                vm.DeviceHealthRows.CollectionChanged += (_, e) =>
                {
                    if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
                    passes++;
                    if (transitioned) return;
                    transitioned = true;
                    // The row now contains Retrying. Complete recovery on a real
                    // worker before the UI exits its in-progress refresh.
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 3; i++) DeviceHealth.Shared.Report(dev, landed: true);
                    }).GetAwaiter().GetResult();
                };
                DeviceHealth.Shared.Report(dev, landed: false);
                DeviceHealth.Shared.Report(dev, landed: false);
                var frame = new System.Windows.Threading.DispatcherFrame();
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                t.Equal(DeviceHealthState.Connected, DeviceHealth.Shared.StateOf(dev), "worker has completed recovery");
                t.Equal(DeviceHealthState.Connected, vm.DeviceHealthRows.Single().State, "UI receives the recovery that occurred after its first row read");
                t.Equal(2, passes, "in-progress transition schedules exactly one follow-up refresh");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (subscription != null) DeviceHealth.Shared.Changed -= subscription;
                vm?.Shows.Dispose();
                vm?.Lcd.Dispose();
                vm?.Lighting.StopAndDrain();
                dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Health refresh regression failed", failure);
    }

    /*==================== the state machine ====================*/

    /// <summary>A clock the test moves by hand. Real elapsed time would make
    /// the "not responding needs two seconds" rule cost two seconds a case.</summary>
    sealed class FakeClock
    {
        public long Ms;
        public long Read() => Ms;
        public void Advance(long ms) => Ms += ms;
    }

    static void Transitions(Harness t)
    {
        t.Section("health: connected -> retrying -> not responding");
        var clock = new FakeClock();
        var health = new DeviceHealth(clock.Read);
        var dev = new FakeDevice { Name = "Pad" };

        // A device nobody has written to yet is Connected, not Unknown:
        // detection opened it and it answered its initialisation, which is the
        // same evidence the device list itself is built from.
        t.Equal(DeviceHealthState.Connected, health.StateOf(dev), "a device nobody has written to reads as connected");

        health.Report(dev, landed: true);
        t.Equal(DeviceHealthState.Connected, health.StateOf(dev), "a landed frame keeps it connected");

        // One refusal is the most ordinary event on these buses and the frame
        // behind it is already the retry. Reacting to it would make the label
        // flicker on a healthy rig.
        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.Connected, health.StateOf(dev), "ONE refused frame does not move the state");

        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.Retrying, health.StateOf(dev), "two refusals in a row is 'retrying'");

        // Still retrying at nine refusals: the count alone is not enough,
        // because at 60 fps ten refusals is 170 ms and no device deserves to be
        // called dead that fast.
        for (int i = 2; i < DeviceHealth.NotRespondingAfterRefusals; i++) health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.Retrying, health.StateOf(dev),
            "the refusal count alone does not declare a device dead");

        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.Retrying, health.StateOf(dev),
            "...even at the full count, while the run is still young");

        // Now give it the time as well. Both conditions, so "not responding"
        // means at least two seconds of nothing landing whichever write path
        // the device happens to be on.
        clock.Advance(DeviceHealth.NotRespondingAfterMs);
        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.NotResponding, health.StateOf(dev),
            "enough refusals AND enough elapsed time is 'not responding'");
        t.Check(health.RefusalsOf(dev) > DeviceHealth.NotRespondingAfterRefusals,
            "...and the refusal streak is there for the diagnostics report");
    }

    static void Hysteresis(Harness t)
    {
        t.Section("health: coming back needs a run of good writes");
        var clock = new FakeClock();
        var health = new DeviceHealth(clock.Read);
        var dev = new FakeDevice { Name = "Hub" };

        for (int i = 0; i < DeviceHealth.NotRespondingAfterRefusals; i++) health.Report(dev, landed: false);
        clock.Advance(DeviceHealth.NotRespondingAfterMs);
        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.NotResponding, health.StateOf(dev), "dead to start with");

        // The failure mode this guards is a flapping dongle: one accepted
        // packet out of a device that is barely there is not recovery, and
        // bouncing the label back and forth is worse than either label.
        health.Report(dev, landed: true);
        t.Equal(DeviceHealthState.NotResponding, health.StateOf(dev), "ONE good write is not recovery");
        health.Report(dev, landed: true);
        t.Equal(DeviceHealthState.NotResponding, health.StateOf(dev), "...nor two");
        health.Report(dev, landed: true);
        t.Equal(DeviceHealthState.Connected, health.StateOf(dev), "three good writes in a row is 'connected' again");

        t.Section("health: a good write resets the run, it does not merely pause it");
        // Refuse, refuse, land, refuse: the streak restarts from that landed
        // frame, so the device is NOT two refusals deep any more.
        var flappy = new FakeDevice { Name = "Flappy" };
        health.Report(flappy, landed: false);
        health.Report(flappy, landed: true);
        health.Report(flappy, landed: false);
        t.Equal(DeviceHealthState.Connected, health.StateOf(flappy),
            "a landed frame in the middle resets the refusal run");

        t.Section("health: transitions are announced, steady states are silent");
        var quiet = new FakeDevice { Name = "Quiet" };
        int changes = 0;
        health.Changed += _ => changes++;
        for (int i = 0; i < 50; i++) health.Report(quiet, landed: true);
        t.Equal(0, changes, "fifty landed frames on a healthy device raise nothing at all");
        health.Report(quiet, landed: false);
        health.Report(quiet, landed: false);
        t.Equal(1, changes, "the move to 'retrying' raises exactly once");
        health.Report(quiet, landed: false);
        t.Equal(1, changes, "...and staying there raises nothing more");
    }

    static void HeldByAnotherApp(Harness t)
    {
        t.Section("health: controlled by another app");
        var clock = new FakeClock();
        var health = new DeviceHealth(clock.Read);
        var dev = new FakeDevice { Name = "Keyboard" };

        health.SetHeldByOther(dev, true, "an OpenRGB client");
        t.Equal(DeviceHealthState.ControlledElsewhere, health.StateOf(dev), "a claimed device is 'controlled elsewhere'");
        t.Equal("an OpenRGB client", health.DetailOf(dev), "...and says who has it");

        // Our writes being refused while somebody else owns the handle is the
        // EXPECTED behaviour, not a fault. Relabelling it 'not responding'
        // would send the user hunting for a cable that is perfectly fine.
        for (int i = 0; i < DeviceHealth.NotRespondingAfterRefusals * 2; i++) health.Report(dev, landed: false);
        clock.Advance(DeviceHealth.NotRespondingAfterMs * 2);
        health.Report(dev, landed: false);
        t.Equal(DeviceHealthState.ControlledElsewhere, health.StateOf(dev),
            "refusals while another app holds it do NOT read as a fault");

        // On release the streaks decide, exactly as they would have if the
        // claim had never happened: a device that was also failing underneath
        // a claim is still failing.
        health.SetHeldByOther(dev, false);
        t.Equal(DeviceHealthState.NotResponding, health.StateOf(dev),
            "on release the write history decides, rather than a jump straight to healthy");

        var healthy = new FakeDevice { Name = "Fine" };
        health.SetHeldByOther(healthy, true, "Corsair iCUE");
        health.SetHeldByOther(healthy, false);
        t.Equal(DeviceHealthState.Connected, health.StateOf(healthy),
            "a device that was fine underneath a claim is connected again when it ends");
        t.Check(health.DetailOf(healthy) == null, "...and the 'who has it' line is cleared with it");
    }

    static void Words(Harness t)
    {
        t.Section("health: the four things the app can say");
        // These exact phrases are the feature. Pinned here so a rename in the
        // enum cannot silently change what the user reads.
        t.Equal("connected", DeviceHealth.Describe(DeviceHealthState.Connected), "connected");
        t.Equal("retrying", DeviceHealth.Describe(DeviceHealthState.Retrying), "retrying");
        t.Equal("not responding", DeviceHealth.Describe(DeviceHealthState.NotResponding), "not responding");
        t.Equal("controlled by another app", DeviceHealth.Describe(DeviceHealthState.ControlledElsewhere),
            "controlled by another app");

        // Retrying is deliberately not worth a badge: it resolves on the next
        // frame, and a warning that clears itself before it can be read is noise.
        t.Check(!DeviceHealth.IsWorthShowing(DeviceHealthState.Connected), "connected is not a warning");
        t.Check(!DeviceHealth.IsWorthShowing(DeviceHealthState.Retrying), "retrying is not worth a badge");
        t.Check(DeviceHealth.IsWorthShowing(DeviceHealthState.NotResponding), "not responding is");
        t.Check(DeviceHealth.IsWorthShowing(DeviceHealthState.ControlledElsewhere), "so is a takeover");

        // A status with no next step is just an accusation.
        t.Check(DeviceHealth.Remedy(DeviceHealthState.NotResponding) != null, "a dead device gets a next step");
        t.Check(DeviceHealth.Remedy(DeviceHealthState.ControlledElsewhere) != null, "so does a taken one");
        t.Check(DeviceHealth.Remedy(DeviceHealthState.Connected) == null, "a working device needs no advice");
    }

    static void DetectionBridge(Harness t)
    {
        t.Section("detection notes speak the same language as health");
        // The whole reason DetectionNotes was extended rather than duplicated:
        // "held by other software" and "controlled by another app" are one
        // fact, and the user must never be shown two different words for it.
        t.Equal(DeviceHealthState.ControlledElsewhere,
            new BlockedDevice("f", "Mouse", BlockReason.HeldByOtherSoftware, "iCUE has it", null).Health,
            "held by vendor software maps to 'controlled by another app'");
        t.Equal(DeviceHealthState.NotResponding,
            new BlockedDevice("f", "RGB memory", BlockReason.DriverMissing, "no PawnIO", null).Health,
            "hardware we cannot drive maps to 'not responding'");
        t.Equal(DeviceHealthState.NotResponding,
            new BlockedDevice("f", "Keyboard", BlockReason.WentAway, "gone", null).Health,
            "and so does one that has been unplugged");

        t.Section("a device that was here and is not");
        DetectionNotes.Clear();
        t.Check(!DetectionNotes.AnythingWentAway, "nothing has gone on a clean scan");
        DetectionNotes.Report("CorsairStrafeMk2", "Corsair Strafe MK.2", BlockReason.WentAway,
            "it answered on the last scan and is not on the bus now", "check the cable");
        t.Check(DetectionNotes.AnythingWentAway, "an unplugged device is noticed");
        t.Equal("it has gone", DetectionNotes.Current[0].ReasonText, "...and has words of its own");
        DetectionNotes.Clear();   // leave the shared channel as we found it
    }

    /*==================== the recovery debounce ====================*/

    static readonly RecoveryConditions Calm = new(SdkClientHolds: false, LightsSuppressed: false);

    static void Debounce(Harness t)
    {
        t.Section("recovery: a burst of events is ONE rescan");
        var p = new RecoveryPolicy();
        long now = 10_000;

        // The real shape of plugging in one wireless dongle: an arrival per
        // interface it exposes, all within tens of milliseconds. A rescan per
        // event would dispose and reopen every device on the rig four times.
        for (int i = 0; i < 12; i++) p.Note(RecoveryReason.DeviceArrived, now + i * 7);

        t.Check(p.Pending, "the burst arms a recovery");
        t.Equal(12, p.PendingEvents, "...and all twelve events are counted");

        // Nothing happens while the plugging is still going on.
        t.Equal(RecoveryAction.Wait, p.Claim(now + 100, Calm).Action, "nothing fires mid-burst");
        t.Equal(RecoveryAction.Wait, p.Claim(now + RecoveryPolicy.DeviceSettleMs - 1, Calm).Action,
            "nothing fires a millisecond early");

        var plan = p.Claim(now + 12 * 7 + RecoveryPolicy.DeviceSettleMs, Calm);
        t.Equal(RecoveryAction.Rescan, plan.Action, "one rescan, once the plugging has stopped");
        t.Equal(12, plan.CollapsedEvents, "...standing in for all twelve events");
        t.Check(!p.Pending, "and the pending recovery is consumed rather than repeated");
        t.Equal(RecoveryAction.None, p.Claim(now + 60_000, Calm).Action, "a second ask does nothing");

        t.Section("recovery: a late event extends the quiet period");
        var q = new RecoveryPolicy();
        q.Note(RecoveryReason.DeviceArrived, 0);
        // One more event just before the deadline pushes it out, so the rescan
        // lands after the plugging is over rather than in the middle of it.
        q.Note(RecoveryReason.DeviceArrived, RecoveryPolicy.DeviceSettleMs - 10);
        t.Equal(RecoveryAction.Wait, q.Claim(RecoveryPolicy.DeviceSettleMs, Calm).Action,
            "the original deadline no longer fires");
        t.Equal(RecoveryAction.Rescan, q.Claim(2 * RecoveryPolicy.DeviceSettleMs, Calm).Action,
            "it fires once things have really gone quiet");
    }

    static void ResumeSettles(Harness t)
    {
        t.Section("recovery: a wake waits longer than a replug");
        // Resume is delivered while the USB host controllers are still being
        // restarted, so redetecting on it immediately can find zero devices on
        // a rig that has all of them.
        t.Check(RecoveryPolicy.ResumeSettleMs > RecoveryPolicy.DeviceSettleMs,
            "the resume settle is the longer of the two");

        var p = new RecoveryPolicy();
        p.Note(RecoveryReason.SystemResumed, 0);
        t.Equal(RecoveryAction.Wait, p.Claim(RecoveryPolicy.DeviceSettleMs + 1, Calm).Action,
            "a wake does not fire on the device timing");
        var plan = p.Claim(RecoveryPolicy.ResumeSettleMs, Calm);
        t.Equal(RecoveryAction.Rescan, plan.Action, "it fires on the resume timing");
        t.Equal(RecoveryReason.SystemResumed, plan.Reason, "...and knows why, so the history can say so");

        t.Section("recovery: the hub replugging itself does not shorten the wake");
        // The case this guards: waking up produces a resume AND an arrival
        // burst as the hub comes back. Taking the earlier of the two deadlines
        // would redetect into a half-enumerated USB stack, which is exactly
        // the failure the longer settle exists to avoid.
        var q = new RecoveryPolicy();
        q.Note(RecoveryReason.SystemResumed, 0);
        q.Note(RecoveryReason.DeviceArrived, 100);
        q.Note(RecoveryReason.DeviceArrived, 150);
        t.Equal(RecoveryAction.Wait, q.Claim(RecoveryPolicy.ResumeSettleMs - 1, Calm).Action,
            "arrivals during the wake never pull the deadline in");
        var qp = q.Claim(RecoveryPolicy.ResumeSettleMs + 200, Calm);
        t.Equal(RecoveryAction.Rescan, qp.Action, "and it still fires exactly once");
        t.Equal(RecoveryReason.SystemResumed, qp.Reason,
            "the wake is the reason the user will recognise, so it outranks the arrivals");
        t.Equal(3, qp.CollapsedEvents, "all three events collapsed into it");
    }

    static void BurstCeiling(Harness t)
    {
        t.Section("recovery: a flapping port cannot postpone recovery forever");
        // A trailing debounce with no ceiling never fires while events keep
        // arriving. A bad cable or a browning-out hub does exactly that, and
        // that machine is the one that needs recovery most.
        var p = new RecoveryPolicy();
        long now = 0;
        RecoveryPlan plan = default;
        for (int i = 0; i < 400; i++)
        {
            now += 100;                                  // an event every 100 ms, forever
            p.Note(RecoveryReason.DeviceArrived, now);
            plan = p.Claim(now, Calm);
            if (plan.Action == RecoveryAction.Rescan) break;
        }
        t.Equal(RecoveryAction.Rescan, plan.Action, "it goes ahead despite the events still arriving");
        t.Check(now <= RecoveryPolicy.MaxDeferMs + 200,
            $"...within the ceiling rather than never (fired at {now} ms)");
    }

    static void MinimumGap(Harness t)
    {
        t.Section("recovery: never two teardowns for one gesture");
        // Unplug then replug is ONE gesture to the user and produces a removal
        // and an arrival seconds apart. A rescan disposes and reopens every
        // device on the rig, so doing it twice is pure disruption.
        var p = new RecoveryPolicy();
        p.Note(RecoveryReason.DeviceRemoved, 0);
        t.Equal(RecoveryAction.Rescan, p.Claim(RecoveryPolicy.DeviceSettleMs, Calm).Action, "the removal recovers");

        long t2 = RecoveryPolicy.DeviceSettleMs + 100;
        p.Note(RecoveryReason.DeviceArrived, t2);
        t.Equal(RecoveryAction.Wait, p.Claim(t2 + RecoveryPolicy.DeviceSettleMs, Calm).Action,
            "the replug right behind it is held off");
        t.Equal(RecoveryAction.Rescan, p.Claim(RecoveryPolicy.DeviceSettleMs + RecoveryPolicy.MinGapMs, Calm).Action,
            "...until the minimum gap has passed, then it runs once");
    }

    static void DefersToSdkClient(Harness t)
    {
        t.Section("recovery: an SDK client is waited for, not stomped");
        // A rescan drops every claim at once, so recovering underneath a
        // client would yank the lighting out of whatever is driving it.
        var busy = new RecoveryConditions(SdkClientHolds: true, LightsSuppressed: false);
        var p = new RecoveryPolicy();
        p.Note(RecoveryReason.DeviceArrived, 0);

        long now = RecoveryPolicy.DeviceSettleMs;
        t.Equal(RecoveryAction.Wait, p.Claim(now, busy).Action, "a claimed rig defers");
        t.Check(p.Pending, "...keeping the recovery pending rather than dropping it");

        now += RecoveryPolicy.BusyRetryMs;
        t.Equal(RecoveryAction.Wait, p.Claim(now, busy).Action, "and again while the client is still there");

        // The instant it lets go, the deferred recovery runs.
        now += RecoveryPolicy.BusyRetryMs;
        t.Equal(RecoveryAction.Rescan, p.Claim(now, Calm).Action, "it goes the moment the client releases");

        t.Section("recovery: but a client that never lets go does not mean a dark device forever");
        var q = new RecoveryPolicy();
        q.Note(RecoveryReason.DeviceArrived, 0);
        long clock = RecoveryPolicy.DeviceSettleMs;
        var plan = q.Claim(clock, busy);
        int guard = 0;
        while (plan.Action == RecoveryAction.Wait && guard++ < RecoveryPolicy.MaxBusyDeferrals + 5)
        {
            clock += RecoveryPolicy.BusyRetryMs;
            plan = q.Claim(clock, busy);
        }
        t.Equal(RecoveryAction.Rescan, plan.Action,
            "after a bounded wait the recovery happens anyway - a permanently dark device is worse");
        t.Check(guard <= RecoveryPolicy.MaxBusyDeferrals + 1,
            "...and the bound is the one the policy advertises");
    }

    static void NeverRelightsADarkWindow(Harness t)
    {
        t.Section("recovery: never lights the room at 3 AM");
        // The rule that needs code rather than luck. LightsOff blacks the
        // hardware WITHOUT touching the stored frames, so a rescan's restore
        // would faithfully repaint the user's colors during a scheduled dark
        // window or a locked session. The policy says so, and the view model
        // re-asserts the blackout right after the rescan.
        var dark = new RecoveryConditions(SdkClientHolds: false, LightsSuppressed: true);
        var p = new RecoveryPolicy();
        p.Note(RecoveryReason.DeviceArrived, 0);
        var plan = p.Claim(RecoveryPolicy.DeviceSettleMs, dark);

        t.Equal(RecoveryAction.Rescan, plan.Action,
            "the device is still redetected, so it is ready when the window ends");
        t.Check(!plan.Relight, "but the lighting is NOT put back while the lights are meant to be off");

        // The same event on a normal evening does relight.
        var q = new RecoveryPolicy();
        q.Note(RecoveryReason.DeviceArrived, 0);
        var lit = q.Claim(RecoveryPolicy.DeviceSettleMs, Calm);
        t.Equal(RecoveryAction.Rescan, lit.Action, "with nothing suppressing them it still rescans");
        t.Check(lit.Relight, "...and puts the lighting back");

        t.Section("recovery: the sentence the user reads says which happened");
        // Two very different facts, and the history has to tell them apart:
        // "your lights came back" and "your lights are staying off on purpose".
        string on = RecoveryPolicy.Sentence(RecoveryReason.SystemResumed, 4, relight: true);
        string off = RecoveryPolicy.Sentence(RecoveryReason.SystemResumed, 4, relight: false);
        t.Check(on != off, "the wording differs when the lights stay off");
        t.Check(off.Contains("off"), "...and says so in as many words");
        t.Check(on.Contains("4"), "the sentence names how many devices came back");
    }
}
