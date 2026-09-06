using Microsoft.Win32;

namespace UnifiedRgb.Core.Games;

/// <summary>Finding CS2 and writing the config file that tells it where to
/// post. The game only reads these at launch, so an install while it is
/// running takes effect next time.</summary>
public static class GsiConfig
{
    /// <summary>Counter-Strike 2.</summary>
    public const int Cs2AppId = 730;

    public const string FileName = "gamestate_integration_unifiedrgb.cfg";

    /// <summary>The config, in Valve's KeyValues format. Written by hand rather
    /// than through a serializer because it is nine lines and the format has no
    /// library here.
    ///
    /// The data block asks for exactly what the effect reads. Every extra
    /// section is more JSON per update, and updates arrive as often as the
    /// throttle allows.</summary>
    public static string Build(string uri, string token) =>
        "\"UnifiedRGB\"\n" +
        "{\n" +
        $"    \"uri\"       \"{uri}\"\n" +
        "    \"timeout\"   \"5.0\"\n" +
        "    \"buffer\"    \"0.1\"\n" +
        "    \"throttle\"  \"0.1\"\n" +
        "    \"heartbeat\" \"5.0\"\n" +
        "    \"auth\"\n" +
        "    {\n" +
        $"        \"token\"  \"{token}\"\n" +
        "    }\n" +
        "    \"data\"\n" +
        "    {\n" +
        "        \"provider\"          \"1\"\n" +
        "        \"map\"               \"1\"\n" +
        "        \"round\"             \"1\"\n" +
        "        \"player_id\"         \"1\"\n" +
        "        \"player_state\"      \"1\"\n" +
        "        \"player_weapons\"    \"1\"\n" +
        "    }\n" +
        "}\n";

    /// <summary>Steam's install root, or null.</summary>
    public static string? SteamPath()
    {
        try
        {
            // Steam writes this per-user; the 32-bit view is where it lives on
            // a 64-bit machine.
            object? v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            string? path = v as string;
            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { return null; }
    }

    /// <summary>Every Steam library root, including the main one. Parsed out of
    /// libraryfolders.vdf by looking for "path" lines: the file is KeyValues
    /// and its exact shape has changed across Steam versions, but that one line
    /// has been stable throughout.</summary>
    public static List<string> LibraryRoots(string? steamPath = null)
    {
        var roots = new List<string>();
        steamPath ??= SteamPath();
        if (steamPath == null) return roots;

        roots.Add(steamPath);
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (!File.Exists(vdf)) return roots;
            foreach (string line in File.ReadLines(vdf))
            {
                string? path = PathFromVdfLine(line);
                if (path != null && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    roots.Add(path);
            }
        }
        catch (Exception ex) { Log.Warn("gsi", $"could not read libraryfolders.vdf: {ex.Message}"); }
        return roots;
    }

    /// <summary>The path out of a `"path"  "C:\\Games\\Steam"` line, with the
    /// doubled backslashes undone. Null for any other line.</summary>
    public static string? PathFromVdfLine(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) return null;
        int open = trimmed.IndexOf('"', "\"path\"".Length);
        if (open < 0) return null;
        int close = trimmed.IndexOf('"', open + 1);
        if (close <= open) return null;
        string path = trimmed[(open + 1)..close].Replace("\\\\", "\\");
        return path.Length == 0 ? null : path;
    }

    /// <summary>Every cfg folder a CS2 install has under the known libraries.
    /// Empty when the game is not installed.</summary>
    public static List<string> Cs2CfgFolders()
    {
        var folders = new List<string>();
        foreach (string root in LibraryRoots())
        {
            string cfg = Path.Combine(root, "steamapps", "common",
                                      "Counter-Strike Global Offensive", "game", "csgo", "cfg");
            try { if (Directory.Exists(cfg)) folders.Add(cfg); }
            catch { }
        }
        return folders;
    }

    /// <summary>Write the config into every CS2 install found. Returns the
    /// files written; an empty list means the game was not found, and a partial
    /// list means one library was not writable. The caller shows the user the
    /// path and the contents either way, so a failure here is never a dead
    /// end.</summary>
    public static List<string> Install(string uri, string token, out string? error)
    {
        error = null;
        var written = new List<string>();
        var folders = Cs2CfgFolders();
        if (folders.Count == 0)
        {
            error = "Counter-Strike 2 was not found in any Steam library.";
            return written;
        }
        string body = Build(uri, token);
        foreach (string folder in folders)
        {
            string file = Path.Combine(folder, FileName);
            try { File.WriteAllText(file, body); written.Add(file); }
            catch (Exception ex) { error = $"{file}: {ex.Message}"; }
        }
        return written;
    }

    /// <summary>Remove the config again, so turning the feature off does not
    /// leave the game posting to a port nothing is listening on.</summary>
    public static void Uninstall()
    {
        foreach (string folder in Cs2CfgFolders())
        {
            try
            {
                string file = Path.Combine(folder, FileName);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) { Log.Warn("gsi", $"could not remove the config: {ex.Message}"); }
        }
    }
}
