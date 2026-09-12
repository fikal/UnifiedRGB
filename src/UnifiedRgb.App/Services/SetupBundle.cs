using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The whole setup as ONE portable file.                        |
|                                                              |
| A user's setup is a scatter of JSON files under %APPDATA%    |
| plus whatever images their LCD screens point at, which is    |
| fine until they want to move to a new machine, keep a copy   |
| before trying something drastic, or send a screen to a       |
| friend. Then it is a folder they have to know about, minus   |
| the images, which live somewhere else entirely.              |
|                                                              |
| So: a zip with the config files, an assets/ folder for the   |
| images those files reference, and a manifest saying what is  |
| inside and which format wrote it.                            |
|                                                              |
| Three rules the rest of this file exists to keep:            |
|                                                              |
|  1. IMPORT NEVER SURPRISES. Preview() reads the bundle and   |
|     says, in plain language, what applying it WOULD do.      |
|     Nothing is written until Apply() is called with the      |
|     user's choices, and a conflicting item defaults to NOT   |
|     overwriting.                                             |
|                                                              |
|  2. IMPORT IS ALL OR NOTHING. Every byte is read and every   |
|     new file's content is computed BEFORE the first write,   |
|     so a bundle that turns out to be truncated fails during  |
|     the preview rather than half way through replacing the   |
|     user's profiles. What does get written is backed up      |
|     first and rolled back if a later write fails.            |
|                                                              |
|  3. DEVICES ARE MATCHED BY IDENTITY, NOT BY NAME. Profiles   |
|     are keyed by device name (see ProfileStore) and that     |
|     does not travel: the same keyboard can be named          |
|     differently by another machine's driver, and two         |
|     identical fan hubs share one name. The bundle records    |
|     Core's DeviceIdentity beside each name, the preview      |
|     shows the proposed remapping, and the user can change    |
|     it before anything is applied.                           |
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

    /// <summary>The remaining app preferences (brightness, favourites, start
    /// minimized...). Never window geometry, first-run state, disabled devices
    /// or the SDK/bridge switches: see SetupBundle.MachineKeys.</summary>
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
        c.MachineHardware = MachineHardware != null;
        SeedRemap(c);
        return c;
    }

    void SeedRemap(ImportChoices c)
    {
        foreach (var d in Devices)
            if (d.IsRemap && d.LocalName != null) c.DeviceRemap[d.BundleName] = d.LocalName;
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
    /// <summary>Wiring files changed, and nothing re-reads those without a
    /// restart. The UI should say so rather than leave the user wondering why
    /// their header layout did not change.</summary>
    public bool RestartRecommended { get; set; }
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

/// <summary>Export and import of the complete setup.</summary>
public static class SetupBundle
{
    public const string KindMarker = "unifiedrgb-setup";

    /// <summary>1 = the first shipped format. A reader refuses anything
    /// higher, because "higher" means a change that could not be expressed by
    /// adding manifest entries.</summary>
    public const int CurrentFormatVersion = 1;

    public const string RootConfig = "config";
    public const string RootLocal = "local";

    public const string FileExtension = ".urgb";
    public const string FileFilter = "UnifiedRGB setup (*.urgb)|*.urgb|All files (*.*)|*.*";

    const string ManifestName = "manifest.json";

    /// <summary>A bundle is a handful of JSON files and a few screen
    /// backgrounds. Anything past this is either a mistake or hostile, and
    /// either way it is not being read into memory.</summary>
    const long MaxBundleBytes = 128L * 1024 * 1024;

    /// <summary>Per image. A pump screen is 320x240; a 24MB background is
    /// already generous and keeps a bundle mailable.</summary>
    const long MaxAssetBytes = 24L * 1024 * 1024;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Everything the app persists that belongs to a SETUP, and what
    /// part of the setup it is.
    ///
    /// Deliberately not "every file in the folder". unifiedrgb.log is a
    /// diagnostic, not a setting. backend.json holds an endpoint key and is
    /// per-install by design, so it must never ride along to another machine.
    /// A *.corrupt-* copy is wreckage kept for evidence. Adding a file here
    /// later is safe in both directions: new bundles carry it, and an older
    /// build reading such a bundle skips the group it does not know.</summary>
    static readonly (string Root, string Name, string Group)[] Files =
    {
        (RootConfig, "profiles.json", "profiles"),
        (RootConfig, "settings.json", "settings"),
        (RootConfig, "scenes.json", "scenes"),
        (RootConfig, "lcd.json", "screen"),
        (RootConfig, "canvas.json", "canvas"),
        (RootConfig, "hardware.json", "machine"),
        (RootConfig, "calibration.json", "machine"),
        (RootConfig, "lianli-layout.json", "machine"),
        (RootConfig, "lianli-uni-layout.json", "machine"),
        (RootLocal, "fan-config.json", "machine"),
    };

    /// <summary>Where imported screen backgrounds land. Under the config dir
    /// so they are inside the thing that gets backed up, and so a re-export
    /// finds them exactly where the design says they are.</summary>
    static string AssetDir => AppPaths.Config("lcd-assets");

    /*==========================================================*\
    |  Export                                                    |
    \*==========================================================*/

    /// <summary>Write the whole setup to one file.</summary>
    /// <param name="destinationPath">Where the user chose to save it.</param>
    /// <param name="devices">The devices attached right now. Optional, but
    /// without them the bundle carries no identities and an import onto a
    /// machine with different device names can only match by name, which is
    /// the limitation this whole feature exists to remove.</param>
    public static ExportResult Export(string destinationPath, IEnumerable<IRgbDevice>? devices = null)
    {
        var result = new ExportResult { Path = destinationPath };
        try
        {
            // Read everything FIRST. A file that vanishes or locks up halfway
            // through should fail before a partial zip exists at the user's
            // chosen path.
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manifest = new BundleManifest
            {
                AppVersion = AppInfo.VersionString,
                CreatedUtc = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                Devices = DeviceIdentity.Survey(devices),
            };

            foreach (var (root, name, group) in Files)
            {
                string source = root == RootLocal ? AppPaths.Local(name) : AppPaths.Config(name);
                if (!File.Exists(source)) continue;
                string text;
                try { text = File.ReadAllText(source); }
                catch (Exception ex)
                {
                    // One unreadable file must not cost the user the other
                    // eight: it is logged and left out, and the manifest then
                    // simply does not list it.
                    Log.Warn("bundle", $"{name} left out of the bundle: {ex.Message}");
                    continue;
                }
                string entryPath = $"{root}/{name}";
                texts[entryPath] = text;
                manifest.Entries.Add(new BundleEntry { Path = entryPath, Root = root, Name = name, Group = group });
            }

            // Screen backgrounds. Gathered from the SAME text that is going
            // into the zip, so the paths recorded here are exactly the ones an
            // importer will be asked to rewrite.
            var assets = CollectAssets(texts, out var assetWarnings);
            foreach (string w in assetWarnings) Log.Warn("bundle", w);

            long total = texts.Values.Sum(t => (long)t.Length) + assets.Sum(a => a.Bytes.LongLength);
            if (total > MaxBundleBytes)
            {
                result.Problem = $"the setup is {total / (1024 * 1024)} MB, which is larger than a bundle is allowed to be";
                return result;
            }

            foreach (var a in assets) manifest.Assets.Add(a.Entry);

            // Write beside the destination, then move into place: an
            // interrupted export leaves the previous bundle intact rather than
            // a truncated file with the right name.
            string dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? ".";
            Directory.CreateDirectory(dir);
            string temp = Path.Combine(dir, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var zip = new ZipArchive(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None),
                                                ZipArchiveMode.Create))
                {
                    WriteText(zip, ManifestName, JsonSerializer.Serialize(manifest, Json));
                    foreach (var kv in texts) WriteText(zip, kv.Key, kv.Value);
                    foreach (var a in assets) WriteBytes(zip, a.Entry.Path, a.Bytes);
                }
                File.Move(temp, destinationPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }

            result.Ok = true;
            result.FileCount = texts.Count;
            result.AssetCount = assets.Count;
            try { result.Bytes = new FileInfo(destinationPath).Length; } catch { }
            Log.Info("bundle", $"exported {result.FileCount} file(s) and {result.AssetCount} image(s) to {destinationPath}");
            return result;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Problem = ex.Message;
            Log.Warn("bundle", $"export failed: {ex}");
            return result;
        }
    }

    static void WriteText(ZipArchive zip, string path, string text)
        => WriteBytes(zip, path, new UTF8Encoding(false).GetBytes(text));

    static void WriteBytes(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Every image an LCD design points at, read into memory.
    ///
    /// This is the subtle half of the whole feature: a design stores an
    /// ABSOLUTE path to its background, so a bundle of pure JSON reproduces
    /// the screen with a hole where the picture was. The file has to travel,
    /// and the path has to be rewritten at the other end.</summary>
    static List<(BundleAsset Entry, byte[] Bytes)> CollectAssets(
        Dictionary<string, string> texts, out List<string> warnings)
    {
        warnings = new List<string>();
        var wanted = new List<string>();

        if (texts.TryGetValue($"{RootConfig}/lcd.json", out string? lcdText))
        {
            try
            {
                var live = LcdDesign.Normalize(JsonSerializer.Deserialize<LcdDesign>(lcdText));
                if (!string.IsNullOrWhiteSpace(live.BackgroundImagePath)) wanted.Add(live.BackgroundImagePath!);
            }
            catch (Exception ex) { warnings.Add($"lcd.json could not be scanned for its background: {ex.Message}"); }
        }

        if (texts.TryGetValue($"{RootConfig}/scenes.json", out string? sceneText))
        {
            var store = SceneStore.TryParse(sceneText);
            if (store == null) warnings.Add("scenes.json could not be scanned for backgrounds");
            else
                foreach (var scene in store.Scenes)
                    if (!string.IsNullOrWhiteSpace(scene.Design?.BackgroundImagePath))
                        wanted.Add(scene.Design!.BackgroundImagePath!);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<(BundleAsset Entry, byte[] Bytes)>();
        int index = 0;
        foreach (string path in wanted)
        {
            if (!seen.Add(path)) continue;   // several screens sharing one image ship one copy
            byte[] bytes;
            try
            {
                if (!File.Exists(path)) { warnings.Add($"screen background is missing and was not bundled: {path}"); continue; }
                var info = new FileInfo(path);
                if (info.Length > MaxAssetBytes)
                {
                    warnings.Add($"screen background is too large to bundle ({info.Length / (1024 * 1024)} MB): {path}");
                    continue;
                }
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) { warnings.Add($"screen background could not be read ({ex.Message}): {path}"); continue; }

            string fileName = SafeFileName(Path.GetFileName(path));
            assets.Add((new BundleAsset
            {
                // Indexed inside the zip so two images with the same file name
                // from different folders cannot collide.
                Path = $"assets/{index++}-{fileName}",
                OriginalPath = path,
                FileName = fileName,
                ContentHash = Hash(bytes),
            }, bytes));
        }
        return assets;
    }

    /*==========================================================*\
    |  Preview                                                   |
    \*==========================================================*/

    /// <summary>Read a bundle and work out what importing it would do. Writes
    /// nothing. Every byte is read and parsed here, so a truncated or corrupt
    /// file is refused NOW, with a reason, rather than during the apply.</summary>
    /// <param name="bundlePath">The file the user picked.</param>
    /// <param name="devices">The devices attached right now, for the identity
    /// matching. Null means "match by name only", and the preview says so.</param>
    public static ImportPreview Preview(string bundlePath, IEnumerable<IRgbDevice>? devices = null)
    {
        var p = new ImportPreview { BundlePath = bundlePath };
        try
        {
            if (!File.Exists(bundlePath))
            {
                p.Problem = "that file no longer exists";
                return p;
            }

            BundleManifest? manifest = ReadArchive(bundlePath, p, out string? problem);
            if (manifest == null) { p.Problem = problem; return p; }
            p.Manifest = manifest;
            p.SameMachine = string.Equals(manifest.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            BuildDeviceMappings(p, manifest, devices);
            // Built by hand rather than with ToDictionary: two identical
            // devices in the bundle share one NAME, and a duplicate key would
            // throw out of the one call that is supposed to be safe.
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in p.Devices)
                if (d.IsRemap && d.LocalName != null && !remap.ContainsKey(d.BundleName))
                    remap[d.BundleName] = d.LocalName;

            DiffProfiles(p, remap);
            DiffScenes(p);
            DiffCanvas(p, remap);
            DiffSettings(p);
            DiffMachine(p);

            p.Ok = true;
            WriteSummary(p);
            return p;
        }
        catch (Exception ex)
        {
            // A preview must never throw at the UI: the whole point is to be
            // the safe half of the operation.
            p.Ok = false;
            p.Problem = $"the bundle could not be read: {ex.Message}";
            Log.Warn("bundle", $"preview failed for {bundlePath}: {ex}");
            return p;
        }
    }

    /// <summary>Read and validate the zip in full. Null return = refuse, with
    /// `problem` set to a sentence. Reading every entry to the end is what
    /// catches a truncated download: the deflate stream's CRC is checked as it
    /// is consumed, so a file that LOOKS like a zip but stops halfway fails
    /// here instead of after three of its five files have been applied.</summary>
    static BundleManifest? ReadArchive(string path, ImportPreview p, out string? problem)
    {
        problem = null;
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException)
        {
            problem = "that file is not a UnifiedRGB setup bundle (it is not a readable zip - it may be truncated or still downloading)";
            return null;
        }
        catch (Exception ex)
        {
            problem = $"that file could not be opened: {ex.Message}";
            return null;
        }

        using (zip)
        {
            long total = zip.Entries.Sum(e => e.Length);
            if (total > MaxBundleBytes)
            {
                problem = $"that bundle claims to hold {total / (1024 * 1024)} MB, which is more than a setup can be";
                return null;
            }
            // CLAIMS is the operative word above: Length is a number the bundle's
            // author wrote into the zip's own central directory, and nothing makes
            // the deflate stream honour it. The real ceiling is this budget, spent
            // against bytes that actually came out.
            long budget = MaxBundleBytes;

            string? manifestText = null;
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;   // directory marker
                byte[] bytes;
                try { bytes = ReadEntry(entry, ref budget); }
                catch (BundleTooBigException ex)
                {
                    problem = ex.Message;
                    return null;
                }
                catch (InvalidDataException)
                {
                    problem = $"the bundle is damaged: '{entry.FullName}' could not be unpacked (the file is truncated or corrupt)";
                    return null;
                }
                catch (Exception ex)
                {
                    problem = $"the bundle is damaged: '{entry.FullName}' could not be read ({ex.Message})";
                    return null;
                }

                if (string.Equals(entry.FullName, ManifestName, StringComparison.OrdinalIgnoreCase))
                    manifestText = new UTF8Encoding(false).GetString(bytes);
                else if (entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    assets[entry.FullName] = bytes;
                else
                    texts[entry.FullName] = new UTF8Encoding(false).GetString(bytes);
            }

            if (manifestText == null)
            {
                problem = "that zip has no manifest, so it is not a UnifiedRGB setup bundle";
                return null;
            }

            BundleManifest? manifest;
            try { manifest = JsonSerializer.Deserialize<BundleManifest>(manifestText); }
            catch (Exception ex)
            {
                problem = $"the bundle's manifest is unreadable ({ex.Message})";
                return null;
            }
            if (manifest == null)
            {
                problem = "the bundle's manifest is empty";
                return null;
            }
            if (!string.Equals(manifest.Kind, KindMarker, StringComparison.OrdinalIgnoreCase))
            {
                problem = "that zip is not a UnifiedRGB setup bundle";
                return null;
            }
            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                // Refuse rather than guess: a newer format exists precisely
                // because something changed that this build cannot honour.
                problem = $"that bundle was made by a newer version of UnifiedRGB "
                        + $"(bundle format {manifest.FormatVersion}, this build reads {CurrentFormatVersion}). Update, then import it.";
                return null;
            }
            manifest.Entries ??= new();
            manifest.Assets ??= new();
            manifest.Devices ??= new();

            // Index by manifest, not by what is in the zip: an entry the
            // manifest does not claim is ignored, and a claimed entry that is
            // missing is a damaged bundle.
            foreach (var e in manifest.Entries.Where(e => e != null).ToList())
            {
                if (!texts.TryGetValue(e.Path ?? "", out string? text))
                {
                    problem = $"the bundle is incomplete: it lists '{e.Path}' but does not contain it";
                    return null;
                }
                if (!IsKnownGroup(e.Group))
                {
                    // Forward compatibility in action: a section a future
                    // build added is skipped and SAID, not silently dropped.
                    p.Warnings.Add($"'{e.Name}' is part of a newer feature this build does not know about, so it will be skipped");
                    continue;
                }
                if (TargetPathFor(e) == null)
                {
                    problem = $"the bundle names a file it is not allowed to write ('{e.Name}')";
                    return null;
                }
                // Keyed by root+name, NOT by the path inside the zip: the
                // manifest is the index, and a bundle that lays its files out
                // differently must still be readable by name.
                p.Texts[KeyOf(e)] = text;
            }
            foreach (var a in manifest.Assets.Where(a => a != null).ToList())
            {
                if (!assets.TryGetValue(a.Path ?? "", out byte[]? bytes))
                {
                    problem = $"the bundle is incomplete: a screen background ('{a.FileName}') is listed but missing";
                    return null;
                }
                p.AssetBytes[a.Path!] = bytes;
            }
            return manifest;
        }
    }

    /// <summary>A bundle that unpacks to more than a setup could possibly be.
    /// Separate from the "damaged" cases because it is not damage: a zip bomb is
    /// a deliberately made file, and the sentence the user sees should not blame
    /// their download.</summary>
    sealed class BundleTooBigException(string message) : Exception(message);

    /// <summary>Read one entry with a hard ceiling on what comes OUT, spending a
    /// budget shared across the whole archive. CopyTo followed the decompressed
    /// stream wherever it went; a bundle is a file from the internet, and the
    /// only size figure available before reading it is one the file states about
    /// itself. Two hundred bytes of deflate can declare a length of 1 KB and
    /// expand to gigabytes - an OutOfMemoryException on the UI thread, from
    /// nothing worse than opening a file someone sent you.</summary>
    static byte[] ReadEntry(ZipArchiveEntry entry, ref long budget)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            budget -= n;
            if (budget < 0)
                throw new BundleTooBigException(
                    $"'{entry.FullName}' unpacks to far more than the {MaxBundleBytes / (1024 * 1024)} MB a setup can hold, "
                    + "so it was not opened");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    static bool IsKnownGroup(string? group)
        => group is "profiles" or "settings" or "scenes" or "screen" or "canvas" or "machine";

    /// <summary>How a config file is addressed once it is out of the zip:
    /// which root it belongs in and what it is called there.</summary>
    static string KeyOf(BundleEntry e) => $"{e.Root}/{e.Name}";

    /// <summary>Where an entry may be written, or null if it may not be.
    ///
    /// A bundle is a file from the internet. Nothing in it chooses a path: the
    /// root must be one of ours and the name must be a bare file name, so
    /// "..\\..\\Startup\\evil.cmd" resolves to null and the whole import is
    /// refused rather than obeyed.</summary>
    internal static string? TargetPathFor(BundleEntry e)
    {
        string name = e.Name ?? "";
        if (name.Length == 0 || Path.GetFileName(name) != name) return null;
        if (name is "." or "..") return null;
        // And the root/name/group triple has to be one WE publish. Confining the
        // destination to our own two folders was not enough on its own: the
        // group is what decides which consent checkbox covers the entry and what
        // sentence the preview shows for it, so a bundle was free to carry
        // settings.json under group "machine" and have it described to the user
        // as "header layout, fan curves and hub layouts" on the way in. An
        // unknown name is refused for the same reason - nothing should be able
        // to write a file into our config folder that we do not put there
        // ourselves. A bundle from a newer build carrying a new file is skipped
        // rather than obeyed, which is the documented direction.
        if (!Files.Any(f => f.Root == e.Root && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && f.Group == e.Group))
            return null;
        return e.Root switch
        {
            RootLocal => AppPaths.Local(name),
            RootConfig => AppPaths.Config(name),
            _ => null,
        };
    }

    /*--- the diffs ---*/

    static void BuildDeviceMappings(ImportPreview p, BundleManifest manifest, IEnumerable<IRgbDevice>? devices)
    {
        var local = DeviceIdentity.Survey(devices);
        var reservedNames = new HashSet<string>((manifest.Devices ?? new()).Where(d => d != null).Select(d => d.Name ?? ""), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byIdentity = new Dictionary<string, DeviceFingerprint>(StringComparer.Ordinal);
        foreach (var f in local) if (!byIdentity.ContainsKey(f.Identity)) byIdentity[f.Identity] = f;
        var byName = new Dictionary<string, DeviceFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in local) if (!byName.ContainsKey(f.Name)) byName[f.Name] = f;

        foreach (var d in (manifest.Devices ?? new()).Where(d => d != null))
        {
            var m = new DeviceMapping
            {
                BundleName = d.Name ?? "",
                BundleIdentity = d.Identity ?? "",
                BundleDescription = d.Describe(),
            };

            // Reserve exact names before using derived ordinals: an absent twin
            // must not steal the surviving twin whose ordinal moved down.
            if (byName.TryGetValue(m.BundleName, out var exact) && used.Add(exact.Name))
            {
                m.LocalName = exact.Name;
                m.Detail = "present on this machine (matched by name)";
            }
            else if (m.BundleIdentity.Length > 0 && byIdentity.TryGetValue(m.BundleIdentity, out var hit)
                && !reservedNames.Contains(hit.Name) && used.Add(hit.Name))
            {
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

    static void DiffProfiles(ImportPreview p, Dictionary<string, string> remap)
    {
        var incoming = BundleProfiles(p, remap);
        if (incoming == null) return;
        var current = CurrentProfiles();
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

    /// <summary>The bundle's profiles, with device names already remapped.
    /// Null when profiles.json is in the bundle but unreadable, which is
    /// reported rather than silently treated as "no profiles".</summary>
    static List<Profile>? BundleProfiles(ImportPreview p, Dictionary<string, string> remap)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/profiles.json", out string? text)) return null;
        List<Profile> list;
        try
        {
            list = JsonSerializer.Deserialize<List<Profile?>>(text)?.OfType<Profile>().ToList() ?? new();
        }
        catch (Exception ex)
        {
            p.Warnings.Add($"the bundle's profiles could not be read ({ex.Message}) and will be skipped");
            return null;
        }
        foreach (var prof in list)
        {
            prof.Name ??= "";
            prof.Effects?.RemoveAll(e => e == null);
            foreach (var e in prof.Effects ?? new()) e.Device ??= "";
            RemapProfile(prof, remap);
        }
        list.RemoveAll(x => string.IsNullOrWhiteSpace(x.Name));
        return list;
    }

    /// <summary>Rewrite a profile's device keys onto this machine's names.
    /// The one place a saved setup stops being name-bound.</summary>
    static void RemapProfile(Profile prof, Dictionary<string, string> remap)
    {
        if (remap.Count == 0) return;
        var frames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in prof.DeviceFrames)
        {
            string key = remap.TryGetValue(kv.Key, out string? to) ? to : kv.Key;
            // Refuse overlapping manual mappings before any files are written.
            if (!frames.TryAdd(key, kv.Value))
                throw new InvalidOperationException($"Device mapping sends more than one frame to '{key}'. Choose distinct target devices.");
        }
        prof.DeviceFrames = frames;
        foreach (var e in prof.Effects ?? new())
            if (remap.TryGetValue(e.Device, out string? to)) e.Device = to;
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

    static void DiffScenes(ImportPreview p)
    {
        if (p.Texts.TryGetValue($"{RootConfig}/scenes.json", out string? text))
        {
            var store = SceneStore.TryParse(text);
            if (store == null) p.Warnings.Add("the bundle's screens could not be read and will be skipped");
            else
            {
                var mine = SceneStore.Load();
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
                // FILE, not about the design: LcdDesign.Load() hands back a
                // built-in default when lcd.json is absent, and calling that a
                // conflict would ask the user to choose between the bundle's
                // screen and a default they never made.
                bool haveOne = File.Exists(AppPaths.Config("lcd.json"));
                var mine = LcdDesign.Load();
                bool same = haveOne && SameDesign(mine, incoming, p);
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
            catch { return false; }
        }
        return a.BgX == b.BgX && a.BgY == b.BgY && a.BgW == b.BgW && a.BgH == b.BgH;
    }

    static void DiffCanvas(ImportPreview p, Dictionary<string, string> remap)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/canvas.json", out string? text)) return;
        var incoming = CanvasLayout.TryParse(text);
        if (incoming == null)
        {
            p.Warnings.Add("the bundle's desk layout could not be read and will be skipped");
            return;
        }
        RemapCanvas(incoming, remap);
        var mine = CanvasLayout.Load();
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

    static void RemapCanvas(CanvasLayout layout, Dictionary<string, string> remap)
    {
        if (remap.Count == 0) return;
        var sources = layout.Items.Select(i => i.Device).Distinct(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            string target = remap.TryGetValue(source, out var mapped) ? mapped : source;
            if (!targets.Add(target)) throw new InvalidOperationException($"Conflicting desk layout mapping for '{target}'.");
        }
        foreach (var i in layout.Items)
            if (remap.TryGetValue(i.Device, out string? to)) i.Device = to;
    }

    static void DiffSettings(ImportPreview p)
    {
        if (!p.Texts.TryGetValue($"{RootConfig}/settings.json", out string? text)) return;
        SettingsData? incoming;
        try { incoming = JsonSerializer.Deserialize<SettingsData>(text); }
        catch (Exception ex)
        {
            p.Warnings.Add($"the bundle's settings could not be read ({ex.Message}) and will be skipped");
            return;
        }
        if (incoming == null) return;

        var mine = ProfileStore.LoadJson<SettingsData>(AppPaths.Config("settings.json"), "settings.json") ?? new SettingsData();

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

        p.Preferences = new ImportItem
        {
            Name = "Preferences",
            Status = ImportStatus.Differs,
            Detail = "brightness, favourites and the other app preferences. "
                   + "Window position, first-run state, disabled devices and the bridge/SDK switches stay as they are on this machine.",
        };
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
            string current = "";
            try { if (File.Exists(target)) current = File.ReadAllText(target); } catch { }
            if (!string.Equals(current, p.Texts[KeyOf(e)], StringComparison.Ordinal)) { anyDifferent = true; break; }
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

    /*==========================================================*\
    |  Apply                                                     |
    \*==========================================================*/

    /// <summary>Write the chosen parts of a previewed bundle.
    ///
    /// The order is the safety property. Everything is computed in memory
    /// first, so a parse failure costs nothing; then each file about to be
    /// replaced is copied into a stamped backup folder; then the writes go out
    /// through SafeFile, which is atomic per file. If one of them fails, the
    /// ones already written are put back from the backup and the import
    /// reports failure, so the setup is never left half converted.
    ///
    /// AFTERWARDS the caller must refresh what it holds in memory, because the
    /// running app is still working from what it loaded at startup and its
    /// next routine save would put the old data straight back:
    ///   ProfilesChanged  -> ProfileStore.Reload()   then re-bind the list
    ///   SettingsChanged  -> ProfileStore.Reload()   then re-read Settings
    ///   ScenesChanged    -> SceneStore.Load()       (and LcdDesign.Load() for
    ///                       the live screen) into the LCD designer
    ///   CanvasChanged    -> CanvasLayout.Reload()   then re-sync the desk view
    ///   RestartRecommended -> tell the user; hardware.json, the Lian Li
    ///                       layouts and fan-config.json are read once when
    ///                       their drivers open.
    /// No device rescan is needed: an import never changes what is attached.</summary>
    public static ImportResult Apply(ImportPreview preview, ImportChoices choices)
    {
        var result = new ImportResult();
        if (preview == null || !preview.Ok)
        {
            result.Problem = preview?.Problem ?? "there is nothing to import";
            return result;
        }
        if (choices == null) { result.Problem = "no import choices were given"; return result; }

        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // target path -> new text
        var newAssets = new List<(string Path, byte[] Bytes)>();

        try
        {
            // The device remapping the user settled on, which may differ from
            // what the preview proposed.
            var remap = new Dictionary<string, string>(choices.DeviceRemap, StringComparer.OrdinalIgnoreCase);

            // 1. Screen backgrounds, but only when a screen is actually being
            //    imported: an import of nothing but profiles must not litter
            //    the config folder with images nothing points at. Their
            //    destination paths are content addressed, so they are known
            //    before a single byte is extracted and the JSON built below can
            //    already point at them.
            var assetMap = choices.CurrentScreen || choices.Screens.Count > 0
                ? PlanAssets(preview, newAssets)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 2. Every file's new contents, in memory.
            BuildProfiles(preview, choices, remap, writes, result);
            BuildScenes(preview, choices, assetMap, writes, result);
            BuildCanvas(preview, choices, remap, writes, result);
            BuildSettings(preview, choices, writes, result);
            BuildMachine(preview, choices, remap, writes, result);

            if (writes.Count == 0 && newAssets.Count == 0)
            {
                result.Ok = true;
                result.Problem = "nothing was selected, so nothing changed";
                return result;
            }
        }
        catch (Exception ex)
        {
            // Nothing has been written yet, so this is a clean refusal.
            result.Problem = $"the import was prepared but could not be built: {ex.Message}";
            Log.Warn("bundle", $"import build failed: {ex}");
            return result;
        }

        // 3. Extract the images. Content-addressed names mean an image that is
        //    already there is already the right one, so re-importing the same
        //    bundle writes nothing twice.
        var extracted = new List<string>();
        try
        {
            Directory.CreateDirectory(AssetDir);
            foreach (var (path, bytes) in newAssets)
            {
                if (File.Exists(path)) continue;
                File.WriteAllBytes(path, bytes);
                extracted.Add(path);
            }
        }
        catch (Exception ex)
        {
            foreach (string f in extracted) { try { File.Delete(f); } catch { } }
            result.Problem = $"a screen background could not be saved: {ex.Message}";
            return result;
        }

        // 4. Back up what is about to be replaced, then write. The backup is
        //    both the rollback source below and the user's only undo.
        string backupDir = AppPaths.Config($"import-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
        var replaced = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // target -> backup copy, null = did not exist
        var written = new List<string>();
        try
        {
            foreach (string target in writes.Keys)
            {
                if (!File.Exists(target)) { replaced[target] = null; continue; }
                Directory.CreateDirectory(backupDir);
                // Named after the file it came from, so the folder is browsable
                // by a human. Two roots could in principle hold the same file
                // name, so a taken name gets a suffix rather than a clobbering.
                string copy = Path.Combine(backupDir, Path.GetFileName(target));
                for (int n = 2; File.Exists(copy); n++)
                    copy = Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(target)}-{n}{Path.GetExtension(target)}");
                File.Copy(target, copy, overwrite: true);
                replaced[target] = copy;
            }

            foreach (var kv in writes)
            {
                SafeFile.WriteAllText(kv.Key, kv.Value);
                written.Add(kv.Key);
            }
        }
        catch (Exception ex)
        {
            // Roll back to exactly the state we found. A file we created is
            // deleted; a file we replaced is restored from its copy.
            var stranded = new List<string>();
            foreach (string target in written)
            {
                try
                {
                    if (replaced.TryGetValue(target, out string? copy) && copy != null) File.Copy(copy, target, overwrite: true);
                    else File.Delete(target);
                }
                catch (Exception undo)
                {
                    stranded.Add(Path.GetFileName(target));
                    Log.Warn("bundle", $"rollback of {target} failed: {undo.Message}");
                }
            }
            foreach (string f in extracted) { try { File.Delete(f); } catch { } }

            // Only claim the rollback worked when it did. A failed undo used to
            // report the same reassuring sentence as a clean one, so the user was
            // told their setup was untouched while some of it had been replaced -
            // and the folder holding the only copies of the originals was never
            // named, because BackupDirectory is set on the success path alone.
            if (stranded.Count == 0)
            {
                result.Problem = $"the import failed partway and was rolled back: {ex.Message}";
                Log.Warn("bundle", $"import failed and was rolled back: {ex}");
            }
            else
            {
                result.RollbackIncomplete = true;
                result.BackupDirectory = Directory.Exists(backupDir) ? backupDir : null;
                result.Problem = $"the import failed partway ({ex.Message}) and could NOT be fully undone: "
                    + string.Join(", ", stranded) + " was left as the bundle wrote it"
                    + (result.BackupDirectory != null ? $". Your original is in {result.BackupDirectory}" : "");
                Log.Error("bundle", $"import failed and rollback was incomplete ({string.Join(", ", stranded)}): {ex}");
            }
            return result;
        }

        result.Ok = true;
        result.BackupDirectory = Directory.Exists(backupDir) ? backupDir : null;
        Log.Info("bundle", $"imported from {preview.BundlePath}: {string.Join("; ", result.Applied)}"
                         + (result.BackupDirectory != null ? $" (replaced files kept at {result.BackupDirectory})" : ""));
        return result;
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
            // The hash is in the NAME so the same image imported twice is the
            // same file, and so two different images that happen to share a
            // file name do not overwrite each other.
            string dest = Path.Combine(AssetDir, $"{stem}-{Hash(bytes)}{ext}");
            map[asset.OriginalPath ?? ""] = dest;
            toWrite.Add((dest, bytes));
        }
        return map;
    }

    static void BuildProfiles(ImportPreview p, ImportChoices c, Dictionary<string, string> remap,
                              Dictionary<string, string> writes, ImportResult result)
    {
        if (c.Profiles.Count == 0) return;
        var incoming = BundleProfiles(p, remap);
        if (incoming == null) return;

        var current = CurrentProfiles();
        int added = 0, replacedCount = 0;
        foreach (var prof in incoming)
        {
            if (!c.Profiles.Contains(prof.Name)) continue;
            int at = current.FindIndex(x => x.Name.Equals(prof.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) { current[at] = prof; replacedCount++; }
            else { current.Add(prof); added++; }
        }
        if (added + replacedCount == 0) return;

        writes[AppPaths.Config("profiles.json")] = JsonSerializer.Serialize(current, Json);
        result.ProfilesChanged = true;
        result.Applied.Add($"{added} profile(s) added, {replacedCount} replaced");
    }

    static List<Profile> CurrentProfiles()
        => ProfileStore.LoadJson<List<Profile?>>(AppPaths.Config("profiles.json"), "profiles.json")
               ?.OfType<Profile>().ToList() ?? new List<Profile>();

    static void BuildScenes(ImportPreview p, ImportChoices c, Dictionary<string, string> assetMap,
                            Dictionary<string, string> writes, ImportResult result)
    {
        if (p.Texts.TryGetValue($"{RootConfig}/scenes.json", out string? text)
            && (c.Screens.Count > 0 || c.Sequences.Count > 0))
        {
            var incoming = SceneStore.TryParse(text);
            if (incoming != null)
            {
                var mine = SceneStore.Load();
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
                    writes[AppPaths.Config("scenes.json")] = JsonSerializer.Serialize(mine, Json);
                    result.ScenesChanged = true;
                    result.Applied.Add($"{added} screen(s) added, {replacedCount} replaced; {seqAdded} show(s) added, {seqReplaced} replaced");
                }
            }
        }

        if (c.CurrentScreen && p.Texts.TryGetValue($"{RootConfig}/lcd.json", out string? lcdText))
        {
            var design = LcdDesign.Normalize(JsonSerializer.Deserialize<LcdDesign>(lcdText));
            RewriteBackground(design, assetMap);
            writes[AppPaths.Config("lcd.json")] = JsonSerializer.Serialize(design, Json);
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
    /// time) is CLEARED rather than left pointing at a folder that does not
    /// exist here, so the screen is deliberately plain instead of subtly
    /// broken.</summary>
    static void RewriteBackground(LcdDesign design, Dictionary<string, string> assetMap)
    {
        if (design == null || string.IsNullOrWhiteSpace(design.BackgroundImagePath)) return;
        if (assetMap.TryGetValue(design.BackgroundImagePath!, out string? here)) design.BackgroundImagePath = here;
        else if (!File.Exists(design.BackgroundImagePath!)) design.BackgroundImagePath = null;
    }

    static void BuildCanvas(ImportPreview p, ImportChoices c, Dictionary<string, string> remap,
                            Dictionary<string, string> writes, ImportResult result)
    {
        if (!c.Canvas || !p.Texts.TryGetValue($"{RootConfig}/canvas.json", out string? text)) return;
        var layout = CanvasLayout.TryParse(text);
        if (layout == null) return;
        RemapCanvas(layout, remap);
        writes[AppPaths.Config("canvas.json")] = JsonSerializer.Serialize(layout, Json);
        result.CanvasChanged = true;
        result.Applied.Add($"the desk layout was replaced ({layout.Items.Count} device(s) placed)");
    }

    /*--- settings: merged field by field, on JsonNode ---*/

    /// <summary>Rules and the scheduler, including the legacy night-mode
    /// fields the migration still reads.</summary>
    static readonly HashSet<string> RuleKeys = new(StringComparer.Ordinal)
    {
        "AppSwitchEnabled", "AutomationRules", "Schedules", "SensorRulesEnabled", "SensorRules",
        "LockLightsOff", "ReturnToStartupProfile", "NightMode", "NightStart", "NightEnd", "NightIdleOnly",
    };

    /// <summary>Handled by their own merge, not by the preferences copy.</summary>
    static readonly HashSet<string> PaletteKeys = new(StringComparer.Ordinal) { "SavedPalettes", "CustomColors" };

    /// <summary>Never copied from a bundle, whatever the user ticks. Each of
    /// these describes THIS machine or this install rather than the setup:
    /// window geometry (a 4K bundle would push the window off a 1080p screen),
    /// first-run state (the wizard would re-run or be skipped wrongly),
    /// disabled devices and the Lian Li hub counts (they name hardware that
    /// may not be here), the startup profile (it may not have been imported),
    /// the CS2 token (per-install secret), and the switches that open a port
    /// or launch a bundled OpenRGB, which are decisions a user makes for their
    /// own machine and must never arrive in a file.</summary>
    static readonly HashSet<string> MachineKeys = new(StringComparer.Ordinal)
    {
        "WindowBounds", "WindowMaximized", "FirstRunDone", "DisabledDevices",
        "LianUniFanCount", "LianUniChannel", "LianUniFansByChannel", "LianSpeedScale",
        "FanLabels", "GsiToken", "Cs2Enabled", "UseOpenRgb", "SdkServerEnabled", "SdkServerLan",
        "StartupProfile", "GithubUpdateCheck",
    };

    static void BuildSettings(ImportPreview p, ImportChoices c,
                              Dictionary<string, string> writes, ImportResult result)
    {
        if (!c.Rules && !c.Preferences && c.Palettes.Count == 0 && !c.CustomColors) return;
        if (!p.Texts.TryGetValue($"{RootConfig}/settings.json", out string? text)) return;

        JsonObject? incoming;
        try { incoming = JsonNode.Parse(text) as JsonObject; }
        catch { return; }
        if (incoming == null) return;

        // Work on the file as NODES, not as SettingsData, so a setting this
        // build does not know about survives the round trip instead of being
        // silently deleted from the user's file.
        JsonObject mine;
        try
        {
            string currentText = File.Exists(AppPaths.Config("settings.json"))
                ? File.ReadAllText(AppPaths.Config("settings.json")) : "{}";
            mine = JsonNode.Parse(currentText) as JsonObject ?? new JsonObject();
        }
        catch { mine = new JsonObject(); }

        var applied = new List<string>();

        if (c.Rules)
        {
            foreach (var key in RuleKeys)
                if (incoming.TryGetPropertyValue(key, out var node)) mine[key] = node?.DeepClone();
            applied.Add("rules and schedules");
        }

        if (c.Preferences)
        {
            foreach (var kv in incoming)
            {
                if (RuleKeys.Contains(kv.Key) || PaletteKeys.Contains(kv.Key) || MachineKeys.Contains(kv.Key)) continue;
                mine[kv.Key] = kv.Value?.DeepClone();
            }
            applied.Add("preferences");
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
        writes[AppPaths.Config("settings.json")] = mine.ToJsonString(Json);
        result.SettingsChanged = true;
        result.Applied.Add("settings: " + string.Join(", ", applied));
    }

    static void BuildMachine(ImportPreview p, ImportChoices c, Dictionary<string, string> remap,
                             Dictionary<string, string> writes, ImportResult result)
    {
        if (!c.MachineHardware) return;
        var names = new List<string>();
        foreach (var e in MachineEntries(p))
        {
            string? target = TargetPathFor(e);
            if (target == null) continue;   // already refused at preview; belt and braces
            string text = p.Texts[KeyOf(e)];
            if (e.Name == "calibration.json")
            {
                var calibration = JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidDataException("The calibration store must be an object.");
                foreach (string section in new[] { "Devices", "Zones" })
                {
                    if (calibration[section] is not JsonObject entries) continue;
                    var mapped = new JsonObject();
                    foreach (var entry in entries)
                    {
                        string key = remap.TryGetValue(entry.Key, out var to) ? to : entry.Key;
                        if (mapped.ContainsKey(key)) throw new InvalidOperationException($"Conflicting calibration mapping for '{key}'.");
                        mapped[key] = entry.Value?.DeepClone();
                    }
                    calibration[section] = mapped;
                }
                text = calibration.ToJsonString(Json);
            }
            writes[target] = text;
            names.Add(e.Name);
        }
        if (names.Count == 0) return;
        result.CalibrationChanged = names.Contains("calibration.json");
        result.RestartRecommended = names.Any(n => n != "calibration.json");
        result.Applied.Add("machine wiring: " + string.Join(", ", names));
    }

    /*==========================================================*\
    |  Small shared bits                                         |
    \*==========================================================*/

    /// <summary>A file name that cannot escape the folder it is written into.
    /// Bundle contents are untrusted: the name inside the zip is only ever a
    /// hint for what to CALL the copy, never where to put it.</summary>
    static string SafeFileName(string? name)
    {
        name = Path.GetFileName(name ?? "");
        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name is "" or "." or "..") name = "image";
        return name.Length > 64 ? name[^64..] : name;
    }

    /// <summary>FNV-1a over the bytes, 12 hex digits. Enough to name a file by
    /// its content; not a security hash, and nothing here depends on it being
    /// one (a collision costs a wrong background, not a wrong file path).</summary>
    static string Hash(byte[] bytes)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in bytes) { h ^= b; h *= prime; }
        return (h & 0xFFFF_FFFF_FFFFUL).ToString("x12");
    }
}
