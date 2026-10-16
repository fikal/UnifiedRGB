using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The trust boundary. A bundle is a file from the internet,    |
| and this is the only code that touches its bytes: everything |
| after ReadArchive works from the ImportPreview it filled.    |
|                                                              |
| The order is the defence. The file's size and the zip's own  |
| entry count are checked before .NET builds an object per     |
| entry; the manifest is read before anything else, and then   |
| only the entries it lists, each against a budget spent on    |
| bytes that actually came out of the deflate stream.          |
\*-----------------------------------------------------------*/

public static partial class SetupBundle
{
    /// <summary>A bundle is a handful of JSON files and a few screen
    /// backgrounds. Anything past this is either a mistake or hostile, and
    /// either way it is not being read into memory.</summary>
    const long MaxBundleBytes = 128L * 1024 * 1024;

    /// <summary>The file on disk. The content budget plus room for the zip's
    /// own headers; a stored (uncompressed) bundle is about the size of what
    /// it holds, so nothing honest gets near this.</summary>
    const long MaxBundleFileBytes = MaxBundleBytes + 1024 * 1024;

    /// <summary>Entries in the zip. An export is a manifest, ten config files
    /// and one image per screen background, so 512 is room to spare. The cap
    /// matters because opening a zip builds an object per entry before a single
    /// one is read: a 100 MB central directory of empty names is millions of
    /// them.</summary>
    internal const int MaxEntries = 512;

    /// <summary>The central directory those entries are described in. 512
    /// entries with our short names is tens of kilobytes.</summary>
    const long MaxDirectoryBytes = 1024 * 1024;

    /// <summary>The manifest: a list of files, images and devices.</summary>
    const long MaxManifestBytes = 1024 * 1024;

    /// <summary>Per image. A pump screen is 320x240; a 24MB background is
    /// already generous and keeps a bundle mailable.</summary>
    const long MaxAssetBytes = 24L * 1024 * 1024;

    /// <summary>Read and validate the parts of the zip the manifest claims.
    /// Null return = refuse, with `problem` set to a sentence. Reading each
    /// claimed entry to the end is what catches a damaged one: the deflate
    /// stream's CRC is checked as it is consumed, so a file that LOOKS like a
    /// zip but is broken inside fails here instead of after three of its five
    /// files have been applied.
    ///
    /// Only claimed entries are read. Every entry used to be decompressed
    /// first and sorted out afterwards, so a bundle could make the preview
    /// inflate anything it liked just by carrying it.</summary>
    static BundleManifest? ReadArchive(string path, ImportPreview p, out string? problem)
    {
        problem = null;
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex)
        {
            problem = $"that file could not be opened: {ex.Message}";
            return null;
        }

        ZipArchive zip;
        try
        {
            // Both checks before ZipArchive sees the file: it turns the whole
            // central directory into objects the moment Entries is touched.
            if (file.Length > MaxBundleFileBytes)
            {
                problem = $"that file is {file.Length / (1024 * 1024)} MB, which is far larger than a setup bundle can be";
                file.Dispose();
                return null;
            }
            problem = CheckDirectory(file);
            if (problem != null) { file.Dispose(); return null; }
            file.Position = 0;
            zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            file.Dispose();
            problem = "that file is not a UnifiedRGB setup bundle (it is not a readable zip - it may be truncated or still downloading)";
            return null;
        }
        catch (Exception ex)
        {
            file.Dispose();
            problem = $"that file could not be opened: {ex.Message}";
            return null;
        }

        using (zip)
        {
            // Belt and braces for the end-record check: this is the count .NET
            // itself arrived at.
            if (zip.Entries.Count > MaxEntries)
            {
                problem = $"that bundle holds {zip.Entries.Count} files; a setup is never more than {MaxEntries}";
                return null;
            }

            // An index by name, so nothing is opened yet. A name that appears
            // twice is refused rather than resolved: which copy a reader picks
            // is exactly the ambiguity a crafted zip uses to show one tool one
            // file and another tool another.
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;   // directory marker
                if (!byName.TryAdd(entry.FullName, entry))
                {
                    problem = $"the bundle holds two files called '{entry.FullName}', so it is not one this app wrote";
                    return null;
                }
            }

            if (!byName.TryGetValue(ManifestName, out var manifestEntry))
            {
                problem = "that zip has no manifest, so it is not a UnifiedRGB setup bundle";
                return null;
            }

            // An entry's Length is a number the bundle's author wrote into the
            // zip's own central directory, and nothing makes the deflate stream
            // honour it. The real ceiling is this budget, spent against bytes
            // that actually came out.
            long budget = MaxBundleBytes;

