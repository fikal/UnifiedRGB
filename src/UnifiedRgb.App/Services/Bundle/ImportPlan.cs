using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/// <summary>Everything an import will write, computed in memory before the
/// first byte goes to disk. ImportTransaction carries it out.</summary>
internal sealed class ImportPlan
{
    /// <summary>Target path -> the file's new text.</summary>
    internal Dictionary<string, string> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Screen backgrounds to put in place, by their content-addressed
    /// destination.</summary>
    internal List<(string Path, byte[] Bytes)> Assets { get; } = new();

    internal bool IsEmpty => Writes.Count == 0 && Assets.Count == 0;
}

/*-----------------------------------------------------------*\
| Apply's first half: the user's choices turned into new file  |
| contents, in memory. The user's own files are read through   |
| CurrentSetup (which refuses a file it cannot read rather     |
| than merge into nothing) and every device key goes through   |
| DeviceRemap, the policy the preview used.                    |
\*-----------------------------------------------------------*/

public static partial class SetupBundle
{
    static ImportPlan Plan(ImportPreview preview, ImportChoices choices, ImportResult result)
    {
        var plan = new ImportPlan();
        // The device mapping the user settled on, which may differ from what
        // the preview proposed.
        var remap = DeviceRemap.For(preview, choices.DeviceRemap);

        // Screen backgrounds, but only when a screen is actually being
        // imported: an import of nothing but profiles must not litter the
        // config folder with images nothing points at. Their destination paths
        // are content addressed, so they are known before a single byte is
        // extracted and the JSON built below can already point at them.
        var assetMap = choices.CurrentScreen || choices.Screens.Count > 0
            ? PlanAssets(preview, plan.Assets)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        BuildProfiles(preview, choices, remap, plan, result);
        BuildScenes(preview, choices, assetMap, plan, result);
        BuildCanvas(preview, choices, remap, plan, result);
        BuildSettings(preview, choices, plan, result);
        BuildMachine(preview, choices, remap, plan, result);
        return plan;
    }

    /// <summary>Whether Apply would refuse this device mapping, and why, in a
    /// sentence; null when it would accept it. For the import window, so a
    /// contradiction is shown beside the mapping that causes it instead of
    /// after the user has pressed Import. Reads nothing but the bundle, and
    /// judges exactly what Apply judges: the ticked profiles, and the desk
    /// layout and calibration only when those are ticked.</summary>
    internal static string? RemapProblem(ImportPreview preview, ImportChoices choices)
    {
        if (preview is not { Ok: true } || choices == null) return null;
        var remap = DeviceRemap.For(preview, choices.DeviceRemap);
        if (remap.IsEmpty) return null;
        var scratch = new List<string>();
        try
        {
            if (choices.Profiles.Count > 0) BundleProfiles(preview, remap, choices.Profiles, scratch, strict: true);
            if (choices.Canvas) BundleCanvas(preview, remap, scratch);
            if (choices.MachineHardware && CalibrationEntry(preview) is { } calibration)
                RemapCalibration(preview.Texts[KeyOf(calibration)], remap, scratch);
            return null;
        }
        catch (RemapConflictException ex) { return ex.Message; }   // the sentence is the answer: the window shows it
        // Anything else is a damaged section, not a mapping problem; Apply
        // refuses it with its own sentence, which is the one to show.
        catch (Exception) { return null; }
    }

