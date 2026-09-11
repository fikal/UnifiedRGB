using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| SensorHub is a static class over real hardware (LHM ring0,   |
| NvAPI, PawnIO, the Lian Li receiver), so the control loop    |
| itself has no seam a harness can drive. What IS pure is the  |
| wireless-fan re-key that runs when a rescan replaces the     |
| LianLiWireless instance: "old arranged slot -> new arranged  |
| slot with the same chain", applied to the mode dictionaries. |
| That mapping is exactly where a wrong answer moves a curve   |
| onto a different physical fan, so it gets pinned down here.  |
\*-----------------------------------------------------------*/
static class SensorHubSuite
{
    const int B = SensorHub.LianFanBase;

    public static void Run(Harness t)
    {
        t.Section("LianSlotMap: old slot -> new slot by chain");
        // --- LianSlotMap: old slot -> new slot by chain ---
        // Identity layout: nothing moves.
        var same = SensorHub.LianSlotMap(new[] { 0, 1, 2, 3 }, new[] { 0, 1, 2, 3 });
        t.Check(same.SequenceEqual(new[] { 0, 1, 2, 3 }), "slot map: identical layout maps every slot to itself");

        // The user rotated the arrangement: chain 2 was at slot 0, is now at slot 1, etc.
        var rot = SensorHub.LianSlotMap(new[] { 2, 0, 1 }, new[] { 0, 2, 1 });
        t.Check(rot.SequenceEqual(new[] { 1, 0, 2 }), "slot map: re-arranged layout follows the chain, not the slot");

        // A fan left the chain: its old slot maps to -1 (drop), the rest still resolve.
        var gone = SensorHub.LianSlotMap(new[] { 0, 1, 2 }, new[] { 2, 0 });
        t.Check(gone.SequenceEqual(new[] { 1, -1, 0 }), "slot map: a chain that no longer exists maps to -1");

        // A fan joined: old slots resolve, the new one simply has no source.
        var grew = SensorHub.LianSlotMap(new[] { 0, 1 }, new[] { 3, 1, 0 });
        t.Check(grew.SequenceEqual(new[] { 2, 1 }), "slot map: a new chain does not disturb the old slots");

        t.Check(SensorHub.LianSlotMap(Array.Empty<int>(), new[] { 0, 1 }).Length == 0, "slot map: no old slots = empty map");

        t.Section("RekeyLianEntries (dictionary): values move with their chain");
        // --- RekeyLianEntries (dictionary): values move with their chain ---
        // Old layout chains [2,0,1] -> new layout [0,2,1]: slot0->1, slot1->0, slot2->2.
        var map = SensorHub.LianSlotMap(new[] { 2, 0, 1 }, new[] { 0, 2, 1 });
        var d = new Dictionary<int, string>
        {
            [B + 0] = "top",      // chain 2
            [B + 1] = "middle",   // chain 0
            [B + 2] = "bottom",   // chain 1
            [3] = "board fan",    // not a Lian key: must be untouched
            [SensorHub.GpuFanIndex] = "gpu",
        };
        SensorHub.RekeyLianEntries(d, map);
        t.Check(d.Count == 5, "rekey dict: entry count preserved when every chain survives");
        t.Check(d.TryGetValue(B + 1, out var v0) && v0 == "top", "rekey dict: chain 2's mode moved from slot 0 to slot 1");
        t.Check(d.TryGetValue(B + 0, out var v1) && v1 == "middle", "rekey dict: chain 0's mode moved from slot 1 to slot 0");
        t.Check(d.TryGetValue(B + 2, out var v2) && v2 == "bottom", "rekey dict: chain 1's mode stayed at slot 2");
        t.Check(d.TryGetValue(3, out var vb) && vb == "board fan", "rekey dict: board-fan key left alone");
        t.Check(d.TryGetValue(SensorHub.GpuFanIndex, out var vg) && vg == "gpu", "rekey dict: GPU key left alone");

        // Swapping two fans must be a SWAP, not a clobber: with a naive
        // move-in-place, writing slot 0's value into slot 1 before slot 1's
        // value has been read would lose one of them.
        var swap = SensorHub.LianSlotMap(new[] { 0, 1 }, new[] { 1, 0 });
        var sd = new Dictionary<int, int> { [B + 0] = 90, [B + 1] = 40 };
        SensorHub.RekeyLianEntries(sd, swap);
        t.Check(sd.Count == 2 && sd[B + 0] == 40 && sd[B + 1] == 90, "rekey dict: swapping two slots keeps both values");

        // A chain that vanished takes its entry with it (no orphan at the old slot).
        var dropMap = SensorHub.LianSlotMap(new[] { 0, 1, 2 }, new[] { 2, 0 });   // chain 1 gone
        var dd = new Dictionary<int, int> { [B + 0] = 50, [B + 1] = 60, [B + 2] = 70 };
        SensorHub.RekeyLianEntries(dd, dropMap);
        t.Check(dd.Count == 2 && dd[B + 1] == 50 && dd[B + 0] == 70 && !dd.ContainsKey(B + 2),
            "rekey dict: entry for a vanished chain is dropped, the others follow their chain");

        // An old slot the map never had (entry keyed beyond the old instance's
        // fan count) is dropped rather than throwing.
        var short_ = new Dictionary<int, int> { [B + 5] = 1 };
        SensorHub.RekeyLianEntries(short_, new[] { 0, 1 });
        t.Check(short_.Count == 0, "rekey dict: a slot beyond the old fan count is dropped, not thrown on");

        t.Section("RekeyLianEntries (set): same rules for the membership sets");
        // --- RekeyLianEntries (set): same rules for the membership sets ---
        var hs = new HashSet<int> { B + 0, B + 2, 7 };
        SensorHub.RekeyLianEntries(hs, map);   // slot0->1, slot2->2
        t.Check(hs.SetEquals(new[] { B + 1, B + 2, 7 }), "rekey set: members follow their chain, non-Lian member untouched");

        var hsDrop = new HashSet<int> { B + 1 };
        SensorHub.RekeyLianEntries(hsDrop, dropMap);   // chain 1 (old slot 1) gone
        t.Check(hsDrop.Count == 0, "rekey set: member for a vanished chain is dropped");

        // Idempotence on a re-run against an identity map (a second rekey after
        // the instance settles must not shuffle anything).
        var idm = SensorHub.LianSlotMap(new[] { 0, 2, 1 }, new[] { 0, 2, 1 });
        var before = new Dictionary<int, string>(d);
        SensorHub.RekeyLianEntries(d, idm);
        t.Check(d.Count == before.Count && before.All(kv => d.TryGetValue(kv.Key, out var x) && x == kv.Value),
            "rekey dict: identity map is a no-op");
    }
}
