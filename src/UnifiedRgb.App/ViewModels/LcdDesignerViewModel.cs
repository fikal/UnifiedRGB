using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>The pump LCD designer's view model: the live design (elements,
/// background rect), the WYSIWYG editor state, and the saved screens (scenes)
/// + timed shows (sequences). The LcdDesignerPane binds to this directly (its
/// DataContext is the main view model's <c>Lcd</c>); the main view model keeps
/// navigation (which pane is on screen) and hands in the profile hooks the
/// shows need.</summary>
public sealed class LcdDesignerViewModel : INotifyPropertyChanged, IDisposable
{
    LcdController? _lcd;
    PawnIoCpuTempProvider? _cpuTemp;
    readonly Func<bool> _isOnScreen, _lightsSuppressed;
    readonly Func<string, bool> _applyProfile;
    readonly Func<IEnumerable<string>> _profileNames;
    readonly DispatcherTimer _lcdSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public LcdDesignerViewModel(Func<bool> isOnScreen, Func<bool> lightsSuppressed,
        Func<string, bool> applyProfile, Func<IEnumerable<string>> profileNames)
    {
        _isOnScreen = isOnScreen; _lightsSuppressed = lightsSuppressed;
        _applyProfile = applyProfile; _profileNames = profileNames;

        AddTimeCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Time), _ => Available);
        AddDateCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Date), _ => Available);
        AddTempCommand     = new RelayCommand(_ => AddElement(LcdElementKind.CpuTemp), _ => Available);
        AddTextCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Text), _ => Available);
        AddGpuTempCommand  = new RelayCommand(_ => AddElement(LcdElementKind.GpuTemp), _ => Available);
        AddFanRpmCommand   = new RelayCommand(_ => AddElement(LcdElementKind.FanRpm), _ => Available);
        AddNetSpeedCommand = new RelayCommand(_ => AddElement(LcdElementKind.NetSpeed), _ => Available);
        AddClockCommand    = new RelayCommand(_ => AddElement(LcdElementKind.AnalogClock), _ => Available);
        AddWeatherCommand  = new RelayCommand(_ => AddElement(LcdElementKind.Weather), _ => Available);
        DeleteElementCommand    = new RelayCommand(_ => DeleteElement(), _ => HasElement);
        ChooseBackgroundCommand = new RelayCommand(_ => ChooseBackground(), _ => Available);
        ClearBackgroundCommand  = new RelayCommand(_ => ClearBackground(), _ => Available);
        PickElementColorCommand = new RelayCommand(o => { if (_selectedElement != null && o is Rgb c) _selectedElement.ColorHex = c.ToString().TrimStart('#'); });
        RelaunchElevatedCommand = new RelayCommand(_ => RelaunchElevated());
    }

    public ICommand AddTimeCommand { get; }
    public ICommand AddDateCommand { get; }
    public ICommand AddTempCommand { get; }
    public ICommand AddTextCommand { get; }
    public ICommand AddGpuTempCommand { get; }
    public ICommand AddFanRpmCommand { get; }
    public ICommand AddNetSpeedCommand { get; }
    public ICommand AddClockCommand { get; }
    public ICommand AddWeatherCommand { get; }
    public ICommand DeleteElementCommand { get; }
    public ICommand ChooseBackgroundCommand { get; }
    public ICommand ClearBackgroundCommand { get; }
    public ICommand PickElementColorCommand { get; }
    public ICommand RelaunchElevatedCommand { get; }

    /// <summary>A pump LCD was found and is being driven.</summary>
    public bool Available => _lcd != null;

    public bool CpuTempUnavailable => _lcd != null && _cpuTemp?.Available != true;

    public ObservableCollection<LcdElement> LcdElements { get; } = new();

    LcdElement? _selectedElement;
    public LcdElement? SelectedElement
    {
        get => _selectedElement;
        set
        {
            _selectedElement = value;
            OnChanged(); OnChanged(nameof(HasElement)); OnChanged(nameof(IsCustomText));
            OnChanged(nameof(SelectedElementColor)); NotifyElemRgb();
        }
    }
    public bool HasElement => _selectedElement != null;
    public bool IsCustomText => _selectedElement?.Kind == LcdElementKind.Text;

    /// <summary>Two-way bridge between the color wheel (Color) and the element's hex.</summary>
    public Color SelectedElementColor
    {
        get => _selectedElement != null ? LcdController.ParseColor(_selectedElement.ColorHex) : Colors.White;
        set { if (_selectedElement != null) _selectedElement.ColorHex = $"{value.R:X2}{value.G:X2}{value.B:X2}"; }
    }

    /// <summary>Numeric R/G/B views of the element color — routed through
    /// SelectedElementColor so the wheel, hex, and boxes all stay in sync.</summary>
    public int ElemR { get => SelectedElementColor.R; set => SetElemRgb(r: value); }
    public int ElemG { get => SelectedElementColor.G; set => SetElemRgb(g: value); }
    public int ElemB { get => SelectedElementColor.B; set => SetElemRgb(b: value); }

    void SetElemRgb(int? r = null, int? g = null, int? b = null)
    {
        var c = SelectedElementColor;
        SelectedElementColor = Color.FromRgb(
            (byte)Math.Clamp(r ?? c.R, 0, 255),
            (byte)Math.Clamp(g ?? c.G, 0, 255),
            (byte)Math.Clamp(b ?? c.B, 0, 255));
    }

    void NotifyElemRgb()
    {
        OnChanged(nameof(ElemR)); OnChanged(nameof(ElemG)); OnChanged(nameof(ElemB));
    }

    public string LcdBackgroundName =>
        string.IsNullOrEmpty(_lcd?.Design.BackgroundImagePath) ? ""
        : System.IO.Path.GetFileName(_lcd!.Design.BackgroundImagePath!);

    /// <summary>Open the panel (if present), load the saved design and start
    /// rendering. Called once from the main view model's constructor.</summary>
    public void Start()
    {
        _lcd = LcdController.TryStart();
        _lcdSave.Tick += (_, _) => { _lcdSave.Stop(); _lcd?.Design.Save(); };
        if (_lcd == null) return;

        _lcd.Design = LcdDesign.Load();
        _cpuTemp = new PawnIoCpuTempProvider();
        _lcd.Temp = _cpuTemp;
        foreach (var e in _lcd.Design.Elements) { LcdElements.Add(e); Hook(e); }
        EnsureBgRect();   // migrate pre-rect designs to an explicit cover rect
        _lcd.Ticked += RefreshDisplays;
        _lcd.Start();
        OnChanged(nameof(Available));
        OnChanged(nameof(CpuTempUnavailable));
    }

    /// <summary>Turn the panel on (normal render) or off (blank frame). The
    /// panel isn't an RGB device, so sleep/lock drives it through here.</summary>
    public void SetOn(bool on)
    {
        if (_lcd != null) { _lcd.On = on; _lcd.Refresh(); }
    }

    // Named handler so elements can be UNHOOKED (delete / design swap): the
    // old anonymous lambda could never be removed, and a scene sequence
    // swapping designs re-hooked fresh handlers every cycle.
    void Hook(LcdElement e)
    {
        e.PropertyChanged -= LcdElementChanged;   // idempotent
        e.PropertyChanged += LcdElementChanged;
    }
    void Unhook(LcdElement e) => e.PropertyChanged -= LcdElementChanged;

    void LcdElementChanged(object? s, PropertyChangedEventArgs args)
    {
        // Live editor pushes are NOT user edits. ClockImage especially: it is
        // set from RefreshDisplays on every render tick, and reacting with
        // TouchLcd() -> Refresh() -> Ticked -> RefreshDisplays -> ClockImage
        // would recurse to a stack overflow the moment an analog clock was on
        // screen with the designer open.
        if (args.PropertyName is nameof(LcdElement.Display) or nameof(LcdElement.ClockImage)) return;
        // Label/ClockSize are derived notifications raised alongside every
        // setter; reacting to them doubled the LCD renders per drag step (4 per
        // mouse-move for an X+Y change).
        if (args.PropertyName is nameof(LcdElement.Label) or nameof(LcdElement.ClockSize)) return;
        if (ReferenceEquals(s, _selectedElement) && args.PropertyName == nameof(LcdElement.ColorHex))
        { OnChanged(nameof(SelectedElementColor)); NotifyElemRgb(); }
        TouchLcd();
    }

    /// <summary>Push the live rendered text into each element for the editor.</summary>
    void RefreshDisplays()
    {
        if (_lcd == null) return;
        // The Display/ClockImage properties feed the WYSIWYG editor only (the
        // physical panel renders separately) — skip the per-tick text pushes
        // and clock-face bitmap when the designer isn't on screen.
        if (!_isOnScreen()) return;
        foreach (var e in LcdElements)
        {
            if (e.Kind == LcdElementKind.AnalogClock) e.ClockImage = LcdController.RenderClockImage(e);
            else e.Display = _lcd.ElementText(e);
        }
    }

    /// <summary>Delete the selected element (bound to the Delete key on the canvas).</summary>
    public void DeleteSelectedElement() => DeleteElement();

    static void RelaunchElevated()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath, UseShellExecute = true, Verb = "runas",
            };
            System.Diagnostics.Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch { /* user declined UAC */ }
    }

    /// <summary>Re-render the pump now and schedule a debounced save.</summary>
    public void TouchLcd()
    {
        _lcd?.Refresh();
        _lcdSave.Stop(); _lcdSave.Start();
    }

    /// <summary>Rendered text for an element (used by the WYSIWYG editor).</summary>
    public string ElementText(LcdElement e) => _lcd?.ElementText(e) ?? e.Label;

    void AddElement(LcdElementKind kind)
    {
        if (_lcd == null) return;
        var e = new LcdElement
        {
            Kind = kind, X = 110, Y = 105,
            FontSize = kind switch
            {
                LcdElementKind.Time => 60,
                LcdElementKind.AnalogClock => 55,       // radius -> 110px face
                LcdElementKind.NetSpeed or LcdElementKind.Weather => 26,
                _ => 32,
            },
            Bold = kind == LcdElementKind.Time,
            Text = kind == LcdElementKind.Text ? "Text" : "",
            ColorHex = kind switch
            {
                LcdElementKind.CpuTemp => "78C8FF",
                LcdElementKind.GpuTemp => "51E087",
                LcdElementKind.FanRpm => "FFB84C",
                LcdElementKind.NetSpeed => "8AD0FF",
                LcdElementKind.Weather => "FFD27A",
                _ => "FFFFFF",
            },
        };
        if (kind == LcdElementKind.AnalogClock) { e.X = 100; e.Y = 60; }
        _lcd.Design.Elements.Add(e);
        LcdElements.Add(e); Hook(e);
        SelectedElement = e;
        TouchLcd();
    }

    void DeleteElement()
    {
        if (_lcd == null || _selectedElement == null) return;
        Unhook(_selectedElement);
        _lcd.Design.Elements.Remove(_selectedElement);
        LcdElements.Remove(_selectedElement);
        SelectedElement = LcdElements.LastOrDefault();
        TouchLcd();
    }

    void ChooseBackground()
    {
        if (_lcd == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose a background image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        _lcd.Design.BackgroundImagePath = dlg.FileName;
        _lcd.Design.BgW = 0;                    // new image: recompute cover
        EnsureBgRect();
        OnChanged(nameof(LcdBackgroundName)); OnChanged(nameof(LcdBackground));
        NotifyBgRect();
        TouchLcd();
    }

    void ClearBackground()
    {
        if (_lcd == null) return;
        _lcd.Design.BackgroundImagePath = null;
        _lcd.Design.BgW = _lcd.Design.BgH = 0;
        OnChanged(nameof(LcdBackgroundName)); OnChanged(nameof(LcdBackground));
        NotifyBgRect();
        TouchLcd();
    }

    /// <summary>Editor-canvas background image source (null => show gradient).
    /// Decoded once per (path, mtime): this getter is bound three times and
    /// re-read on every LcdBgX/Y change, i.e. six full-resolution decodes per
    /// mouse-move while dragging a background - visible stutter on a 4K JPG.</summary>
    string? _bgCachePath; DateTime _bgCacheStamp; ImageSource? _bgCache;
    public ImageSource? LcdBackground
    {
        get
        {
            var p = _lcd?.Design.BackgroundImagePath;
            if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) { _bgCachePath = null; _bgCache = null; return null; }
            var stamp = System.IO.File.GetLastWriteTimeUtc(p);
            if (p == _bgCachePath && stamp == _bgCacheStamp) return _bgCache;
            _bgCachePath = p; _bgCacheStamp = stamp; _bgCache = null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(p); img.EndInit(); img.Freeze();
                _bgCache = img;
            }
            catch { }
            return _bgCache;
        }
    }

    /*-----------------------------------------------------*\
    | Background placement: one rect, edited here, rendered |
    | identically by the editor canvas and the panel.       |
    \*-----------------------------------------------------*/
    double _bgNatW = 1, _bgNatH = 1;   // natural pixel size (aspect source)

    /// <summary>Load the image's natural size and, when the design has no
    /// stored rect yet, materialize a centered cover rect (the panel's old
    /// behavior, so existing designs look unchanged).</summary>
    void EnsureBgRect()
    {
        var d = _lcd?.Design;
        if (d == null) return;
        var src = LcdBackground as BitmapSource;
        if (src == null) return;
        _bgNatW = Math.Max(1, src.PixelWidth);
        _bgNatH = Math.Max(1, src.PixelHeight);
        if (d.BgW > 0.5) return;
        double scale = Math.Max(320.0 / _bgNatW, 240.0 / _bgNatH);
        d.BgW = Math.Round(_bgNatW * scale);
        d.BgH = Math.Round(_bgNatH * scale);
        d.BgX = Math.Round((320 - d.BgW) / 2);
        d.BgY = Math.Round((240 - d.BgH) / 2);
    }

    double BgAspect => _bgNatW / Math.Max(1.0, _bgNatH);

    public bool LcdHasBackground => LcdBackground != null;

    public double LcdBgX
    {
        get => _lcd?.Design.BgX ?? 0;
        set { if (_lcd == null) return; _lcd.Design.BgX = Math.Round(value); NotifyBgRect(); TouchLcd(); }
    }
    public double LcdBgY
    {
        get => _lcd?.Design.BgY ?? 0;
        set { if (_lcd == null) return; _lcd.Design.BgY = Math.Round(value); NotifyBgRect(); TouchLcd(); }
    }
    public double LcdBgW
    {
        get => _lcd?.Design.BgW ?? 0;
        set
        {
            if (_lcd == null) return;
            var d = _lcd.Design;
            d.BgW = Math.Round(Math.Clamp(value, 8, 2000));
            if (d.BgAspectLock) d.BgH = Math.Round(d.BgW / BgAspect);
            NotifyBgRect(); TouchLcd();
        }
    }
    public double LcdBgH
    {
        get => _lcd?.Design.BgH ?? 0;
        set
        {
            if (_lcd == null) return;
            var d = _lcd.Design;
            d.BgH = Math.Round(Math.Clamp(value, 8, 2000));
            if (d.BgAspectLock) d.BgW = Math.Round(d.BgH * BgAspect);
            NotifyBgRect(); TouchLcd();
        }
    }
    public bool LcdBgAspectLock
    {
        get => _lcd?.Design.BgAspectLock ?? true;
        set
        {
            if (_lcd == null) return;
            _lcd.Design.BgAspectLock = value;
            // Re-locking snaps the height back onto the image's aspect.
            if (value) _lcd.Design.BgH = Math.Round(_lcd.Design.BgW / BgAspect);
            NotifyBgRect(); TouchLcd();
        }
    }

    // Resize grip sits at the rect's bottom-right corner.
    public double LcdBgGripX => LcdBgX + LcdBgW - 7;
    public double LcdBgGripY => LcdBgY + LcdBgH - 7;

    void NotifyBgRect()
    {
        OnChanged(nameof(LcdBgX)); OnChanged(nameof(LcdBgY));
        OnChanged(nameof(LcdBgW)); OnChanged(nameof(LcdBgH));
        OnChanged(nameof(LcdBgAspectLock)); OnChanged(nameof(LcdHasBackground));
        OnChanged(nameof(LcdBgGripX)); OnChanged(nameof(LcdBgGripY));
    }

    /// <summary>The three placements share one rect computation: scale the
    /// natural size by `scale` (or keep the current size when null), center.</summary>
    void PlaceBackground(double? scale)
    {
        if (_lcd == null) return;
        var d = _lcd.Design;
        if (scale is double s) { d.BgW = Math.Round(_bgNatW * s); d.BgH = Math.Round(_bgNatH * s); }
        d.BgX = Math.Round((320 - d.BgW) / 2); d.BgY = Math.Round((240 - d.BgH) / 2);
        NotifyBgRect(); TouchLcd();
    }

    /// <summary>Cover the whole panel (crops the overflow), centered.</summary>
    public void BgFill() => PlaceBackground(Math.Max(320.0 / _bgNatW, 240.0 / _bgNatH));

    /// <summary>Fit the whole image on the panel (letterboxed), centered.</summary>
    public void BgFit() => PlaceBackground(Math.Min(320.0 / _bgNatW, 240.0 / _bgNatH));

    /// <summary>Center the image at its current size.</summary>
    public void BgCenter() => PlaceBackground(null);

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
    public IReadOnlyList<string> ProfileChoices => new[] { KeepChoice }.Concat(_profileNames()).ToList();

    /// <summary>The profile list changed (the Show tab's lights dropdowns are
    /// computed from it and would otherwise stay frozen at launch time).</summary>
    public void NotifyProfilesChanged() => OnChanged(nameof(ProfileChoices));

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
    void SceneActionChanged(object? s, PropertyChangedEventArgs e) => _scenes.Save();

    /// <summary>Populate the scene/sequence lists and auto-run the startup show.
    /// Called once, after the main view model has applied the startup profile.</summary>
    public void InitScenes()
    {
        foreach (var sc in _scenes.Scenes) SceneNames.Add(sc.Name);
        foreach (var sq in _scenes.Sequences) Sequences.Add(sq);
        _sequencer = new SceneSequencer(ApplySceneAction);
        _sequencer.StateChanged += () =>
        {
            OnChanged(nameof(SequenceRunning));
            OnChanged(nameof(RunButtonText));
            OnChanged(nameof(SequenceStatus));
        };
        // Auto-run the active sequence with the app.
        var active = _scenes.Sequences.FirstOrDefault(x => x.Name == _scenes.ActiveSequence);
        if (active != null && Available)
        {
            SelectedSequence = active;
            _sequencer.Start(active);
        }
    }

    void ApplySceneAction(SceneAction a)
    {
        // Lights deliberately off (locked / night): hold the step. Applying a
        // profile here relit the case while locked AND cleared the automation's
        // return point, so the unlock had nothing to restore and left the pump
        // LCD blank. The sequencer keeps ticking; the next step after the
        // lights return applies normally.
        if (_lightsSuppressed()) return;
        if (!string.IsNullOrEmpty(a.Scene) &&
            _scenes.Scenes.FirstOrDefault(x => x.Name == a.Scene) is LcdScene sc)
        {
            _selectedSceneName = sc.Name;   // reflect without re-loading twice
            OnChanged(nameof(SelectedSceneName));
            LoadDesignIntoEditor(SceneStore.Clone(sc.Design));
        }
        if (!string.IsNullOrEmpty(a.Profile))
            _applyProfile(a.Profile);
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

    public void Dispose()
    {
        _lcdSave.Stop();
        _lcd?.Design.Save();
        _lcd?.Dispose();
        _cpuTemp?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
