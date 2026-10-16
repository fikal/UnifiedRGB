using System.Text.Json.Nodes;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| Moving per-device data onto this machine's device names.     |
|                                                              |
| Everything an import re-keys - a profile's frames, zone      |
| layouts and effects, the desk layout's placements, the       |
| calibration trims - goes through ONE policy, decided per     |
| key. There used to be three, and they disagreed: the         |
| preview told the user "the mapped device wins" about a stale |
| entry, and Apply then refused the whole import over that     |
| same entry, in a profile the user had not even ticked.       |
|                                                              |
| For one table, every key lands somewhere or is dropped:      |
|  - a mapped key lands on its target, and mapped keys claim   |
|    their targets first;                                      |
|  - two mapped keys on one target is a contradiction;         |
|  - an UNMAPPED key already sitting on a claimed target is    |
|    either a device the bundle really has (the manifest lists |
|    it), so two devices' lighting would land on one: a        |
|    contradiction; or a leftover for a device that is gone (a |
|    renamed keyboard leaves its old entry behind): stale,     |
|    dropped, and said.                                        |
| A contradiction refuses the import. Nothing is guessed.      |
\*-----------------------------------------------------------*/

/// <summary>A device mapping that contradicts itself. Its message is a
/// sentence for the user.</summary>
internal sealed class RemapConflictException(string message) : InvalidOperationException(message);

