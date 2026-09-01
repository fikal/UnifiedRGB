namespace UnifiedRgb.Core;

/// <summary>Atomic text-file writes: write a temp file, then swap it into
/// place. A crash or power cut mid-save leaves either the old file or the new
/// one — never a truncated husk. Used for everything the user would cry about
/// losing (profiles, settings, hardware config, LCD designs).</summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }
}
