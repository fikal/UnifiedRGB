using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App.Services;

/// <summary>The user's own files as an import sees them, through ONE reader
/// that only looks.
///
/// The stores' own loaders are for the running app and have consequences:
/// ProfileStore.LoadJson re-arms saves to a file that could not be read at
/// startup, and a damaged file is copied aside (CanvasLayout.Load does the
/// same). The preview read scenes.json and lcd.json through those loaders, so
/// merely opening the import dialog re-armed saves to a file the app was
/// running on defaults for - and on exit the default design was written over
/// the user's real one. Nothing here touches that state. The reload that
/// follows a successful import (ImportResult's flags) is what re-arms saves,
/// because only that also replaces what is held in memory.
///
/// Both halves of an import read through here: the preview, which reports a
/// file it cannot read and carries on, and the plan Apply builds, which
/// refuses to merge into a file it cannot read rather than replace everything
/// in it with the bundle's part.</summary>
internal static class CurrentSetup
{
    /// <summary>A file this machine has but that could not be read right now
    /// (locked by a sync client or a scanner, permissions).</summary>
    internal sealed class UnreadableException(string what, string? why)
        : IOException($"your {what} could not be read right now ({why}), so nothing was imported over it. Close whatever has it open and try again.");

    /// <summary>Read one of our files. Null = absent, or present but unreadable
    /// (then <paramref name="trouble"/> says why). Never throws, never logs,
    /// never records anything. A sharing violation is retried briefly, as the
    /// app's own loader does (ProfileStore.ReadWithRetry): a sync client or a
    /// scanner holding the file for a moment is the usual reason it is locked.</summary>
    internal static string? Peek(string path, out string? trouble)
    {
        trouble = null;
        for (int attempt = 0; ; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            // Vanished between the check and the read: nothing there, which is all a peek reports.
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }   // retried below
            catch (Exception ex) { trouble = ex.Message; return null; }   // handed back; every caller says it or refuses over it
        }
    }

    /// <summary>One store, parsed by its own rules. Null when the file is
    /// absent or unusable.</summary>
    /// <param name="forApply">True for the plan Apply builds, false for the
    /// preview; it only changes how a DAMAGED file is described. A file that
    /// stays locked throws UnreadableException either way: the preview used to
    /// compare it as if it were empty, offering the user's own items as new.</param>
    /// <param name="trouble">A sentence when the file is there but damaged. A damaged file reads as empty
    /// either way: that is what the app itself runs on in its place, and an
    /// import that replaces it backs the original up first.</param>
    static T? Read<T>(string name, bool forApply, Func<string, T?> parse, out string? trouble) where T : class
    {
        string? text = Peek(AppPaths.Config(name), out string? why);
        if (text == null)
        {
            trouble = null;
            if (why == null) return null;
            throw new UnreadableException(name, why);
        }
        T? value;
        try { value = parse(text); }
        catch (Exception) { value = null; }   // malformed JSON is the 'damaged' case just below, said in trouble
        trouble = value == null
            ? forApply
                ? $"your {name} was damaged and has been replaced; the original is in the backup folder"
                : $"your {name} is damaged and could not be read, so it is treated as empty; importing over it keeps a copy of the original"
            : null;
        return value;
    }

    internal static List<Profile> Profiles(bool forApply, out string? trouble)
        => Read("profiles.json", forApply, text =>
        {
            var list = JsonSerializer.Deserialize<List<Profile?>>(text)?.OfType<Profile>().ToList();
            // The same scrub the store applies on load, so a hand-edited null
            // name cannot throw out of a comparison here.
            foreach (var p in list ?? new())
            {
                p.Name ??= "";
                p.Effects?.RemoveAll(e => e == null);
                foreach (var e in p.Effects ?? new()) e.Device ??= "";
            }
            return list;
        }, out trouble) ?? new List<Profile>();

    /// <summary>scenes.json, through SceneStore.TryParse and so through the
    /// store's own Normalize.</summary>
    internal static SceneStore Scenes(bool forApply, out string? trouble)
        => Read("scenes.json", forApply, SceneStore.TryParse, out trouble) ?? new SceneStore();

    /// <summary>The design on the panel (lcd.json) through LcdDesign.Normalize,
    /// or null when there is none to compare with. Null is deliberately not
    /// LcdDesign.Load's built-in default: calling a bundle's screen a conflict
    /// with a default the user never made would ask them to choose between the
    /// two. <paramref name="exists"/> says whether the file is there at all.</summary>
    internal static LcdDesign? LiveDesign(bool forApply, out bool exists, out string? trouble)
    {
        exists = File.Exists(AppPaths.Config("lcd.json"));
        return Read("lcd.json", forApply,
            text => JsonSerializer.Deserialize<LcdDesign>(text) is { } d ? LcdDesign.Normalize(d) : null,
            out trouble);
    }

    /// <summary>canvas.json, through CanvasLayout.TryParse: Load copies a
    /// damaged file aside, which a preview must not.</summary>
    internal static CanvasLayout Canvas(bool forApply, out string? trouble)
        => Read("canvas.json", forApply, CanvasLayout.TryParse, out trouble) ?? new CanvasLayout();

    /// <summary>settings.json as JSON nodes, so a merge can write back the
    /// keys it means to change and leave every other one as it found it.</summary>
    internal static JsonObject SettingsNodes(bool forApply, out string? trouble)
        => Read("settings.json", forApply, text => JsonNode.Parse(text) as JsonObject, out trouble) ?? new JsonObject();

    /// <summary>One of the machine wiring files, verbatim; "" when absent or
    /// unreadable, which compares as different and so is written rather than
    /// skipped.</summary>
    internal static string RawText(string path) => Peek(path, out _) ?? "";
}
