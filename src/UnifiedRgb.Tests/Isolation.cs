using System.IO;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Isolation, and the hard gate that enforces it.               |
|                                                              |
| Several suites exercise real persistence, and AppPaths       |
| resolves its roots in a static initializer the first time    |
| anything touches it. Point those roots at a private          |
| directory so the harness can never read or write the running |
| user's real settings.                                        |
|                                                              |
| Enforce() MUST be the first thing the process does. It is    |
| the first statement in Program.cs for exactly that reason:   |
| nothing may touch AppPaths before the redirect is in place.  |
\*-----------------------------------------------------------*/
public static class Isolation
{
    /// <summary>The private directory this run is confined to.</summary>
    public static string Root { get; private set; } = "";

    /// <summary>Redirect the config roots and prove the redirect took. False
    /// means the caller must exit WITHOUT running a single test.
    ///
    /// This is a gate, not an assertion, and the difference is the whole
    /// point. The soft version was not enough: the redirect was once silently
    /// inert (AppPaths resolved its roots from a static field that had not
    /// been initialized yet), three isolation assertions duly FAILED, and the
    /// harness carried on anyway - straight into the tests that write
    /// scenes.json and lcd.json, against the real profile. They wrote their
    /// fixtures and deleted the files on the way out, and the user's saved LCD
    /// design and screens went with them. A suite that has lost its isolation
    /// must not run one test; it must refuse to start.</summary>
    public static bool Enforce()
    {
        Root = Path.Combine(Path.GetTempPath(), "UnifiedRgbTests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("UNIFIEDRGB_CONFIG_DIR", Path.Combine(Root, "config"));
        Environment.SetEnvironmentVariable("UNIFIEDRGB_LOCAL_DIR", Path.Combine(Root, "local"));

        // A run that is killed before its cleanup leaves one directory behind
        // under the OS temp tree, which is the right place for it to be swept up.
        string root = Root;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        };

        if (AppPaths.ConfigDir.StartsWith(Root, StringComparison.OrdinalIgnoreCase) &&
            AppPaths.LocalDir.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            return true;

        Console.Error.WriteLine("REFUSING TO RUN: the test config redirect is not in effect.");
        Console.Error.WriteLine($"  expected under : {Root}");
        Console.Error.WriteLine($"  ConfigDir      : {AppPaths.ConfigDir}");
        Console.Error.WriteLine($"  LocalDir       : {AppPaths.LocalDir}");
        Console.Error.WriteLine("Tests write real files. Running now would edit the user's own settings,");
        Console.Error.WriteLine("profiles, screens and layouts. Fix AppPaths.Redirect before running.");
        return false;
    }
}
