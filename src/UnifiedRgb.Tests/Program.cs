using System.Diagnostics;
using UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| UnifiedRGB test runner - zero dependencies, on purpose.      |
|                                                              |
| Every assertion in this project is a Check(bool, string).    |
| That is the whole framework: no attributes, no reflection,   |
| no packages, and a run is one process that either prints     |
| "0 failed" or names what broke.                              |
|                                                              |
|   dotnet run --project src/UnifiedRgb.Tests                  |
|   dotnet run --project src/UnifiedRgb.Tests -- Devices       |
|   dotnet run --project src/UnifiedRgb.Tests -- --list        |
|                                                              |
| Exit code = the number of failures, so a build step or a     |
| habit that reaches for `dotnet test` still gets a red run.   |
|                                                              |
| Suites live in Suites/ and are listed in Suites.cs; the      |
| shared fakes and helpers are in Support/.                    |
\*-----------------------------------------------------------*/

// Isolation FIRST, before any other statement. AppPaths resolves its roots in
// a static initializer the first time anything touches it, so the redirect has
// to be in place before a single suite type is loaded - and a run that lost
// its isolation must refuse to start rather than write the user's real files.
if (!Isolation.Enforce()) return 1;

if (args.Contains("--list"))
{
    foreach (var (suiteName, _) in Suites.All) Console.WriteLine(suiteName);
    return 0;
}

// Anything that is not a switch is a name filter, matched case-insensitively
// as a substring. `--filter` is accepted and ignored so both spellings work.
var filters = args.Where(a => !a.StartsWith('-')).ToList();
bool quiet = args.Contains("--quiet");

var harness = new Harness();
var total = Stopwatch.StartNew();
int ran = 0, skipped = 0;

foreach (var (name, run) in Suites.All)
{
    if (filters.Count > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
    {
        skipped++;
        continue;
    }

    ran++;
    int before = harness.Passed + harness.Failed;
    harness.BeginSuite(name);
    var sw = Stopwatch.StartNew();
    try
    {
        run(harness);
    }
    catch (Exception ex)
    {
        // A suite that throws loses every check after the throw. Record it as
        // a failure so the total cannot quietly shrink and read as a pass.
        harness.Check(false, $"the suite threw, so its remaining checks never ran: {ex}");
    }
    sw.Stop();
    if (!quiet)
        Console.WriteLine($"  {name,-16}{harness.Passed + harness.Failed - before,6} checks {sw.Elapsed.TotalSeconds,7:0.00}s");
}
total.Stop();

if (ran == 0)
{
    Console.Error.WriteLine($"no suite matched: {string.Join(", ", filters)}");
    Console.Error.WriteLine("known suites: " + string.Join(", ", Suites.All.Select(s => s.Name)));
    return 1;
}

if (harness.Failed > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{harness.Failed} failure(s):");
    foreach (string f in harness.Failures) Console.WriteLine($"  {f}");
}

Console.WriteLine();
Console.WriteLine($"{harness.Passed} passed, {harness.Failed} failed  "
                + $"({ran} suite(s){(skipped > 0 ? $", {skipped} skipped" : "")}, {total.Elapsed.TotalSeconds:0.00}s)");
return harness.Failed;
