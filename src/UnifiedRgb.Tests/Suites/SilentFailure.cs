using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Silence is this codebase's recurring bug.                    |
|                                                              |
| Not one of these was a wrong calculation. Each was something  |
| failing and saying nothing:                                   |
|                                                              |
|   a profile saved for hardware that had since changed - the   |
|     frame truncated, the out-of-range effect dropped by a     |
|     bare `continue`, a third of the board dark                |
|   a mouse asleep behind a charging pad - OpenPad returned an  |
|     empty list, so the device was simply absent               |
|   the session lock flag stranded by a suspend - the app       |
|     stopped writing to EVERYTHING until it was restarted      |
|   a pad we invented a 20-LED strip for - it took colours,     |
|     reported success, lit nothing, and filled a user's log    |
|     with 5,546 copies of the refusal                          |
|                                                              |
| Every one cost hours, and one destroyed the log evidence for  |
| the bug underneath it. They share a shape: a catch that       |
| swallows, or a path that gives up, without a word.            |
|                                                              |
| So this suite reads the SOURCE. Two rules:                    |
|                                                              |
|   1. In the hot areas - drivers, the network/bridge layer,    |
|      the services, the view-model and profile store - a catch |
|      that neither logs nor rethrows must SAY WHY in a comment.|
|      Those are the paths where silence has actually cost us.  |
|   2. Everywhere else, a ratchet: the count may fall, never    |
|      rise. Nobody has to go and annotate 119 dialog handlers  |
|      today, but nor can the number quietly grow again.        |
|                                                              |
| The rule is deliberately about a COMMENT, not about adding a  |
| log line everywhere. Plenty of these are genuinely best       |
| effort - a clipboard another app owns, a DragMove outside a   |
| drag. The bug was never that they swallow; it is that a       |
| reader cannot tell those apart from the accidents.            |
\*-----------------------------------------------------------*/
static class SilentFailureSuite
{
    /// <summary>Directories where a swallowed failure has actually bitten, so
    /// the comment is required rather than merely counted.</summary>
    static readonly string[] Hot =
    {
        "UnifiedRgb.Core/Devices/",
        "UnifiedRgb.Core/Net/",
        "UnifiedRgb.App/Services/",
        "UnifiedRgb.App/MainViewModel",
        "UnifiedRgb.App/ProfileStore",
    };

    /// <summary>The cold-area count on the day this was written. It may only go
    /// DOWN. If a change makes this fail, the fix is to say why the new catch
    /// swallows - not to raise the number.</summary>
    const int ColdBaseline = 119;

    /// <summary>A catch body that mentions any of these is not silent: it tells
    /// somebody. Note that returning a default is NOT on the list - "return
    /// null" is precisely how a sleeping mouse went missing.
    ///
    /// Handing the message BACK also counts, because the caller puts it in front
    /// of the user: the import path fills a `problem` out-parameter that way and
    /// the shim installer collects the first failure into `firstErr` to return.
    /// Only assigning an actual message counts - "problem = null" at the top of
    /// a method is not an explanation.</summary>
    static readonly Regex Speaks = new(
        @"Log\.|Report\(|throw|Console\.|Note\(|[Pp]roblem\s*\?*=\s*(?!null)|[Ee]rr\w*\s*\?*=\s*(?!null)",
        RegexOptions.Compiled);

    /// <summary>catch, an optional (Exception ex), then a body with no nested
    /// braces. A handler complex enough to need braces inside it is doing real
    /// work and is not the silent kind this is looking for.</summary>
    static readonly Regex Catch = new(@"catch\s*(\([^)]*\))?\s*\{([^{}]*)\}", RegexOptions.Compiled);

    public static void Run(Harness t)
    {
        string? root = RepoRoot();
        if (root == null)
        {
            t.Skip("silent-failure scan needs the source tree, which a published binary does not carry");
            return;
        }

        var unexplainedHot = new List<string>();
        int cold = 0;

        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = path.Substring(root.Length).Replace('\\', '/').TrimStart('/');
            // Tests are allowed to swallow: a fake that eats an exception is the
            // test's business, and the harness reports the failure either way.
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("UnifiedRgb.Tests/")) continue;

            string text = File.ReadAllText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (Match m in Catch.Matches(text))
            {
                string body = m.Groups[2].Value;
                if (Speaks.IsMatch(body)) continue;
                int ln = text.Take(m.Index).Count(c => c == '\n');   // 0-based
                if (Explained(lines, ln, body)) continue;
                if (Hot.Any(h => rel.Contains(h))) unexplainedHot.Add($"{rel}:{ln + 1}");
                else cold++;
            }
        }

        t.Section("a swallowed failure says why");
        t.Check(unexplainedHot.Count == 0,
            unexplainedHot.Count == 0
                ? "every silent catch in a driver, service, the view-model or the profile store explains itself"
                : $"{unexplainedHot.Count} silent catch(es) in hot code with no reason: {string.Join(", ", unexplainedHot.Take(6))}"
                  + (unexplainedHot.Count > 6 ? " …" : ""));

        t.Section("the rest may only get quieter");
        t.Check(cold <= ColdBaseline,
            cold <= ColdBaseline
                ? $"unexplained silent catches outside the hot paths: {cold} (baseline {ColdBaseline}, may only fall)"
                : $"unexplained silent catches rose to {cold}, over the baseline of {ColdBaseline} - say why the new one swallows");
    }

    /// <summary>A reason is a comment ON the catch line after the keyword, INSIDE
    /// the braces, or on the line immediately above it. All three read naturally
    /// at the call site, which is the point - the reader must not have to go
    /// anywhere to find out whether the silence was meant.</summary>
    static bool Explained(string[] lines, int ln, string body)
    {
        if (body.Contains("//") || body.Contains("/*")) return true;
        string here = lines[ln];
        int at = here.IndexOf("catch", StringComparison.Ordinal);
        if (at >= 0 && here.IndexOf("//", at, StringComparison.Ordinal) >= 0) return true;
        return ln > 0 && lines[ln - 1].TrimStart().StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>The repo, found from THIS file's compile-time path rather than
    /// the working directory: the harness runs from bin/, and CI runs it from
    /// somewhere else again.</summary>
    static string? RepoRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here) ?? ".");
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UnifiedRgb.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
