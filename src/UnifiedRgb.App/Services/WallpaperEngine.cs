using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-------------------------------------------------------------*\
| Wallpaper Engine, driven through its own control channel.     |
|                                                               |
| The case screen is the third panel on this desk - the LEDs    |
| and the pump LCD already ride on a profile, and the monitor   |
| inside the case did not, so "switch to Night" changed two of  |
| the three things a person can see.                            |
|                                                               |
| Named PROFILES only, deliberately. Wallpaper Engine can also  |
| be told to put one file on one monitor, but then a UnifiedRGB |
| profile would have to carry a wallpaper path per display and  |
| stay correct across monitors being unplugged, re-ordered and  |
| renumbered. A Wallpaper Engine profile already IS that whole  |
| arrangement, named and owned by the user, and it moves with   |
| their setup instead of against it. If they have not made one, |
| this feature is not offered at all.                           |
|                                                               |
| Fire and forget, and said so in the UI. The control channel   |
| returns nothing: the process exits immediately whether or not |
| the wallpaper changed, so unlike a hardware write this cannot |
| honestly report that it landed.                               |
\*-------------------------------------------------------------*/

/// <summary>Finds Wallpaper Engine, lists the profiles the user has made, and
/// asks it to apply one.</summary>
public static class WallpaperEngine
{
    /// <summary>The 64-bit host is the one a current install runs; the 32-bit
    /// one is kept as a fallback for an old machine rather than a preference.</summary>
    static readonly string[] Hosts = { "wallpaper64.exe", "wallpaper32.exe" };

    static readonly object _gate = new();
    static string? _exe;
    static bool _searched;

    /// <summary>The control host, or null when Wallpaper Engine is not
    /// installed. Looked up once: a Steam install does not move while the app
    /// is running, and a miss costs a registry read plus a few file probes.</summary>
    public static string? ExePath
    {
        get
        {
            lock (_gate)
            {
                if (!_searched) { _searched = true; _exe = FindHost(); }
                return _exe;
            }
        }
    }

    public static bool Installed => ExePath != null;

    /*--- finding it ---*/

