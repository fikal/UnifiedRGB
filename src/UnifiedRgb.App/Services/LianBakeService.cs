using System.Collections.Concurrent;
using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------*\
| Lian Li animation baking: the wireless fans loop a     |
| multi-frame animation in HARDWARE. Streaming single    |
| frames over RF is capped at ~8 fps (the lag). Instead  |
| we bake one loop of all the device's bakeable effects  |
| into N frames and upload ONCE; the receiver plays it   |
| smoothly. Live effects (audio/temp/wallpaper) can't be |
| baked, so those fall back to streaming.                |
|                                                        |
| Threading: everything here except the render worker    |
| and the applier lane runs on the UI thread - Rebake    |
| (timer tick), ForgetSignatures / Invalidate / Stop     |
| (view-model calls) and the (un)confirmed-animation     |
| handlers (marshalled). _lastSig and _misses are plain  |
| collections for that reason; _gen is concurrent        |
| because the worker and the applier lane read it.       |
\*-----------------------------------------------------*/
public sealed class LianBakeService
{
    /// <summary>Shortest window the receiver plays smoothly (a shorter loop
    /// is repeated: {0.8 s} bakes as two loops in 1.6 s).</summary>
    public const double MinWindowSeconds = 1.5;
    /// <summary>Longest upload that still fits the RF packet budget at a
    /// usable frame interval (full-hue-turn effects: Color Cycle is 12 s).</summary>
    public const double MaxWindowSeconds = 12.0;
    /// <summary>How far a channel's cycle count may sit from a whole number
    /// inside the chosen window before its wrap would visibly pop.</summary>
    public const double CycleTolerance = 0.02;

    readonly LightingController _lighting;
    readonly Func<IEnumerable<LianLiWireless>> _devices;
    // Captured once: the service is constructed in MainViewModel's constructor
    // on the UI thread, and every reset of SuppressStreaming must land there
    // so it is ordered against Rebake (see the worker's catch below).
    readonly Dispatcher _ui;
    DispatcherTimer? _timer;
    readonly Dictionary<LianLiWireless, string> _lastSig = new();   // skip redundant re-bakes
    // Per-device bake generation. Bumped by every new bake AND by every path
    // that abandons baking (static apply, live effect, Rescan, shutdown), so an
    // older bake still rendering on its worker can never post its upload over
    // a state that no longer wants it. Entries are never removed: an in-flight
    // task looks its device up here and a missing key would read as "current".
    readonly ConcurrentDictionary<LianLiWireless, int> _gen = new();
    // The handlers subscribed to each device's AnimationUnconfirmed and
    // AnimationConfirmed, kept so the next bake can unsubscribe exactly those
    // delegates before re-subscribing (a closure cannot be found again by value).
    readonly Dictionary<LianLiWireless, (Action Unconfirmed, Action Confirmed)> _handlers = new();
    // The generation whose upload has actually REACHED the device (set on the
    // applier lane right before UploadAnimation). A device's unconfirmed event
    // can only be about an upload that happened; one that fires between a new
    // bake's subscribe and its upload belongs to the previous upload and must
    // not spend the new bake's retry.
    readonly ConcurrentDictionary<LianLiWireless, int> _uploaded = new();

    /// <summary>What a device's fans have NOT taken: the bake signature whose
    /// uploads went unconfirmed, how many did since it last confirmed, and
    /// when it may be uploaded again. Per signature, not per device: an
    /// effect too dense to land in one RF burst never confirms while a simpler
    /// one does, so a new effect always gets its own tries.</summary>
    sealed class Misses
    {
        public string Sig = "";
        public int Count;
        public long RetryAtMs;
    }
    readonly Dictionary<LianLiWireless, Misses> _misses = new();

    /// <summary>The wait after the first whole round (an upload and its one
    /// retry) of a signature goes unconfirmed. Each further round doubles it,
    /// up to the cap. Settable only for the harness.</summary>
    internal static long UnconfirmedBackoffMs = 60_000;
    internal static long UnconfirmedBackoffCapMs = 30 * 60_000;

    internal static long BackoffFor(int rounds)
        => Math.Min(UnconfirmedBackoffCapMs, UnconfirmedBackoffMs << Math.Clamp(rounds - 1, 0, 30));

