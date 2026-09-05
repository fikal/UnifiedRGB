using System.ComponentModel;
using System.Text.Json.Serialization;

namespace UnifiedRgb.Core.Automation;

public enum ScheduleAction
{
    /// <summary>Lights out for the window. What Night mode used to be.</summary>
    LightsOff,
    /// <summary>Apply a profile for the window.</summary>
    Profile,
}

/// <summary>"Weekdays 18:00 to 20:00, apply Evening." Night mode generalised:
/// a window, the days it runs on, and what to do while it is open.
///
/// Raises change notifications because the editor toggles day bits and swaps
/// the action, and the row has to follow (the profile picker is only live for
/// a profile schedule). Same shape as SceneAction, which does this already.</summary>
public sealed class ScheduleRule : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    bool _enabled = true;
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(nameof(Enabled)); } }

    /// <summary>Bit 0 is Monday through bit 6 Sunday. Default: every day.</summary>
    int _days = 0x7F;
    public int Days { get => _days; set { _days = value; Changed(nameof(Days)); NotifyDays(); } }

    string _start = "23:00";
    public string Start { get => _start; set { _start = value; Changed(nameof(Start)); } }

    /// <summary>End before Start means the window crosses midnight.</summary>
    string _end = "07:00";
    public string End { get => _end; set { _end = value; Changed(nameof(End)); } }

    ScheduleAction _action = ScheduleAction.LightsOff;
    public ScheduleAction Action
    {
        get => _action;
        set { _action = value; Changed(nameof(Action)); Changed(nameof(IsProfileAction)); }
    }

    string? _profile;
    public string? Profile { get => _profile; set { _profile = value; Changed(nameof(Profile)); } }

    /// <summary>Wait for the machine to go idle before acting, so an evening
    /// session is never cut off mid-use.</summary>
    bool _idleOnly;
    public bool IdleOnly { get => _idleOnly; set { _idleOnly = value; Changed(nameof(IdleOnly)); } }

    /*--- day bits as bindable flags, so the editor is seven check boxes ---*/
    bool Bit(int i) => (_days & (1 << i)) != 0;
    void SetBit(int i, bool on)
    {
        int next = on ? _days | (1 << i) : _days & ~(1 << i);
        if (next == _days) return;
        _days = next;
        Changed(nameof(Days));
        NotifyDays();
    }

    void NotifyDays()
    {
        foreach (var n in new[] { nameof(Mon), nameof(Tue), nameof(Wed), nameof(Thu), nameof(Fri), nameof(Sat), nameof(Sun) })
            Changed(n);
    }

    [JsonIgnore] public bool Mon { get => Bit(0); set => SetBit(0, value); }
    [JsonIgnore] public bool Tue { get => Bit(1); set => SetBit(1, value); }
    [JsonIgnore] public bool Wed { get => Bit(2); set => SetBit(2, value); }
    [JsonIgnore] public bool Thu { get => Bit(3); set => SetBit(3, value); }
    [JsonIgnore] public bool Fri { get => Bit(4); set => SetBit(4, value); }
    [JsonIgnore] public bool Sat { get => Bit(5); set => SetBit(5, value); }
    [JsonIgnore] public bool Sun { get => Bit(6); set => SetBit(6, value); }

    /// <summary>True while the action is Profile, so the row can grey out the
    /// profile picker for a lights-off schedule.</summary>
    [JsonIgnore] public bool IsProfileAction => Action == ScheduleAction.Profile;

    /*--- the pure part ---*/

    /// <summary>Bit index for a day. .NET counts from Sunday; schedules read
    /// Monday first, the way a week is written.</summary>
    public static int BitOf(DayOfWeek d) => ((int)d + 6) % 7;

    public static bool RunsOn(ScheduleRule r, DayOfWeek d) => (r.Days & (1 << BitOf(d))) != 0;

    /// <summary>Is `now` inside this rule's window? A window that crosses
    /// midnight belongs to the day it STARTED, so 23:00 to 07:00 on Monday
    /// covers 01:00 Tuesday.</summary>
    public static bool InWindow(ScheduleRule r, DateTime now)
    {
        if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end)) return false;
        if (start == end) return false;   // zero length: never open
        var t = now.TimeOfDay;
        if (start < end) return t >= start && t < end && RunsOn(r, now.DayOfWeek);
        if (t >= start) return RunsOn(r, now.DayOfWeek);              // started today
        if (t < end) return RunsOn(r, now.AddDays(-1).DayOfWeek);     // started yesterday
        return false;
    }

    /// <summary>Window is open, the rule is on, and (for an idle-only rule) the
    /// machine has actually gone quiet.</summary>
    public static bool IsActive(ScheduleRule r, DateTime now, double idleSeconds, double idleThreshold)
        => r.Enabled && InWindow(r, now) && (!r.IdleOnly || idleSeconds >= idleThreshold);

    /// <summary>The next time any enabled rule opens, for the "what happens
    /// next" line in Settings. Null when nothing is scheduled.</summary>
    public static (DateTime When, ScheduleRule Rule)? NextChange(IReadOnlyList<ScheduleRule>? rules, DateTime now)
    {
        if (rules == null) return null;
        (DateTime When, ScheduleRule Rule)? best = null;
        foreach (var r in rules)
        {
            if (!r.Enabled || r.Days == 0 || !TimeSpan.TryParse(r.Start, out var start)) continue;
            // Look ahead a whole week plus today, so a rule that runs only on
            // one weekday still reports its next run.
            for (int d = 0; d <= 7; d++)
            {
                var day = now.Date.AddDays(d);
                if (!RunsOn(r, day.DayOfWeek)) continue;
                var when = day + start;
                if (when <= now) continue;
                if (best == null || when < best.Value.When) best = (when, r);
                break;
            }
        }
        return best;
    }

    /// <summary>"Weekdays 18:00 to 20:00, apply Evening".</summary>
    public static string Describe(ScheduleRule r)
    {
        string what = r.Action == ScheduleAction.LightsOff
            ? "lights off"
            : $"apply {(string.IsNullOrWhiteSpace(r.Profile) ? "a profile" : r.Profile)}";
        return $"{DaysText(r.Days)} {r.Start} to {r.End}, {what}";
    }

    static readonly string[] Initials = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static string DaysText(int days) => (days & 0x7F) switch
    {
        0x7F => "Every day",
        0x1F => "Weekdays",
        0x60 => "Weekends",
        0 => "Never",
        _ => string.Join(" ", Enumerable.Range(0, 7).Where(i => (days & (1 << i)) != 0).Select(i => Initials[i])),
    };
}
