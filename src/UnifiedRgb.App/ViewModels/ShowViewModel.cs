using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/*-------------------------------------------------------------*\
| Shows: a timeline of profiles.                                |
|                                                               |
| This lived inside the pump LCD's view model, which is where   |
| it started and is no longer where it belongs. A show steps    |
| through PROFILES, and a profile is the whole desk - the       |
| lights, the pump panel and the screen in the case - so a show |
| filed under the panel was filed under the smallest of the     |
| three things it drives.                                       |
|                                                               |
| It still borrows the pump LCD's store, because scenes and     |
| shows share one file, and it tells the panel when a show has  |
| taken it so the show's screens are never flushed over the     |
| user's own canvas. Those two are the whole of the coupling    |
| that is left, and both are passed in rather than reached for. |
\*-------------------------------------------------------------*/

/// <summary>The saved shows, the one that is running, and the timer that walks
/// it.</summary>
public sealed class ShowViewModel : INotifyPropertyChanged, IDisposable
{
    readonly Func<SceneStore> _store;
    readonly Func<bool> _lightsSuppressed;
    readonly Func<string, bool> _applyProfile;
    readonly Func<IEnumerable<string>> _profileNames;
    readonly Func<string?> _currentProfile;
    readonly Action _showTookThePanel;

    SceneSequencer? _sequencer;
    SceneSequence? _selectedShow;

    /// <summary>A show asked for before Init built the sequencer.</summary>
    string? _pending;

    /// <param name="store">Read through a function, not captured: an import
    /// replaces the whole store, and a snapshot taken here would leave this
    /// editing a file nobody saves any more.</param>
    /// <param name="showTookThePanel">Tell the pump LCD that what is on it now
    /// belongs to a show. Without it the show's screens are saved as the user's
    /// own canvas, and the next step overwrites their real design.</param>
    public ShowViewModel(Func<SceneStore> store, Func<bool> lightsSuppressed,
                         Func<string, bool> applyProfile, Func<IEnumerable<string>> profileNames,
                         Func<string?> currentProfile, Action showTookThePanel)
    {
        _store = store;
        _lightsSuppressed = lightsSuppressed;
        _applyProfile = applyProfile;
        _profileNames = profileNames;
        _currentProfile = currentProfile;
        _showTookThePanel = showTookThePanel;
    }

    /*--- the lists ---*/

    public ObservableCollection<SceneSequence> Shows { get; } = new();
    public ObservableCollection<SceneAction> Steps { get; } = new();

    public const string KeepChoice = "(no change)";
    public IReadOnlyList<string> ProfileChoices => new[] { KeepChoice }.Concat(_profileNames()).ToList();

    /// <summary>The profile list changed. The step dropdowns are computed from
    /// it and would otherwise stay frozen at launch time.</summary>
    public void NotifyProfilesChanged() => OnChanged(nameof(ProfileChoices));

    string _nameInput = "";
    /// <summary>The name box beside the show list. Its own, rather than shared
    /// with the one that names scenes: they sit on different pages now, and one
    /// box feeding two features is how a half-typed show name ended up naming a
    /// screen.</summary>
    public string NameInput { get => _nameInput; set { _nameInput = value; OnChanged(); } }

    public SceneSequence? SelectedShow
    {
        get => _selectedShow;
        set
        {
            _selectedShow = value;
            OnChanged();
            Steps.Clear();
            foreach (var a in value?.Actions ?? new()) { Steps.Add(a); Hook(a); }
        }
    }

    // Named handler + remove-before-add: steps persist across show selections,
    // and an anonymous lambda stacked one MORE save handler per select
    // (A->B->A = every edit wrote scenes.json three times).
    void Hook(SceneAction a)
    {
        a.PropertyChanged -= StepChanged;
        a.PropertyChanged += StepChanged;
    }
    void StepChanged(object? s, PropertyChangedEventArgs e) => _store().Save();

    /*--- startup ---*/

