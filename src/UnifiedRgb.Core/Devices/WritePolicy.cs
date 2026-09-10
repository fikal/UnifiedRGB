namespace UnifiedRgb.Core.Devices;

/*-----------------------------------------------------------*\
| The hardware-write contract, expressed once instead of       |
| thirteen times.                                              |
|                                                              |
| Every driver in this folder had to decide for itself what a  |
| refused packet meant, when to try again, and when it was     |
| allowed to remember a frame as sent. They mostly agreed,     |
| but "mostly" is how the same bug shipped in four of them:    |
| a frame recorded as sent before it went out, so the engine's |
| own keepalive re-send - which exists precisely to cover a    |
| lost packet - was deduped away and the device sat on a stale |
| color until something else changed it.                      |
|                                                              |
| The rules, in one place:                                     |
|                                                              |
| 1. SUCCESS. SetColors/SetZone return true when the frame     |
|    reached the device OR was correctly skipped because the   |
|    device is already showing it. They return false only when |
|    the device REFUSED it. False is not an exception: the     |
|    engine's breaker counts throws, and a refusal is a          |
|    transient thing that the next frame should fix.           |
|                                                              |
| 2. DEDUP AFTER SUCCESS, NEVER BEFORE. A frame is recorded as |
|    "what the device is showing" only once the transport has  |
|    accepted every packet of it. A refusal drops the cache    |
|    (Refused below does both), so the identical frame that    |
|    follows is really re-sent. Any mode change - handing the  |
|    device back to its firmware, toggling direct mode,        |
|    re-initialising after a resume - drops it too, through    |
|    IRgbDevice.InvalidateCache.                               |
|                                                              |
| 3. RETRY. On the STREAMING path the retry is the next frame. |
|    That is deliberate and it is why nothing here loops: a    |
|    device that has stopped answering must not be sent the    |
|    same packet twice per frame, each one blocking out its    |
|    400 ms write timeout while holding the driver's lock.     |
|    Drivers back off instead (see LogitechG403 and RazerHid). |
|    The bounded immediate retry lives in MustLand, for the    |
|    writes that have no next frame.                           |
|                                                              |
| 4. MUST LAND. A static apply, a lights-off and an exit       |
|    behaviour are terminal: nothing comes after them to       |
|    correct a refusal, so "it will be fixed next frame" is    |
|    false for exactly the writes the user notices most. Those |
|    go through MustLand, which bypasses dedup, retries to a   |
|    bounded deadline, and says so loudly in the log when it   |
|    still could not deliver.                                  |
|                                                              |
| Nothing on the streaming side of this file allocates: the    |
| write path runs at up to 60 fps for as long as the app is    |
| running. The MustLand side takes a closure, which is fine    |
| because it runs on user actions and on the way out.          |
\*-----------------------------------------------------------*/
public static class WritePolicy
{
    /*-----------------------------------------------------*\
    | The streaming path: frame helpers with no allocation.  |
    \*-----------------------------------------------------*/

    /// <summary>The color for LED <paramref name="i"/> of a frame that may be
    /// SHORT. The contract's answer to a short frame is "repeat the last
    /// color": dropping the frame and padding with black have both shipped,
    /// and both looked like a dead device.</summary>
    public static Rgb ColorAt(IReadOnlyList<Rgb> colors, int i)
        => colors[i < colors.Count ? i : colors.Count - 1];

    /// <summary>True when <paramref name="colors"/> is exactly what the device
    /// is already showing, so the write can be skipped and still reported as a
    /// success. Index loop rather than SequenceEqual: the latter boxes two
    /// struct enumerators, and this runs on every frame of every device.</summary>
    public static bool Unchanged(Rgb[]? last, IReadOnlyList<Rgb> colors)
    {
        if (last == null || last.Length != colors.Count) return false;
        for (int i = 0; i < last.Length; i++) if (last[i] != colors[i]) return false;
        return true;
    }

    /// <summary>Record a frame as what the device is showing. Call this ONLY
    /// after the transport accepted every packet of it - that ordering is rule
    /// 2, and inverting it is the bug this class exists to stop.</summary>
    public static void Cache(ref Rgb[]? last, IReadOnlyList<Rgb> colors)
    {
        if (last == null || last.Length != colors.Count) last = new Rgb[colors.Count];
        for (int i = 0; i < colors.Count; i++) last[i] = colors[i];
    }

