using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The preview's half: what importing WOULD do, item by item,   |
| compared against the user's own files. Every read of those   |
| files goes through CurrentSetup, which only looks, and every |
| device key through DeviceRemap, the same policy Apply uses - |
| so the preview describes the import Apply would perform.     |
\*-----------------------------------------------------------*/

public static partial class SetupBundle
{
    static void BuildDeviceMappings(ImportPreview p, BundleManifest manifest, IReadOnlyList<DeviceFingerprint> local)
    {
        var rows = (manifest.Devices ?? new()).Where(d => d != null).Select(d => new DeviceMapping
        {
            BundleName = d.Name ?? "",
            BundleIdentity = d.Identity ?? "",
            BundleDescription = d.Describe(),
        }).ToList();
        var reservedNames = new HashSet<string>(rows.Select(m => m.BundleName), StringComparer.OrdinalIgnoreCase);
        var byIdentity = new Dictionary<string, DeviceFingerprint>(StringComparer.Ordinal);
        foreach (var f in local) if (!byIdentity.ContainsKey(f.Identity)) byIdentity[f.Identity] = f;
        // Every local device of a name, not the first: names are not unique.
        var byName = new Dictionary<string, List<DeviceFingerprint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in local)
        {
            if (!byName.TryGetValue(f.Name, out var same)) byName[f.Name] = same = new();
            same.Add(f);
        }
        var taken = new HashSet<DeviceFingerprint>(ReferenceEqualityComparer.Instance);
        var remapTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exact names first, for every row, before any identity is consulted:
        // reserving them is what stops an absent twin stealing the surviving
        // twin whose ordinal moved down.
        //
        // One local DEVICE per row, not one row per name. Two devices can share
        // a name - two identical keyboards, two RAM kits the driver names alike -
        // and the name used to be spent by the first of them, so the second fell
        // through to the identity match, found its twin's name reserved, and was
        // reported "not attached" while it sat right there. Paired by identity
        // first (it numbers identical devices in name order on both machines),
        // then in order, so the pairing comes out the same every time.
        for (int pass = 0; pass < 2; pass++)
            foreach (var m in rows)
            {
                if (m.LocalName != null || !byName.TryGetValue(m.BundleName, out var same)) continue;
                var pick = same.FirstOrDefault(f => !taken.Contains(f)
                                                    && (pass == 1 || string.Equals(f.Identity, m.BundleIdentity, StringComparison.Ordinal)));
                if (pick == null) continue;
                taken.Add(pick);
                m.LocalName = pick.Name;
                m.Detail = "present on this machine (matched by name)";
            }

