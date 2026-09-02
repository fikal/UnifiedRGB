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
        internal Rgb[] BaseFrame = Array.Empty<Rgb>();
        internal Thread? Worker;
        internal volatile bool Running;

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
            BaseFrame = (Rgb[])baseFrame.Clone(),
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
        foreach (var c in victims) c.Worker?.Join(300);
    }

    public void StopAll()
    {
        List<Channel> all;
        lock (_lock) { all = new(_channels); _channels.Clear(); }
        foreach (var c in all) c.Running = false;
        foreach (var c in all) c.Worker?.Join(300);
    }

    /// <summary>The channel exactly matching a target range, if any.</summary>
    public Channel? FindExact(IRgbDevice dev, int offset, int count)
    {
        lock (_lock)
            return _channels.FirstOrDefault(c => ReferenceEquals(c.Device, dev) &&
                                                 c.Offset == offset && c.Count == count);
    }

    /// <summary>Snapshot of the channels animating a device (for preview compose).</summary>
    public List<Channel> ChannelsFor(IRgbDevice dev)
    {
        lock (_lock) return _channels.Where(c => ReferenceEquals(c.Device, dev)).ToList();
    }

    /// <summary>Render a channel's current frame (buf.Length == channel.Count).</summary>
    public bool RenderChannel(Channel ch, Rgb[] buf)
    {
        if (!ch.Running || buf.Length != ch.Count) return false;   // same guard as RenderChannelAt
        ch.Effect.Render(ch.Device, ch.Offset, buf, ch.Pos, _clock.Elapsed.TotalSeconds, ch.Speed, ch.BaseColor);
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
            // the engine renders for the preview only and never streams here.
            // Baked mode: hardware plays the loop, this thread has nothing to
            // do — long nap (was 120 ms = 8 wakeups/s of pure no-op).
            if (ch.Device is Devices.LianLiWireless { SuppressStreaming: true })
            { Thread.Sleep(400); continue; }
            frame.Restart();
            try
            {
                // Render inside the try: an unhandled exception on a worker
                // thread would take the whole process down.
                ch.Effect.Render(ch.Device, ch.Offset, zoneBuf, ch.Pos, _clock.Elapsed.TotalSeconds, ch.Speed, ch.BaseColor);
                // Master brightness scales at the write boundary — effects
                // render at full range, hardware gets the dimmed frame.
                if (zoneDev != null)
                {
                    Master.Scale(zoneBuf);
                }
                else
                {
                    // Base statics are pre-scaled ONCE per brightness value;
                    // per frame only the channel's own slice gets scaled
                    // (the old path re-scaled the entire device every frame).
                    double b = Master.Brightness;
                    if (b != scaledForBrightness)
                    {
                        Array.Copy(ch.BaseFrame, scaledBase!, Math.Min(ch.BaseFrame.Length, scaledBase!.Length));
                        Master.Scale(scaledBase!);
                        scaledForBrightness = b;
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
            // to 60 fps the instant a frame differs. Always yield at least
            // 1 ms so a slow device can't make this thread spin flat-out.
            int target = idleStreak > 30 ? 100 : MinFrameMs;
            Thread.Sleep(elapsed < target ? target - elapsed : 1);
        }
    }

    static LedPos[] ZonePositions(IRgbDevice dev, int offset, int count)
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