            string manifestText;
            try { manifestText = new UTF8Encoding(false).GetString(ReadEntry(manifestEntry, ref budget, MaxManifestBytes, "a manifest can be")); }
            catch (Exception ex)
            {
                problem = Unreadable(manifestEntry, ex);
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
            manifest.Entries = manifest.Entries?.Where(e => e != null).ToList() ?? new();
            manifest.Assets = manifest.Assets?.Where(a => a != null).ToList() ?? new();
            manifest.Devices ??= new();
            // The manifest is the bundle's own words too: listing one small
            // entry a million times would read it a million times.
            if (manifest.Entries.Count + manifest.Assets.Count > MaxEntries)
            {
                problem = $"the bundle's manifest lists {manifest.Entries.Count + manifest.Assets.Count} files; a setup is never more than {MaxEntries}";
                return null;
            }

            // Index by manifest, not by what is in the zip: an entry the
            // manifest does not claim is never opened, and a claimed entry that
            // is missing is a damaged bundle. Config files live outside assets/
            // and images inside it, as the exporter lays them out.
            var texts = new List<(BundleEntry Entry, ZipArchiveEntry Zip)>();
            foreach (var e in manifest.Entries)
            {
                string at = e.Path ?? "";
                if (at.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(at, ManifestName, StringComparison.OrdinalIgnoreCase)
                    || !byName.TryGetValue(at, out var zipEntry))
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
                if (!IsSafeName(e.Name ?? ""))
                {
                    problem = $"the bundle names a file it is not allowed to write ('{e.Name}')";
                    return null;
                }
                if (TargetPathFor(e) == null)
                {
                    // A safe name this build does not publish under that root and
                    // group: a file a NEWER build added. Skipped and said, like an
                    // unknown group - refusing the whole bundle for it was the
                    // opposite of the forward compatibility the manifest promises.
                    p.Warnings.Add($"'{e.Name}' is a file this build does not know about (a newer feature?), so it will be skipped");
                    continue;
                }
                texts.Add((e, zipEntry));
            }

            var images = new List<(BundleAsset Asset, ZipArchiveEntry Zip)>();
            foreach (var a in manifest.Assets)
            {
                string at = a.Path ?? "";
                if (!at.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || !byName.TryGetValue(at, out var zipEntry))
                {
                    problem = $"the bundle is incomplete: a screen background ('{a.FileName}') is listed but missing";
                    return null;
                }
                if (zipEntry.Length > MaxAssetBytes)
                {
                    // The exporter never packs one this big, so it was made by
                    // hand. The screen imports without it rather than the whole
                    // bundle being refused over a picture.
                    p.Warnings.Add($"the screen background '{a.FileName}' is larger than {MaxAssetBytes / (1024 * 1024)} MB and will be left out");
                    continue;
                }
                images.Add((a, zipEntry));
            }

            // What the claimed entries SAY they hold, before any is opened.
            // Saturating, not Sum: Enumerable.Sum over long is CHECKED, so two
            // entries each declaring a huge uncompressed size threw
            // OverflowException, which Preview's catch-all reported as "the
            // bundle could not be read: Arithmetic operation resulted in an
            // overflow" - blaming the reader for what the bundle declared.
            long total = 0;
            foreach (var entry in texts.Select(t => t.Zip).Concat(images.Select(i => i.Zip)).Distinct())
            {
                long len = entry.Length;
                if (len < 0 || total > MaxBundleBytes - len) { total = long.MaxValue; break; }
                total += len;
            }
            if (total > MaxBundleBytes)
            {
                problem = $"that bundle claims to hold {total / (1024 * 1024)} MB, which is more than a setup can be";
                return null;
            }

            foreach (var (e, zipEntry) in texts)
            {
                byte[] bytes;
                try { bytes = ReadEntry(zipEntry, ref budget, MaxBundleBytes, "a setup can hold"); }
                catch (Exception ex)
                {
                    problem = Unreadable(zipEntry, ex);
                    return null;
                }
                // Keyed by root+name, NOT by the path inside the zip: the
                // manifest is the index, and a bundle that lays its files out
                // differently must still be readable by name.
                p.Texts[KeyOf(e)] = new UTF8Encoding(false).GetString(bytes);
            }
            foreach (var (a, zipEntry) in images)
            {
                try { p.AssetBytes[a.Path!] = ReadEntry(zipEntry, ref budget, MaxAssetBytes, "a screen background can be"); }
                catch (Exception ex)
                {
                    problem = Unreadable(zipEntry, ex);
                    return null;
                }
            }
            return manifest;
        }
    }

    /// <summary>The sentence for an entry that would not come out of the zip.
    /// A zip bomb is not damage, so it is not blamed on the download.</summary>
    static string Unreadable(ZipArchiveEntry entry, Exception ex) => ex switch
    {
        BundleTooBigException => ex.Message,
        InvalidDataException => $"the bundle is damaged: '{entry.FullName}' could not be unpacked (the file is truncated or corrupt)",
        _ => $"the bundle is damaged: '{entry.FullName}' could not be read ({ex.Message})",
    };

    /// <summary>What the zip says about itself in its end record, read before
    /// .NET is asked to open it. Null when it is within bounds, or when there
    /// is no end record at all (then it is not a zip, and the open says so in
    /// its own words).
    ///
    /// The record is found the way .NET finds it, scanning back from the end,
    /// so both read the same one. A Zip64 locator is refused outright: it
    /// exists for archives past 65,535 entries or 4 GB, which no setup is, and
    /// its counts are ones a 16-bit check here would not see.</summary>
    static string? CheckDirectory(FileStream file)
    {
        long len = file.Length;
        if (len < 22) return null;
        int window = (int)Math.Min(len, 22 + ushort.MaxValue + 20);
        var buf = new byte[window];
        file.Position = len - window;
        file.ReadExactly(buf);
        for (int i = window - 22; i >= 0; i--)
        {
            if (buf[i] != 0x50 || buf[i + 1] != 0x4B || buf[i + 2] != 0x05 || buf[i + 3] != 0x06) continue;
            int entries = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 10));
            uint directory = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 12));
            bool zip64 = i >= 20 && buf[i - 20] == 0x50 && buf[i - 19] == 0x4B && buf[i - 18] == 0x06 && buf[i - 17] == 0x07;
            if (zip64 || entries > MaxEntries)
                return $"that bundle holds {(zip64 ? "too many" : entries.ToString())} files; a setup is never more than {MaxEntries}";
            if (directory > MaxDirectoryBytes)
                return $"that bundle's file list alone is {directory / 1024} KB, which no setup needs";
            return null;
        }
        return null;
    }

    /// <summary>A bundle that unpacks to more than a setup could possibly be.
    /// Separate from the "damaged" cases because it is not damage: a zip bomb is
    /// a deliberately made file, and the sentence the user sees should not blame
    /// their download.</summary>
    sealed class BundleTooBigException(string message) : Exception(message);

    /// <summary>Read one entry with a hard ceiling on what comes OUT: its own
    /// cap, and a budget shared across the whole archive. CopyTo followed the
    /// decompressed stream wherever it went; a bundle is a file from the
    /// internet, and the only size figure available before reading it is one
    /// the file states about itself. Two hundred bytes of deflate can declare a
    /// length of 1 KB and expand to gigabytes - an OutOfMemoryException from
    /// nothing worse than opening a file someone sent you.</summary>
    /// <param name="cap">The most this one entry may hold, and
    /// <paramref name="capIs"/> what that limit is, for the sentence.</param>
    static byte[] ReadEntry(ZipArchiveEntry entry, ref long budget, long cap, string capIs)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > cap)
                throw new BundleTooBigException(
                    $"'{entry.FullName}' is more than the {cap / (1024 * 1024)} MB {capIs}, so it was not opened");
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

    /// <summary>A bare file name and nothing else: no separators, no "." or
    /// "..". The reader refuses a bundle that fails this; a name that passes but
    /// is not one we publish is merely skipped (see ReadArchive).</summary>
    internal static bool IsSafeName(string name)
        => name.Length > 0 && Path.GetFileName(name) == name && name is not ("." or "..");

    /// <summary>Where an entry may be written, or null if it may not be.
    ///
    /// A bundle is a file from the internet. Nothing in it chooses a path: the
    /// root must be one of ours and the name must be a bare file name, so
    /// "..\\..\\Startup\\evil.cmd" resolves to null and the whole import is
    /// refused rather than obeyed.</summary>
    internal static string? TargetPathFor(BundleEntry e)
    {
        string name = e.Name ?? "";
        if (!IsSafeName(name)) return null;
        // And the root/name/group triple has to be one WE publish. Confining the
        // destination to our own two folders was not enough on its own: the
        // group is what decides which consent checkbox covers the entry and what
        // sentence the preview shows for it, so a bundle was free to carry
        // settings.json under group "machine" and have it described to the user
        // as "header layout, fan curves and hub layouts" on the way in. An
        // unknown name is refused for the same reason - nothing should be able
        // to write a file into our config folder that we do not put there
        // ourselves. A bundle from a newer build carrying a new file is skipped
        // rather than obeyed (ReadArchive turns this null into a warning), which
        // is the documented direction.
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
}
