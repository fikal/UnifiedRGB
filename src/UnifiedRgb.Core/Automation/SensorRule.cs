using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Core.Automation;

/// <summary>"When the CPU hits 85 degrees, go red." One threshold rule over a
/// sensor the app already reads. Deliberately not a general expression engine:
/// a source, a direction, a number, a profile.</summary>
public sealed class SensorRule
{
    /// <summary>One of the <see cref="SensorSources"/> constants, or a prefixed
    /// name for the per-board lists ("Board:Fan #2", "Fan:CPU Fan").</summary>
    public string Source { get; set; } = SensorSources.CpuTemp;

    /// <summary>True: fire at or above Threshold. False: at or below.</summary>
    public bool Above { get; set; } = true;

    public double Threshold { get; set; } = 85;

    /// <summary>How far past the threshold the value must come back before the
    /// rule releases, so a reading sitting on the line cannot chatter. Applied
    /// on the far side of the threshold from the trigger direction.</summary>
    public double ClearMargin { get; set; } = 3;

    /// <summary>The condition must hold this long before the rule flips, in
    /// EITHER direction. With ClearMargin this makes a toggle faster than once
    /// per hold impossible.</summary>
    public int HoldSeconds { get; set; } = 5;

    public string Profile { get; set; } = "";
    public bool Enabled { get; set; } = true;
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
/// the caller owns the state array across ticks.</summary>
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

    /// <summary>Human name for the rules UI and the status line.</summary>
    public static string Label(string source) => source switch
    {
        CpuTemp => "CPU temp",
        GpuTemp => "GPU temp",
        Hottest => "Hottest of CPU/GPU",
        CpuLoad => "CPU load",
        GpuLoad => "GPU load",
        // Qualified, because a bare "Temperature #3" in a list next to "CPU temp"
        // says nothing about what it measures.
        _ when source.StartsWith(BoardPrefix, StringComparison.Ordinal) => "Motherboard: " + source[BoardPrefix.Length..],
        _ when source.StartsWith(FanPrefix, StringComparison.Ordinal) => "Fan: " + source[FanPrefix.Length..],
        _ => source,
    };

    public static string Unit(string source) => source switch
    {
        CpuTemp or GpuTemp or Hottest => "°C",
        CpuLoad or GpuLoad => "%",
        _ when source.StartsWith(FanPrefix, StringComparison.Ordinal) => " RPM",
        _ when source.StartsWith(BoardPrefix, StringComparison.Ordinal) => "°C",
        _ => "",
    };

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
        return null;
    }
}
