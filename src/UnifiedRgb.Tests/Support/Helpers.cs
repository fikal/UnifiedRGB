using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Small shared helpers. Suites pull these in unqualified with  |
|   using static UnifiedRgb.Tests.TestHelpers;                 |
| so a call site reads Grid(8, 8) rather than a class prefix   |
| on every line of geometry.                                   |
\*-----------------------------------------------------------*/
public static class TestHelpers
{
    /// <summary>Poll a condition to a deadline. For the paths that finish on
    /// another thread (a socket answering, an applier lane draining), where
    /// asserting straight after the call would race the work.</summary>
    public static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(5); }
        return cond();
    }

    /// <summary>n LEDs in a row, evenly spaced across the x axis.</summary>
    public static LedPos[] Line(int n)
    {
        var p = new LedPos[n];
        for (int i = 0; i < n; i++) p[i] = new LedPos(n <= 1 ? 0.5f : i / (float)(n - 1), 0.5f);
        return p;
    }

    /// <summary>A w x h matrix of LEDs, row-major - keyboard-shaped geometry
    /// for the effects that render against real positions.</summary>
    public static LedPos[] Grid(int w, int h)
    {
        var p = new LedPos[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                p[y * w + x] = new LedPos(x / (float)(w - 1), y / (float)(h - 1));
        return p;
    }

    /// <summary>Frame equality with a tolerance, in LSBs per channel: the
    /// effects do floating-point color maths, so two renders of the same
    /// instant can differ by a bit without being different frames.</summary>
    public static bool SameWithin(Rgb[] a, Rgb[] b, int lsb)
    {
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i].R - b[i].R) > lsb || Math.Abs(a[i].G - b[i].G) > lsb || Math.Abs(a[i].B - b[i].B) > lsb) return false;
        return true;
    }

    /// <summary>A scratch directory for the file-format suites. NOT under the
    /// isolation root: these exercise plain file IO at a path of their own
    /// choosing, and never go near AppPaths.</summary>
    public static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "unifiedrgb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Make a bundle this machine exported read as made somewhere else.
    /// The manifest's MachineName is the only thing that says where a bundle came
    /// from (ImportPreview.SameMachine), and an Export in the harness always
    /// writes this machine's, which is the trusted case.</summary>
    public static void MadeOn(string bundlePath, string machine)
    {
        using var zip = System.IO.Compression.ZipFile.Open(bundlePath, System.IO.Compression.ZipArchiveMode.Update);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("the bundle has no manifest");
        System.Text.Json.Nodes.JsonObject manifest;
        using (var reader = new StreamReader(entry.Open()))
            manifest = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        manifest["MachineName"] = machine;
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new System.Text.UTF8Encoding(false));
        writer.Write(manifest.ToJsonString());
    }

    /// <summary>The repo root, found from THIS file's compile-time path rather
    /// than the working directory: the harness runs from bin/, and CI runs it
    /// from somewhere else again. Null for a harness built outside a source tree.</summary>
    public static string? RepoRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? ".");
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UnifiedRgb.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>A repo file's text ('/'-separated path from the root); null
    /// outside a source tree or when the file is not there.</summary>
    public static string? SourceOf(string rel)
    {
        if (RepoRoot() is not string root) return null;
        string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
