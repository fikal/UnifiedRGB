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
        set
        {
            // The delay box is a plain double binding and "NaN" parses: NaN
            // passes Clamp, TimeSpan.FromSeconds(NaN) then throws in the
            // sequencer's timer and JSON refuses to serialize it (every later
            // scenes.json save fails). Keep the previous value instead.
            if (double.IsNaN(value)) value = _delaySeconds;
            _delaySeconds = Math.Clamp(value, 0, 24 * 3600);
            Notify(nameof(DelaySeconds));
        }
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

    public static SceneStore Load() => Normalize(ProfileStore.LoadJson<SceneStore>(Path, "scenes.json"));

    /// <summary>Parse a store out of text that is NOT scenes.json - the copy
    /// inside a setup bundle. Null when the text will not deserialize, so the
    /// importer can refuse that bundle with a reason rather than quietly
    /// report zero screens to import.
    ///
    /// Everything that comes back is Normalized, because the importer walks
    /// the designs (rewriting background image paths onto this machine) and
    /// must not be the first caller to meet a null Elements list.</summary>
    public static SceneStore? TryParse(string json)
    {
        SceneStore? parsed;
        try { parsed = JsonSerializer.Deserialize<SceneStore>(json); }
        catch { return null; }
        if (parsed is null) return null;   // the literal text `null` parses, and is just as useless
        return Normalize(parsed);
    }

    /// <summary>Make a deserialized store safe to walk.
    ///
    /// A property initializer only runs when the JSON leaves the property out.
    /// `{"Scenes": null}` is valid JSON, so it deserializes to a null list and
    /// the startup walk over it throws before the window is even up - and this
    /// runs on every launch, LCD attached or not. Same for a null entry inside
    /// a list, or a sequence whose Actions came back null. Hand-edited and
    /// half-written files really do look like this.</summary>
    static SceneStore Normalize(SceneStore? s)
    {
        s ??= new SceneStore();
        s.Scenes = s.Scenes?.Where(x => x != null).ToList() ?? new();
        s.Sequences = s.Sequences?.Where(x => x != null).ToList() ?? new();
        foreach (var sc in s.Scenes)
        {
            sc.Name ??= "";
            sc.Design = LcdDesign.Normalize(sc.Design);
        }
        foreach (var sq in s.Sequences)
        {
            sq.Name ??= "";
            sq.Actions = sq.Actions?.Where(a => a != null).ToList() ?? new();
        }
        // A scene with no usable name can never be selected or saved over, and
        // a sequence step naming it would silently do nothing.
        s.Scenes.RemoveAll(x => string.IsNullOrWhiteSpace(x.Name));
        return s;
    }

    // Through the shared store writer: a scenes.json that could not be read
    // at startup must not be overwritten by the defaults (see ProfileStore.LoadJson).
    public void Save() => ProfileStore.Save(Path, this, "scenes.json");

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
