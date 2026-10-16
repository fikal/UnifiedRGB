using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Core.Automation;

/// <summary>"When the CPU hits 85 degrees, go red." One threshold rule over a
/// sensor the app already reads. Deliberately not a general expression engine:
/// a source, a direction, a number, a profile.</summary>
public sealed class SensorRule : System.ComponentModel.INotifyPropertyChanged
{
    // Change notification like ScheduleRule: the rules dialog edits these
    // objects in place and the Settings page's summary line binds to the same
    // objects, so without it the summary stayed stale until a rule was added
    // or removed. The serializer ignores the event.
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }

    string _source = SensorSources.CpuTemp, _profile = "";
    bool _above = true, _enabled = true;
    double _threshold = 85, _clearMargin = 3;
    int _holdSeconds = 5;

    /// <summary>One of the <see cref="SensorSources"/> constants, or a prefixed
    /// name for the per-board lists ("Board:Fan #2", "Fan:CPU Fan").</summary>
    public string Source { get => _source; set => Set(ref _source, value, nameof(Source)); }

    /// <summary>True: fire at or above Threshold. False: at or below.</summary>
    public bool Above { get => _above; set => Set(ref _above, value, nameof(Above)); }

    /// <summary>Finite only, here in the model and not just in the dialog.
    /// double.TryParse accepts "NaN" and overflows "1e999" to infinity, and a
    /// rule carrying either could not be written: the JSON serializer refuses
    /// non-finite numbers, the settings save failed, and because the rules share
    /// settings.json with everything else, NO preference persisted from then on
    /// while the previous file sat on disk looking fine. A non-finite value is
    /// dropped and the old one re-announced, so a bound box snaps back to it.</summary>
    public double Threshold { get => _threshold; set => SetFinite(ref _threshold, value, nameof(Threshold)); }

    /// <summary>How far past the threshold the value must come back before the
    /// rule releases, so a reading sitting on the line cannot chatter. Applied
    /// on the far side of the threshold from the trigger direction.</summary>
    public double ClearMargin { get => _clearMargin; set => SetFinite(ref _clearMargin, value, nameof(ClearMargin)); }

    void SetFinite(ref double field, double value, string name)
    {
        if (double.IsFinite(value)) Set(ref field, value, name);
        else Changed(name);   // refused: whoever is bound re-reads the value that stands
    }

    /// <summary>The condition must hold this long before the rule flips, in
    /// EITHER direction. With ClearMargin this makes a toggle faster than once
    /// per hold impossible.</summary>
    public int HoldSeconds { get => _holdSeconds; set => Set(ref _holdSeconds, value, nameof(HoldSeconds)); }

    public string Profile { get => _profile; set => Set(ref _profile, value, nameof(Profile)); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value, nameof(Enabled)); }

    /// <summary>Fields this build does not know, written back untouched so a
    /// rollback build does not strip what a newer one stored on the rule.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>Where a rule stands between ticks: whether it is currently firing,
/// and when the pending flip started (null when nothing is pending).</summary>
public readonly record struct SensorRuleState(bool Active, double? SinceSeconds);

/// <summary>The winning rule for a tick, with the numbers behind it so the UI
/// can explain itself.</summary>
public readonly record struct SensorHit(
    string Source, string Profile, double Value, double Threshold, bool Above)
{
    /// <summary>"CPU temp 87°C at or above 85°C", the readable half of the
    /// automation status line.</summary>
    public string Describe()
    {
        string unit = SensorSources.Unit(Source);
        string dir = Above ? "at or above" : "at or below";
        return $"{SensorSources.Label(Source)} {Value:0}{unit} {dir} {Threshold:0}{unit}";
    }
}

/// <summary>Pure hysteresis + hold state machine. One step per tick per rule;
/// the caller owns the state across ticks (SensorRuleTracker, in the app).</summary>
public static class SensorRuleEvaluator
{
    /// <param name="value">Current reading, or null when the sensor is
    /// unavailable (no PawnIO, board sensor gone). Null always resolves to
    /// inactive and forgets any pending flip.</param>
    /// <param name="nowSeconds">Any monotonic clock, in seconds.</param>
    public static SensorRuleState Step(SensorRule rule, double? value, SensorRuleState state, double nowSeconds)
    {
        if (value is not double v) return new SensorRuleState(false, null);

        double margin = Math.Abs(rule.ClearMargin);
        // Trip on the threshold; release only once the value has cleared the
        // margin on the other side. Between the two the rule holds its state,
        // which is the whole point of the band.
        bool wantsFlip = state.Active
            ? (rule.Above ? v < rule.Threshold - margin : v > rule.Threshold + margin)
            : (rule.Above ? v >= rule.Threshold : v <= rule.Threshold);

        // Not heading anywhere: forget a part-finished hold so a value that
        // bounces back out has to start its wait over.
        if (!wantsFlip) return new SensorRuleState(state.Active, null);

        double since = state.SinceSeconds ?? nowSeconds;
        if (nowSeconds - since >= Math.Max(0, rule.HoldSeconds))
            return new SensorRuleState(!state.Active, null);
        return new SensorRuleState(state.Active, since);
    }