    /// <summary>Build the sequencer and load the saved shows. Called once, after
    /// the main view model has applied the startup profile - which is why a show
    /// that profile asked for arrives before we are ready and has to be held.</summary>
    public void Init()
    {
        foreach (var sq in _store().Sequences) Shows.Add(sq);
        _sequencer = new SceneSequencer(ApplyStep);
        _sequencer.StateChanged += NotifyRunState;

        // Nothing auto-starts here. A show used to start itself from its own
        // "active" flag, which made a second owner of the pump panel: the startup
        // profile put its screen up and this painted over it. A profile names its
        // own show now.
        SelectedShow = Shows.FirstOrDefault();

        if (_pending is string pending)
        {
            _pending = null;
            Start(pending);
        }
    }

    /// <summary>Throw away the sequencer and the lists so an import can rebuild
    /// them against a replaced store.</summary>
    public void Reset()
    {
        _sequencer?.Stop();
        if (_sequencer != null) _sequencer.StateChanged -= NotifyRunState;
        _sequencer = null;
        foreach (var seq in Shows)
            foreach (var step in seq.Actions ?? new()) step.PropertyChanged -= StepChanged;
        SelectedShow = null;
        Shows.Clear();
    }

    void NotifyRunState()
    {
        OnChanged(nameof(Running));
        OnChanged(nameof(RunButtonText));
        OnChanged(nameof(Status));
        OnChanged(nameof(Paused));
        OnChanged(nameof(CanPause));
        OnChanged(nameof(PauseButtonText));
        OnChanged(nameof(RunningLine));
    }

    /*--- driving one ---*/

