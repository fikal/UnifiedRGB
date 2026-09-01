using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.App;

// Scenes & sequences (plus the LCD background bridge that lived here) — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*\
    | Scenes & sequences: the canvas edits ONE design; a     |
    | scene is that design saved under a name; a sequence    |
    | chains actions (delay -> scene and/or lighting), loops,|
    | and can be the startup show.                           |
    \*-----------------------------------------------------*/
    readonly SceneStore _scenes = SceneStore.Load();
    SceneSequencer? _sequencer;

    public ObservableCollection<string> SceneNames { get; } = new();
    public ObservableCollection<SceneSequence> Sequences { get; } = new();
    public ObservableCollection<SceneAction> SequenceActions { get; } = new();

    string _sceneNameInput = "";
    public string SceneNameInput { get => _sceneNameInput; set { _sceneNameInput = value; OnChanged(); } }

    public const string KeepChoice = "(no change)";
    public IReadOnlyList<string> SceneChoices => new[] { KeepChoice }.Concat(SceneNames).ToList();
    public IReadOnlyList<string> ProfileChoices => new[] { KeepChoice }.Concat(Profiles.Select(p => p.Name)).ToList();

    string? _selectedSceneName;
    public string? SelectedSceneName
    {
        get => _selectedSceneName;
        set
        {
            _selectedSceneName = value;
            OnChanged();
            // Selecting a scene loads it into the editor (and onto the pump).
            var sc = _scenes.Scenes.FirstOrDefault(x => x.Name == value);
            if (sc != null) LoadDesignIntoEditor(SceneStore.Clone(sc.Design));
        }
    }

    SceneSequence? _selectedSequence;
    public SceneSequence? SelectedSequence
    {
        get => _selectedSequence;
        set
        {
            _selectedSequence = value;
            OnChanged();
            SequenceActions.Clear();
            foreach (var a in value?.Actions ?? new()) { SequenceActions.Add(a); HookAction(a); }
            OnChanged(nameof(SequenceActiveAtStartup));
        }
    }

    // Named handler + remove-before-add: SceneActions persist across sequence
    // selections, and the old anonymous lambda stacked one MORE save handler
    // per select (A->B->A = every edit wrote scenes.json 3x).
    void HookAction(SceneAction a)
    {
        a.PropertyChanged -= SceneActionChanged;
        a.PropertyChanged += SceneActionChanged;
    }
    void SceneActionChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => _scenes.Save();

    void InitScenes()
    {
        foreach (var sc in _scenes.Scenes) SceneNames.Add(sc.Name);
        foreach (var sq in _scenes.Sequences) Sequences.Add(sq);
        // Every profile-name list in the UI is computed from Profiles (Show
        // tab lights dropdowns, app-rule pickers); without this they stay
        // frozen at whatever existed at launch.
        Profiles.CollectionChanged += (_, _) =>
        { OnChanged(nameof(ProfileChoices)); OnChanged(nameof(ProfileNames)); };
        _sequencer = new SceneSequencer(ApplySceneAction);
        _sequencer.StateChanged += () =>
        {
            OnChanged(nameof(SequenceRunning));
            OnChanged(nameof(RunButtonText));
            OnChanged(nameof(SequenceStatus));
        };
        // Auto-run the active sequence with the app.
        var active = _scenes.Sequences.FirstOrDefault(x => x.Name == _scenes.ActiveSequence);
        if (active != null && LcdAvailable)
        {
            SelectedSequence = active;
            _sequencer.Start(active);
        }
    }

    void ApplySceneAction(SceneAction a)
    {
        if (!string.IsNullOrEmpty(a.Scene) &&
            _scenes.Scenes.FirstOrDefault(x => x.Name == a.Scene) is LcdScene sc)
        {
            _selectedSceneName = sc.Name;   // reflect without re-loading twice
            OnChanged(nameof(SelectedSceneName));
            LoadDesignIntoEditor(SceneStore.Clone(sc.Design));
        }
        if (!string.IsNullOrEmpty(a.Profile))
            ApplyProfileByName(a.Profile);
    }

    /// <summary>Swap the live design (editor + pump) for another one.</summary>
    void LoadDesignIntoEditor(LcdDesign d)
    {
        if (_lcd == null) return;
        _lcd.Design = d;
        foreach (var old in LcdElements) Unhook(old);   // swap-out: no stranded handlers
        LcdElements.Clear();
        foreach (var e in d.Elements) { LcdElements.Add(e); Hook(e); }
        SelectedElement = null;
        EnsureBgRect();
        OnChanged(nameof(LcdBackground)); OnChanged(nameof(LcdBackgroundName));
        NotifyBgRect();
        TouchLcd();
    }

    /// <summary>Save the canvas as a scene: under the typed name if given,
    /// else overwriting the selected scene.</summary>
    public void SaveScene()
    {
        if (_lcd == null) return;
        string name = !string.IsNullOrWhiteSpace(SceneNameInput) ? SceneNameInput.Trim()
                    : _selectedSceneName ?? "";
        if (string.IsNullOrWhiteSpace(name)) return;
        var sc = _scenes.Scenes.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sc == null)
        {
            sc = new LcdScene { Name = name };
            _scenes.Scenes.Add(sc);
            SceneNames.Add(name);
        }
        sc.Design = SceneStore.Clone(_lcd.Design);
        _scenes.Save();
        SceneNameInput = "";
        _selectedSceneName = sc.Name;
        OnChanged(nameof(SelectedSceneName)); OnChanged(nameof(SceneChoices));
    }

    public void DeleteScene()
    {
        if (_selectedSceneName == null) return;
        _scenes.Scenes.RemoveAll(x => x.Name == _selectedSceneName);
        SceneNames.Remove(_selectedSceneName);
        _selectedSceneName = null;
        OnChanged(nameof(SelectedSceneName)); OnChanged(nameof(SceneChoices));
        _scenes.Save();
    }

    public void NewSequence()
    {
        string name = !string.IsNullOrWhiteSpace(SceneNameInput) ? SceneNameInput.Trim()
                    : $"Sequence {_scenes.Sequences.Count + 1}";
        if (_scenes.Sequences.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var sq = new SceneSequence { Name = name };
        _scenes.Sequences.Add(sq);
        Sequences.Add(sq);
        SceneNameInput = "";
        SelectedSequence = sq;
        _scenes.Save();
    }

    public void DeleteSequence()
    {
        if (_selectedSequence == null) return;
        if (_sequencer?.RunningName == _selectedSequence.Name) _sequencer.Stop();
        if (_scenes.ActiveSequence == _selectedSequence.Name) _scenes.ActiveSequence = null;
        _scenes.Sequences.Remove(_selectedSequence);
        Sequences.Remove(_selectedSequence);
        SelectedSequence = Sequences.FirstOrDefault();
        _scenes.Save();
    }

    public void AddSequenceAction()
    {
        if (_selectedSequence == null) return;
        var a = new SceneAction { Scene = _selectedSceneName ?? SceneNames.FirstOrDefault(), DelaySeconds = 5 };
        _selectedSequence.Actions.Add(a);
        SequenceActions.Add(a);
        HookAction(a);
        _scenes.Save();
    }

    public void RemoveSequenceAction(SceneAction a)
    {
        if (_selectedSequence == null) return;
        _selectedSequence.Actions.Remove(a);
        SequenceActions.Remove(a);
        _scenes.Save();
    }

    public void MoveSequenceAction(SceneAction a, int delta)
    {
        if (_selectedSequence == null) return;
        int i = _selectedSequence.Actions.IndexOf(a);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _selectedSequence.Actions.Count) return;
        _selectedSequence.Actions.RemoveAt(i);
        _selectedSequence.Actions.Insert(j, a);
        SequenceActions.Move(i, j);
        _scenes.Save();
    }

    public bool SequenceRunning => _sequencer?.Running == true;
    public string RunButtonText => SequenceRunning ? "Stop" : "Run";
    public string SequenceStatus => SequenceRunning
        ? $"running '{_sequencer!.RunningName}' - loops until stopped" : "";

    public void ToggleSequence()
    {
        if (_sequencer == null) return;
        if (SequenceRunning) _sequencer.Stop();
        else if (_selectedSequence is { Actions.Count: > 0 }) _sequencer.Start(_selectedSequence);
    }

    /// <summary>This sequence starts (and loops) whenever the app launches.</summary>
    public bool SequenceActiveAtStartup
    {
        get => _selectedSequence != null && _scenes.ActiveSequence == _selectedSequence.Name;
        set
        {
            if (_selectedSequence == null) return;
            _scenes.ActiveSequence = value ? _selectedSequence.Name : null;
            _scenes.Save();
            OnChanged();
        }
    }

    public void PersistScenes() => _scenes.Save();

    void ClearBackground()
    {
        if (_lcd == null) return;
        _lcd.Design.BackgroundImagePath = null;
        _lcd.Design.BgW = _lcd.Design.BgH = 0;
        OnChanged(nameof(LcdBackgroundName)); OnChanged(nameof(LcdBackground));
        NotifyBgRect();
        TouchLcd();
    }

    /// <summary>Editor-canvas background image source (null => show gradient).</summary>
    public ImageSource? LcdBackground
    {
        get
        {
            var p = _lcd?.Design.BackgroundImagePath;
            if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(p); img.EndInit(); img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }
}
