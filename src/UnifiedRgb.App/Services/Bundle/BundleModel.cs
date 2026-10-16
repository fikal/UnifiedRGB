using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The setup bundle's data shapes: what a bundle says about     |
| itself (the manifest), what a preview found, what the user   |
| ticked, and what an import did. No logic beyond the presets. |
| The rest of the feature is split by job; SetupBundle.cs has  |
| the map.                                                     |
\*-----------------------------------------------------------*/

/// <summary>What a bundle says about itself. Everything a reader needs to
/// decide whether it can read the bundle at all, plus the INDEX of what is
/// inside: readers walk Entries rather than assuming a fixed set of files, so
/// a future version that bundles one more config file produces something an
/// older build still reads (it imports what it knows and reports the rest as
/// ignored).</summary>
public sealed class BundleManifest
{
    /// <summary>Marks the zip as ours. A random zip fails here with a sentence
    /// the user can act on instead of a deserialization error.</summary>
    public string Kind { get; set; } = SetupBundle.KindMarker;

    /// <summary>Bumped only for a change an older reader CANNOT cope with.
    /// Adding an entry to Entries is not such a change, which is the point of
    /// the index.</summary>
    public int FormatVersion { get; set; } = SetupBundle.CurrentFormatVersion;

    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>Where it came from. Shown in the preview ("made on DESKTOP-7
    /// on 3 May"), and used to decide whether machine-specific sections are
    /// worth offering: restoring your own backup onto the same box is a very
    /// different act from importing a friend's setup.</summary>
    public string MachineName { get; set; } = "";

    public List<BundleEntry> Entries { get; set; } = new();
    public List<BundleAsset> Assets { get; set; } = new();

    /// <summary>The devices attached to the machine that made the bundle.
    /// This is what makes a profile portable: the frames are keyed by name,
    /// and these say which physical device each of those names WAS.</summary>
    public List<DeviceFingerprint> Devices { get; set; } = new();
}

/// <summary>One config file inside the bundle.</summary>
public sealed class BundleEntry
{
    /// <summary>Path inside the zip.</summary>
    public string Path { get; set; } = "";

    /// <summary>"config" (%APPDATA%) or "local" (%LOCALAPPDATA%).</summary>
    public string Root { get; set; } = SetupBundle.RootConfig;

    /// <summary>Bare file name in that root. Never a path: see
    /// SetupBundle.TargetPathFor, which refuses anything else.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which part of the setup this file is, so a reader can group it
    /// without a table of file names: "profiles", "settings", "scenes",
    /// "screen", "canvas", "machine". An unrecognised group is skipped and
    /// reported rather than guessed at.</summary>
    public string Group { get; set; } = "";
}

/// <summary>An image an LCD design points at. Designs store an ABSOLUTE path,
/// which is meaningless on the receiving machine, so the file travels here and
/// the path is rewritten on import.</summary>
public sealed class BundleAsset
{
    public string Path { get; set; } = "";           // inside the zip
    public string OriginalPath { get; set; } = "";   // what the design said on the source machine
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";    // names the imported copy, so re-import is idempotent
}

/// <summary>What an import would do to one named thing.</summary>
public enum ImportStatus
{
    /// <summary>Not on this machine; importing only adds.</summary>
    New,
    /// <summary>Present and byte-identical; importing changes nothing.</summary>
    Same,
    /// <summary>Present under the same name but different. THIS is the
    /// conflict the preview exists for.</summary>
    Differs,
}

/// <summary>One row of the preview: a profile, a screen, a sequence, a
/// palette, or a whole section such as the desk layout.</summary>
public sealed class ImportItem
{
    public string Name { get; set; } = "";
    public ImportStatus Status { get; set; }

    /// <summary>A sentence for the UI. Written for a person, not a log.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Pre-ticked by DefaultChoices. New things are on, conflicts are
    /// off: the safe default for "import someone's setup" is to add what is
    /// missing and leave what you already have alone. A user restoring their
    /// own backup ticks everything (see ImportPreview.EverythingChoices).</summary>
    public bool Selected { get; set; }
}

/// <summary>How one device in the bundle lines up with this machine.</summary>
public sealed class DeviceMapping
{
    public string BundleName { get; set; } = "";
    public string BundleIdentity { get; set; } = "";

    /// <summary>The bundle's own description, for a row a human can read.</summary>
    public string BundleDescription { get; set; } = "";

    /// <summary>The device here that it resolves to, or null when nothing on
    /// this machine matches. Null does NOT mean the data is dropped: it is
    /// imported under the bundle's own name, so plugging the device back in
    /// brings its lighting with it.</summary>
    public string? LocalName { get; set; }

