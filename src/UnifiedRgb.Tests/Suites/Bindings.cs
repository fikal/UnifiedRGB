using System.IO;
using System.Text.RegularExpressions;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The one XAML mistake that fails SILENTLY.                    |
|                                                              |
| RelativeSource={RelativeSource AncestorType=Window} makes    |
| the WINDOW the binding source, not its DataContext. So a     |
| view-model path written that way is looked up on MainWindow, |
| finds nothing, and does nothing - no exception, no warning,  |
| just a control that never updates.                           |
|                                                              |
| It shipped a Shows page whose Visibility never resolved, so  |
| the page was permanently visible on top of the lighting      |
| pane. Nothing in a build or a test run could see it; only    |
| looking at the screen could. This is the cheapest thing that |
| can.                                                         |
|                                                              |
| Scoped to the panes MainWindow hosts. Binding to a window's  |
| OWN property is a real pattern - ExitBehaviorWindow and      |
| SensorRulesWindow both do it - so the rule is not "never     |
| use AncestorType=Window", it is "when you do, the path must  |
| resolve: either through DataContext, or to a property        |
| MainWindow actually has".                                    |
\*-----------------------------------------------------------*/
static class BindingsSuite
{
    public static void Run(Harness t)
    {
        t.Section("view bindings resolve");

        string? views = FindViewsDir();
        if (views == null)
        {
            // Running from somewhere without the sources beside it. Say so
            // rather than passing quietly, which would make this look like
            // coverage it is not.
            t.Check(true, "SKIPPED: the XAML sources are not next to this assembly");
            return;
        }

        int checkedCount = 0;

        foreach (string file in Directory.GetFiles(views, "*.xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (string expr in BindingExpressions(text))
            {
                if (!expr.Contains("AncestorType=Window", StringComparison.Ordinal)) continue;

                // The path is the first positional argument, if there is one.
                var m = Regex.Match(expr, @"^\{Binding\s+(?<path>[A-Za-z_][\w.]*)\s*[,}]");
                if (!m.Success) continue;   // {Binding RelativeSource=...} with no path binds the source itself
                string path = m.Groups["path"].Value;
                checkedCount++;

                // Through the DataContext is always fine: that is the view model.
                if (path.StartsWith("DataContext.", StringComparison.Ordinal)) continue;

                // Otherwise it has to be a property the window really has.
                string first = path.Split('.')[0];
                bool onWindow = typeof(UnifiedRgb.App.MainWindow).GetProperty(first) != null;
                t.Check(onWindow,
                    $"{Path.GetFileName(file)}: {{Binding {path}}} with AncestorType=Window resolves against "
                    + "MainWindow itself - prefix it with DataContext. or drop the RelativeSource");
            }
        }

        t.Check(checkedCount > 0, $"the scan found window-relative bindings to check ({checkedCount})");
    }

    /// <summary>Every {Binding ...} in the text, with nested braces respected.
    /// A regex cannot do this: the first closing brace usually belongs to an
    /// inner {StaticResource ...}, so a naive pattern stops before reaching the
    /// RelativeSource that matters - which is exactly how the first version of
    /// this check passed while the bug it was written for sat in the file.</summary>
    static IEnumerable<string> BindingExpressions(string text)
    {
        const string open = "{Binding";
        for (int i = text.IndexOf(open, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(open, i + 1, StringComparison.Ordinal))
        {
            int depth = 0;
            for (int j = i; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}' && --depth == 0) { yield return text[i..(j + 1)]; break; }
            }
        }
    }

    /// <summary>Walk up from the test assembly looking for the repo, so this
    /// works from bin/Debug and bin/Release alike. Null when the sources are not
    /// there, which is a skip rather than a failure.</summary>
    static string? FindViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; dir != null && up < 8; up++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "UnifiedRgb.slnx"))) continue;
            string views = Path.Combine(dir.FullName, "src", "UnifiedRgb.App", "Views");
            return Directory.Exists(views) ? views : null;
        }
        return null;
    }
}