    /// <summary>Start a saved show because a profile asked for it. Reported
    /// rather than thrown: the profile's lighting has already applied by the
    /// time we get here.
    ///
    /// A show that is ALREADY running is left alone rather than restarted, so
    /// re-applying a profile does not jump back to step one of something
    /// somebody is watching. That also breaks the loop a show can make of
    /// itself, since a step applies a profile and a profile may name a show.</summary>
    public bool Start(string name)
    {
        if (_sequencer == null)
        {
            // Asked before the shows were loaded, which is the NORMAL order at
            // launch: the startup profile applies first and Init runs after it.
            // Refusing it here meant "show could not start" at every launch with
            // only the first profile ever playing, so it is remembered instead,
            // and true because it WILL happen.
            _pending = name;
            Log.Info("scenes", $"show '{name}' asked for before the shows were loaded; starting it when they are");
            return true;
        }

        var seq = Shows.FirstOrDefault(x => x != null && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (seq == null)
        {
            Log.Warn("scenes", $"a profile asked for show '{name}', which does not exist");
            return false;
        }
        if (seq.Actions == null || seq.Actions.Count == 0)
        {
            Log.Warn("scenes", $"a profile asked for show '{name}', which has no steps");
            return false;
        }
        if (_sequencer.RunningName == seq.Name) return true;
        SelectedShow = seq;
        _sequencer.Start(seq);
        return true;
    }

    /// <summary>Stop whatever is running. A profile that pins ONE screen has to
    /// say so to a panel a show is driving, or the show's next step paints over
    /// that screen a second later.</summary>
    public void Stop()
    {
        _pending = null;   // asked for and then called off must not start late
        _sequencer?.Stop();
    }

    public bool Running => _sequencer?.Running == true;
    public bool Paused => _sequencer?.Paused == true;
    public bool CanPause => Running;

    public string RunButtonText => Running ? "Stop" : "Run";
    public string PauseButtonText => Paused ? "Resume show" : "Pause show";

    public string Status => !Running ? ""
        : Paused ? $"'{_sequencer!.RunningName}' is paused where it is"
        : $"running '{_sequencer!.RunningName}' - loops until stopped";

    public string RunningLine => !Running ? ""
        : Paused
            ? $"Show “{_sequencer!.RunningName}” is paused."
            : $"Show “{_sequencer!.RunningName}” is running and changes your desk every few seconds.";

    public void Toggle()
    {
        if (_sequencer == null) return;
        if (Running) _sequencer.Stop();
        else if (_selectedShow is { Actions.Count: > 0 }) _sequencer.Start(_selectedShow);
    }

    /// <summary>Hold the show where it is without forgetting it, so settings can
    /// be changed without the desk moving underneath.</summary>
    public void TogglePaused()
    {
        if (_sequencer is not { Running: true }) return;
        _sequencer.Paused = !_sequencer.Paused;
    }

    /*--- one step ---*/

    void ApplyStep(SceneAction a)
    {
        // Lights deliberately off (locked / night): hold the step. Applying a
        // profile here relit the case while locked AND cleared the automation's
        // return point, so the unlock had nothing to restore and left the pump
        // panel blank. The timer keeps ticking; the next step after the lights
        // return applies normally.
        if (_lightsSuppressed()) return;

        // A step IS a profile. Everything a step used to set separately travels
        // on the profile, and the screen in the case comes with it for free.
        if (string.IsNullOrWhiteSpace(a.Profile)) return;

        // Re-applying what is already on is not free: loading a profile stops and
        // restarts every effect channel, so a step naming the running profile
        // visibly resets the animation instead of leaving it alone. This is also
        // the tightest point of the loop a self-referencing show would make.
        if (string.Equals(_currentProfile(), a.Profile, StringComparison.OrdinalIgnoreCase)) return;

        // Show territory, flagged BEFORE the profile applies: the profile's
        // screen is the show's screen now, and loaded as the user's canvas it
        // starts the debounced save, which the next step then flushes over their
        // real design.
        _showTookThePanel();
        _applyProfile(a.Profile);
    }

    /*--- editing ---*/

    public void New()
    {
        var scenes = _store();
        string name = !string.IsNullOrWhiteSpace(NameInput) ? NameInput.Trim()
                    : $"Show {scenes.Sequences.Count + 1}";
        if (scenes.Sequences.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var sq = new SceneSequence { Name = name };
        scenes.Sequences.Add(sq);
        Shows.Add(sq);
        NameInput = "";
        SelectedShow = sq;
        scenes.Save();
    }

    public void Delete()
    {
        if (_selectedShow == null) return;
        var scenes = _store();
        if (_sequencer?.RunningName == _selectedShow.Name) _sequencer.Stop();
        scenes.Sequences.Remove(_selectedShow);
        Shows.Remove(_selectedShow);
        SelectedShow = Shows.FirstOrDefault();
        scenes.Save();
    }

    public void AddStep()
    {
        if (_selectedShow == null) return;
        var a = new SceneAction { Profile = _currentProfile() ?? _profileNames().FirstOrDefault(), DelaySeconds = 5 };
        _selectedShow.Actions.Add(a);
        Steps.Add(a);
        Hook(a);
        _store().Save();
    }

    public void RemoveStep(SceneAction a)
    {
        if (_selectedShow == null) return;
        _selectedShow.Actions.Remove(a);
        Steps.Remove(a);
        _store().Save();
    }

    public void MoveStep(SceneAction a, int delta)
    {
        if (_selectedShow == null) return;
        int i = _selectedShow.Actions.IndexOf(a);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _selectedShow.Actions.Count) return;
        _selectedShow.Actions.RemoveAt(i);
        _selectedShow.Actions.Insert(j, a);
        Steps.Move(i, j);
        _store().Save();
    }

    /*--- the retired auto-start flag ---*/

    /// <summary>The show that used to be marked "start with the app", or null.
    /// Read once by the migration that moves it onto a profile.</summary>
    public string? LegacyActiveShow => _store().ActiveSequence;

    /// <summary>Forget the retired flag once it has been moved somewhere real,
    /// so the migration runs once rather than on every launch.</summary>
    public void ClearLegacyActiveShow()
    {
        var scenes = _store();
        if (scenes.ActiveSequence == null) return;
        scenes.ActiveSequence = null;
        scenes.Save();
    }

    /// <summary>Move steps that named a pump scene onto the profile that carries
    /// it, now that a step is a profile.</summary>
    public void MigrateSceneSteps(IEnumerable<Profile> profiles)
    {
        var scenes = _store();
        scenes.MigrateSceneSteps(profiles);
        scenes.Save();
    }

    public void Dispose() => _sequencer?.Stop();   // a queued step must not drive a disposed controller

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