    /// <summary>True when the match was made by identity across a DIFFERENT
    /// name, i.e. the case name matching used to get wrong.</summary>
    public bool IsRemap => LocalName != null
        && !string.Equals(LocalName, BundleName, StringComparison.OrdinalIgnoreCase);

    public string Detail { get; set; } = "";
}

/// <summary>What the user ticked. Built from a preview, handed back to Apply.
/// Names are matched case-insensitively, exactly as the stores match them.</summary>
public sealed class ImportChoices
{
    public HashSet<string> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Screens { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Palettes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The screen that was on the panel when the bundle was made
    /// (lcd.json), as opposed to the saved screens.</summary>
    public bool CurrentScreen { get; set; }

    /// <summary>The desk layout (canvas.json). Whole-file: the layout is one
    /// arrangement, not a set of independent rows.</summary>
    public bool Canvas { get; set; }

    /// <summary>Automation rules, schedules and sensor rules.</summary>
    public bool Rules { get; set; }

    /// <summary>The portable app preferences: brightness, starred effects and
    /// start minimized - plus, from a backup of this machine only, the settings
    /// that set its fans (the hand-off on exit, fan names, the Lian Li fan
    /// settings). Allow-lists (SetupBundle.PreferenceKeys and
    /// MachinePreferenceKeys), so a setting that opens a port or launches a
    /// program can never arrive in a file, however the bundle was made.</summary>
    public bool Preferences { get; set; }

    /// <summary>hardware.json, the Lian Li layouts and fan-config.json. These
    /// describe a specific machine's wiring, so they are off unless the bundle
    /// came from this machine.</summary>
    public bool MachineHardware { get; set; }

    /// <summary>The saved color swatches.</summary>
    public bool CustomColors { get; set; }

    /// <summary>bundle device name -> this machine's device name. Seeded from
    /// the preview's identity matching; the UI may change or clear any row,
    /// which is what makes the remapping explicit rather than magic.</summary>
    public Dictionary<string, string> DeviceRemap { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The answer to "what would importing this file do?". Also holds the
/// bundle's fully-read contents, so Apply never touches the zip again: a file
/// that can be previewed can be applied.</summary>
public sealed class ImportPreview
{
    public string BundlePath { get; set; } = "";

    /// <summary>False = nothing here can be applied; Problem says why in one
    /// sentence.</summary>
    public bool Ok { get; set; }
    public string? Problem { get; set; }

    public BundleManifest? Manifest { get; set; }

    public List<ImportItem> Profiles { get; } = new();
    public List<ImportItem> Screens { get; } = new();
    public List<ImportItem> Sequences { get; } = new();
    public List<ImportItem> Palettes { get; } = new();
    public ImportItem? CurrentScreen { get; set; }
    public ImportItem? Canvas { get; set; }
    public ImportItem? Rules { get; set; }
    public ImportItem? Preferences { get; set; }
    public ImportItem? MachineHardware { get; set; }

    public List<DeviceMapping> Devices { get; } = new();

    /// <summary>The whole thing in sentences, in the order a person would want
    /// to read them. The UI can show this verbatim above the detail lists.</summary>
    public List<string> Summary { get; } = new();

    /// <summary>Things that are not fatal but that the user should see: a
    /// section this build does not understand, an image the bundle references
    /// but does not contain.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>True when the bundle was made on this machine, which is what
    /// makes the machine-specific sections worth offering.</summary>
    public bool SameMachine { get; set; }

    /// <summary>Everything the bundle actually contained. Internal because it
    /// is the raw payload, not a UI concern; Apply works from this so no file
    /// on disk is touched until every byte has already been read and parsed.</summary>
    internal Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, byte[]> AssetBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The safe tick set: add what is missing, keep what you have.
    /// A conflicting item stays unticked, so a careless import cannot lose a
    /// profile the user spent an evening on.</summary>
    public ImportChoices DefaultChoices()
    {
        var c = new ImportChoices();
        foreach (var i in Profiles) if (i.Status == ImportStatus.New) c.Profiles.Add(i.Name);
        foreach (var i in Screens) if (i.Status == ImportStatus.New) c.Screens.Add(i.Name);
        foreach (var i in Sequences) if (i.Status == ImportStatus.New) c.Sequences.Add(i.Name);
        foreach (var i in Palettes) if (i.Status == ImportStatus.New) c.Palettes.Add(i.Name);
        c.CurrentScreen = CurrentScreen?.Status == ImportStatus.New;
        c.Canvas = Canvas?.Status == ImportStatus.New;
        c.Rules = Rules?.Status == ImportStatus.New;
        c.Preferences = false;                       // preferences are never "missing", only different
        c.CustomColors = c.Palettes.Count > 0;
        // Restoring your own backup onto your own machine is the one case
        // where the wiring files mean anything, so it is the one case where
        // they are offered by default.
        c.MachineHardware = SameMachine && MachineHardware != null && MachineHardware.Status != ImportStatus.Same;
        SeedRemap(c);
        return c;
    }

    /// <summary>Everything, conflicts included: the "restore this backup over
    /// what is here" button. Still goes through Apply's backup and rollback.</summary>
    public ImportChoices EverythingChoices()
    {
        var c = new ImportChoices();
        foreach (var i in Profiles) c.Profiles.Add(i.Name);
        foreach (var i in Screens) c.Screens.Add(i.Name);
        foreach (var i in Sequences) c.Sequences.Add(i.Name);
        foreach (var i in Palettes) c.Palettes.Add(i.Name);
        c.CurrentScreen = CurrentScreen != null;
        c.Canvas = Canvas != null;
        c.Rules = Rules != null;
        c.Preferences = Preferences != null;
        c.CustomColors = true;
        // Another machine's wiring is never part of "everything": its fan curves
        // would drive THIS board's headers by name the moment they land (the
        // hub reloads an imported fan config at once). Still tickable by hand.
        c.MachineHardware = MachineHardware != null && SameMachine;
        SeedRemap(c);
        return c;
    }

    void SeedRemap(ImportChoices c)
    {
        foreach (var kv in ProposedRemap()) c.DeviceRemap[kv.Key] = kv.Value;
    }

    /// <summary>The identity matching as a name map: the one the preview's own
    /// diff uses and the one the presets hand to Apply. First row wins, because
    /// two identical devices share one name in the bundle and a profile can
    /// only carry one entry under it. The presets used to take the LAST such
    /// row while the diff took the first, so the preview described one mapping
    /// and Apply was handed another.</summary>
    internal Dictionary<string, string> ProposedRemap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Devices)
            if (d.IsRemap && d.LocalName != null) map.TryAdd(d.BundleName, d.LocalName);
        return map;
    }

    /// <summary>Applying nothing is not a failure, but the UI should say so
    /// rather than report a successful import that did nothing.</summary>
    public static bool IsEmpty(ImportChoices c)
        => c.Profiles.Count == 0 && c.Screens.Count == 0 && c.Sequences.Count == 0
           && c.Palettes.Count == 0 && !c.CurrentScreen && !c.Canvas && !c.Rules
           && !c.Preferences && !c.MachineHardware && !c.CustomColors;
}

/// <summary>What Apply did.</summary>
public sealed class ImportResult
{
    public bool Ok { get; set; }
    public string? Problem { get; set; }
    /// <summary>Some files could not be restored after an import failed.</summary>
    public bool RollbackIncomplete { get; set; }