        foreach (var m in rows)
        {
            if (m.LocalName != null) { p.Devices.Add(m); continue; }
            if (byName.TryGetValue(m.BundleName, out var namesake))
            {
                // More devices of this name in the bundle than here. A profile
                // keeps one entry per name, so this one's lighting IS its
                // namesake's, and it lands on the device of that name. Not
                // remapped onto a differently named twin found by identity: that
                // would move the one entry off the device the name already reached.
                m.LocalName = namesake[0].Name;
                m.Detail = $"shares the name \"{m.BundleName}\" with another device in the bundle, and this machine has "
                         + "fewer devices of that name - its lighting is that name's, which lands on the one here";
            }
            else if (m.BundleIdentity.Length > 0 && byIdentity.TryGetValue(m.BundleIdentity, out var hit)
                && !taken.Contains(hit) && !reservedNames.Contains(hit.Name) && remapTargets.Add(hit.Name))
            {
                taken.Add(hit);
                m.LocalName = hit.Name;
                m.Detail = m.IsRemap
                    ? $"matched by hardware to \"{hit.Name}\" on this machine, despite the different name"
                    : "present on this machine";
            }
            else
            {
                m.Detail = "not attached to this machine - its lighting is imported unchanged and comes back if you plug it in";
            }
            p.Devices.Add(m);
        }
    }

    static void DiffProfiles(ImportPreview p, DeviceRemap remap)
    {
        var incoming = BundleProfiles(p, remap, only: null, notes: p.Warnings, strict: false);
        if (incoming == null) return;
        var current = CurrentSetup.Profiles(forApply: false, out string? trouble);
        if (trouble != null) p.Warnings.Add(trouble);
        foreach (var prof in incoming)
        {
            var mine = current.FirstOrDefault(x => x.Name.Equals(prof.Name, StringComparison.OrdinalIgnoreCase));
            var item = new ImportItem { Name = prof.Name };
            if (mine == null)
            {
                item.Status = ImportStatus.New;
                item.Detail = $"new profile ({prof.DeviceFrames.Count} device(s))";
            }
            else if (SameProfile(mine, prof))
            {
                item.Status = ImportStatus.Same;
                item.Detail = "you already have this profile, unchanged";
            }
            else
            {
                item.Status = ImportStatus.Differs;
                item.Detail = "a DIFFERENT profile of this name already exists here; importing replaces yours";
            }
            p.Profiles.Add(item);
        }
    }

    /// <summary>Same profile in every way a user would notice. Not a JSON
    /// comparison: DeviceFrames is a dictionary, and two identical profiles
    /// can serialize their devices in different orders.</summary>
    static bool SameProfile(Profile a, Profile b)
    {
        if (!string.Equals(a.Screen ?? "", b.Screen ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Show ?? "", b.Show ?? "", StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Wallpaper ?? "", b.Wallpaper ?? "", StringComparison.Ordinal)) return false;
        if (!SameSequence(a.CustomColors, b.CustomColors)) return false;
        if (a.DeviceFrames.Count != b.DeviceFrames.Count) return false;
        foreach (var kv in a.DeviceFrames)
        {
            if (!b.DeviceFrames.TryGetValue(kv.Key, out string[]? other)) return false;
            if (!SameSequence(kv.Value, other)) return false;
        }
        // The layout decides where those frames and effects LAND on hardware
        // that has changed, so two profiles that differ only here are not the
        // same profile: one of them puts the colours on the right LEDs.
        if (a.DeviceZones.Count != b.DeviceZones.Count) return false;
        foreach (var kv in a.DeviceZones)
        {
            if (!b.DeviceZones.TryGetValue(kv.Key, out ZoneSpan[]? other)) return false;
            if (!SameZones(kv.Value, other)) return false;
        }
        var ea = a.Effects ?? new();
        var eb = b.Effects ?? new();
        if (ea.Count != eb.Count) return false;
        for (int i = 0; i < ea.Count; i++)
            if (JsonSerializer.Serialize(ea[i]) != JsonSerializer.Serialize(eb[i])) return false;
        return true;
    }

    static bool SameSequence(string[]? a, string[]? b)
    {
        if (a == null || b == null) return (a?.Length ?? 0) == (b?.Length ?? 0);
        return a.Length == b.Length && !a.Where((v, i) => !string.Equals(v, b[i], StringComparison.OrdinalIgnoreCase)).Any();
    }

    static bool SameZones(ZoneSpan[]? a, ZoneSpan[]? b)
    {
        if (a == null || b == null) return (a?.Length ?? 0) == (b?.Length ?? 0);
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (x == null || y == null) { if (x != y) return false; continue; }
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal) || x.Offset != y.Offset || x.Count != y.Count) return false;
        }
        return true;
    }

    static void DiffScenes(ImportPreview p)
    {
        if (p.Texts.TryGetValue($"{RootConfig}/scenes.json", out string? text))
        {
            var store = SceneStore.TryParse(text);
            if (store == null) p.Warnings.Add("the bundle's screens could not be read and will be skipped");
            else
            {
                // CurrentSetup, not SceneStore.Load: see CurrentSetup for what
                // the store's loader did to a file that was unreadable at startup.
                var mine = CurrentSetup.Scenes(forApply: false, out string? trouble);
                if (trouble != null) p.Warnings.Add(trouble);
                foreach (var scene in store.Scenes)
                {
                    var here = mine.Scenes.FirstOrDefault(x => x.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
                    var item = new ImportItem { Name = scene.Name };
                    if (here == null) { item.Status = ImportStatus.New; item.Detail = "new screen"; }
                    else if (SameDesign(here.Design, scene.Design, p)) { item.Status = ImportStatus.Same; item.Detail = "you already have this screen, unchanged"; }
                    else { item.Status = ImportStatus.Differs; item.Detail = "a DIFFERENT screen of this name already exists here; importing replaces yours"; }
                    p.Screens.Add(item);
                }
                foreach (var seq in store.Sequences)
                {
                    var here = mine.Sequences.FirstOrDefault(x => x.Name.Equals(seq.Name, StringComparison.OrdinalIgnoreCase));
                    var item = new ImportItem { Name = seq.Name };
                    if (here == null) { item.Status = ImportStatus.New; item.Detail = $"new show ({seq.Actions.Count} step(s))"; }
                    else if (JsonSerializer.Serialize(here) == JsonSerializer.Serialize(seq)) { item.Status = ImportStatus.Same; item.Detail = "unchanged"; }
                    else { item.Status = ImportStatus.Differs; item.Detail = "a DIFFERENT show of this name already exists here"; }
                    p.Sequences.Add(item);
                }
            }
        }

        if (p.Texts.TryGetValue($"{RootConfig}/lcd.json", out string? lcdText))
        {
            LcdDesign? incoming = null;
            try { incoming = LcdDesign.Normalize(JsonSerializer.Deserialize<LcdDesign>(lcdText)); }
            catch (Exception ex) { p.Warnings.Add($"the bundle's current screen could not be read ({ex.Message})"); }
            if (incoming != null)
            {
                // "Is there a live screen here at all" is a question about the
                // FILE, not about the design: calling the bundle's screen a
                // conflict with a built-in default would ask the user to choose
                // between it and a screen they never made.
                var mine = CurrentSetup.LiveDesign(forApply: false, out bool haveOne, out string? trouble);
                if (trouble != null) p.Warnings.Add(trouble);
                bool same = haveOne && mine != null && SameDesign(mine, incoming, p);
                p.CurrentScreen = new ImportItem
                {
                    Name = incoming.SceneName ?? "(the screen that was on the panel)",
                    Status = !haveOne ? ImportStatus.New : same ? ImportStatus.Same : ImportStatus.Differs,
                    Detail = !haveOne ? "sets the screen on the panel; you have none saved yet"
                           : same ? "the panel already shows this"
                           : "replaces what is currently on the panel (your saved screens are not touched)",
                };
            }
        }
    }

    /// <summary>Compare elements, placement and background content. Paths may
    /// differ across machines, but different picture bytes are a conflict.</summary>
    static bool SameDesign(LcdDesign a, LcdDesign b, ImportPreview preview)
    {
        a = LcdDesign.Normalize(a);
        b = LcdDesign.Normalize(b);
        if (a.Elements.Count != b.Elements.Count) return false;
        for (int i = 0; i < a.Elements.Count; i++)
            if (JsonSerializer.Serialize(a.Elements[i]) != JsonSerializer.Serialize(b.Elements[i])) return false;
        bool oneHasImage = !string.IsNullOrWhiteSpace(a.BackgroundImagePath);
        bool otherHasImage = !string.IsNullOrWhiteSpace(b.BackgroundImagePath);
        if (oneHasImage != otherHasImage) return false;
        if (oneHasImage)
        {
            var asset = preview.Manifest?.Assets.FirstOrDefault(x => x != null &&
                string.Equals(x.OriginalPath, b.BackgroundImagePath, StringComparison.OrdinalIgnoreCase));
            if (asset == null || !preview.AssetBytes.TryGetValue(asset.Path, out var bytes)) return false;
            try { if (!File.ReadAllBytes(a.BackgroundImagePath!).AsSpan().SequenceEqual(bytes)) return false; }
            catch { return false; }   // unreadable on disk: treat as different, which is the safe answer
        }
        return a.BgX == b.BgX && a.BgY == b.BgY && a.BgW == b.BgW && a.BgH == b.BgH;
    }

    static void DiffCanvas(ImportPreview p, DeviceRemap remap)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/canvas.json", out string? text)) return;
        var incoming = CanvasLayout.TryParse(text);
        if (incoming == null)
        {
            p.Warnings.Add("the bundle's desk layout could not be read and will be skipped");
            return;
        }
        string? conflict = remap.Apply(incoming, p.Warnings);
        if (conflict != null) p.Warnings.Add($"{conflict}; choose distinct target devices before importing it");
        // CurrentSetup, not CanvasLayout.Load: Load copies a damaged file aside.
        var mine = CurrentSetup.Canvas(forApply: false, out string? trouble);
        if (trouble != null) p.Warnings.Add(trouble);
        var item = new ImportItem { Name = "Desk layout" };
        if (mine.Items.Count == 0)
        {
            item.Status = ImportStatus.New;
            item.Detail = $"places {incoming.Items.Count} device(s) on the desk; you have no layout yet";
        }
        else if (JsonSerializer.Serialize(mine) == JsonSerializer.Serialize(incoming))
        {
            item.Status = ImportStatus.Same;
            item.Detail = "identical to your current layout";
        }
        else
        {
            item.Status = ImportStatus.Differs;
            item.Detail = $"REPLACES your desk layout ({mine.Items.Count} device(s) placed) with the bundle's ({incoming.Items.Count})";
        }
        p.Canvas = item;
    }

    static void DiffSettings(ImportPreview p)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/settings.json", out string? text)) return;
        SettingsData? incoming;
        JsonObject? incomingNodes;
        try
        {
            incoming = JsonSerializer.Deserialize<SettingsData>(text);
            incomingNodes = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex)
        {
            p.Warnings.Add($"the bundle's settings could not be read ({ex.Message}) and will be skipped");
            return;
        }
        if (incoming == null || incomingNodes == null) return;

        // CurrentSetup, not ProfileStore.LoadJson: this is the PREVIEW, and a
        // dialog the user may cancel must not leave a .corrupt- copy behind or
        // change whether saves to the file are allowed.
        var mineNodes = CurrentSetup.SettingsNodes(forApply: false, out string? trouble);
        if (trouble != null) p.Warnings.Add(trouble);
        SettingsData mine;
        try { mine = mineNodes.Deserialize<SettingsData>() ?? new SettingsData(); }
        catch (Exception ex)
        {
            mine = new SettingsData();
            p.Warnings.Add($"your settings.json could not be read ({ex.Message}), so it is compared as if it were empty");
        }

        foreach (var pal in incoming.SavedPalettes ?? new())
        {
            if (pal == null || string.IsNullOrWhiteSpace(pal.Name)) continue;
            var here = (mine.SavedPalettes ?? new()).FirstOrDefault(x => x != null && x.Name.Equals(pal.Name, StringComparison.OrdinalIgnoreCase));
            var item = new ImportItem { Name = pal.Name };
            if (here == null) { item.Status = ImportStatus.New; item.Detail = $"new palette ({pal.Colors?.Length ?? 0} colors)"; }
            else if (SameSequence(here.Colors, pal.Colors)) { item.Status = ImportStatus.Same; item.Detail = "you already have this palette"; }
            else { item.Status = ImportStatus.Differs; item.Detail = "a DIFFERENT palette of this name already exists here"; }
            p.Palettes.Add(item);
        }

        int rules = (incoming.AutomationRules?.Count ?? 0) + (incoming.Schedules?.Count ?? 0) + (incoming.SensorRules?.Count ?? 0);
        int myRules = (mine.AutomationRules?.Count ?? 0) + (mine.Schedules?.Count ?? 0) + (mine.SensorRules?.Count ?? 0);
        if (rules > 0 || myRules > 0)
        {
            p.Rules = new ImportItem
            {
                Name = "Rules",
                Status = myRules == 0 ? ImportStatus.New
                       : SameRules(mine, incoming) ? ImportStatus.Same : ImportStatus.Differs,
                Detail = myRules == 0
                    ? $"{rules} rule(s) to add: app switching, schedules and sensor thresholds"
                    : $"REPLACES your {myRules} rule(s) with the bundle's {rules}",
            };
        }

        // Exactly the keys Apply would copy, so "already the same" is the
        // truth about what ticking it would change. This row used to read as
        // an overwrite even on a backup of this very machine.
        bool changes = PreferenceKeysFor(p).Any(k => incomingNodes.TryGetPropertyValue(k, out var theirs)
                                                    && !JsonNode.DeepEquals(theirs, mineNodes[k]));
        p.Preferences = new ImportItem
        {
            Name = "Preferences",
            Status = changes ? ImportStatus.Differs : ImportStatus.Same,
            Detail = PreferencesDetail(p, changes),
        };
    }

    /// <summary>What the Preferences row takes, and - named, with the reason -
    /// what it leaves out. The fan settings come from a backup of this machine
    /// only (MachinePreferenceKeys), so the row has to say which case this is:
    /// leaving them out of a restore of your own backup without a word is how
    /// the hand-off came to be switched off with nobody the wiser.</summary>
    static string PreferencesDetail(ImportPreview p, bool changes)
    {
        const string fans = "the fan hand-off on exit, fan names and the Lian Li fan settings";
        const string never = " Never taken from any bundle: window position, disabled devices and the startup profile, "
                           + "which belong to this install, and the update, OpenRGB, SDK server and CS2 switches, "
                           + "which open ports or start programs.";
        if (p.SameMachine)
            return changes
                ? $"brightness, starred effects and start minimized, and {fans}, because this is a backup of this machine.{never}"
                : $"your brightness, starred effects, start-minimized setting and fan settings already match this backup's";
        string where = string.IsNullOrWhiteSpace(p.Manifest?.MachineName) ? "another machine" : p.Manifest!.MachineName;
        string leftOut = $" Left out because the bundle was made on {where}, not this machine: {fans}, which set that machine's fans.";
        return changes
            ? $"brightness, starred effects and start minimized.{leftOut}{never}"
            : $"your brightness, starred effects and start-minimized setting already match the bundle's.{leftOut}";
    }

    static bool SameRules(SettingsData a, SettingsData b)
        => JsonSerializer.Serialize(a.AutomationRules ?? new()) == JsonSerializer.Serialize(b.AutomationRules ?? new())
        && JsonSerializer.Serialize(a.Schedules ?? new()) == JsonSerializer.Serialize(b.Schedules ?? new())
        && JsonSerializer.Serialize(a.SensorRules ?? new()) == JsonSerializer.Serialize(b.SensorRules ?? new());

    static void DiffMachine(ImportPreview p)
    {
        var names = MachineEntries(p).Select(e => e.Name).ToList();
        if (names.Count == 0) return;

        bool anyDifferent = false;
        foreach (var e in MachineEntries(p))
        {
            string? target = TargetPathFor(e);
            if (target == null) continue;
            // Unreadable counts as "" and therefore as different, so the import
            // writes rather than skipping a file it could not compare.
            if (!string.Equals(CurrentSetup.RawText(target), p.Texts[KeyOf(e)], StringComparison.Ordinal)) { anyDifferent = true; break; }
        }

        p.MachineHardware = new ImportItem
        {
            Name = "Machine wiring",
            Status = anyDifferent ? ImportStatus.Differs : ImportStatus.Same,
            Detail = p.SameMachine
                ? $"header layout, fan curves and hub layouts ({string.Join(", ", names)}) from a backup of THIS machine"
                : $"header layout, fan curves and hub layouts ({string.Join(", ", names)}) from {p.Manifest?.MachineName}. "
                  + "These describe that machine's wiring, so leave this off unless the two are built the same.",
        };
    }

    static IEnumerable<BundleEntry> MachineEntries(ImportPreview p)
        => (p.Manifest?.Entries ?? new()).Where(e => e != null && e.Group == "machine" && p.Texts.ContainsKey(KeyOf(e)));

    /// <summary>The preview in sentences. Ordered the way a person reads it:
    /// where the bundle came from, what is new, what would be overwritten,
    /// then the hardware that will not line up.</summary>
    static void WriteSummary(ImportPreview p)
    {
        var m = p.Manifest!;
        // A hand-made or older manifest can leave the date out entirely, and
        // "0001-01-01" reads as a bug rather than as "not recorded".
        string when = m.CreatedUtc == default ? "an unknown date" : m.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        p.Summary.Add($"Made by UnifiedRGB {m.AppVersion} on {m.MachineName} at {when}"
                    + (p.SameMachine ? " (this machine)" : ""));

        Count(p.Profiles, "profile", "profiles");
        Count(p.Screens, "screen", "screens");
        Count(p.Sequences, "show", "shows");
        Count(p.Palettes, "palette", "palettes");

        if (p.CurrentScreen is { Status: ImportStatus.Differs })
            p.Summary.Add("The screen currently on the panel would be replaced.");
        if (p.Canvas != null)
            p.Summary.Add(p.Canvas.Status switch
            {
                ImportStatus.New => "A desk layout would be added (you have none).",
                ImportStatus.Same => "The desk layout is identical to yours.",
                _ => "The desk layout would be REPLACED.",
            });
        if (p.AssetBytes.Count > 0)
            p.Summary.Add($"{p.AssetBytes.Count} screen background image(s) travel with this bundle and will be copied into your config folder.");

        int missing = p.Devices.Count(d => d.LocalName == null);
        int remapped = p.Devices.Count(d => d.IsRemap);
        if (p.Devices.Count == 0)
            p.Summary.Add("The bundle records no devices, so lighting can only be matched by device name.");
        else
        {
            if (remapped > 0)
                p.Summary.Add($"{remapped} device(s) are named differently here and will be remapped by hardware identity.");
            if (missing > 0)
                p.Summary.Add($"{missing} device(s) in the bundle are not attached to this machine. "
                            + "Their lighting is kept as-is and comes back if you plug them in.");
        }

        foreach (string w in p.Warnings) p.Summary.Add("Note: " + w);

        void Count(List<ImportItem> items, string one, string many)
        {
            int add = items.Count(i => i.Status == ImportStatus.New);
            int clash = items.Count(i => i.Status == ImportStatus.Differs);
            int same = items.Count(i => i.Status == ImportStatus.Same);
            if (add + clash + same == 0) return;
            var parts = new List<string>();
            if (add > 0) parts.Add($"{add} new");
            if (clash > 0) parts.Add($"{clash} that would OVERWRITE {(clash == 1 ? "one of yours" : "yours")}");
            if (same > 0) parts.Add($"{same} you already have");
            p.Summary.Add($"{(add + clash + same == 1 ? one : many)}: {string.Join(", ", parts)}.");
        }
    }
}
