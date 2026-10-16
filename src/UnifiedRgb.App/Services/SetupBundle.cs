using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Core;

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
| Three rules the rest of this feature exists to keep:         |
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
|                                                              |
| Where each part lives (Services/Bundle/):                    |
|   BundleModel        the manifest, preview, choices, results |
|   BundleReader       the zip: limits, manifest-first reads,  |
|                      and where an entry may be written       |
|   DeviceRemap        the one device-key collision policy     |
|   CurrentSetup       the user's own files, read-only         |
|   ImportDiff         the preview's comparisons               |
|   ImportPlan         Apply's new file contents, in memory    |
|   ImportTransaction  images, backup, write, rollback         |
| This file holds the three entry points and Export.           |
\*-----------------------------------------------------------*/

/// <summary>Export and import of the complete setup.</summary>
public static partial class SetupBundle
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

    /// <summary>What a bundled screen background is allowed to be. The set the
    /// designer itself can load, so anything else was never usable anyway.</summary>
    static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

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
            // The reader's own limit, so nothing exported here is a bundle this
            // build would then refuse to import.
            if (1 + texts.Count + assets.Count > MaxEntries)
            {
                result.Problem = $"the setup has {assets.Count} screen backgrounds, more than a bundle can carry ({MaxEntries} files in all)";
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
                try { File.Delete(temp); } catch { }   // the export already failed and is rethrown below; do not mask it
                throw;
            }

            result.Ok = true;
            result.FileCount = texts.Count;
            result.AssetCount = assets.Count;
            try { result.Bytes = new FileInfo(destinationPath).Length; } catch { }   // size is for the summary line; the export itself succeeded
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
    /// nothing, and changes nothing about how the app treats its own files.
    /// Every claimed byte is read and parsed here, so a truncated or corrupt
    /// file is refused NOW, with a reason, rather than during the apply.</summary>
    /// <param name="bundlePath">The file the user picked.</param>
    /// <param name="devices">The devices attached right now, for the identity
    /// matching. Null means "match by name only", and the preview says so.</param>
    public static ImportPreview Preview(string bundlePath, IEnumerable<IRgbDevice>? devices = null)
        => PreviewAgainst(bundlePath, DeviceIdentity.Survey(devices));

    /// <summary>The same, against devices already fingerprinted. For the import
    /// window, which runs the preview on a worker: the survey reads the live
    /// device objects and so stays on the UI thread, and only this - files and
    /// fingerprints - goes to the pool.</summary>
    internal static ImportPreview PreviewAgainst(string bundlePath, IReadOnlyList<DeviceFingerprint> here)
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

            BuildDeviceMappings(p, manifest, here ?? Array.Empty<DeviceFingerprint>());
            // The mapping the presets will hand to Apply, judged by the policy
            // Apply will use, so what the preview says is what Apply does.
            var remap = DeviceRemap.For(p, p.ProposedRemap());

            Section(p, () => DiffProfiles(p, remap));
            Section(p, () => DiffScenes(p));
            Section(p, () => DiffCanvas(p, remap));
            Section(p, () => DiffSettings(p));
            Section(p, () => DiffMachine(p));

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

    /*==========================================================*\
    |  Apply                                                     |
    \*==========================================================*/

    /// <summary>Write the chosen parts of a previewed bundle: ImportPlan works
    /// out every file's new contents in memory, so a parse failure or a
    /// contradictory device mapping costs nothing, then ImportTransaction backs
    /// up, writes and, if a write fails, rolls back.
    ///
    /// AFTERWARDS the caller must refresh what it holds in memory, because the
    /// running app is still working from what it loaded at startup and its
    /// next routine save would put the old data straight back:
    ///   ProfilesChanged  -> ProfileStore.Reload()   then re-bind the list
    ///   SettingsChanged  -> ProfileStore.Reload()   then re-read Settings
    ///   ScenesChanged    -> SceneStore.Load()       (and LcdDesign.Load() for
    ///                       the live screen) into the LCD designer
    ///   CanvasChanged    -> CanvasLayout.Reload()   then re-sync the desk view
    ///   FanConfigChanged -> make the running fan hub re-read fan-config.json
    ///                       (SensorHub.ReloadFanConfig) before its next save
    ///   RestartRecommended -> tell the user; hardware.json, the Lian Li
    ///                       layouts and the wired hub's counts are read once
    ///                       when their drivers open.
    /// Those reloads are also what re-arm saves to a file the app could not
    /// read at startup; nothing in the import itself does (see CurrentSetup).
    /// No device rescan is needed: an import never changes what is attached.</summary>
    /// <summary>One part of the preview. A file of ours that stayed locked
    /// through CurrentSetup's retries leaves ITS part out, with a warning: it
    /// used to be compared as if it were empty, so every same-named item of
    /// the user's showed up as NEW, pre-ticked, and a plain Import replaced it.</summary>
    static void Section(ImportPreview p, Action diff)
    {
        try { diff(); }
        catch (CurrentSetup.UnreadableException ex)
        {
            p.Warnings.Add($"{ex.Message} That part is left out of this preview; open the import again once it is free.");
        }
    }

    public static ImportResult Apply(ImportPreview preview, ImportChoices choices)
    {
        var result = new ImportResult();
        if (preview == null || !preview.Ok)
        {
            result.Problem = preview?.Problem ?? "there is nothing to import";
            return result;
        }
        if (choices == null) { result.Problem = "no import choices were given"; return result; }

        ImportPlan plan;
        try
        {
            plan = Plan(preview, choices, result);
            if (plan.IsEmpty)
            {
                result.Ok = true;
                result.Problem = "nothing was selected, so nothing changed";
                return result;
            }
        }
        catch (Exception ex)
        {
            // Nothing has been written yet, so this is a clean refusal.
            result.Applied.Clear();
            result.Problem = ex is RemapConflictException or CurrentSetup.UnreadableException
                ? ex.Message
                : $"the import was prepared but could not be built: {ex.Message}";
            Log.Warn("bundle", $"import build failed: {ex}");
            return result;
        }

        ImportTransaction.Commit(plan, result, preview.BundlePath);
        return result;
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
