using System.IO;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/// <summary>Apply's second half: put an ImportPlan on disk, all or nothing.
///
/// The order is the safety property. The images go first, under names that
/// are their content, so one already present is already right. Then each file
/// about to be replaced is copied into a stamped backup folder, then the
/// writes go out through SafeFile, which is atomic per file. If one of them
/// fails, the ones already written are put back from the backup and the import
/// reports failure, so the setup is never left half converted - and when even
/// the undo fails, it says which files and where their originals are.</summary>
internal static class ImportTransaction
{
    /// <summary>How many import backups to keep, the one this import made
    /// included. Every import that replaces a file leaves one, and nothing ever
    /// removed them: a user who imports a bundle a few times a year accumulated
    /// a folder per import, forever, in the config directory. Keeping a handful
    /// keeps the undo that matters - the most recent - and the oldest ones are
    /// of no use to anybody.</summary>
    const int KeepImportBackups = 5;

    internal static void Commit(ImportPlan plan, ImportResult result, string bundlePath)
    {
        // 1. Extract the images.
        var extracted = new List<string>();
        try
        {
            foreach (var (path, bytes) in plan.Assets)
                if (PlaceAsset(path, bytes)) extracted.Add(path);
        }
        catch (Exception ex)
        {
            // Rolling back what we unpacked. result.Problem below is what the
            // user sees; a file we cannot remove is not worth a second message.
            foreach (string f in extracted) { try { File.Delete(f); } catch { } }   // see above: the problem below is the message
            result.Problem = $"a screen background could not be saved: {ex.Message}";
            Log.Warn("bundle", $"import failed extracting images: {ex}");
            return;
        }

        // 2. Back up what is about to be replaced, then write. The backup is
        //    both the rollback source below and the user's only undo.
        string backupDir = AppPaths.Config($"import-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
        var replaced = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);   // target -> backup copy, null = did not exist
        var written = new List<string>();
        try
        {
            foreach (string target in plan.Writes.Keys)
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

            foreach (var kv in plan.Writes)
            {
                SafeFile.WriteAllText(kv.Key, kv.Value);
                written.Add(kv.Key);
            }
        }
        catch (Exception ex)   // RollBack logs it and puts it in front of the user
        {
            RollBack(ex, written, replaced, extracted, backupDir, result);
            return;
        }

        result.Ok = true;
        result.BackupDirectory = Directory.Exists(backupDir) ? backupDir : null;
        Log.Info("bundle", $"imported from {bundlePath}: {string.Join("; ", result.Applied)}"
                         + (result.BackupDirectory != null ? $" (replaced files kept at {result.BackupDirectory})" : ""));
        // After the import, so the backup it just made is one of the five kept.
        // Pruning first kept five old ones plus this one, and a failed import
        // still cost the user their oldest undo.
        PruneOldImportBackups();
    }

    /// <summary>Put things back exactly as they were found: a file we created
    /// is deleted, a file we replaced is restored from its copy.</summary>
    static void RollBack(Exception cause, List<string> written, Dictionary<string, string?> replaced,
                         List<string> extracted, string backupDir, ImportResult result)
    {
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
        // Only when the undo actually worked. A stranded scenes.json has
        // already been rewritten to point at these files, so deleting them
        // leaves the user's screen designs referencing images that are gone
        // and rendering blank - strictly worse than "left as the bundle wrote
        // it", which is what they are being told happened. Keeping them costs
        // nothing: the names are content-addressed, so a re-import matches
        // them rather than piling up copies.
        if (stranded.Count == 0)
            foreach (string f in extracted) { try { File.Delete(f); } catch { } }   // content-addressed names: a leftover is matched, not duplicated

        // Only claim the rollback worked when it did. A failed undo used to
        // report the same reassuring sentence as a clean one, so the user was
        // told their setup was untouched while some of it had been replaced -
        // and the folder holding the only copies of the originals was never
        // named, because BackupDirectory is set on the success path alone.
        if (stranded.Count == 0)
        {
            result.Problem = $"the import failed partway and was rolled back: {cause.Message}";
            Log.Warn("bundle", $"import failed and was rolled back: {cause}");
        }
        else
        {
            result.RollbackIncomplete = true;
            result.BackupDirectory = Directory.Exists(backupDir) ? backupDir : null;
            result.Problem = $"the import failed partway ({cause.Message}) and could NOT be fully undone: "
                + string.Join(", ", stranded) + " was left as the bundle wrote it"
                + (result.BackupDirectory != null ? $". Your original is in {result.BackupDirectory}" : "");
            Log.Error("bundle", $"import failed and rollback was incomplete ({string.Join(", ", stranded)}): {cause}");
        }
    }

    /// <summary>Put one screen background in place, whole or not at all.
    /// True when this call created the file, so a rollback may remove it.
    ///
    /// File.WriteAllBytes used to write straight to the content-addressed
    /// name, and a crash mid-write left a truncated file under the RIGHT name.
    /// Every later import then saw the name, skipped the write and pointed the
    /// screen at the stub, for good. Now the bytes land under a temporary name
    /// and are moved into place, and a file already there is checked against
    /// what it should hold: one that does not match is replaced.</summary>
    static bool PlaceAsset(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (SameContent(path, bytes)) return false;
            Log.Warn("bundle", $"{path} does not hold the image its name says (a write cut short?); replacing it");
            SafeFile.WriteAllBytes(path, bytes);
            return false;   // it was there before this import, so a rollback leaves it, now repaired
        }
        SafeFile.WriteAllBytes(path, bytes);
        return true;
    }

    static bool SameContent(string path, byte[] bytes)
        => new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    static void PruneOldImportBackups()
    {
        try
        {
            var dirs = Directory.GetDirectories(AppPaths.ConfigDir, "import-backup-*")
                .OrderByDescending(d => d, StringComparer.Ordinal)   // the name is a sortable timestamp
                .Skip(KeepImportBackups)
                .ToList();
            foreach (string dir in dirs)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { Log.Warn("bundle", $"could not remove old import backup {dir}: {ex.Message}"); }
            }
            if (dirs.Count > 0) Log.Info("bundle", $"removed {dirs.Count} import backup(s) older than the last {KeepImportBackups}");
        }
        // Housekeeping must never be the reason an import fails.
        catch (Exception ex) { Log.Warn("bundle", $"could not tidy old import backups: {ex.Message}"); }
    }
}