    /// <summary>Decide where each bundled image will live, and which of them
    /// still have to be written. Returns the map the designs are rewritten
    /// through: the exporting machine's path -> this machine's path.</summary>
    static Dictionary<string, string> PlanAssets(ImportPreview preview, List<(string Path, byte[] Bytes)> toWrite)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in preview.Manifest?.Assets ?? new())
        {
            if (asset == null || !preview.AssetBytes.TryGetValue(asset.Path ?? "", out byte[]? bytes)) continue;
            string stem = Path.GetFileNameWithoutExtension(SafeFileName(asset.FileName));
            string ext = Path.GetExtension(SafeFileName(asset.FileName));
            if (stem.Length == 0) stem = "background";
            // SafeFileName sanitises the NAME; the extension came straight from
            // the bundle, so a file off the internet could drop arbitrary bytes
            // into lcd-assets under any extension it liked. Nothing here runs
            // it, but a screen background is an image and there is no reason to
            // write anything else.
            if (!ImageExtensions.Contains(ext))
            {
                Log.Warn("bundle", $"skipped bundled background '{asset.FileName}': '{ext}' is not an image");
                continue;
            }
            // The hash is in the NAME so the same image imported twice is the
            // same file, and so two different images that happen to share a
            // file name do not overwrite each other.
            string dest = Path.Combine(AssetDir, $"{stem}-{Hash(bytes)}{ext}");
            map[asset.OriginalPath ?? ""] = dest;
            toWrite.Add((dest, bytes));
        }
        return map;
    }

    /*--- the bundle's per-device tables, remapped ---*/

    /// <summary>The bundle's profiles with device names remapped. Null when
    /// profiles.json is not in the bundle, or is unreadable (said in
    /// <paramref name="notes"/> rather than silently treated as "no
    /// profiles").</summary>
    /// <param name="only">The profiles to return, or null for all of them.
    /// Apply passes the ticked set: a profile the user did not tick is not
    /// remapped, so a contradiction in it cannot refuse the ones they did.</param>
    /// <param name="strict">True for Apply, where a contradiction in the
    /// mapping refuses the import; false for the preview, which says so and
    /// compares that profile as the bundle has it.</param>
    static List<Profile>? BundleProfiles(ImportPreview p, DeviceRemap remap, ISet<string>? only, List<string> notes, bool strict)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/profiles.json", out string? text)) return null;
        List<Profile> list;
        try
        {
            list = JsonSerializer.Deserialize<List<Profile?>>(text)?.OfType<Profile>().ToList() ?? new();
        }
        catch (Exception ex)
        {
            notes.Add($"the bundle's profiles could not be read ({ex.Message}) and will be skipped");
            return null;
        }
        foreach (var prof in list)
        {
            prof.Name ??= "";
            prof.Effects?.RemoveAll(e => e == null);
            foreach (var e in prof.Effects ?? new()) e.Device ??= "";
        }
        list.RemoveAll(x => string.IsNullOrWhiteSpace(x.Name));
        if (only != null) list.RemoveAll(x => !only.Contains(x.Name));
        foreach (var prof in list)
        {
            string? conflict = remap.Apply(prof, notes);
            if (conflict == null) continue;
            if (strict) throw Contradiction(conflict);
            notes.Add($"{conflict}; choose distinct target devices before importing it");
        }
        return list;
    }

    /// <summary>The bundle's desk layout, remapped. Null when there is none or
    /// it is unreadable.</summary>
    static CanvasLayout? BundleCanvas(ImportPreview p, DeviceRemap remap, List<string> notes)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/canvas.json", out string? text)) return null;
        var layout = CanvasLayout.TryParse(text);
        if (layout == null) return null;
        string? conflict = remap.Apply(layout, notes);
        if (conflict != null) throw Contradiction(conflict);
        return layout;
    }

    static BundleEntry? CalibrationEntry(ImportPreview p)
        => MachineEntries(p).FirstOrDefault(e => string.Equals(e.Name, "calibration.json", StringComparison.OrdinalIgnoreCase));

    /// <summary>calibration.json's text with its device keys remapped.</summary>
    static string RemapCalibration(string text, DeviceRemap remap, List<string> notes)
    {
        var calibration = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("The calibration store must be an object.");
        string? conflict = remap.Apply(calibration, notes);
        if (conflict != null) throw Contradiction(conflict);
        return calibration.ToJsonString(Json);
    }

    static RemapConflictException Contradiction(string conflict)
        => new($"the device mapping contradicts itself ({conflict}). Choose a different device for one of them.");

    /*--- each file's new contents ---*/

    static void BuildProfiles(ImportPreview p, ImportChoices c, DeviceRemap remap, ImportPlan plan, ImportResult result)
    {
        if (c.Profiles.Count == 0) return;
        var notes = new List<string>();
        var incoming = BundleProfiles(p, remap, c.Profiles, notes, strict: true);
        if (incoming == null || incoming.Count == 0) return;

        var current = CurrentSetup.Profiles(forApply: true, out string? trouble);
        int added = 0, replacedCount = 0;
        foreach (var prof in incoming)
        {
            int at = current.FindIndex(x => x.Name.Equals(prof.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) { current[at] = prof; replacedCount++; }
            else { current.Add(prof); added++; }
        }
        if (added + replacedCount == 0) return;

        plan.Writes[AppPaths.Config("profiles.json")] = JsonSerializer.Serialize(current, Json);
        result.ProfilesChanged = true;
        result.Applied.Add($"{added} profile(s) added, {replacedCount} replaced");
        result.Applied.AddRange(notes);
        if (trouble != null) result.Applied.Add(trouble);
    }

    static void BuildScenes(ImportPreview p, ImportChoices c, Dictionary<string, string> assetMap,
                            ImportPlan plan, ImportResult result)
    {
        if (p.Texts.TryGetValue($"{RootConfig}/scenes.json", out string? text)
            && (c.Screens.Count > 0 || c.Sequences.Count > 0))
        {
            var incoming = SceneStore.TryParse(text);
            if (incoming != null)
            {
                // CurrentSetup, not SceneStore.Load, for the reason it gives:
                // if nothing below ends up written (or a later section refuses
                // the import), the store's loader would still have re-armed
                // saves to a scenes.json the app could not read at startup.
                var mine = CurrentSetup.Scenes(forApply: true, out string? trouble);
                int added = 0, replacedCount = 0;
                foreach (var scene in incoming.Scenes)
                {
                    if (!c.Screens.Contains(scene.Name)) continue;
                    RewriteBackground(scene.Design, assetMap);
                    int at = mine.Scenes.FindIndex(x => x.Name.Equals(scene.Name, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) { mine.Scenes[at] = scene; replacedCount++; }
                    else { mine.Scenes.Add(scene); added++; }
                }
                int seqAdded = 0, seqReplaced = 0;
                foreach (var seq in incoming.Sequences)
                {
                    if (!c.Sequences.Contains(seq.Name)) continue;
                    int at = mine.Sequences.FindIndex(x => x.Name.Equals(seq.Name, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) { mine.Sequences[at] = seq; seqReplaced++; }
                    else { mine.Sequences.Add(seq); seqAdded++; }
                }
                // The bundle's active show only takes over when that show came
                // with it; otherwise whatever is running here keeps running.
                if (!string.IsNullOrWhiteSpace(incoming.ActiveSequence) && c.Sequences.Contains(incoming.ActiveSequence!))
                    mine.ActiveSequence = incoming.ActiveSequence;

                if (added + replacedCount + seqAdded + seqReplaced > 0)
                {
                    plan.Writes[AppPaths.Config("scenes.json")] = JsonSerializer.Serialize(mine, Json);
                    result.ScenesChanged = true;
                    result.Applied.Add($"{added} screen(s) added, {replacedCount} replaced; {seqAdded} show(s) added, {seqReplaced} replaced");
                    if (trouble != null) result.Applied.Add(trouble);
                }
            }
        }

        if (c.CurrentScreen && p.Texts.TryGetValue($"{RootConfig}/lcd.json", out string? lcdText))
        {
            var design = LcdDesign.Normalize(JsonSerializer.Deserialize<LcdDesign>(lcdText));
            RewriteBackground(design, assetMap);
            plan.Writes[AppPaths.Config("lcd.json")] = JsonSerializer.Serialize(design, Json);
            result.ScenesChanged = true;
            result.CurrentScreenChanged = true;
            result.Applied.Add("the screen on the panel was replaced");
        }
    }

    /// <summary>Point a design's background at the copy that came with the
    /// bundle. Without this the design keeps the exporting machine's absolute
    /// path and the screen renders with a hole where the picture was.
    ///
    /// A path with no bundled image (it was missing or too large at export
    /// time) is CLEARED, always, without so much as asking the filesystem
    /// whether it exists here. It used to be kept when it did, and "does it
    /// exist" is a probe: a bundle naming a UNC path made this machine reach
    /// out to that server, credentials first, on import. And a path that does
    /// exist here is a file the bundle's author chose, which a later export
    /// would then pack and share. The only picture an import brings is the one
    /// that travelled inside it.</summary>
    static void RewriteBackground(LcdDesign design, Dictionary<string, string> assetMap)
    {
        if (design == null || string.IsNullOrWhiteSpace(design.BackgroundImagePath)) return;
        design.BackgroundImagePath = assetMap.TryGetValue(design.BackgroundImagePath!, out string? here) ? here : null;
    }

    static void BuildCanvas(ImportPreview p, ImportChoices c, DeviceRemap remap, ImportPlan plan, ImportResult result)
    {
        if (!c.Canvas) return;
        var notes = new List<string>();
        var layout = BundleCanvas(p, remap, notes);
        if (layout == null) return;
        plan.Writes[AppPaths.Config("canvas.json")] = JsonSerializer.Serialize(layout, Json);
        result.CanvasChanged = true;
        result.Applied.Add($"the desk layout was replaced ({layout.Items.Count} device(s) placed)");
        result.Applied.AddRange(notes);
    }

    /*--- settings: merged field by field, on JsonNode ---*/

    /// <summary>Rules and the scheduler, including the legacy night-mode
    /// fields the migration still reads.</summary>
    static readonly HashSet<string> RuleKeys = new(StringComparer.Ordinal)
    {
        "AppSwitchEnabled", "AutomationRules", "Schedules", "SensorRulesEnabled", "SensorRules",
        "LockLightsOff", "ReturnToStartupProfile", "NightMode", "NightStart", "NightEnd", "NightIdleOnly",
    };

    /// <summary>The only settings "Preferences" copies from any bundle.
    ///
    /// An ALLOW-list. This used to be a deny-list of machine keys with
    /// everything else copied, so every setting added since - and any key at
    /// all an untrusted bundle cared to carry - came along: LianHandoffOnExit
    /// (whether the fans hand over to the board on exit) arrived from a file.
    /// What stays out, and why: window geometry and first-run state describe
    /// this screen and this install; disabled devices decide which drivers are
    /// opened at all; the startup profile may not have been imported; the GSI
    /// token is a per-install secret; and the update check, OpenRGB, SDK server
    /// and CS2 switches open a port or launch a program. The settings that set
    /// this machine's fans are MachinePreferenceKeys, below. A new setting stays
    /// out until someone decides it is a portable preference and adds it here.</summary>
    static readonly HashSet<string> PreferenceKeys = new(StringComparer.Ordinal)
    {
        "MasterBrightness", "FavoriteEffects", "StartMinimized",
    };

    /// <summary>Settings that set this machine's fans: taken by "Preferences"
    /// only from a backup of THIS machine (ImportPreview.SameMachine).
    ///
    /// The allow-list above left them out of every bundle, which also left them
    /// out of the one bundle they are for: restoring your own backup quietly
    /// switched the fan hand-off off and dropped your fan names. From another
    /// machine they stay out - they name headers and calibrate fans that may not
    /// be here, and the hand-off would tie this machine's wireless fans, at exit,
    /// to a header that may not drive them.
    ///
    /// "This machine" is only what the manifest says (its MachineName), so
    /// nothing here may open a port, start a program or be a secret: the most a
    /// bundle that lies about where it was made can do is set these, all of
    /// which the user can see and set back.</summary>
    static readonly HashSet<string> MachinePreferenceKeys = new(StringComparer.Ordinal)
    {
        "LianHandoffOnExit", "FanLabels", "LianSpeedScale", "LianUniChannel", "LianUniFansByChannel", "LianUniFanCount",
    };

    /// <summary>The wired hub's connector and fan counts reach its driver once,
    /// before detection at launch, so an import of them changes nothing until a
    /// restart, exactly like the hub layout file.</summary>
    static readonly HashSet<string> RestartPreferenceKeys = new(StringComparer.Ordinal)
    {
        "LianUniChannel", "LianUniFansByChannel", "LianUniFanCount",
    };

    /// <summary>Exactly the keys "Preferences" takes from this bundle. The
    /// preview compares these and Apply copies these, so the row says what
    /// ticking it would change.</summary>
    static IEnumerable<string> PreferenceKeysFor(ImportPreview p)
        => p.SameMachine ? PreferenceKeys.Concat(MachinePreferenceKeys) : PreferenceKeys;

    static void BuildSettings(ImportPreview p, ImportChoices c, ImportPlan plan, ImportResult result)
    {
        if (!c.Rules && !c.Preferences && c.Palettes.Count == 0 && !c.CustomColors) return;
        if (!p.Texts.TryGetValue($"{RootConfig}/settings.json", out string? text)) return;

        // The bundle's file has to be one this build can LOAD, not merely JSON:
        // every value copied below lands in the user's settings.json, and one of
        // the wrong type would make that whole file unreadable at the next
        // launch, which starts every setting over from its default.
        JsonObject? incoming;
        try
        {
            if (JsonSerializer.Deserialize<SettingsData>(text) == null) return;
            incoming = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Warn("bundle", $"the bundle's settings were not imported: {ex.Message}");
            return;
        }
        if (incoming == null) return;

        // Work on the user's file as NODES, not as SettingsData, so the import
        // rewrites only the keys it means to and leaves the rest of the file as
        // it found it. That is all it can promise: after the reload, the app's
        // next routine save writes back what SettingsData carries, so a key
        // this build does not model lasts only as long as SettingsData keeps it.
        var mine = CurrentSetup.SettingsNodes(forApply: true, out string? trouble);

        var applied = new List<string>();

        if (c.Rules)
        {
            foreach (var key in RuleKeys)
                if (incoming.TryGetPropertyValue(key, out var node)) mine[key] = node?.DeepClone();
            applied.Add("rules and schedules");
        }

        if (c.Preferences)
        {
            int changed = 0;
            foreach (string key in PreferenceKeysFor(p))
            {
                if (!incoming.TryGetPropertyValue(key, out var node) || JsonNode.DeepEquals(node, mine[key])) continue;
                mine[key] = node?.DeepClone();
                changed++;
                if (RestartPreferenceKeys.Contains(key)) result.RestartRecommended = true;
            }
            if (changed > 0) applied.Add("preferences");
        }

        if (c.Palettes.Count > 0)
        {
            var target = mine["SavedPalettes"] as JsonArray;
            if (target == null) { target = new JsonArray(); mine["SavedPalettes"] = target; }
            int added = 0, replacedCount = 0;
            foreach (var node in (incoming["SavedPalettes"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject pal) continue;
                string name = pal["Name"]?.GetValue<string>() ?? "";
                if (name.Length == 0 || !c.Palettes.Contains(name)) continue;
                int at = -1;
                for (int i = 0; i < target.Count; i++)
                    if (string.Equals((target[i] as JsonObject)?["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                if (at >= 0) { target[at] = pal.DeepClone(); replacedCount++; }
                else { target.Add(pal.DeepClone()); added++; }
            }
            if (added + replacedCount > 0) applied.Add($"{added} palette(s) added, {replacedCount} replaced");
        }

        if (c.CustomColors && incoming["CustomColors"] is JsonArray swatches)
        {
            // A union, oldest first: swatches are a scratch pad, and losing
            // one to an import would be a nasty little surprise.
            var target = mine["CustomColors"] as JsonArray;
            if (target == null) { target = new JsonArray(); mine["CustomColors"] = target; }
            var have = new HashSet<string>(target.Select(n => n?.GetValue<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var node in swatches)
            {
                string hex = node?.GetValue<string>() ?? "";
                if (hex.Length == 0 || !have.Add(hex)) continue;
                target.Add((JsonNode?)JsonValue.Create(hex));
                added++;
            }
            if (added > 0) applied.Add($"{added} color swatch(es)");
        }

        if (applied.Count == 0) return;
        plan.Writes[AppPaths.Config("settings.json")] = mine.ToJsonString(Json);
        result.SettingsChanged = true;
        result.Applied.Add("settings: " + string.Join(", ", applied));
        if (trouble != null) result.Applied.Add(trouble);
    }

    static void BuildMachine(ImportPreview p, ImportChoices c, DeviceRemap remap, ImportPlan plan, ImportResult result)
    {
        if (!c.MachineHardware) return;
        var names = new List<string>();
        var notes = new List<string>();
        foreach (var e in MachineEntries(p))
        {
            string? target = TargetPathFor(e);
            if (target == null) continue;   // already refused at preview; belt and braces
            string text = p.Texts[KeyOf(e)];
            // Case-insensitively, because that is how the allow-table admitted
            // this entry (TargetPathFor). A manifest spelling it "Calibration.json"
            // passed the gate, wrote over the real file on a case-insensitive
            // filesystem, and skipped the device remap AND the reload flag below -
            // so the trims landed keyed to the SOURCE machine's device names and
            // vanished with nothing said.
            if (string.Equals(e.Name, "calibration.json", StringComparison.OrdinalIgnoreCase))
                text = RemapCalibration(text, remap, notes);
            plan.Writes[target] = text;
            names.Add(e.Name);
        }
        if (names.Count == 0) return;
        result.CalibrationChanged = names.Contains("calibration.json", StringComparer.OrdinalIgnoreCase);
        // Calibration and the fan config are reloaded in place (the caller acts
        // on CalibrationChanged / FanConfigChanged); only the wiring files the
        // drivers read once when they open need a restart. Or-ed in: the wired
        // hub's counts in the preferences (BuildSettings, earlier) need one too.
        result.RestartRecommended |= names.Any(n => !string.Equals(n, "calibration.json", StringComparison.OrdinalIgnoreCase)
                                                 && !string.Equals(n, "fan-config.json", StringComparison.OrdinalIgnoreCase));
        result.FanConfigChanged = names.Contains("fan-config.json", StringComparer.OrdinalIgnoreCase);
        result.Applied.Add("machine wiring: " + string.Join(", ", names));
        result.Applied.AddRange(notes);
    }
}
