using System.Diagnostics;

namespace UnifiedRgb.Core.Effects;

/// <summary>Multi-channel effect engine: every channel animates one LED range of
/// one device on its own worker thread, with its own effect/speed/base color —
/// so the fans can run a rainbow while the keyboard breathes, simultaneously.
/// All channels share one clock, so everything stays phase-locked. Starting a
/// channel replaces any channel overlapping the same range of the same device.</summary>
public sealed class EffectEngine
{
    const int MinFrameMs = 16;   // ~60 fps cap per channel

    public sealed class Channel
    {
        public IRgbDevice Device { get; internal init; } = null!;
        public int Offset { get; internal init; }
        public int Count { get; internal init; }
        public IEffect Effect { get; internal init; } = null!;

        // Live-tunable while running.
        public double Speed;
        public Rgb BaseColor;

        internal LedPos[] Pos = Array.Empty<LedPos>();
        /// <summary>The device's LIVE static frame (not a copy): non-zone
        /// channels compose every hardware frame over it. Bumping BaseVersion
        /// (InvalidateBase) tells the worker to re-snapshot it.</summary>
        internal Rgb[] BaseFrame = Array.Empty<Rgb>();
        internal volatile int BaseVersion;
        internal Thread? Worker;
        internal volatile bool Running;

        /// <summary>The worker's most recent render of this channel (Count
        /// entries, UNscaled effect output - the same thing RenderChannel
        /// produces), copied under FrameLock right after each render so the
        /// preview can read it instead of re-rendering the effect on the UI
        /// thread. HasFrame is false until the first render and while the
        /// worker is idle in baked mode (nothing rendered, so the copy would
        /// be stale).</summary>
        internal Rgb[] LastFrame = Array.Empty<Rgb>();
        internal readonly object FrameLock = new();
        internal volatile bool HasFrame;

        /// <summary>False once the engine stopped this channel - including the
        /// failure breaker's self-stop, which the owner otherwise never learns
        /// about and keeps tinting a dead channel.</summary>
        public bool IsRunning => Running;
    }

    readonly object _lock = new();
    readonly List<Channel> _channels = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Start an effect on [offset, offset+count) of a device, replacing
    /// any overlapping channel on that device.</summary>
    public Channel Start(IRgbDevice dev, int offset, int count, Rgb[] baseFrame,
                         IEffect effect, double speed, Rgb baseColor)
    {
        StopRange(dev, offset, count);

        var ch = new Channel
        {
            Device = dev, Offset = offset, Count = count, Effect = effect,
            Speed = speed, BaseColor = baseColor,
            Pos = ZonePositions(dev, offset, count),
            LastFrame = new Rgb[count],
            // Held by reference, not cloned: a static colour picked later on a
            // sibling zone of a non-zone device (G403 Wheel/Logo) used to be
            // overwritten forever by the Start-time snapshot. The array is
            // stable per device (LightingController.FrameFor) and outlives
            // the channel; the worker re-copies it on InvalidateBase.
            BaseFrame = baseFrame,
        };
        lock (_lock) _channels.Add(ch);
        ch.Running = true;
        ch.Worker = new Thread(() => Run(ch)) { IsBackground = true, Name = $"fx:{dev.Name}@{offset}" };
        ch.Worker.Start();
        return ch;
    }

    /// <summary>Stop every channel overlapping [offset, offset+count) on a device.</summary>
    public void StopRange(IRgbDevice dev, int offset, int count)
    {
        List<Channel> victims;
        lock (_lock)
        {
            victims = _channels.Where(c => ReferenceEquals(c.Device, dev) &&
                                           offset < c.Offset + c.Count && c.Offset < offset + count).ToList();
            foreach (var c in victims) { c.Running = false; _channels.Remove(c); }
        }
        foreach (var c in victims) JoinWorker(c);
    }

    public void StopAll()
    {
        List<Channel> all;
        lock (_lock) { all = new(_channels); _channels.Clear(); }
        foreach (var c in all) c.Running = false;
        foreach (var c in all) JoinWorker(c);
    }