    /// <summary>The first enabled rule that is currently firing. List order is
    /// priority, like the app rules.</summary>
    /// <param name="profileExists">Optional guard: a rule pointing at a profile
    /// that was renamed or deleted is skipped rather than applying nothing.</param>
    public static SensorHit? FirstActive(
        IReadOnlyList<SensorRule>? rules, IReadOnlyList<SensorRuleState>? states,
        IReadOnlyList<double?>? values, Func<string, bool>? profileExists = null)
    {
        if (rules == null || states == null || values == null) return null;
        int n = Math.Min(rules.Count, Math.Min(states.Count, values.Count));
        for (int i = 0; i < n; i++)
        {
            var r = rules[i];
            if (!r.Enabled || !states[i].Active) continue;
            if (string.IsNullOrWhiteSpace(r.Profile)) continue;
            if (profileExists != null && !profileExists(r.Profile)) continue;
            if (values[i] is not double v) continue;
            return new SensorHit(r.Source, r.Profile, v, r.Threshold, r.Above);
        }
        return null;
    }
}

/// <summary>Every sensor rule's hysteresis state between ticks, kept against the
/// RULE rather than its place in the list.
///
/// It used to be an array indexed by position, reset only when the list object
/// or its length changed. A reorder changes neither, so the firing state moved
/// to whichever rule now sat in that slot: a rule reading 50 degrees could be
/// "firing" because the 90-degree rule had just been dragged below it, and its
/// profile went on. Adding or removing any rule changed the length and reset
/// them all, so the rule that WAS firing dropped out, sat through its hold
/// again, and the lighting went to Base and back. Keyed by identity, a rule
/// keeps its own state wherever it moves, and only a rule that has left the
/// list loses it. Allocates nothing on a tick once the list has been seen.</summary>
public sealed class SensorRuleTracker
{
    readonly Dictionary<SensorRule, SensorRuleState> _states = new(ReferenceEqualityComparer.Instance);
    // Per-tick scratch in the list's CURRENT order, because list order is still
    // the priority FirstActive reads. Resized only when the count changes.
    SensorRuleState[] _ordered = Array.Empty<SensorRuleState>();
    double?[] _values = Array.Empty<double?>();
    readonly List<SensorRule> _gone = new();

    /// <summary>Advance every rule one tick and return the winner.</summary>
    /// <param name="read">The reading for a source, or null when it has none.</param>
    /// <param name="firstMissing">The source of the first enabled rule that
    /// had no reading, for the "sensor rule paused" status; null when every
    /// enabled rule could read.</param>
    public SensorHit? Step(IReadOnlyList<SensorRule> rules, Func<string, double?> read, double nowSeconds,
                           Func<string, bool>? profileExists, out string? firstMissing)
    {
        firstMissing = null;
        if (_ordered.Length != rules.Count)
        {
            _ordered = new SensorRuleState[rules.Count];
            _values = new double?[rules.Count];
        }
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            double? v = r.Enabled ? read(r.Source) : null;
            _states.TryGetValue(r, out var was);
            var now = SensorRuleEvaluator.Step(r, v, was, nowSeconds);
            _states[r] = now;
            _ordered[i] = now;
            _values[i] = v;
            if (r.Enabled && v == null) firstMissing ??= r.Source;
        }
        Prune(rules);
        return SensorRuleEvaluator.FirstActive(rules, _ordered, _values, profileExists);
    }

    /// <summary>Forget the rules that are no longer in the list, so a removed
    /// rule's state does not outlive it.</summary>
    void Prune(IReadOnlyList<SensorRule> rules)
    {
        foreach (var kept in _states.Keys)
        {
            bool present = false;
            for (int i = 0; i < rules.Count && !present; i++) present = ReferenceEquals(rules[i], kept);
            if (!present) _gone.Add(kept);
        }
        foreach (var r in _gone) _states.Remove(r);
        _gone.Clear();
    }

    /// <summary>Whether this rule is firing as of the last step.</summary>
    internal bool IsActive(SensorRule rule) => _states.TryGetValue(rule, out var s) && s.Active;

    /// <summary>How many rules have state, for the harness: removed rules must
    /// not linger.</summary>
    internal int Tracked => _states.Count;
}

