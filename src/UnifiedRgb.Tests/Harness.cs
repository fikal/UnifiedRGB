namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The scoreboard every suite writes into.                      |
|                                                              |
| One instance per run, handed to each suite in turn. It keeps |
| the counts, remembers which suite and section a failure came |
| from, and prints failures as they happen so a long run says  |
| what went wrong without waiting for the end.                 |
|                                                              |
| Deliberately tiny and dependency-free: `Check(bool, string)` |
| is the whole contract, and it is what every one of the       |
| assertions in this project is written against.               |
\*-----------------------------------------------------------*/
public sealed class Harness
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Every failure, suite-and-section qualified, for the recap.</summary>
    public IReadOnlyList<string> Failures => _failures;

    readonly List<string> _failures = new();
    string _suite = "", _section = "";

    /// <summary>Name the group of checks that follow. Free-form and purely for
    /// reporting: a failure names its section, so a bare assertion message
    /// like "roundtrip" is still traceable to the thing being tested.</summary>
    public void Section(string name) => _section = name;

    internal void BeginSuite(string name) { _suite = name; _section = ""; }

    public void Check(bool cond, string what)
    {
        if (cond) { Passed++; return; }
        Failed++;
        string where = _section.Length > 0 ? $"{_suite} / {_section}" : _suite;
        _failures.Add($"{where}: {what}");
        Console.WriteLine($"  FAIL  {where}: {what}");
    }

    /// <summary>Equality with both values in the message - the failure line
    /// says what was expected AND what arrived, which a bare Check cannot.</summary>
    public void Equal<T>(T expected, T actual, string what)
        => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected {expected}, got {actual}");
}