    /// <summary>What a driver does about a refused frame, in one call: forget
    /// the cached frame so the next identical one is really sent, say so at a
    /// rate limit, and hand back the false the caller returns.
    ///
    /// It returns false so a driver can end a branch with
    /// `return WritePolicy.Refused(ref _last, ...)` and have the invalidation
    /// and the log line be impossible to forget.</summary>
    public static bool Refused(ref Rgb[]? last, string key, string source, string what)
    {
        last = null;
        Log.Occasional(key, source, what);
        return false;
    }

    /// <summary>Same, for a driver whose cache is not a frame array (one
    /// color, a per-cluster table, a "primed" flag): the caller drops its own
    /// cache, this one logs and returns the false.</summary>
    public static bool Refused(string key, string source, string what)
    {
        Log.Occasional(key, source, what);
        return false;
    }

    /*-----------------------------------------------------*\
    | The must-land path: terminal writes.                   |
    \*-----------------------------------------------------*/

    /// <summary>How long a terminal write may keep trying. Deliberately small:
    /// the whole exit path shares a 2000 ms budget across every device, and on
    /// the logoff path that budget is what stands between a wedged SMBus write
    /// and the user losing their session.</summary>
    public const int MustLandBudgetMs = 400;

    /// <summary>Gap between attempts. Long enough that a device busy with the
    /// previous packet has a moment to catch up, short enough that the budget
    /// buys a dozen or so tries.</summary>
    public const int MustLandGapMs = 25;

    /// <summary>Deliver something that has no next frame behind it, retrying
    /// until it lands or the budget runs out.
    ///
    /// Returns whether it landed. A false here is the failure users actually
    /// notice - the machine sleeps with a color still lit because the off
    /// command was dropped - so it is logged at error level with the device
    /// and the attempt count, not swallowed.</summary>
    public static bool MustLand(string device, string what, Func<bool> send, int budgetMs = MustLandBudgetMs)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, budgetMs);
        int attempts = 0;
        string? thrown = null;
        while (true)
        {
            attempts++;
            // A throw here is a dead device, which is the caller's problem on
            // the streaming path but not on this one: we are on the way out
            // and there is nothing left to stop, so it counts as one refused
            // attempt and the message is carried into the failure line.
            try { if (send()) break; }
            catch (Exception ex) { thrown = ex.Message; }
            if (Environment.TickCount64 >= deadline)
            {
                Log.Error("write", thrown == null
                    ? $"{device}: '{what}' was refused on all {attempts} attempts in {budgetMs} ms and did NOT reach the device"
                    : $"{device}: '{what}' failed on all {attempts} attempts in {budgetMs} ms and did NOT reach the device: {thrown}");
                return false;
            }
            Thread.Sleep(MustLandGapMs);
        }
        // Worth a line: a terminal write that needed several goes is the early
        // warning for the one that eventually does not land at all.
        if (attempts > 1) Log.Info("write", $"{device}: '{what}' landed on attempt {attempts}");
        return true;
    }

    /// <summary>A whole frame that must land. Dedup is bypassed on EVERY
    /// attempt, not just the first: the device's cache may say it is already
    /// showing this frame while the hardware has since been reset, put to
    /// sleep, or handed back to its firmware, and a deduped "success" would
    /// leave the wrong color lit with nothing in the log.</summary>
    public static bool MustLand(IRgbDevice device, IReadOnlyList<Rgb> frame, string what,
                                int budgetMs = MustLandBudgetMs)
        => MustLand(device.Name, what, () => DeviceHealth.Attempt(device,
            () => { device.InvalidateCache(); return device.SetColors(frame); }), budgetMs);

    /// <summary>The same for one zone. The device is passed alongside the zone
    /// writer because InvalidateCache lives on IRgbDevice and a zone driver's
    /// per-zone caches are dropped with the rest of them.</summary>
    public static bool MustLand(IRgbDevice device, IZoneWritable zones, int offset, IReadOnlyList<Rgb> colors,
                                string what, int budgetMs = MustLandBudgetMs)
        => MustLand(device.Name, what, () => DeviceHealth.Attempt(device,
            () => { device.InvalidateCache(); return zones.SetZone(offset, colors); }), budgetMs);
}
