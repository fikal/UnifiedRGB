using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/*-----------------------------------------------------------*\
| Scenes & sequences for the pump LCD.                        |
|                                                             |
|   Scene    — a named LCD layout (background + elements),    |
|              i.e. a saved LcdDesign.                        |
|   Action   — one step of a show: wait DelaySeconds, then    |
|              show Scene and/or apply a lighting Profile.    |
|              (Both in one action = the panel and the case   |
|              lights change in sync. More kinds can join     |
|              later — the model is deliberately loose.)      |
|   Sequence — a named, ordered list of actions, looped.      |
|                                                             |
| Everything persists to scenes.json; one sequence can be     |
| marked active, which auto-starts it with the app.           |
\*-----------------------------------------------------------*/

public sealed class LcdScene
{
    public string Name { get; set; } = "";
    public LcdDesign Design { get; set; } = new();
}

public sealed class SceneAction : INotifyPropertyChanged
{
    double _delaySeconds = 5;
    public double DelaySeconds
    {
        get => _delaySeconds;
        set { _delaySeconds = Math.Clamp(value, 0, 24 * 3600); Notify(nameof(DelaySeconds)); }
    }

    string? _scene;
    /// <summary>Scene to show; null/empty = leave the panel as is.</summary>
    public string? Scene
    {
        get => _scene;
        set { _scene = value; Notify(nameof(Scene)); }
    }

    string? _profile;
    /// <summary>Lighting profile to apply; null/empty = leave lighting alone.</summary>
    public string? Profile
    {
        get => _profile;
        set { _profile = value; Notify(nameof(Profile)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class SceneSequence
{
    public string Name { get; set; } = "";
    public List<SceneAction> Actions { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class SceneStore
{
    public List<LcdScene> Scenes { get; set; } = new();
    public List<SceneSequence> Sequences { get; set; } = new();
    public string? ActiveSequence { get; set; }

    static string Path => AppPaths.Config("scenes.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static SceneStore Load() => ProfileStore.LoadJson<SceneStore>(Path, "scenes.json") ?? new SceneStore();

    public void Save()
    {
        try { SafeFile.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Warn("scenes", $"save failed: {ex.Message}"); }
    }

    /// <summary>Deep-clone a design via JSON round-trip (scenes must never
    /// share element instances with the live editor).</summary>
    public static LcdDesign Clone(LcdDesign d)
        => JsonSerializer.Deserialize<LcdDesign>(JsonSerializer.Serialize(d))!;
}

/// <summary>Plays a sequence: waits each action's delay, applies it, loops.
/// Owns nothing — applying goes through the callback so the VM stays the
/// single writer to the LCD and the lighting.</summary>
public sealed class SceneSequencer
{
    // Normal priority, NOT the DispatcherTimer default (Background): the UI
    // thread constantly renders the LCD and sensor ticks, and a Background
    // timer starves under that load — sub-second step delays stretched so
    // badly that fractional waits looked ignored.
    readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);
    readonly Action<SceneAction> _apply;
    SceneSequence? _seq;
    int _index;
    long _dueAt;

    public bool Running => _seq != null;
    public string? RunningName => _seq?.Name;
    public event Action? StateChanged;

    public SceneSequencer(Action<SceneAction> apply)
    {
        _apply = apply;
        _timer.Tick += (_, _) => Step();
    }

    public void Start(SceneSequence seq)
    {
        Stop();
        if (seq.Actions.Count == 0) return;
        _seq = seq;
        _index = -1;
        ScheduleNext();
        StateChanged?.Invoke();
        Log.Info("scenes", $"sequence '{seq.Name}' started ({seq.Actions.Count} action(s))");
    }

    public void Stop()
    {
        if (_seq == null) return;
        _timer.Stop();
        Log.Info("scenes", $"sequence '{_seq.Name}' stopped");
        _seq = null;
        StateChanged?.Invoke();
    }

    void ScheduleNext()
    {
        if (_seq == null || _seq.Actions.Count == 0) { Stop(); return; }
        var next = _seq.Actions[(_index + 1) % _seq.Actions.Count];
        double s = Math.Max(0.05, next.DelaySeconds);
        _timer.Interval = TimeSpan.FromSeconds(s);
        _dueAt = Environment.TickCount64 + (long)(s * 1000);
        _timer.Start();
    }

    void Step()
    {
        _timer.Stop();
        if (_seq == null || _seq.Actions.Count == 0) { Stop(); return; }
        long late = Environment.TickCount64 - _dueAt;
        if (late > 150)
            Log.Occasional("scenes", "seq-late", $"step ran {late} ms late (UI thread busy)");
        _index = (_index + 1) % _seq.Actions.Count;
        try { _apply(_seq.Actions[_index]); }
        catch (Exception ex) { Log.Warn("scenes", $"action failed: {ex.Message}"); }
        ScheduleNext();
    }
}