/// <summary>The sensors a rule can watch: fixed ids for the headline values,
/// prefixed names for the per-board lists, which vary by machine.</summary>
public static class SensorSources
{
    public const string CpuTemp = "CpuTemp";
    public const string GpuTemp = "GpuTemp";
    public const string Hottest = "Hottest";
    public const string CpuLoad = "CpuLoad";
    public const string GpuLoad = "GpuLoad";

    public const string BoardPrefix = "Board:";   // a motherboard temperature, by name
    public const string FanPrefix = "Fan:";       // a fan RPM, by name
    public const string BatteryPrefix = "Battery:"; // a wireless device's charge, by name

    /// <summary>Human name for the rules UI and the status line.</summary>
    public static string Label(string source) => source switch
    {
        CpuTemp => "CPU temp",
        GpuTemp => "GPU temp",
        Hottest => "Hottest of CPU/GPU",
        CpuLoad => "CPU load",
        GpuLoad => "GPU load",
        // Short enough to read in a dropdown. "MB" marks it as a board sensor;
        // LHM's own "Temperature #3" collapses to "Temp #3".
        _ when source.StartsWith(BoardPrefix, StringComparison.Ordinal)
            => "MB " + source[BoardPrefix.Length..].Replace("Temperature", "Temp"),
        // Fan names are already self-describing ("CPU Fan", "Fan #3").
        _ when source.StartsWith(FanPrefix, StringComparison.Ordinal) => source[FanPrefix.Length..],
        _ when source.StartsWith(BatteryPrefix, StringComparison.Ordinal)
            => source[BatteryPrefix.Length..] + " battery",
        _ => source,
    };

    public static string Unit(string source) => source switch
    {
        CpuTemp or GpuTemp or Hottest => "°C",
        CpuLoad or GpuLoad => "%",
        _ when source.StartsWith(FanPrefix, StringComparison.Ordinal) => " RPM",
        _ when source.StartsWith(BoardPrefix, StringComparison.Ordinal) => "°C",
        _ when source.StartsWith(BatteryPrefix, StringComparison.Ordinal) => "%",
        _ => "",
    };

    /// <summary>True when this source is read out of a SensorHub sweep at all.
    /// Battery is not: it is pushed in by the app's own slow poller, so a
    /// battery rule must not wake the temperature poller for a number it
    /// never looks at.</summary>
    public static bool NeedsHub(string source) =>
        !source.StartsWith(BatteryPrefix, StringComparison.Ordinal);

    /// <summary>True when reading this source needs SensorHub's full sweep
    /// (GPU load, board temps and fans) rather than the cheap temp-only one.
    /// Keeps a rule on CPU temperature from waking the whole poller.</summary>
    public static bool NeedsFullSweep(string source) =>
        source is CpuLoad or GpuLoad
        || source.StartsWith(BoardPrefix, StringComparison.Ordinal)
        || source.StartsWith(FanPrefix, StringComparison.Ordinal);

    /// <summary>Live reading for a source, or null when it is unavailable.
    /// Reads SensorHub's published snapshot; the caller is responsible for
    /// having touched the hub so the snapshot is fresh.</summary>
    public static double? Read(string source)
    {
        switch (source)
        {
            case CpuTemp: return SensorHub.CpuTempC;
            case GpuTemp: return SensorHub.GpuTempC;
            case Hottest: return SensorHub.HottestC;
            case CpuLoad: return SensorHub.CpuLoadPct;
            case GpuLoad: return SensorHub.GpuLoadPct;
        }
        if (source.StartsWith(BoardPrefix, StringComparison.Ordinal))
        {
            string name = source[BoardPrefix.Length..];
            foreach (var t in SensorHub.BoardTemps)
                if (t.Name == name) return t.TempC;
            return null;
        }
        if (source.StartsWith(FanPrefix, StringComparison.Ordinal))
        {
            string name = source[FanPrefix.Length..];
            foreach (var f in SensorHub.BoardFans)
                if (f.Name == name) return f.Rpm;
            return null;
        }
        if (source.StartsWith(BatteryPrefix, StringComparison.Ordinal))
        {
            string name = source[BatteryPrefix.Length..];
            foreach (var b in SensorHub.Batteries)
                if (b.Name == name) return b.Percent;
            return null;
        }
        return null;
    }
}
