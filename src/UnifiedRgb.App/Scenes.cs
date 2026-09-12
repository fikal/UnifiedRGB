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
    /// <summary>LEGACY. A step used to be able to name a pump scene of its own,
    /// alongside or instead of a profile. A show is a timeline of PROFILES now:
    /// a profile already carries the lights, the pump screen and the screen in
    /// the case, so a second way to set one of the three only created an argument
    /// about which won.
    ///
    /// Still deserialized so an existing scenes.json can be MIGRATED rather than
    /// silently emptied - see SceneStore.MigrateSceneSteps - and written back out
    /// as null once it has been. Nothing applies it.</summary>
    public string? Scene
    {
        get => _scene;
        set { _scene = value; Notify(nameof(Scene)); }
    }

    string? _profile;
    /// <summary>The profile this step goes to: the whole desk state, lights and
    /// pump screen and case screen together. Null or empty means the step does
    /// nothing, which is what a half-migrated file looks like and why the
    /// migration reports rather than guesses.</summary>
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
    /// <summary>LEGACY. One show could mark itself "start with the app", which
    /// made a second owner of the pump panel: the startup profile put its screen
    /// up and this show's first step painted over it a moment later. A profile
    /// names its own show now. Kept only so the setting can be moved onto the
    /// startup profile instead of vanishing.</summary>
    public string? ActiveSequence { get; set; }

    /// <summary>Move steps that named a pump SCENE onto the profile that carries
    /// that scene, now that a step is a profile.
    ///
    /// A combined profile + scene step meant that profile's lighting with the
    /// explicit scene applied last. Preserve that combination in a separate
    /// profile when necessary, and persist it before changing the step. A
    /// scene-only step can adopt its single owning profile. Ambiguous or failed
    /// migrations keep their original fields so a later attempt can recover.
    ///
    /// Returns the number of steps that could not be resolved.</summary>
    public int MigrateSceneSteps(IEnumerable<Profile> profiles, Func<Profile, bool>? addProfile = null)
    {
        var available = profiles.Where(p => p != null).ToList();
        var variants = new List<(string Profile, string Scene, Profile Variant)>();
        var byScene = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in available)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Screen)) continue;
            if (!byScene.TryGetValue(p.Screen!, out var list)) byScene[p.Screen!] = list = new();
            list.Add(p.Name);
        }

        int moved = 0, stranded = 0;
        foreach (var seq in Sequences)
        {
            var steps = seq?.Actions;
            if (steps == null) continue;
            for (int i = 0; i < steps.Count; i++)
            {
                var a = steps[i];
                if (a == null || string.IsNullOrWhiteSpace(a.Scene)) continue;
                string scene = a.Scene!;
                if (!string.IsNullOrWhiteSpace(a.Profile))
                {
                    var source = available.FirstOrDefault(p => p.Name.Equals(a.Profile, StringComparison.OrdinalIgnoreCase));
                    if (source != null && string.Equals(source.Screen, scene, StringComparison.OrdinalIgnoreCase))
                    {
                        a.Scene = null;
                        moved++;
                        continue;
                    }

                    if (source != null && addProfile != null)
                    {
                        var variant = variants.FirstOrDefault(v =>
                            v.Profile.Equals(source.Name, StringComparison.OrdinalIgnoreCase) &&
                            v.Scene.Equals(scene, StringComparison.OrdinalIgnoreCase)).Variant;
                        if (variant == null)
                        {
                            // Clone every saved field, including unavailable devices and
                            // effect settings. The user's original profile stays intact.
                            variant = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(source))!;
                            variant.Screen = scene;
                            // A previous launch may have saved the new profile but been
                            // unable to save scenes.json. Reuse an exact persisted match.
                            var persisted = available.FirstOrDefault(p =>
                            {
                                if (!string.Equals(p.Screen, scene, StringComparison.OrdinalIgnoreCase)) return false;
                                variant.Name = p.Name;
                                return JsonSerializer.Serialize(variant) == JsonSerializer.Serialize(p);
                            });
                            string baseName = $"{source.Name} ({scene})";
                            variant.Name = baseName;
                            for (int suffix = 2; available.Any(p => p.Name.Equals(variant.Name, StringComparison.OrdinalIgnoreCase)); suffix++)
                                variant.Name = $"{baseName} {suffix}";
                            if (persisted != null) variant = persisted;
                            else if (!addProfile(variant)) variant = null;
                            else
                                available.Add(variant);
                            if (variant != null)
                                variants.Add((source.Name, scene, variant));
                        }
                        if (variant != null)
                        {
                            a.Profile = variant.Name;
                            a.Scene = null;
                            moved++;
                            Log.Info("scenes", $"show '{seq!.Name}' step {i + 1}: saved profile '{variant.Name}' to preserve profile '{source.Name}' with screen '{scene}'");
                            continue;
                        }
                    }

                    stranded++;
                    Log.Warn("scenes", $"show '{seq!.Name}' step {i + 1}: could not preserve profile '{a.Profile}' with screen '{scene}'; original step kept for migration");
                    continue;
                }

                if (byScene.TryGetValue(scene, out var owners) && owners.Count == 1)
                {
                    a.Profile = owners[0];
                    a.Scene = null;
                    moved++;
                    Log.Info("scenes", $"show '{seq!.Name}' step {i + 1}: screen '{scene}' is now profile '{owners[0]}'");
                }
                else
                {
                    stranded++;
                    Log.Warn("scenes", $"show '{seq!.Name}' step {i + 1} showed screen '{scene}', and "
                        + (owners == null
                           ? "no profile carries that screen"
                           : $"{owners.Count} profiles do ({string.Join(", ", owners)})")
                        + " - the step does nothing until you point it at a profile");
                }
            }
        }
        if (moved > 0 || stranded > 0)
            Log.Info("scenes", $"show steps migrated to profiles: {moved} moved, {stranded} need a profile chosen");
        return stranded;
    }

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

    bool _paused;
    long _remainingMs;

    public sealed record Playback(string Name, int Index, long RemainingMs, bool Paused);

    public Playback? Capture() => _seq == null ? null : new Playback(_seq.Name, _index,
        _paused ? _remainingMs : Math.Max(0, _dueAt - Environment.TickCount64), _paused);

    /// <summary>Resume the saved wait without applying a step during restoration.</summary>
    public void Restore(SceneSequence seq, Playback state)
    {
        Stop();
        if (seq.Actions.Count == 0) return;
        _seq = seq;
        _index = Math.Clamp(state.Index, -1, seq.Actions.Count - 1);
        _paused = state.Paused;
        _remainingMs = Math.Clamp(state.RemainingMs, 50, 24L * 3600 * 1000);
        _timer.Interval = TimeSpan.FromMilliseconds(_remainingMs);
        _dueAt = Environment.TickCount64 + _remainingMs;
        if (!_paused) _timer.Start();
        StateChanged?.Invoke();
    }

    /// <summary>Hold the show where it is without forgetting it, so somebody can
    /// change their settings without the desk moving underneath them, then carry
    /// on from the same step.
    ///
    /// Distinct from Stop, which forgets the show entirely. The time already
    /// served on the current step is kept too: pausing four seconds into a five
    /// second wait and resuming should leave one second, not five, or a pause
    /// taken to look at something would silently restart the wait every time.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value || _seq == null) return;
            _paused = value;
            if (value)
            {
                _remainingMs = Math.Max(0, _dueAt - Environment.TickCount64);
                _timer.Stop();
                Log.Info("scenes", $"show '{_seq.Name}' paused");
            }
            else
            {
                // Never zero: a DispatcherTimer with a zero interval fires on
                // every dispatcher pass, which is a busy loop dressed as a timer.
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _remainingMs));
                _dueAt = Environment.TickCount64 + Math.Max(50, _remainingMs);
                _timer.Start();
                Log.Info("scenes", $"show '{_seq.Name}' resumed");
            }
            StateChanged?.Invoke();
        }
    }

    public SceneSequencer(Action<SceneAction> apply)
    {
        _apply = apply;
        _timer.Tick += (_, _) => Step();
    }

    public void Start(SceneSequence seq)
    {
        Stop();
        _paused = false;   // a new show starts running, whatever the last one was doing
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
        _paused = false;
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
