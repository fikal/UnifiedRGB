using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| Field problem: ONE buggy detector crashes the whole  |
| bundled OpenRGB during its hardware scan — server dies, the  |
| user sees zero devices, every launch repeats it. Nobody can  |
| tell which detector did it from a ucrtbase access violation. |
|                                                              |
| Fix: automatic binary search. After two detection-phase      |
| crashes (persisted across sessions), bisect the enabled      |
| detector list: disable half, relaunch, observe crash/no-     |
| crash, narrow, repeat. Converges in ~log2(N) relaunches to   |
| the culprit, which stays disabled permanently (recorded in   |
| bisect state + a user-visible note). Everything else is      |
| re-enabled. Multiple culprits: the crash counter re-arms     |
| and a new bisect runs over the remaining detectors.          |
\*-----------------------------------------------------------*/
static class OpenRgbCrashBisect
{
    sealed class State
    {
        public int CrashCount { get; set; }
        public bool Active { get; set; }
        public List<string> Suspects { get; set; } = new();
        public List<string> DisabledHalf { get; set; } = new();
        public List<string> Culprits { get; set; } = new();
    }

    const int ArmAfterCrashes = 2;

    static string StatePath(string configDir) => Path.Combine(configDir, "crash-bisect.json");
    static string ConfigPath(string configDir) => Path.Combine(configDir, "OpenRGB.json");

    static State LoadState(string configDir)
    {
        try
        {
            if (File.Exists(StatePath(configDir)))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath(configDir))) ?? new State();
        }
        catch { }
        return new State();
    }

    static void SaveState(string configDir, State s)
    {
        try { SafeFile.WriteAllText(StatePath(configDir), JsonSerializer.Serialize(s)); } catch { }
    }

    /// <summary>Detectors the bisect has convicted on this machine.</summary>
    public static IReadOnlyList<string> Culprits(string configDir) => LoadState(configDir).Culprits;

    /// <summary>Make sure every convicted detector is still off. OpenRGB can
    /// regenerate its config (truncated file, version upgrade); without this the
    /// culprit came back enabled, CrashCount had been reset by the clean run,
    /// and the user ate two crashes plus a ~10-relaunch bisect all over again.</summary>
    public static void ReapplyCulprits(string configDir)
    {
        var culprits = Culprits(configDir);
        if (culprits.Count > 0) SetDetectors(configDir, culprits, enabled: false);
    }

    /// <summary>Server crashed during detection. Returns true when the bisect
    /// changed the detector config and the caller should relaunch; false =
    /// not armed yet (fail normally — arming needs repeat crashes).</summary>
    public static bool OnCrash(string configDir)
    {
        var s = LoadState(configDir);
        if (!s.Active)
        {
            s.CrashCount++;
            if (s.CrashCount < ArmAfterCrashes)
            {
                SaveState(configDir, s);
                Log.Warn("openrgb", $"detection crash #{s.CrashCount} — bisect arms at {ArmAfterCrashes}");
                return false;
            }
            // Arm: suspects = every currently-enabled detector.
            s.Active = true;
            s.Suspects = EnabledDetectors(configDir);
            Log.Warn("openrgb", $"detection crash #{s.CrashCount} — starting detector bisect over {s.Suspects.Count} detectors");
            if (s.Suspects.Count == 0) { s.Active = false; SaveState(configDir, s); return false; }
        }
        else
        {
            // Crashed with DisabledHalf off => culprit is among the ENABLED
            // suspects. The disabled half is exonerated.
            s.Suspects = s.Suspects.Except(s.DisabledHalf).ToList();
            Log.Info("openrgb", $"bisect: crash with half disabled — {s.Suspects.Count} suspect(s) remain");
        }
        return Narrow(configDir, s);
    }

    /// <summary>Detection succeeded. Returns true when the bisect is mid-run
    /// and needs another relaunch (narrowing continues); false = nothing to do.</summary>
    public static bool OnSuccess(string configDir)
    {
        var s = LoadState(configDir);
        if (!s.Active)
        {
            if (s.CrashCount != 0) { s.CrashCount = 0; SaveState(configDir, s); }
            return false;
        }
        // No crash with DisabledHalf off => the culprit is in the disabled half.
        s.Suspects = s.DisabledHalf.ToList();
        Log.Info("openrgb", $"bisect: clean run — culprit among the {s.Suspects.Count} disabled detector(s)");
        return Narrow(configDir, s);
    }

    /// <summary>Split the suspects, write the config, persist state. When one
    /// suspect remains it becomes the culprit: left disabled forever, all other
    /// suspects re-enabled, bisect closed.</summary>
    static bool Narrow(string configDir, State s)
    {
        if (s.Suspects.Count == 0)
        {
            // Shouldn't happen (crash with nothing suspected) — stand down.
            SetDetectors(configDir, s.DisabledHalf, enabled: true);
            s.Active = false; s.DisabledHalf.Clear(); s.CrashCount = 0;
            SaveState(configDir, s);
            return false;
        }
        if (s.Suspects.Count == 1)
        {
            string culprit = s.Suspects[0];
            s.Culprits.Add(culprit);
            // Re-enable every previously-disabled non-culprit; keep the culprit off.
            SetDetectors(configDir, s.DisabledHalf.Where(d => d != culprit), enabled: true);
            SetDetectors(configDir, new[] { culprit }, enabled: false);
            s.Active = false; s.Suspects.Clear(); s.DisabledHalf.Clear(); s.CrashCount = 0;
            SaveState(configDir, s);
            Log.Warn("openrgb", $"bisect COMPLETE: detector '{culprit}' crashes OpenRGB on this machine — disabled permanently");
            return true;   // relaunch once more with the final config
        }

        // Re-enable last round's disabled half (their fate is decided), then
        // disable the first half of the current suspects.
        SetDetectors(configDir, s.DisabledHalf, enabled: true);
        s.DisabledHalf = s.Suspects.Take(s.Suspects.Count / 2).ToList();
        SetDetectors(configDir, s.DisabledHalf, enabled: false);
        SaveState(configDir, s);
        Log.Info("openrgb", $"bisect: testing with {s.DisabledHalf.Count} of {s.Suspects.Count} suspects disabled");
        return true;
    }

    static List<string> EnabledDetectors(string configDir)
    {
        try
        {
            if (!File.Exists(ConfigPath(configDir))) return new();
            var root = JsonNode.Parse(File.ReadAllText(ConfigPath(configDir)));
            if (root?["Detectors"]?["detectors"] is not JsonObject det) return new();
            return det.Where(kv => kv.Value?.GetValue<bool>() != false)
                      .Select(kv => kv.Key).ToList();
        }
        catch { return new(); }
    }

    static void SetDetectors(string configDir, IEnumerable<string> names, bool enabled)
    {
        try
        {
            if (!File.Exists(ConfigPath(configDir))) return;
            var root = JsonNode.Parse(File.ReadAllText(ConfigPath(configDir))) ?? new JsonObject();
            if (root["Detectors"]?["detectors"] is not JsonObject det) return;
            bool changed = false;
            foreach (var n in names)
                if (det[n]?.GetValue<bool>() != enabled) { det[n] = enabled; changed = true; }
            if (changed)
                SafeFile.WriteAllText(ConfigPath(configDir),   // atomic: a torn OpenRGB.json re-enables everything
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn("openrgb", $"bisect config write failed: {ex.Message}"); }
    }
}