    /// <summary>One line per thing that changed, for the confirmation and the
    /// log.</summary>
    public List<string> Applied { get; } = new();

    /// <summary>Where the replaced files were copied before being replaced.
    /// Null when nothing needed replacing. Kept, not deleted: it costs a few
    /// kilobytes and it is the only undo an import has.</summary>
    public string? BackupDirectory { get; set; }

    /*--- what the caller has to do next; see SetupBundle.Apply ---*/
    public bool ProfilesChanged { get; set; }
    public bool ScenesChanged { get; set; }
    public bool CanvasChanged { get; set; }
    public bool SettingsChanged { get; set; }
    public bool CalibrationChanged { get; set; }
    public bool CurrentScreenChanged { get; set; }
    /// <summary>Wiring the drivers read once when they open changed (hardware.json,
    /// the Lian Li layouts, or the wired hub's connector and fan counts from a
    /// backup of this machine's preferences), and nothing re-reads it without a
    /// restart. The UI should say so rather than leave the user wondering why
    /// their header layout did not change. Not set for fan-config.json alone:
    /// the running fan hub reloads that one (FanConfigChanged).</summary>
    public bool RestartRecommended { get; set; }
    /// <summary>fan-config.json was written. The running fan hub holds its own
    /// copy and saves it back, so the caller has to make it re-read the file,
    /// or the hub's next save puts the old curves straight back.</summary>
    public bool FanConfigChanged { get; set; }
}

/// <summary>What Export did.</summary>
public sealed class ExportResult
{
    public bool Ok { get; set; }
    public string? Problem { get; set; }
    public string Path { get; set; } = "";
    public int FileCount { get; set; }
    public int AssetCount { get; set; }
    public long Bytes { get; set; }
}
