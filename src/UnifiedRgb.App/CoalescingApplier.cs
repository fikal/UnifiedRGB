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
        public readonly Dictionary<object, Action> Pending = new();
        public bool Running;
    }

    readonly object _mapLock = new();
    readonly Dictionary<object, Lane> _lanes = new();

    /// <summary>Queue a device write. Coalescing is latest-wins PER KEY (one
    /// key per device/zone); laneKey picks the worker — writes on different
    /// lanes run in parallel, writes on one lane run in order.</summary>
    public void Post(object laneKey, object key, Action work)
    {
        Lane? lane;
        lock (_mapLock)
            if (!_lanes.TryGetValue(laneKey, out lane))
                _lanes[laneKey] = lane = new Lane();

        lock (lane.Lock)
        {
            lane.Pending[key] = work;
            if (lane.Running) return;
            lane.Running = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                Action job;
                lock (lane.Lock)
                {
                    if (lane.Pending.Count == 0) { lane.Running = false; return; }
                    var first = lane.Pending.First();
                    lane.Pending.Remove(first.Key);
                    job = first.Value;
                }
                try { job(); }
                catch (Exception ex)
                {
                    // Keep the UI alive, but a failing write must be visible.
                    UnifiedRgb.Core.Log.Occasional("applier", "applier", $"device write failed: {ex.Message}");
                }
            }
        });
    }
}