    /// <summary>Wait for a stopped channel's worker. One device write can
    /// outlast the bound (HID write timeouts, the wireless receiver's paced
    /// transmit); the worker re-checks Running right before every write, so a
    /// timeout here means at most one already-started frame is still landing
    /// - logged so a "switched effect but the device kept the old one" report
    /// has a trace.</summary>
    static void JoinWorker(Channel c)
    {
        if (c.Worker is { } w && !w.Join(300))
            Log.Occasional($"fx-join:{c.Device.Name}@{c.Offset}", "engine",
                $"'{c.Device.Name}' effect worker still inside a device write 300 ms after stop");
    }

    /// <summary>The channel exactly matching a target range, if any.</summary>
    public Channel? FindExact(IRgbDevice dev, int offset, int count)
    {
        lock (_lock)
            return _channels.FirstOrDefault(c => ReferenceEquals(c.Device, dev) &&
                                                 c.Offset == offset && c.Count == count);
    }

    /// <summary>Snapshot of the channels animating a device (for preview compose).
    /// Plain loop: this runs per preview pull (30 Hz), so no LINQ closure/iterator.</summary>
    public List<Channel> ChannelsFor(IRgbDevice dev)
    {
        var list = new List<Channel>();
        lock (_lock)
            foreach (var c in _channels)
                if (ReferenceEquals(c.Device, dev)) list.Add(c);
        return list;
    }

    /// <summary>The device's static frame was edited and pushed (a static
    /// colour on a zone beside a running effect). Non-zone channels compose
    /// every hardware frame over the statics, so they re-snapshot the live
    /// frame on their next pass instead of streaming a stale copy.</summary>
    public void InvalidateBase(IRgbDevice dev)
    {
        lock (_lock)
            foreach (var c in _channels)
                if (ReferenceEquals(c.Device, dev)) c.BaseVersion++;
    }

    /// <summary>Render a channel's current frame (buf.Length == channel.Count).</summary>
    public bool RenderChannel(Channel ch, Rgb[] buf)
    {
        if (!ch.Running || buf.Length != ch.Count) return false;   // same guard as RenderChannelAt
        ch.Effect.Render(ch.Device, ch.Offset, buf, ch.Pos, _clock.Elapsed.TotalSeconds, ch.Speed, ch.BaseColor);
        return true;
    }

    /// <summary>Copy the channel's last worker-rendered frame (unscaled, the
    /// same output RenderChannel gives) into dest at destOffset - the preview
    /// path, so a 30 Hz pull never re-renders the effect on the UI thread.
    /// False when nothing has been rendered yet or the channel is idle in
    /// baked mode; the caller then falls back to RenderChannel. The lock is
    /// held for the copy only, never across a device write.</summary>
    public bool TryCopyLastFrame(Channel ch, Rgb[] dest, int destOffset)
    {
        if (!ch.Running || !ch.HasFrame) return false;
        int n = Math.Min(ch.Count, dest.Length - destOffset);
        if (n <= 0) return true;
        lock (ch.FrameLock)
        {
            if (!ch.HasFrame) return false;
            Array.Copy(ch.LastFrame, 0, dest, destOffset, n);
        }
        return true;
    }

    /// <summary>The shared animation clock in seconds - the same time base every
    /// streaming channel renders against. Baking a Lian Li loop from this phase
    /// keeps the fans lined up with the streamed devices.</summary>
    public double ClockSeconds => _clock.Elapsed.TotalSeconds;

    /// <summary>Restart the shared clock so every effect jumps back to the top of
    /// its cycle at once - a synchronized restart (used by "All devices").</summary>
    public void RestartClock() => _clock.Restart();

    /// <summary>Render a channel at an ARBITRARY time (for baking a fixed
    /// animation loop). No clock, no side effects.</summary>
    public bool RenderChannelAt(Channel ch, Rgb[] buf, double seconds)
    {
        if (buf.Length != ch.Count) return false;
        ch.Effect.Render(ch.Device, ch.Offset, buf, ch.Pos, seconds, ch.Speed, ch.BaseColor);
        return true;
    }