    public LianBakeService(LightingController lighting, Func<IEnumerable<LianLiWireless>> devices)
    {
        _lighting = lighting;
        _devices = devices;
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Debounced (150 ms): every wireless device re-bakes once the
    /// burst of edits settles.</summary>
    public void Request()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => { _timer.Stop(); Prune(); foreach (var d in _devices()) Rebake(d); };
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Forget the last uploaded signatures so the next Request re-uploads
    /// even an unchanged effect (speed calibration changed; an explicit "All
    /// devices" sync that must re-align the fans to the clock).</summary>
    public void ForgetSignatures() => _lastSig.Clear();

    /// <summary>Abandon every outstanding bake: bump each known device's
    /// generation so a worker that is still rendering (or an upload already
    /// queued on the applier lane) becomes a no-op, and forget all signatures
    /// so the next Request bakes afresh. Call before a Rescan replaces the
    /// device instances and before shutdown drains the applier - without it a
    /// slow bake finished after a static apply / rescan / exit and posted
    /// UploadAnimation with a generation that was still valid.</summary>
    public void Invalidate()
    {
        foreach (var dev in _gen.Keys) Bump(dev);
        _lastSig.Clear();
        _misses.Clear();
    }

    /// <summary>Stop the debounce AND abandon outstanding bakes: a Stop is a
    /// shutdown, and nothing baked after it may reach the hardware.</summary>
    public void Stop() { _timer?.Stop(); Invalidate(); }

    /// <summary>The lights are going off: abandon every bake still rendering
    /// or queued, so none uploads its animation after the black frame and
    /// relights the fans on a locked desk. Unlike Invalidate the signatures
    /// and the unconfirmed-upload record stay - nothing about the settings
    /// changed, and the black frame itself clears AnimationActive, so the
    /// restore re-uploads them.</summary>
    public void CancelInFlight()
    {
        _timer?.Stop();
        foreach (var dev in _gen.Keys) Bump(dev);
    }

    /// <summary>An SDK client took this device: abandon its bake in flight
    /// (still rendering, or its upload queued), so the takeover is not
    /// answered a moment later by the animation landing over the client's
    /// picture. The signature stays, like CancelInFlight: the client's frames
    /// cancel the animation in hardware, and the release re-uploads it.</summary>
    public void Cancel(LianLiWireless dev) => Bump(dev);

    /// <summary>Asked right before an upload reaches the fans: true while the
    /// lights are meant to be off (MainViewModel.LightsSuppressed). The
    /// generation check already drops every bake LightsOff cancelled; this
    /// keeps any later path that starts one during the dark window from
    /// lighting the fans.</summary>
    internal Func<bool>? Suppressed { get; set; }

    /// <summary>Drop every device instance a Rescan has replaced: unsubscribe
    /// its handler and forget its generation, signature and marks. Each Rescan
    /// used to leave one disposed instance behind in every map for the life of
    /// the process. Safe for a worker still in flight for the old instance: a
    /// MISSING generation reads as superseded (see the upload job), never as
    /// current.</summary>
    void Prune()
    {
        var current = new HashSet<LianLiWireless>(_devices());
        foreach (var dev in _gen.Keys.Where(d => !current.Contains(d)).ToList())
        {
            Unsubscribe(dev);
            _gen.TryRemove(dev, out _);
            _uploaded.TryRemove(dev, out _);
            _lastSig.Remove(dev);
            _misses.Remove(dev);
        }
    }

    void Bump(LianLiWireless dev) => _gen.AddOrUpdate(dev, 1, (_, g) => g + 1);

    /// <summary>Hand the device back to streaming. Bumps the generation first
    /// so a bake still in flight for it cannot re-suppress streaming with a
    /// stale upload, and forgets the signature so the next Request does not
    /// think the (never uploaded) settings are already on the fans.</summary>
    void StreamInstead(LianLiWireless dev, string why)
    {
        Bump(dev);
        _lastSig.Remove(dev);
        dev.SuppressStreaming = false;
        Log.Info("LianBake", why);
    }

    /// <summary>Pick the bake window for a set of channel periods: the smallest
    /// multiple of the longest period, inside MinWindowSeconds..MaxWindowSeconds,
    /// that is a whole number of cycles (within CycleTolerance) of EVERY
    /// period. Periods of 0 (time-invariant effects) are ignored; with none
    /// left, the shortest window is returned. False means no common window
    /// fits (a single period above 12 s, or two periods whose common multiple
    /// does not) - the caller streams instead, because a window that cuts a
    /// channel mid-cycle pops at every wrap. This replaced a max-then-clamp:
    /// clamping a slow effect cut it mid-cycle, and the larger of two periods
    /// is not a common period (4 s beside 9 s reset the 4 s channel after
    /// 2.25 cycles).</summary>
    public static bool ChooseWindow(IReadOnlyList<double> periods, out double seconds)
    {
        double tmax = 0;
        int live = 0;
        foreach (double p in periods)
        {
            if (!(p > 0)) continue;                          // 0 (or NaN) = constant: any window is a period
            if (!double.IsFinite(p)) { seconds = 0; return false; }   // never repeats: cannot be looped
            live++;
            if (p > tmax) tmax = p;
        }
        if (live == 0) { seconds = MinWindowSeconds; return true; }

        // Any common window must be a multiple of the longest period (that
        // channel must complete whole cycles too), so only those are candidates.
        for (int m = 1; ; m++)
        {
            double T = m * tmax;
            if (T > MaxWindowSeconds * (1 + 1e-9)) break;
            if (T < MinWindowSeconds) continue;              // too short: loop it more than once
            bool ok = true;
            foreach (double p in periods)
            {
                if (!(p > 0)) continue;
                double cycles = T / p;
                double whole = Math.Round(cycles);
                // ABSOLUTE tolerance, in cycles: the seam error the fans show
                // is the fractional cycle left over at the wrap, whatever the
                // cycle count. Relative to the count it grew with every cycle
                // (Rotate 4 s beside Breathe at 0.79 s passed at 5.09 cycles -
                // a 33 degree phase jump in the breathing every 4 s).
                if (Math.Abs(cycles - whole) > CycleTolerance) { ok = false; break; }
            }
            if (ok) { seconds = T; return true; }
        }
        seconds = 0;
        return false;
    }

    /// <summary>Identity of the inputs that affect every baked frame.
    ///
    /// <paramref name="device"/> is optional only because a caller comparing two
    /// signatures against each other does not need it; the real bake always
    /// passes it. It is here for the same reason master brightness is: the
    /// baked frames are the finished write, so a per-device calibration trim
    /// changes every one of them. Without it in the key, trimming the fans
    /// would leave them replaying an animation baked from the OLD trim while
    /// every streamed device on the desk already showed the new one, and the
    /// user would conclude the sliders do not work on the fans. The
    /// fingerprint covers this device's ZONE trims as well as its own, so
    /// trimming one fan header on its own stales the bake just the same.</summary>
    internal static string BakeSignature(IEnumerable<EffectEngine.Channel> channels, IReadOnlyList<Rgb> baseFrame,
                                         string device = "")
    {
        var key = new System.Text.StringBuilder();
        key.Append(FormattableString.Invariant($"brightness:{Master.Brightness:R}|"));
        key.Append("cal:").Append(Calibration.Fingerprint(device)).Append('|');
        foreach (var c in channels.OrderBy(c => c.Offset))
        {
            key.Append(FormattableString.Invariant($"{c.Offset}:{c.Count}:{c.Effect.Name}:{c.Speed:R}:{c.BaseColor}:{c.Effect.BakeKey}:"));
            if (c.Effect is IPaletteEffect pe) key.AppendJoin(",", pe.Palette);
            foreach (var p in c.Positions)
                key.Append(FormattableString.Invariant($";{p.X:R},{p.Y:R}"));
            key.Append('|');
        }
        // Static sibling zones are baked into every frame too.
        foreach (var color in baseFrame) key.Append(color.ToHex()).Append(',');
        return key.ToString();
    }

    /// <summary><paramref name="retry"/> is the one immediate retry after an
    /// unconfirmed upload: part of a round that was already let through, so
    /// the unconfirmed backoff does not apply to it.</summary>
    void Rebake(LianLiWireless dev, bool retry = false)
    {
        var engine = _lighting.Engine;
        var channels = engine.ChannelsFor(dev);
        // Both early returns abandon any bake in flight: switching to static
        // lighting (no channels) or to a live effect used to leave the old
        // bake's generation valid, so it could finish afterwards and upload an
        // animation over the static frame.
        if (channels.Count == 0) { StreamInstead(dev, "no channels - streaming/static"); return; }
        if (!channels.All(c => c.Effect.Bakeable)) { StreamInstead(dev, $"live effect present ({channels.First(c => !c.Effect.Bakeable).Effect.Name}) - streaming"); return; }

        // Skip the re-bake if nothing about the effect actually changed. Phase
        // alignment re-samples the clock every bake, so without this a redundant
        // re-apply (pressing All devices again) would re-upload a phase-shifted
        // copy and visibly reset the fans mid-loop while streamed devices flow on.
        // BakeKey carries the settings the fields here miss (Custom Pattern's
        // motion / density / direction / tail / own palette): an edit to those
        // re-requested a bake, this check called it unchanged, and the fans
        // kept the old animation while the preview showed the new one.
        //
        // "Unchanged" also needs the animation to still be ON the fans. Any
        // static frame cancels it in hardware (LightsOff's black, the unlock
        // restore, identify's put-back), and none of those paths clears the
        // signature - so the same effect asked for again afterwards matched,
        // nothing was uploaded, and the fans sat static or black while the
        // preview animated. The device knows whether it is still playing - and
        // "playing" includes an upload still awaiting the fans' confirmation,
        // so a re-request inside that window is not a second upload either.
        var baseFrame = (Rgb[])_lighting.FrameFor(dev).Clone();
        string sig = BakeSignature(channels, baseFrame, dev.Name);
        if (dev.SuppressStreaming && dev.AnimationActive
            && _lastSig.TryGetValue(dev, out var prev) && prev == sig) return;

        // These exact settings went a whole round unconfirmed not long ago.
        // AnimationActive went false when the confirm deadline lapsed, so the
        // check above no longer holds them back, and every rebake request -
        // a profile re-applied, an unlock, identify's put-back - used to cost
        // another upload and its retry: twelve seconds of RF resends and the
        // fans flipping between a restarted animation and streaming. Until
        // the backoff runs out they are streamed, which shows the effect.
        // Streaming, never suppressed-and-skipped: that is what keeps D1 -
        // the fans can not be left static or black behind a skipped bake,
        // because the engine is painting them.
        if (!retry && _misses.TryGetValue(dev, out var miss) && miss.Sig == sig
            && Environment.TickCount64 < miss.RetryAtMs)
        {
            if (dev.SuppressStreaming) StreamInstead(dev, "these settings were not confirmed by the fans recently - streaming them");
            return;
        }
        _lastSig[dev] = sig;

        // One window that every channel closes on; none means a faithful
        // stream beats a popping bake.
        var periods = channels.Select(c => c.Effect.LoopSeconds(c.Speed)).ToList();
        if (!ChooseWindow(periods, out double T))
        {
            StreamInstead(dev, $"no common loop window <= {MaxWindowSeconds:0}s for periods [{string.Join(",", periods.Select(p => p.ToString("0.00")))}] - streaming");
            return;
        }

        dev.SuppressStreaming = true;
        // Frame count is chosen so the per-frame interval lands in the hardware's
        // honored range (L-Connect never exceeds ~77ms = SpeedType 7 x 11). A
        // large interval (e.g. 64 frames over 9s = 140ms) gets clamped by the
        // receiver and plays too fast, out of sync with the streamed devices. So
        // pick N to target ~60ms/frame: more frames, smaller interval, same loop.
        int N = (int)Math.Clamp(Math.Round(T * 1000.0 / 60.0), 32, 160);
        Log.Info("LianBake", $"baking {channels.Count} channel(s) [{string.Join(",", channels.Select(c => c.Effect.Name))}], T={T:0.0}s, N={N}{(retry ? " (retry)" : "")}");
        // Snapshot the statics under the UI thread; the render loop itself runs
        // on a WORKER — 28k+ LED evaluations per device was a visible dispatcher
        // hitch. A generation stamp makes a superseded bake's upload a no-op
        // (a slower older bake can otherwise finish after a newer one).
        // The statics are stored at full range and finished at the write
        // boundary everywhere else (PushFrame, the engine's base snapshot); the
        // baked frames ARE the write, so finish them here too - the channel
        // LEDs below are finished and an untransformed base left any LED
        // outside the effect's range at full brightness and untrimmed under a
        // dimmed, calibrated master. baseFrame is already a clone, so this
        // still never touches stored state.
        Master.Finish(dev, baseFrame);
        int myGen = _gen.AddOrUpdate(dev, 1, (_, g) => g + 1);
        Subscribe(dev, myGen, sig);
        // Bake from the clock's current phase so the fans' frame 0 is the same
        // point in the cycle the streamed devices are on (red right after an All-
        // devices restart). No look-ahead: on a restart we want them to START on
        // that color, not where the keyboard will have drifted to by upload time.
        double baseTime = engine.ClockSeconds;
        _ = Task.Run(() =>
        {
            try
            {
                var frames = new Rgb[N][];
                // One scratch buffer per channel, reused across all N frames (the
                // old per-frame allocation threw away ~half the bake's memory).
                var bufs = new Rgb[channels.Count][];
                for (int c = 0; c < channels.Count; c++) bufs[c] = new Rgb[channels[c].Count];
                for (int f = 0; f < N; f++)
                {
                    var frame = (Rgb[])baseFrame.Clone();
                    double time = baseTime + T * f / N;
                    for (int c = 0; c < channels.Count; c++)
                    {
                        var ch = channels[c];
                        var buf = bufs[c];
                        if (engine.RenderChannelAt(ch, buf, time))
                        {
                            // Each slice is finished exactly once, over a base
                            // that was already finished above - the same
                            // one-transform-per-pixel rule the streaming
                            // compose path follows.
                            // The channel's slice, so the trim is told it
                            // starts at device LED ch.Offset.
                            Master.Finish(dev, buf, ch.Offset);
                            for (int i = 0; i < ch.Count && ch.Offset + i < frame.Length; i++)
                                frame[ch.Offset + i] = buf[i];
                        }
                    }
                    frames[f] = frame;
                }
                // No seam crossfade: the window is a whole number of every
                // channel's period (ChooseWindow), so frame N-1 -> frame 0 is
                // already continuous.
                // The upload is paced (many RF packets, sleeps between them) - run it
                // on the device lane. Keyed so a fresh bake supersedes a still-queued
                // one; the generation check drops an out-of-date bake entirely.
                //
                // moveToEnd, like every other static-intent post: a re-posted key
                // otherwise keeps its FIRST slot in the lane, so this upload could
                // run before a static frame queued after the earlier bake - and
                // that frame would then cancel the newer animation it preceded.
                double frameMs = T * 1000.0 / N;
                _lighting.Applier.Post(LightingController.LaneOf(dev), (dev, "anim"), () =>
                {
                    // Under the device's write gate, the boundary every other
                    // hardware write shares with the effect workers: a worker's
                    // straggler frame landing after the upload is a static frame,
                    // and it would cancel the animation on the fans.
                    //
                    // And everything that decides WHETHER to upload is asked
                    // inside it, at the moment of writing. Asked before taking
                    // it, an upload waiting there for a slow write could pass
                    // every check, then a lights-off or a client's takeover
                    // land, and the animation still go out after them.
                    lock (EffectEngine.WriteGateFor(dev))
                    {
                        // Missing = superseded too: Prune drops a replaced instance's
                        // generation, and a bake for it must never reach the hardware.
                        if (!_gen.TryGetValue(dev, out int cur) || cur != myGen) return;
                        if (Suppressed?.Invoke() == true) return;
                        // A client owns the device: the claim is the fact that
                        // matters, whatever else bumped or did not bump the
                        // generation (the takeover does, through Cancel). A
                        // claim landing after this point is safe: its frames
                        // queue behind this job on the same lane and replace
                        // the loop.
                        if (_lighting.IsClientOwned(dev)) return;
                        _uploaded[dev] = myGen;   // from here an unconfirmed event can be about THIS bake
                        dev.UploadAnimation(frames, frameMs);
                    }
                }, moveToEnd: true);
            }
            catch (Exception ex)
            {
                // A faulted, discarded Task is silent: the device would sit on
                // SuppressStreaming with nothing uploaded and the next identical
                // Request short-circuited on the signature above. Log it and,
                // unless a newer bake has since taken over, hand the device
                // back to streaming so the effect still shows. (The applier
                // already catches and logs the posted upload itself.) The reset
                // is marshalled to the UI thread so the generation check and
                // the flag write are ordered against Rebake, which sets the
                // flag before it bumps the generation - a worker-side check
                // could pass against the old generation and then clear the
                // flag the newer bake had just set.
                Log.Error("LianBake", ex);
                _ui.BeginInvoke(() =>
                {
                    if (_gen.TryGetValue(dev, out int gen) && gen == myGen) StreamInstead(dev, "bake failed - streaming");
                });
            }
        });
    }

    /// <summary>Listen for the device's verdict on this bake's upload.
    /// Idempotent per device: the previous handlers are removed first, so a
    /// device only ever carries one pair and it closes over the CURRENT
    /// generation and signature.</summary>
    void Subscribe(LianLiWireless dev, int gen, string sig)
    {
        Unsubscribe(dev);
        // Both fire on the device's telemetry thread; everything the service
        // owns is UI-thread state, so marshal before touching any of it.
        Action unconfirmed = () => _ui.BeginInvoke(() => OnUnconfirmed(dev, gen, sig));
        Action confirmed = () => _ui.BeginInvoke(() => OnConfirmed(dev, gen, sig));
        _handlers[dev] = (unconfirmed, confirmed);
        dev.AnimationUnconfirmed += unconfirmed;
        dev.AnimationConfirmed += confirmed;
    }

    void Unsubscribe(LianLiWireless dev)
    {
        if (!_handlers.Remove(dev, out var old)) return;
        dev.AnimationUnconfirmed -= old.Unconfirmed;
        dev.AnimationConfirmed -= old.Confirmed;
    }

    /// <summary>The fans never confirmed the upload (RF loss for the whole
    /// resend window, or a receiver whose read-back never answers). Before the
    /// first hook here the device stayed on SuppressStreaming with the
    /// signature cached, so the same settings were skipped forever and the
    /// fans sat frozen on the previous animation.
    ///
    /// A round is one upload and one immediate retry (UploadAnimation's hash
    /// was reset by the device, so identical frames go out again). A round
    /// that ends unconfirmed hands the device to streaming AND holds these
    /// settings back from the next upload for a while, doubling each round:
    /// without that, a receiver that never confirms got the whole round again
    /// on every rebake request. The count is kept per signature and survives
    /// new requests for the same settings - it used to be a per-device mark
    /// that any request reset, so requests arriving between a lapse and this
    /// handler restarted the round and it never ended.</summary>
    void OnUnconfirmed(LianLiWireless dev, int gen, string sig)
    {
        // Stale if anything moved on since this bake: a newer bake, a static
        // apply, a Rescan or shutdown (Invalidate bumps every device). That
        // also covers a device instance replaced by a Rescan: its telemetry
        // thread can still fire after disposal, but its generation was bumped
        // by Invalidate and its handler closes over the old one, so it can
        // never act on the new instance's state.
        if (!_gen.TryGetValue(dev, out int cur) || cur != gen) return;
        // Not yet uploaded: the device is reporting on the PREVIOUS upload's
        // deadline lapsing (its resend window can outlive our re-bake). That
        // upload is superseded anyway; acting on it here would spend this
        // bake's single retry and drop its upload as "superseded" before it
        // ever went out.
        if (!_uploaded.TryGetValue(dev, out int up) || up != gen) return;
        if (!_misses.TryGetValue(dev, out var miss) || miss.Sig != sig)
            _misses[dev] = miss = new Misses { Sig = sig };
        if (++miss.Count % 2 == 1)
        {
            Log.Warn("LianBake", "animation not confirmed by the fans - retrying the bake once");
            _lastSig.Remove(dev);   // or the retry short-circuits on "unchanged"
            Rebake(dev, retry: true);
            return;
        }
        int rounds = miss.Count / 2;
        long wait = BackoffFor(rounds);
        miss.RetryAtMs = Environment.TickCount64 + wait;
        Log.Occasional($"lianbake:unconfirmed:{dev.Name}", "LianBake",
            $"{dev.Name}: the fans have not confirmed these settings in {rounds} round(s) of upload and retry - "
            + $"streaming them instead, and not uploading them again for {wait / 1000} s");
        StreamInstead(dev, "animation not confirmed twice - falling back to streaming");
    }

    /// <summary>The fans took this bake's upload: whatever these settings
    /// missed before was the air, not the effect, so the count starts over.
    /// Only for an upload of ours that reached the device - a confirmation
    /// arriving between a newer bake's subscribe and its upload is about the
    /// previous upload, not this signature.</summary>
    void OnConfirmed(LianLiWireless dev, int gen, string sig)
    {
        if (!_uploaded.TryGetValue(dev, out int up) || up != gen) return;
        if (_misses.TryGetValue(dev, out var miss) && miss.Sig == sig) _misses.Remove(dev);
    }
}