/// <summary>The device mapping an import uses, with the one collision policy
/// every re-keyed table goes through.</summary>
internal sealed class DeviceRemap
{
    readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _bundleDevices = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="map">bundle device name -> this machine's device name. A
    /// name mapped onto itself is not a mapping and is ignored; for a name
    /// given twice the first wins.</param>
    /// <param name="bundleDevices">The device names the bundle's manifest
    /// records as attached to the machine that made it.</param>
    internal DeviceRemap(IEnumerable<KeyValuePair<string, string>> map, IEnumerable<string> bundleDevices)
    {
        foreach (var kv in map)
            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)
                && !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                _map.TryAdd(kv.Key, kv.Value);
        foreach (string name in bundleDevices) _bundleDevices.Add(name ?? "");
    }

    /// <summary>The mapping for one previewed bundle, judged against the
    /// devices that bundle recorded.</summary>
    internal static DeviceRemap For(ImportPreview preview, IEnumerable<KeyValuePair<string, string>> map)
        => new(map, (preview.Manifest?.Devices ?? new()).Where(d => d != null).Select(d => d.Name ?? ""));

    internal bool IsEmpty => _map.Count == 0;

    /// <summary>What happens to every key of one table.</summary>
    internal sealed class Decision
    {
        /// <summary>key -> the name it lands under. A key absent here is
        /// dropped.</summary>
        internal Dictionary<string, string> Target { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keys dropped as leftovers of a device that is gone.</summary>
        internal List<string> Stale { get; } = new();

        /// <summary>Why the mapping cannot be applied to this table, or null.</summary>
        internal string? Conflict { get; set; }
    }

    /// <summary>Decide every key of one table. Keys are compared ignoring case,
    /// as the stores compare device names.</summary>
    internal Decision Decide(IEnumerable<string?> keys)
    {
        var d = new Decision();
        var distinct = keys.Select(k => k ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // landing name -> the key that took it
        // Mapped keys first, so a stale leftover can never beat the device the
        // user (or the identity match) pointed at that name.
        foreach (string key in distinct.Where(k => _map.ContainsKey(k)))
        {
            string to = _map[key];
            if (owner.TryGetValue(to, out string? first))
            {
                d.Conflict ??= $"'{first}' and '{key}' are both mapped to '{to}'";
                continue;
            }
            owner[to] = key;
            d.Target[key] = to;
        }
        foreach (string key in distinct.Where(k => !_map.ContainsKey(k)))
        {
            if (owner.TryGetValue(key, out string? mapped))
            {
                if (_bundleDevices.Contains(key))
                    d.Conflict ??= $"'{mapped}' is mapped to '{key}', which has lighting of its own in the bundle";
                else
                    d.Stale.Add(key);
                continue;
            }
            owner[key] = key;
            d.Target[key] = key;
        }
        return d;
    }

    /// <summary>Rewrite a profile's device keys onto this machine's names: its
    /// frames, its zone layouts and its effects, decided together so the three
    /// cannot disagree about one device. The zone layout travels with the frame
    /// under the same key because the apply looks both up by the CURRENT device
    /// name; a frame moved with its layout left behind falls back to raw LED
    /// numbers on changed hardware.</summary>
    /// <returns>Null when it went through (a dropped leftover is added to
    /// <paramref name="notes"/>), else the contradiction.</returns>
    internal string? Apply(Profile profile, List<string> notes)
    {
        if (IsEmpty) return null;
        var effects = profile.Effects ?? new();
        var d = Decide(profile.DeviceFrames.Keys.Concat(profile.DeviceZones.Keys).Concat(effects.Select(e => e.Device)));
        if (d.Conflict != null) return $"profile '{profile.Name}': {d.Conflict}";
        var dropped = new List<string>(d.Stale);
        profile.DeviceFrames = Rekey(profile.DeviceFrames, d, dropped);
        profile.DeviceZones = Rekey(profile.DeviceZones, d, dropped);
        effects.RemoveAll(e => !d.Target.ContainsKey(e.Device ?? ""));
        foreach (var e in effects) e.Device = d.Target[e.Device ?? ""];
        if (dropped.Count > 0)
            notes.Add($"profile '{profile.Name}': its stale entry for {Names(dropped)} is dropped, the mapped device wins");
        return null;
    }

    /// <summary>The desk layout's placements, by the same rule.</summary>
    internal string? Apply(CanvasLayout layout, List<string> notes)
    {
        if (IsEmpty) return null;
        var d = Decide(layout.Items.Select(i => i.Device));
        if (d.Conflict != null) return $"desk layout: {d.Conflict}";
        int stale = layout.Items.RemoveAll(i => !d.Target.ContainsKey(i.Device ?? ""));
        foreach (var i in layout.Items) i.Device = d.Target[i.Device ?? ""];
        if (stale > 0)
            notes.Add($"desk layout: {stale} stale placement(s) of {Names(d.Stale)} are dropped, the mapped placement wins");
        return null;
    }

    /// <summary>calibration.json's per-device and per-zone trims, as JSON
    /// nodes so a field this build does not know rides through untouched. Both
    /// sections are keyed by device name and are decided together.</summary>
    internal string? Apply(JsonObject calibration, List<string> notes)
    {
        if (IsEmpty) return null;
        var sections = new[] { "Devices", "Zones" }
            .Select(name => (Name: name, Entries: calibration[name] as JsonObject))
            .Where(s => s.Entries != null)
            .ToList();
        var d = Decide(sections.SelectMany(s => s.Entries!.Select(kv => kv.Key)));
        if (d.Conflict != null) return $"calibration: {d.Conflict}";
        var dropped = new List<string>(d.Stale);
        foreach (var (name, entries) in sections)
        {
            var mapped = new JsonObject();
            foreach (var kv in entries!)
            {
                if (!d.Target.TryGetValue(kv.Key, out string? to)) continue;
                if (mapped.ContainsKey(to)) { dropped.Add(kv.Key); continue; }   // a second spelling of one device; the first is kept
                mapped[to] = kv.Value?.DeepClone();
            }
            calibration[name] = mapped;
        }
        if (dropped.Count > 0)
            notes.Add($"calibration: the stale trims for {Names(dropped)} are dropped, the mapped device wins");
        return null;
    }

    /// <summary>One per-device table re-keyed by a decision. A key the
    /// decision drops is left out; a second spelling of a key ("Keeb" and
    /// "keeb" in a hand-edited file) keeps the first and is reported with the
    /// stale ones.</summary>
    static Dictionary<string, T> Rekey<T>(Dictionary<string, T> source, Decision d, List<string> dropped)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            if (!d.Target.TryGetValue(kv.Key ?? "", out string? to)) continue;
            if (!result.TryAdd(to, kv.Value)) dropped.Add(kv.Key ?? "");
        }
        return result;
    }

    static string Names(IEnumerable<string> names)
        => string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => $"'{n}'"));
}