    static string? FindHost()
    {
        try
        {
            foreach (string root in SteamLibraries())
            {
                string dir = Path.Combine(root, "steamapps", "common", "wallpaper_engine");
                foreach (string host in Hosts)
                {
                    string exe = Path.Combine(dir, host);
                    if (File.Exists(exe))
                    {
                        // Normalised: the registry hands back the Steam path with
                        // forward slashes, so the raw join reads as half one
                        // convention and half the other in the log.
                        exe = Path.GetFullPath(exe);
                        Log.Info("wallpaper", $"Wallpaper Engine found at {exe}");
                        return exe;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn("wallpaper", $"could not look for Wallpaper Engine: {ex.Message}"); }
        Log.Info("wallpaper", "Wallpaper Engine is not installed");
        return null;
    }

    /// <summary>Every Steam library root on this machine. The main one comes
    /// from the registry; the rest come from libraryfolders.vdf, because a
    /// second drive is the normal place for a games library and hard-coding
    /// Program Files would find it on this machine and nowhere else.</summary>
    static IEnumerable<string> SteamLibraries()
    {
        string? steam = SteamRoot();
        if (steam == null) yield break;
        yield return steam;

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        // The file is Valve's own key/value format. Only one thing is wanted
        // from it, so it is read with a regex rather than by taking on a VDF
        // parser: "path"  "D:\\SteamLibrary".
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            string p = m.Groups[1].Value.Replace("\\\\", "\\");
            if (!string.Equals(p, steam, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }

    static string? SteamRoot()
    {
        // Per user first: someone with Steam installed for their account only
        // has nothing under HKLM at all.
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser,  @"Software\Valve\Steam",              "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam",  "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam",              "InstallPath"),
        })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string s && s.Length > 0 && Directory.Exists(s))
                    return s;
            }
            catch { /* a locked-down machine is a miss, not a crash */ }
        }
        return null;
    }

    /*--- the profiles the user has made ---*/

    static readonly object _profileGate = new();
    static string[] _profiles = Array.Empty<string>();
    static DateTime _configStamp = DateTime.MinValue;
    static string? _configPath;

    /// <summary>The names of the profiles the user has created in Wallpaper
    /// Engine, newest read of config.json. Empty when there are none, which is
    /// the case that switches this whole feature off.
    ///
    /// Re-read by TIMESTAMP rather than cached for the session: a person who
    /// reads the hint, alt-tabs to Wallpaper Engine and makes a profile should
    /// find it in the list when they come back, not after a restart.</summary>
    public static IReadOnlyList<string> Profiles
    {
        get
        {
            string? cfg = ConfigPath;
            if (cfg == null) return Array.Empty<string>();
            lock (_profileGate)
            {
                DateTime stamp;
                // Year 1601 for a missing file, without throwing.
                try { stamp = File.GetLastWriteTimeUtc(cfg); } catch { return _profiles; }
                if (stamp == _configStamp) return _profiles;
                _configStamp = stamp;
                _profiles = ReadProfiles(cfg);
            }
            return _profiles;
        }
    }

    static string? ConfigPath
    {
        get
        {
            if (_configPath != null) return _configPath;
            string? exe = ExePath;
            if (exe == null) return null;
            string dir = Path.GetDirectoryName(exe) ?? "";
            string cfg = Path.Combine(dir, "config.json");
            return _configPath = File.Exists(cfg) ? cfg : null;
        }
    }

    /// <summary>Pull the profile names out of Wallpaper Engine's config.
    ///
    /// Searched for by KEY NAME anywhere in the tree rather than at a fixed
    /// path. The config is keyed by Windows user name at the top level and its
    /// shape is Wallpaper Engine's business, not ours - an update that moves
    /// the section one level deeper should cost us nothing. What is stable is
    /// that the section is called "profiles", which is the name the control
    /// channel uses too.</summary>
    internal static string[] ReadProfiles(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var found = new List<string>();
            Walk(doc.RootElement, found, 0);
            var names = found
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            Log.Info("wallpaper", names.Length == 0
                ? "Wallpaper Engine has no profiles saved"
                : $"Wallpaper Engine profiles: {string.Join(", ", names)}");
            return names;
        }
        catch (Exception ex)
        {
            // A config we cannot read is the same as no profiles: the feature
            // stays off rather than the app failing to start a profile.
            Log.Warn("wallpaper", $"could not read Wallpaper Engine's config: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static void Walk(JsonElement e, List<string> into, int depth)
    {
        // The config is a handful of levels deep. The bound is here so a future
        // version with a pathological nesting cannot turn a profile list into a
        // stack overflow on the UI thread.
        if (depth > 8) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    if (prop.NameEquals("profiles")) Collect(prop.Value, into);
                    Walk(prop.Value, into, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, into, depth + 1);
                break;
        }
    }

    /// <summary>A "profiles" section, whichever of the two reasonable shapes it
    /// is in: an object keyed by name, or a list of names or of objects that
    /// carry one.</summary>
    static void Collect(JsonElement profiles, List<string> into)
    {
        if (profiles.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in profiles.EnumerateObject()) into.Add(p.Name);
            return;
        }
        if (profiles.ValueKind != JsonValueKind.Array) return;
        foreach (var item in profiles.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { into.Add(item.GetString() ?? ""); continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (string key in new[] { "name", "title", "profile" })
                if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    into.Add(v.GetString() ?? "");
                    break;
                }
        }
    }

    /// <summary>True when this name is still one of the user's profiles. A
    /// profile they deleted in Wallpaper Engine must not be sent: the control
    /// channel would accept it, do nothing, and say nothing.</summary>
    public static bool Knows(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && Profiles.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));

    /*--- applying one ---*/

    /// <summary>Ask Wallpaper Engine to switch to a profile. Returns whether the
    /// request was SENT, which is all that can honestly be claimed: the control
    /// channel exits immediately and reports nothing about what the wallpaper
    /// did afterwards.</summary>
    public static bool Apply(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return false;
        string? exe = ExePath;
        if (exe == null)
        {
            Log.Warn("wallpaper", $"a profile asked for wallpaper '{profile}', but Wallpaper Engine is not installed");
            return false;
        }
        if (!Knows(profile))
        {
            Log.Warn("wallpaper", $"a profile asked for wallpaper '{profile}', which Wallpaper Engine no longer has");
            return false;
        }

        try
        {
            // ArgumentList rather than one string: a profile called "Night Sky"
            // has a space in it, and quoting by hand is how that turns into two
            // arguments and a silent no-op.
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.ArgumentList.Add("-control");
            psi.ArgumentList.Add("openProfile");
            psi.ArgumentList.Add("-profile");
            psi.ArgumentList.Add(profile);
            using var p = Process.Start(psi);
            Log.Info("wallpaper", $"asked Wallpaper Engine for profile '{profile}'");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("wallpaper", $"could not ask Wallpaper Engine for profile '{profile}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Test seam: forget what was found so the next read looks again.
    /// Nothing in the app calls this - a Steam install does not move mid-session
    /// - but a suite that has just written a fake config needs it.</summary>
    internal static void Forget()
    {
        lock (_gate) { _searched = false; _exe = null; }
        lock (_profileGate) { _profiles = Array.Empty<string>(); _configStamp = DateTime.MinValue; _configPath = null; }
    }
}
