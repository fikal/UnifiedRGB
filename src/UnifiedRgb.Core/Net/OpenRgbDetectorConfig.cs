using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnifiedRgb.Core.Net;

/// <summary>The one place that edits the bundled OpenRGB's detector map
/// (OpenRGB.json -> Detectors.detectors: name -> bool): load, mutate, write
/// back atomically when something changed. The crash bisect, the conflict
/// policy and the natively-driven disable list each used to carry their own
/// copy of this parse/walk/write.</summary>
static class OpenRgbDetectorConfig
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static string PathIn(string configDir) => Path.Combine(configDir, "OpenRGB.json");

    /// <summary>Names of the enabled detectors; empty when the file or the
    /// section is missing or unreadable.</summary>
    public static List<string> Enabled(string configDir)
    {
        try
        {
            string path = PathIn(configDir);
            if (!File.Exists(path)) return new();
            if (JsonNode.Parse(File.ReadAllText(path))?["Detectors"]?["detectors"] is not JsonObject det) return new();
            return det.Where(kv => kv.Value?.GetValue<bool>() != false).Select(kv => kv.Key).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Run <paramref name="mutate"/> over the detector map and write
    /// the file back when it reports a change. With createIfMissing the
    /// skeleton is built when the file or section is absent (the app's own
    /// disable list must survive OpenRGB regenerating its config); otherwise a
    /// missing file is a no-op. Throws on I/O and malformed JSON - callers own
    /// the log line. Returns true when the file was written.</summary>
    public static bool Edit(string configDir, Func<JsonObject, bool> mutate, bool createIfMissing = false)
    {
        string path = PathIn(configDir);
        bool exists = File.Exists(path);
        if (!exists && !createIfMissing) return false;
        JsonNode root = (exists ? JsonNode.Parse(File.ReadAllText(path)) : null) ?? new JsonObject();
        if (root["Detectors"]?["detectors"] is not JsonObject det)
        {
            if (!createIfMissing) return false;
            det = new JsonObject();
            root["Detectors"] = new JsonObject { ["detectors"] = det };
        }
        if (!mutate(det)) return false;
        SafeFile.WriteAllText(path, root.ToJsonString(Indented));   // atomic: a torn OpenRGB.json re-enables everything
        return true;
    }

    /// <summary>Set every name to <paramref name="enabled"/>; true when any entry changed.</summary>
    public static bool Set(JsonObject det, IEnumerable<string> names, bool enabled)
    {
        bool changed = false;
        foreach (var n in names)
            if (det[n]?.GetValue<bool>() != enabled) { det[n] = enabled; changed = true; }
        return changed;
    }
}
