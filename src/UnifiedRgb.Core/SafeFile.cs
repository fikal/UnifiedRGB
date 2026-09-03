using System.Text;

namespace UnifiedRgb.Core;

/// <summary>Atomic text-file writes: write a temp file, flush it to disk, then
/// swap it into place. A crash or power cut mid-save leaves either the old
/// file or the new one — never a truncated husk. Used for everything the user
/// would cry about losing (profiles, settings, hardware config, LCD designs).</summary>
public static class SafeFile
{
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        // Every store may assume its folder exists (fan-config.json's did not
        // on a machine that never enabled the OpenRGB bridge).
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Utf8NoBom.GetBytes(contents));
                // FlushFileBuffers: NTFS journals the rename below but not the
                // data, so without this a power cut could commit the new name
                // over an empty file. Saves are debounced; one sync is cheap.
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            // Never leave a half-written .tmp for the next save to trip over.
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
