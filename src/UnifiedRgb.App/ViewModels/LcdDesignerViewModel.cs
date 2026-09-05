using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
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
        Func<string, bool> applyProfile, Func<IEnumerable<string>> profileNames,
        Func<string?> currentProfile)
    {
        _isOnScreen = isOnScreen; _lightsSuppressed = lightsSuppressed;
        _applyProfile = applyProfile; _profileNames = profileNames;
        _currentProfile = currentProfile;

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

    /// <summary>A pump LCD was found and is being driven.</summary>
    public bool Available => _lcd != null;

    /// <summary>The panel is up but the PawnIO driver is not: the CPU-temp
    /// element shows "--". Elevation is guaranteed by the manifest, so the
    /// driver is the only thing that can be missing; re-evaluated live.</summary>
    public bool CpuTempUnavailable => _lcd != null && _cpuTemp?.Available != true;

    /// <summary>PawnIO got installed in-app: the banner clears without a restart.</summary>
    public void NotifyPawnIoChanged() => OnChanged(nameof(CpuTempUnavailable));

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
        // Editing has paused: the design as it stands is what the next undo
        // should return to.
        _undoSettle.Tick += (_, _) => SettleUndo();
        _history.Changed += NotifyHistory;
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
        // After the fact, so this records _baseline (the pre-burst design) and
        // coalesces the rest of the burst into that one entry.
        CaptureUndo();
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
    int _clockSecond = -1;   // wall-clock second the editor clock face was last rasterized for
    void RefreshDisplays()
    {
        if (_lcd == null) return;
        // The Display/ClockImage properties feed the WYSIWYG editor only (the
        // physical panel renders separately) — skip the per-tick text pushes
        // and clock-face bitmap when the designer isn't on screen, including
        // while the window is hidden in the tray with the LCD item selected.
        if (!_isOnScreen() || !MainWindowState.Visible) return;
        // With a GIF background Ticked fires at 10 Hz; the clock face only
        // changes once a second, so the RenderTargetBitmap is made once per
        // second (TouchLcd resets this so an edit re-rasterizes at once).
        int sec = DateTime.Now.Second;
        bool clockDue = sec != _clockSecond;
        foreach (var e in LcdElements)
        {
            if (e.Kind == LcdElementKind.AnalogClock)
            {
                if (clockDue || e.ClockImage == null) e.ClockImage = LcdController.RenderClockImage(e);
            }
            else
            {
                var text = _lcd.ElementText(e);
                if (text != e.Display) e.Display = text;   // the setter notifies unconditionally
            }
        }
        _clockSecond = sec;
    }

    /*-----------------------------------------------------*\
    | Undo / redo.                                          |
    |                                                       |
    | Whole-design snapshots (a few KB of JSON each). The    |
    | tricky part is that property changes arrive AFTER the  |
    | edit, so the pre-edit state has to be kept standing:   |
    | _baseline is the design as of the last quiet moment,   |
    | and that is what gets pushed. A settle timer refreshes |
    | it once editing pauses, which is also what collapses a |
    | slider drag into ONE undo step instead of forty.       |
    \*-----------------------------------------------------*/

    readonly UndoStack<string> _history = new(50);
    readonly DispatcherTimer _undoSettle = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string _baseline = "";

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    string Snapshot() => _lcd == null ? "" : JsonSerializer.Serialize(_lcd.Design);

    /// <summary>Call BEFORE a one-shot action: an add, a delete, a background
    /// change. Always records, because these are never a continuation of
    /// anything. Coalescing them was a bug: acting within half a second of any
    /// property change (a colour box writing back as the selection moved, say)
    /// folded the action into that burst and left nothing to undo.</summary>
    public void CaptureUndoNow()
    {
        if (_lcd == null) return;
        SettleUndo();                 // close any open burst: _baseline is now current
        _history.Push(_baseline);
        _undoSettle.Start();
    }

    /// <summary>Mouse down on the canvas. Everything until EndGesture is ONE
    /// undo step; the stack owns the bookkeeping.</summary>
    public void BeginGesture()
    {
        if (_lcd == null || _history.InGesture) return;
        SettleUndo();            // _baseline is the design as you grabbed it
        _history.BeginGesture(_baseline);
    }

    /// <summary>Mouse up, or capture lost to an Alt+Tab.</summary>
    public void EndGesture()
    {
        if (!_history.InGesture) return;
        _history.EndGesture();
        _undoSettle.Stop();
        _baseline = Snapshot();
    }

    /// <summary>Call BEFORE a property edit. The first in a burst records; the
    /// rest extend it, so dragging a slider is one undo step and not forty.</summary>
    public void CaptureUndo()
    {
        if (_lcd == null) return;
        if (_history.InGesture)
        {
            // The first movement records where the drag started; the rest of it
            // is the same entry, however long the drag runs or pauses.
            _history.GestureEdit();
            return;
        }
        if (_undoSettle.IsEnabled)
        {
            _undoSettle.Stop(); _undoSettle.Start();
            return;
        }
        if (_baseline.Length == 0) _baseline = Snapshot();
        _history.Push(_baseline);
        _undoSettle.Start();
    }

    /// <summary>End any open burst, so the next capture records the state as it
    /// is now rather than as it was before the burst.</summary>
    void SettleUndo()
    {
        _undoSettle.Stop();
        _baseline = Snapshot();
    }

    public void Undo()
    {
        if (_lcd == null) return;
        SettleUndo();
        if (_history.Undo(_baseline) is string prev) ApplySnapshot(prev);
        else Log.Occasional("lcd", "undo-empty", "undo requested with nothing to step back to");
    }

    public void Redo()
    {
        if (_lcd == null) return;
        SettleUndo();
        if (_history.Redo(_baseline) is string next) ApplySnapshot(next);
    }

    void ApplySnapshot(string json)
    {
        LcdDesign? d;
        try { d = JsonSerializer.Deserialize<LcdDesign>(json); }
        catch (Exception ex) { Log.Warn("lcd", $"undo snapshot unreadable: {ex.Message}"); return; }
        if (d == null) return;

        // Put the selection back on the same row, which is what the eye expects
        // after undoing a change to one element.
        int index = _selectedElement == null ? -1 : LcdElements.IndexOf(_selectedElement);
        LoadDesignIntoEditor(d, fromShow: false);
        if (index >= 0 && index < LcdElements.Count) SelectedElement = LcdElements[index];
        TouchLcd();
        _baseline = Snapshot();
        NotifyHistory();
    }

    void NotifyHistory() { OnChanged(nameof(CanUndo)); OnChanged(nameof(CanRedo)); }

    /// <summary>Delete the selected element (bound to the Delete key on the canvas).</summary>
    public void DeleteSelectedElement() => DeleteElement();

    /// <summary>Re-render the pump now and schedule a debounced save.</summary>
    public void TouchLcd()
    {
        _liveIsShowScene = false;   // a user edit: the live design is the canvas again
        _clockSecond = -1;
        _lcd?.Refresh();
        _lcdSave.Stop(); _lcdSave.Start();
    }

    /// <summary>Rendered text for an element (used by the WYSIWYG editor).</summary>
    public string ElementText(LcdElement e) => _lcd?.ElementText(e) ?? e.Label;

    void AddElement(LcdElementKind kind)
    {
        if (_lcd == null) return;
        CaptureUndoNow();
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
        CaptureUndoNow();
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
        CaptureUndoNow();
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
        CaptureUndoNow();
        _lcd.Design.BackgroundImagePath = null;
        _lcd.Design.BgW = _lcd.Design.BgH = 0;
        OnChanged(nameof(LcdBackgroundName)); OnChanged(nameof(LcdBackground));
        NotifyBgRect();
        TouchLcd();
    }

    /// <summary>Editor-canvas background image source (null => show gradient).
    /// The controller owns the ONE decoded copy (the panel render draws the
    /// same bitmap; this view model used to hold a second full-resolution
    /// decode), cached there per (path, write time) - so this getter, re-read
    /// on every LcdBgX/Y change while dragging, is a field read.</summary>
    public ImageSource? LcdBackground => _lcd?.Background;

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
        if (_lcd == null || _lcd.Background == null) return;
        var d = _lcd.Design;
        var (w, h) = _lcd.BackgroundSize;
        _bgNatW = Math.Max(1, w);
        _bgNatH = Math.Max(1, h);
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
            CaptureUndo();
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

    /// <summary>Position / size in ONE step (one panel render): the canvas
    /// drags used to render twice per mouse-move via the X-then-Y / W-then-H
    /// setters above. Same clamps and aspect handling as the setters.</summary>
    public void MoveBg(double x, double y)
    {
        if (_lcd == null) return;
        CaptureUndo();
        _lcd.Design.BgX = Math.Round(x); _lcd.Design.BgY = Math.Round(y);
        NotifyBgRect(); TouchLcd();
    }
    public void SetBgSize(double w, double h)
    {
        if (_lcd == null) return;
        CaptureUndo();          // the grip drag writes here, not through LcdBgW
        var d = _lcd.Design;
        d.BgW = Math.Round(Math.Clamp(w, 8, 2000));
        d.BgH = d.BgAspectLock ? Math.Round(d.BgW / BgAspect) : Math.Round(Math.Clamp(h, 8, 2000));
        NotifyBgRect(); TouchLcd();
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

    readonly Func<string?> _currentProfile;

    void ApplySceneAction(SceneAction a)
    {
        // Lights deliberately off (locked / night): hold the step. Applying a
        // profile here relit the case while locked AND cleared the automation's
        // return point, so the unlock had nothing to restore and left the pump
        // LCD blank. The sequencer keeps ticking; the next step after the
        // lights return applies normally.
        if (_lightsSuppressed()) return;
        // Re-applying what is already on is not free: loading a profile stops
        // and restarts every effect channel, so a step that names the running
        // profile visibly resets the animation instead of leaving it alone.
        // Steps that genuinely change nothing now just tick past.
        if (!string.IsNullOrEmpty(a.Scene) &&
            _scenes.Scenes.FirstOrDefault(x => x.Name == a.Scene) is LcdScene sc &&
            // Only safe to skip when the show itself put this screen up. If the
            // user has been editing, the live design is no longer that scene.
            !(_liveIsShowScene && _selectedSceneName == sc.Name))
        {
            _selectedSceneName = sc.Name;   // reflect without re-loading twice
            OnChanged(nameof(SelectedSceneName));
            LoadDesignIntoEditor(SceneStore.Clone(sc.Design), fromShow: true);
        }
        if (!string.IsNullOrEmpty(a.Profile) &&
            !string.Equals(_currentProfile(), a.Profile, StringComparison.OrdinalIgnoreCase))
            _applyProfile(a.Profile);
    }

    /// <summary>True while the live design was swapped in by a running show
    /// rather than by the user. Rendered, but never persisted as the canvas: a
    /// show used to rewrite lcd.json on every step (a 5 s show = ~17,000 atomic
    /// file replaces a day) and replace the user's own design with whichever
    /// scene played last.</summary>
    bool _liveIsShowScene;

    /// <summary>Swap the live design (editor + pump) for another one.</summary>
    void LoadDesignIntoEditor(LcdDesign d, bool fromShow = false)
    {
        if (_lcd == null) return;
        // A user edit still waiting for its debounced save lands before its
        // design is swapped out (the save writes whatever Design is current).
        if (fromShow && _lcdSave.IsEnabled) { _lcdSave.Stop(); _lcd.Design.Save(); }
        _lcd.Design = d;
        foreach (var old in LcdElements) Unhook(old);   // swap-out: no stranded handlers
        LcdElements.Clear();
        foreach (var e in d.Elements) { LcdElements.Add(e); Hook(e); }
        SelectedElement = null;
        EnsureBgRect();
        OnChanged(nameof(LcdBackground)); OnChanged(nameof(LcdBackgroundName));
        NotifyBgRect();
        // A show replacing the canvas is not an edit: it must not record an
        // undo entry, and the baseline has to follow it or the next real edit
        // would undo to a design that is no longer on screen.
        if (fromShow) { _undoSettle.Stop(); _baseline = Snapshot(); }
        if (fromShow) { _liveIsShowScene = true; _clockSecond = -1; _lcd.Refresh(); }
        else TouchLcd();
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
        _sequencer?.Stop();   // a queued step must not drive the disposed controller
        _lcdSave.Stop();
        if (_lcd != null)
        {
            _lcd.Ticked -= RefreshDisplays;
            if (!_liveIsShowScene) _lcd.Design.Save();   // the canvas, not the show's last scene
            _lcd.Dispose();
            _lcd = null;          // Available and every setter short-circuit from here on
        }
        _cpuTemp?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
