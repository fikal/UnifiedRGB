using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>Runs device writes on background workers with latest-wins
/// coalescing, so dragging the wheel never blocks the UI and never queues a
/// backlog of stale frames.
///
/// Writes are spread across parallel LANES so every device starts its write
/// at the same moment — a single shared worker made slow writers (DRAM over
/// SMBus) visibly lag the fast ones on profile flips. Devices that share a
/// transport (both DRAM sticks on one SMBus, all OpenRGB devices on one
/// socket) must share a lane: the lane serializes them so concurrent
/// transactions can't interleave on the wire.</summary>
public sealed class CoalescingApplier
{
    sealed class Lane
    {
        public readonly object Lock = new();
        // Insertion-ordered: a plain Dictionary recycles freed slots, so after
        // the worker removed the running job a NEWER key could enumerate ahead
        // of an older pending one and break the lane's in-order promise.
        public readonly OrderedDictionary<object, Action> Pending = new();
        public bool Running;
    }

    readonly object _mapLock = new();
    readonly Dictionary<object, Lane> _lanes = new();

    /// <summary>Drop every idle lane. Native devices key their lane by instance,
    /// so without this each Rescan left a lane per replaced device behind (and
    /// pinned the disposed device with it) for the process lifetime.</summary>
    public void PruneIdle()
    {
        lock (_mapLock)
        {
            var idle = new List<object>();
            foreach (var (key, lane) in _lanes)
                lock (lane.Lock) if (!lane.Running && lane.Pending.Count == 0) idle.Add(key);
            foreach (var key in idle) _lanes.Remove(key);
        }
    }

    /// <summary>Block until every lane has run dry (or the timeout passes).
    /// Call before disposing devices: a queued static write landing on a freed
    /// handle was a "device write failed" on every Rescan and at exit.</summary>
    public void Drain(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            bool busy = false;
            lock (_mapLock)
                foreach (var lane in _lanes.Values)
                    lock (lane.Lock) if (lane.Running) { busy = true; break; }
            if (!busy) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Queue a device write. Coalescing is latest-wins PER KEY (one
    /// key per device/zone); laneKey picks the worker — writes on different
    /// lanes run in parallel, writes on one lane run in order.
    ///
    /// A re-post of a key that is already pending replaces the job IN PLACE:
    /// it keeps the old job's position in the queue, and that is deliberate -
    /// it is what keeps the lane's in-order promise for jobs posted later
    /// under OTHER keys. It is also a trap for a caller whose keys overlap on
    /// the hardware: a whole-device job re-posted in place runs BEFORE a
    /// partial that was posted after its first version, so the stale partial
    /// paints over the newer whole frame. The rule for callers is therefore
    /// one accumulated frame per device (LightingController.PushExternalFrame)
    /// or moveToEnd for overlapping static ranges that retain separate keys
    /// to avoid disturbing effects on sibling zones.</summary>
    /// <param name="moveToEnd">For overlapping static ranges, place a
    /// replacement after intervening writes so the newest intent wins.</param>
    public void Post(object laneKey, object key, Action work, bool moveToEnd = false)
    {
        Lane? lane;
        bool start;
        lock (_mapLock)
        {
            if (!_lanes.TryGetValue(laneKey, out lane))
                _lanes[laneKey] = lane = new Lane();
            // Enqueue while still holding the map lock (same nesting order as
            // PruneIdle/Drain): a lane with a pending job or a running worker
            // can then never be pruned out from under a Post that already
            // looked it up - an orphan lane Drain would not see.
            lock (lane.Lock)
            {
                if (moveToEnd) lane.Pending.Remove(key);
                lane.Pending[key] = work;
                start = !lane.Running;
                if (start) lane.Running = true;
            }
        }
        if (!start) return;
        Task.Run(() =>
        {
            while (true)
            {
                Action job;
                object jobKey;
                lock (lane.Lock)
                {
                    if (lane.Pending.Count == 0) { lane.Running = false; return; }
                    var first = lane.Pending.GetAt(0);   // oldest key first
                    lane.Pending.Remove(first.Key);
                    job = first.Value;
                    jobKey = first.Key;
                }
                try { job(); }
                catch (Exception ex)
                {
                    // Keep the UI alive, but a failing write must be visible AND
                    // attributable. The keys are mostly device OBJECTS (alone or
                    // inside a tuple), and formatting one directly printed its
                    // type name: two Razer mice logged identically and, worse,
                    // shared one rate-limit bucket, so the one failing every
                    // frame muted the other for a minute at a time. Described by
                    // name, element by element, both the line and the bucket
                    // say which device and which write.
                    string what = $"{Describe(laneKey)} ({Describe(jobKey)})";
                    UnifiedRgb.Core.Log.Occasional($"applier:{what}", "lighting",
                        $"write failed on {what}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>A lane or job key as a person would name it: a device by its
    /// Name, a tuple element by element, anything else as itself. Only ever
    /// called on the failure path.</summary>
    internal static string Describe(object? key) => key switch
    {
        null => "null",
        IRgbDevice d => d.Name,
        System.Runtime.CompilerServices.ITuple tuple => DescribeTuple(tuple),
        _ => key.ToString() ?? "",
    };

    static string DescribeTuple(System.Runtime.CompilerServices.ITuple tuple)
    {
        var parts = new string[tuple.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = Describe(tuple[i]);
        return "(" + string.Join(", ", parts) + ")";
    }
}
