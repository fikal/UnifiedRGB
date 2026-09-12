namespace UnifiedRgb.Core.Automation;

/// <summary>Foreground-app rule: when a process whose name contains Process
/// is in the foreground, apply Profile. Lives in Core (not with the rest of
/// the settings model) because the automation decision it feeds is pure logic
/// the test harness drives directly.</summary>
public sealed class AutomationRule : System.ComponentModel.INotifyPropertyChanged
{
    // Change notification like ScheduleRule: the rules dialog edits these
    // objects in place, and the Settings page's "cs2 -> Gaming" summary binds
    // to the same objects, so without it the summary stayed stale until a rule
    // was added or removed. The serializer ignores the event.
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    string _process = "", _profile = "";
    public string Process { get => _process; set { if (_process == value) return; _process = value; Changed(nameof(Process)); } }
    public string Profile { get => _profile; set { if (_profile == value) return; _profile = value; Changed(nameof(Profile)); } }

    /// <summary>Profile of the first rule matching the foreground process, or
    /// null. Order is priority: the top match wins, which is the order the
    /// rules dialog lets the user drag. A trailing ".exe" is tolerated because
    /// people paste file names, and the match is a substring so "cs2" catches
    /// "cs2.exe" and launcher variants.</summary>
    public static string? Match(IReadOnlyList<AutomationRule>? rules, string? process)
    {
        if (rules == null || rules.Count == 0 || string.IsNullOrEmpty(process)) return null;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (string.IsNullOrWhiteSpace(r.Process) || string.IsNullOrWhiteSpace(r.Profile)) continue;
            string want = r.Process.Trim();
            if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want = want[..^4];
            if (want.Length == 0) continue;
            if (process.Contains(want, StringComparison.OrdinalIgnoreCase)) return r.Profile;
        }
        return null;
    }
}