    const int KeepaliveMs = 1000;   // resend an unchanged frame at most this often

    /// <summary>True when the first n entries match — frame dedup. ~100 struct
    /// compares is orders of magnitude cheaper than one USB write.</summary>
    static bool SameFrame(Rgb[] a, Rgb[] b, int n)
    {
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void Run(Channel ch)
    {
        var zoneBuf = new Rgb[ch.Count];
        var zoneDev = ch.Device as IZoneWritable;
        var full = zoneDev == null ? new Rgb[ch.Device.LedCount] : null;

        // Dedup at the write boundary: slow effects (Time Warmth changes per
        // MINUTE, Palette Cycle holds each color) used to stream 60 identical
        // USB frames/sec 24/7. Skip writes whose scaled output matches the
        // last one sent, with a keepalive so hardware never sees a dead link.
        var sendBuf = zoneDev != null ? zoneBuf : full!;
        var lastSent = new Rgb[sendBuf.Length];
        var scaledBase = zoneDev == null ? new Rgb[full!.Length] : null;
        double scaledForBrightness = -1;   // forces the first pre-scale
        int seenBase = -1;                 // BaseVersion the pre-scale was taken at
        bool haveLast = false;
        long lastWrite = 0;
        int idleStreak = 0;   // consecutive unchanged frames — throttles the render rate

        // Failure policy: transient hiccups are tolerated, but a device that
        // fails continuously (unplugged, backing server gone) must not spin a
        // silent 60fps throw-loop forever — log rate-limited, then stop.
        const int MaxConsecutiveFailures = 300;   // ~5s at 60fps
        int failures = 0;

        var frame = new Stopwatch();
        while (ch.Running)
        {
            // Lian Li fans in baked mode: a hardware animation is playing, so
            // nothing is rendered or streamed here (the preview renders on
            // demand - HasFrame is dropped so it does not show a stale copy).
            // Long nap (was 120 ms = 8 wakeups/s of pure no-op), sliced so a
            // stop is seen within 100 ms: JoinWorker's 300 ms bound must be
            // met here, or its "still inside a device write" WARN fires for a
            // worker that was merely asleep.
            if (ch.Device is Devices.LianLiWireless { SuppressStreaming: true })
            {
                ch.HasFrame = false;
                for (int i = 0; i < 4 && ch.Running; i++) Thread.Sleep(100);
                continue;
            }
            frame.Restart();
            try
            {
                // Render inside the try: an unhandled exception on a worker
                // thread would take the whole process down.
                ch.Effect.Render(ch.Device, ch.Offset, zoneBuf, ch.Pos, _clock.Elapsed.TotalSeconds, ch.Speed, ch.BaseColor);
                // Publish the unscaled render for the preview (TryCopyLastFrame).
                lock (ch.FrameLock)
                {
                    Array.Copy(zoneBuf, ch.LastFrame, ch.Count);
                    ch.HasFrame = true;
                }
                // Master brightness scales at the write boundary — effects
                // render at full range, hardware gets the dimmed frame.
                if (zoneDev != null)
                {
                    Master.Scale(zoneBuf);
                }
                else
                {
                    // Base statics are pre-scaled ONCE per brightness value
                    // and re-taken when InvalidateBase says the live frame
                    // changed; per frame only the channel's own slice gets
                    // scaled (the old path re-scaled the entire device every
                    // frame). Version is read BEFORE the copy so a bump that
                    // lands mid-copy triggers one more re-copy next frame.
                    double b = Master.Brightness;
                    int ver = ch.BaseVersion;
                    if (b != scaledForBrightness || ver != seenBase)
                    {
                        Array.Copy(ch.BaseFrame, scaledBase!, Math.Min(ch.BaseFrame.Length, scaledBase!.Length));
                        Master.Scale(scaledBase!);
                        scaledForBrightness = b;
                        seenBase = ver;
                    }
                    Array.Copy(scaledBase!, full!, full!.Length);
                    Master.Scale(zoneBuf);
                    for (int i = 0; i < ch.Count && ch.Offset + i < full!.Length; i++)
                        full![ch.Offset + i] = zoneBuf[i];
                }

                long now = Environment.TickCount64;
                bool same = haveLast && SameFrame(sendBuf, lastSent, sendBuf.Length);
                bool unchanged = same && now - lastWrite < KeepaliveMs;
                if (!unchanged)
                {
                    // Re-check at the write boundary: StopRange/StopAll may
                    // have cleared Running while this frame rendered, and the
                    // replacement channel or the static restore can already be
                    // writing this range - a stale frame landing now would win
                    // until the next differing frame (up to the keepalive).
                    if (!ch.Running) break;
                    if (zoneDev != null) zoneDev.SetZone(ch.Offset, zoneBuf);
                    else ch.Device.SetColors(full!);
                    Array.Copy(sendBuf, lastSent, sendBuf.Length);
                    haveLast = true;
                    lastWrite = now;
                }
                idleStreak = same ? idleStreak + 1 : 0;
                failures = 0;
            }
            catch (Exception ex)
            {
                failures++;
                // Deferred message: in a sustained failure loop this runs ~20x/s
                // and the string is suppressed 59 times out of 60.
                Log.Occasional($"fx:{ch.Device.Name}@{ch.Offset}", "engine",
                    () => $"'{ch.Device.Name}' effect frame failed: {ex.Message}");
                if (failures >= MaxConsecutiveFailures)
                {
                    Log.Error("engine",
                        $"'{ch.Device.Name}' failed {failures} consecutive frames - stopping its effect channel");
                    ch.Running = false;
                    lock (_lock) _channels.Remove(ch);
                    return;
                }
                Thread.Sleep(50);   // don't hot-spin a dead device
            }
            int elapsed = (int)frame.ElapsedMilliseconds;
            // Adaptive rate: an effect whose output hasn't changed for ~0.5 s
            // (Time Warmth, a Palette Cycle hold, a parked static) drops to a
            // 10 fps check loop — 6x fewer wakeups and renders — snapping back
            // to 60 fps the instant a frame differs. Live-input effects (typing,
            // audio) are exempt: their output is static exactly while they wait
            // for the next key/beat, and the 100 ms nap became press-to-light
            // latency. Always yield at least 1 ms so a slow device can't make
            // this thread spin flat-out.
            int target = idleStreak > 30 && !ch.Effect.LiveInput ? 100 : MinFrameMs;
            Thread.Sleep(elapsed < target ? target - elapsed : 1);
        }
    }

    /// <summary>Per-LED positions of [offset, offset+count) normalised to the
    /// range's own bounding box (0..1 each axis; 0.5 on a degenerate axis) -
    /// the geometry every channel renders against, also used by the app to
    /// render a range outside the engine.</summary>
    public static LedPos[] ZonePositions(IRgbDevice dev, int offset, int count)
    {
        LedPos[] src;
        if (dev.LedPositions is { Count: > 0 } p && p.Count == dev.LedCount)
            src = p.ToArray();
        else
        {
            src = new LedPos[dev.LedCount];
            for (int i = 0; i < src.Length; i++)
                src[i] = new LedPos(dev.LedCount <= 1 ? 0.5f : i / (float)(dev.LedCount - 1), 0.5f);
        }

        var zone = new LedPos[count];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            var q = src[Math.Min(offset + i, src.Length - 1)];
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        float rx = maxX - minX, ry = maxY - minY;
        for (int i = 0; i < count; i++)
        {
            var q = src[Math.Min(offset + i, src.Length - 1)];
            zone[i] = new LedPos(rx > 1e-4f ? (q.X - minX) / rx : 0.5f, ry > 1e-4f ? (q.Y - minY) / ry : 0.5f);
        }
        return zone;
    }
}
