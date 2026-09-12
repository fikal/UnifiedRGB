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
    // Shows used to live here and brought their own dependencies with them: a
    // way to apply a profile, the profile list, what is applied now. They left
    // with the shows.
    readonly Func<bool> _isOnScreen;
    readonly DispatcherTimer _lcdSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public LcdDesignerViewModel(Func<bool> isOnScreen)
    {
        _isOnScreen = isOnScreen;

        AddTimeCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Time), _ => Available);
        AddDateCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Date), _ => Available);
        AddTempCommand     = new RelayCommand(_ => AddElement(LcdElementKind.CpuTemp), _ => Available);
        AddTextCommand     = new RelayCommand(_ => AddElement(LcdElementKind.Text), _ => Available);
        AddGpuTempCommand  = new RelayCommand(_ => AddElement(LcdElementKind.GpuTemp), _ => Available);
        AddFanRpmCommand   = new RelayCommand(_ => AddElement(LcdElementKind.FanRpm), _ => Available);
        AddNetSpeedCommand = new RelayCommand(_ => AddElement(LcdElementKind.NetSpeed), _ => Available);
        AddClockCommand    = new RelayCommand(_ => AddElement(LcdElementKind.AnalogClock), _ => Available);
        AddWeatherCommand  = new RelayCommand(_ => AddElement(LcdElementKind.Weather), _ => Available);
        AddNowPlayingCommand = new RelayCommand(_ => AddElement(LcdElementKind.NowPlaying), _ => Available);
        AddAlbumArtCommand   = new RelayCommand(_ => AddElement(LcdElementKind.AlbumArt), _ => Available);
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
    public ICommand AddNowPlayingCommand { get; }
    public ICommand AddAlbumArtCommand { get; }
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
    /// <summary>Looks for the panel until it finds it. Fast at first, then
    /// slowly forever.
    ///
    /// This used to be one attempt in the constructor. Started from the logon
    /// task, the app can be running before the panel's USB interface has been
    /// enumerated, so a cold boot found nothing and the screen then stayed dark
    /// for the whole session while launching by hand worked every time. Slowing
    /// down rather than giving up also picks up a panel plugged in later.</summary>
    readonly System.Windows.Threading.DispatcherTimer _find =
        new() { Interval = TimeSpan.FromSeconds(3) };
    int _findAttempts;

    public void Start()
    {
        _lcdSave.Tick += (_, _) => { _lcdSave.Stop(); _lcd?.Design.Save(); };
        // Editing has paused: the design as it stands is what the next undo
        // should return to.
        _undoSettle.Tick += (_, _) => SettleUndo();
        _history.Changed += NotifyHistory;

        if (TryAttach(late: false)) return;

        UnifiedRgb.Core.Log.Info("lcd", "no pump LCD yet, still looking");
        _find.Tick += (_, _) =>
        {
            if (TryAttach(late: true)) { _find.Stop(); return; }
            // A boot race resolves in seconds; past that this is only here to
            // catch a panel plugged in later, so it stops being a busy loop.
            if (++_findAttempts == 20)
            {
                _find.Interval = TimeSpan.FromSeconds(30);
                UnifiedRgb.Core.Log.Info("lcd", "still no pump LCD after a minute, checking occasionally from here");
            }
        };
        _find.Start();
    }

    /// <summary>Open the panel and bring everything that depends on it to life,
    /// or false when it is not there. Safe to call repeatedly.</summary>
    /// <param name="late">True when the panel turned up after startup, which
    /// is the only case that has to ask for the device list to be rebuilt. On
    /// the normal path the list has not been built yet and rebuilding it here
    /// would run against an empty device collection for nothing.</param>
    bool TryAttach(bool late)
    {
        if (_lcd != null) return true;
        var lcd = LcdController.TryStart();
        if (lcd == null) return false;

        _lcd = lcd;
        _lcd.Design = LcdDesign.Load();
        _cpuTemp = new PawnIoCpuTempProvider();
        _lcd.Temp = _cpuTemp;
        foreach (var e in _lcd.Design.Elements) { LcdElements.Add(e); Hook(e); }
        EnsureBgRect();   // migrate pre-rect designs to an explicit cover rect
        _lcd.Ticked += RefreshDisplays;
        _lcd.Start();
        OnChanged(nameof(Available));
        OnChanged(nameof(CpuTempUnavailable));
        SelectLoadedScene();

        UnifiedRgb.Core.Log.Info("lcd",
            $"pump LCD opened, {_lcd.Design.Elements.Count} element(s)"
            + (_findAttempts > 0 ? $" (after {_findAttempts} retr{(_findAttempts == 1 ? "y" : "ies")})" : ""));

        // The left list only grows a Pump LCD row when this is available, so a
        // panel that turns up late has to ask for the list to be rebuilt.
        if (late) Attached?.Invoke();
        return true;
    }

    /// <summary>Raised once the panel is open, however long that took.</summary>
    public event Action? Attached;

    /// <summary>Turn the panel on (normal render) or off (blank frame). The
    /// panel isn't an RGB device, so sleep/lock drives it through here.</summary>
    /// <summary>Blank the panel or bring it back. Logged on change, because a
    /// panel deliberately blanked (a scheduled dark window, a locked session)
    /// and a panel that never opened look identical from the outside, and the
    /// log could not tell them apart either.</summary>
    public void SetOn(bool on)
    {
        if (_lcd == null) return;
        bool was = _lcd.On;
        _lcd.On = on;
        _lcd.Refresh();
        if (was != on) UnifiedRgb.Core.Log.Info("lcd", on ? "panel on" : "panel blanked");
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
        // Live editor pushes are NOT user edits. DrawnImage especially: it is
        // set from RefreshDisplays on every render tick, and reacting with
        // TouchLcd() -> Refresh() -> Ticked -> RefreshDisplays -> DrawnImage
        // would recurse to a stack overflow the moment a clock or cover art was
        // on screen with the designer open.
        if (args.PropertyName is nameof(LcdElement.Display) or nameof(LcdElement.DrawnImage)) return;
        // Label/ClockSize/DrawnSize/EditorMaxWidth are derived notifications
        // raised alongside every setter; reacting to them doubled the LCD
        // renders per drag step (4 per mouse-move for an X+Y change).
        if (args.PropertyName is nameof(LcdElement.Label) or nameof(LcdElement.ClockSize)
            or nameof(LcdElement.DrawnSize) or nameof(LcdElement.EditorMaxWidth)) return;

        // Only now, past the live pushes: a real edit. Recording BEFORE the
        // guards meant the clock face and the ticking time pushed an undo entry
        // every second the designer was open, so within a minute the whole
        // history was snapshots of an unchanged design and Ctrl+Z did nothing
        // visible.
        CaptureUndo();
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
                if (clockDue || e.DrawnImage == null) e.DrawnImage = LcdController.RenderClockImage(e);
            }
            else if (e.Kind == LcdElementKind.AlbumArt)
            {
                MediaService.EnsureStarted();
                var art = MediaService.Art;
                if (!ReferenceEquals(art, e.DrawnImage)) e.DrawnImage = art;
            }
            else
            {
                var text = _lcd.ElementText(e);
                // Nothing playing: show the label so the element stays visible
                // and grabbable in the editor even though the panel draws
                // nothing. Only now-playing can come back empty.
                if (text.Length == 0 && e.Kind == LcdElementKind.NowPlaying) text = e.Label;
                if (text != e.Display) e.Display = text;   // the setter notifies unconditionally
            }
        }
        _clockSecond = sec;
    }

    /*-----------------------------------------------------*\
    | Undo / redo.                                           |
    |                                                        |
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

    /// <summary>True while the mouse is down on the canvas. Undo is refused
    /// during a drag: it would rebuild the element list under the hand that is
    /// holding one.</summary>
    public bool InGesture => _history.InGesture;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    string Snapshot() => _lcd == null ? "" : JsonSerializer.Serialize(_lcd.Design);

    /// <summary>Call BEFORE a one-shot action: an add, a delete, a background
    /// change. Always records, because these are never a continuation of
    /// anything. Coalescing them was a bug: acting within half a second of any
    /// property change (a color box writing back as the selection moved, say)
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
    /// <summary>Counts the user's own changes to what the pump shows: canvas
    /// edits, picking a screen, saving or deleting one. An automation snapshot
    /// records it, and a restore whose count has moved on leaves the panel
    /// alone - the user changed the screen during the override and that edit
    /// is the newest intent, exactly as LightingApplied protects RGB edits.
    /// Screens put up by a profile or a show do not count.</summary>
    int _userEdits;

    public void TouchLcd()
    {
        _userEdits++;
        MarkCanvas();
    }

    /// <summary>The live design is the user's canvas (not a show scene):
    /// re-render and schedule the debounced save. WITHOUT counting a user
    /// edit - a design loaded by a profile's screen, or put back by an
    /// automation restore, goes through here too, and counting those would
    /// make every override look like the user had edited during it.</summary>
    void MarkCanvas()
    {
        _liveIsShowScene = false;   // the live design is the canvas again
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
                LcdElementKind.NowPlaying => 22,
                LcdElementKind.AlbumArt => 90,          // a 90px square of cover
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
                LcdElementKind.NowPlaying => "D9A6FF",
                _ => "FFFFFF",
            },
        };
        if (kind == LcdElementKind.AnalogClock) { e.X = 100; e.Y = 60; }
        if (kind == LcdElementKind.NowPlaying) { e.X = 20; e.Y = 200; }   // a caption line, low and wide
        if (kind == LcdElementKind.AlbumArt) { e.X = 115; e.Y = 40; }
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
            CaptureUndo();
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
            CaptureUndo();
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
    SceneStore _scenes = SceneStore.Load();

    public ObservableCollection<string> SceneNames { get; } = new();

    string _sceneNameInput = "";
    public string SceneNameInput { get => _sceneNameInput; set { _sceneNameInput = value; OnChanged(); } }

    public const string KeepChoice = "(no change)";
    public IReadOnlyList<string> SceneChoices => new[] { KeepChoice }.Concat(SceneNames).ToList();


    string? _selectedSceneName;
    public string? SelectedSceneName
    {
        get => _selectedSceneName;
        set
        {
            _userEdits++;   // the dropdown: the user's choice
            _selectedSceneName = value;
            OnChanged();
            // Selecting a scene loads it into the editor (and onto the pump).
            var sc = _scenes.Scenes.FirstOrDefault(x => x.Name == value);
            if (sc != null) LoadDesignIntoEditor(FromScene(sc));
        }
    }

    /// <summary>The saved screen the pump is showing, or null when the canvas
    /// is not one. What a profile records at save time.</summary>
    public string? CurrentScreen => _lcd?.Design.SceneName;

    /// <summary>Put a saved screen up because a profile asked for it. False
    /// when there is no panel or no such screen - the profile's lighting has
    /// already applied by then, so this is reported, not thrown. A screen that
    /// is already up is left alone, edits and all: switching profiles must not
    /// stomp on a design someone is in the middle of.</summary>
    /// <param name="fromShow">A running show is asking, rather than a profile
    /// applied by hand. That flips which "already up" counts as nothing to do,
    /// and the two cases genuinely differ:
    ///
    /// By hand, a screen already up is LEFT ALONE, edits and all - switching
    /// profiles must not stomp a design somebody is in the middle of. From a
    /// show, an edited canvas must be RELOADED from the saved scene, because the
    /// show is replaying a screen and the edit is not part of it. The skip used
    /// to live in the step handler; with a step now being a profile it has to
    /// live here, which is the one place both callers pass through.</param>
    public bool ShowScreen(string name, bool fromShow = false)
    {
        if (_lcd == null) return false;
        var sc = _scenes.Scenes.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sc == null)
        {
            Log.Warn("scenes", $"a profile asked for pump screen '{name}', which does not exist");
            return false;
        }
        // Nothing to do only when the CURRENT owner is the one asking again.
        if (_lcd.Design.SceneName == sc.Name && _liveIsShowScene == fromShow) return true;
        // Not through the SelectedSceneName setter: that is the user's dropdown
        // and counts as their edit.
        //
        // fromShow carries the ownership now, in place of the flag this used to
        // consult. A show step's screen loads with show-only status and never
        // becomes the saved canvas; a HAND-applied profile's screen takes the
        // canvas, which is what asking for that profile means.
        //
        // One rough edge is left: a profile applied by hand that pins a screen
        // AND starts a show arrives with fromShow false while the previous show
        // still owns the panel, so its screen becomes the canvas. Arguably right
        // - the user asked for it - but it is the first place to look if a design
        // is ever overwritten unexpectedly.
        _selectedSceneName = sc.Name;
        OnChanged(nameof(SelectedSceneName));
        LoadDesignIntoEditor(FromScene(sc), fromShow: fromShow);
        return true;
    }



    /// <summary>Populate the scene/sequence lists and auto-run the startup show.
    /// Called once, after the main view model has applied the startup profile.</summary>
    public void InitScenes()
    {
        foreach (var sc in _scenes.Scenes) SceneNames.Add(sc.Name);
        SelectLoadedScene();
    }

    /// <summary>The store shows share with scenes: one file holds both. Handed
    /// to ShowViewModel through a function rather than as a value, because an
    /// import replaces the whole thing.</summary>
    internal SceneStore Scenes => _scenes;

    /// <summary>Replace imported stores without leaving old timers or save handlers alive.</summary>
    public void ReloadScenes(bool currentScreenChanged)
    {
        _lcdSave.Stop();
        SceneNames.Clear();
        _scenes = SceneStore.Load();
        _selectedSceneName = null;
        OnChanged(nameof(SelectedSceneName));
        if (currentScreenChanged && _lcd != null)
            LoadDesignIntoEditor(LcdDesign.Load());
        InitScenes();
        OnChanged(nameof(SceneChoices));
    }

    /// <summary>What the pump is showing right now, detached: the live design
    /// plus whether a show put it there. Automation snapshots carry this next
    /// to the LED frames, so an app/sensor rule that applied a profile with a
    /// different screen can put the ORIGINAL screen back - the snapshot used
    /// to restore only the RGB and leave the panel on the rule's screen.</summary>
    public sealed record LcdSnapshot(LcdDesign Design, bool FromShow, int Edits);

    public LcdSnapshot? SnapshotDesign()
        => _lcd == null ? null : new LcdSnapshot(SceneStore.Clone(_lcd.Design), _liveIsShowScene, _userEdits);

    /// <summary>Put a snapshot back on the panel. A design the user had on the
    /// canvas comes back as the canvas (persisted, undoable); one a show had put
    /// up comes back with the same show-only status so it is never written to
    /// lcd.json as if the user had drawn it. A no-op when the panel is already
    /// showing that exact design, so a round trip does not reset a running clock
    /// element. And a no-op when the user changed the screen since the snapshot
    /// was taken: a rule window can last hours, and restoring over an edit made
    /// during it would revert the edit on the panel and then, through the
    /// debounced save, on disk.</summary>
    public void RestoreDesign(LcdSnapshot snap)
    {
        if (_lcd == null) return;
        if (_userEdits != snap.Edits)
        {
            Log.Info("lcd", "keeping the screen you set during the override");
            return;
        }
        if (Fingerprint(_lcd.Design) == Fingerprint(snap.Design) && _liveIsShowScene == snap.FromShow) return;
        var d = SceneStore.Clone(snap.Design);
        // The Screens dropdown follows the canvas: a saved screen selects
        // itself, an unnamed design selects nothing - leaving the rule's screen
        // selected let an empty-name "Save screen" overwrite it with this canvas.
        _selectedSceneName = d.SceneName != null && SceneNames.Contains(d.SceneName) ? d.SceneName : null;
        OnChanged(nameof(SelectedSceneName));
        LoadDesignIntoEditor(d, fromShow: snap.FromShow);
        if (!snap.FromShow) _liveIsShowScene = false;   // the user's canvas again
    }

    /// <summary>True while the live design was swapped in by a running show
    /// rather than by the user. Rendered, but never persisted as the canvas: a
    /// show used to rewrite lcd.json on every step (a 5 s show = ~17,000 atomic
    /// file replaces a day) and replace the user's own design with whichever
    /// scene played last.</summary>
    bool _liveIsShowScene;

    /// <summary>A scene's design, detached from the store and carrying the name
    /// it came from, ready to become the live one.</summary>
    static LcdDesign FromScene(LcdScene sc)
    {
        var d = SceneStore.Clone(sc.Design);
        d.SceneName = sc.Name;   // scenes saved before SceneName existed have none
        return d;
    }

    /// <summary>Preselect the screen the live design came from, so the Screens
    /// tab opens on it and an edit followed by "Save screen" updates that
    /// screen. Deliberately writes the FIELD: the setter would reload the design
    /// that is already showing. A no-op until both the panel and the scene list
    /// exist, so it is safe to call from either startup order.</summary>
    void SelectLoadedScene()
    {
        if (_lcd == null) return;
        string? name = _lcd.Design.SceneName;

        // A design saved before the name was recorded, which on an existing
        // install is every design there is. If it is identical to a saved
        // screen it IS that screen, so adopt the name once and carry it from
        // here on rather than leaving the dropdown blank forever.
        if (name == null)
        {
            string live = Fingerprint(_lcd.Design);
            name = _scenes.Scenes.FirstOrDefault(x => Fingerprint(x.Design) == live)?.Name;
            if (name != null) _lcd.Design.SceneName = name;
        }

        if (name == null || !SceneNames.Contains(name)) return;
        _selectedSceneName = name;
        OnChanged(nameof(SelectedSceneName));
        // Which screen the pump is showing is state a bundle needs: "the LCD
        // looks wrong" is a different question depending on whether it is a
        // saved screen or a canvas that was never saved.
        Log.Info("scenes", $"canvas is screen '{name}'");
    }

    /// <summary>A design's content, with the screen name left out so it does not
    /// take part in the comparison. Compared as JSON because that is exactly
    /// what gets persisted: two designs that serialize the same are the same
    /// screen. Cloned first, so nothing here touches the live design.</summary>
    static string Fingerprint(LcdDesign d)
    {
        var c = SceneStore.Clone(d);
        c.SceneName = null;
        return JsonSerializer.Serialize(c);
    }

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
        // A show replacing the canvas also invalidates any drag in progress:
        // its remembered snapshot is of a design that is no longer on screen,
        // and recording it later would undo to something the user never saw.
        if (fromShow) { _history.EndGesture(); _undoSettle.Stop(); _baseline = Snapshot(); }
        if (fromShow) { _liveIsShowScene = true; _clockSecond = -1; _lcd.Refresh(); }
        else MarkCanvas();   // not a user edit in itself: the caller decides that
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
        // The canvas IS this screen from here on: the next "Save screen" with
        // an empty name updates it, and a restart comes back selected on it.
        _userEdits++;
        _lcd.Design.SceneName = sc.Name;
        sc.Design = SceneStore.Clone(_lcd.Design);
        _scenes.Save();
        _lcd.Design.Save();
        SceneNameInput = "";
        _selectedSceneName = sc.Name;
        OnChanged(nameof(SelectedSceneName)); OnChanged(nameof(SceneChoices));
    }

    public void DeleteScene()
    {
        if (_selectedSceneName is not string name) return;
        _userEdits++;
        // Own the name locally: removing it from SceneNames makes the bound
        // ComboBox push a null selection back through the setter at once, so
        // anything read from _selectedSceneName after that line is already gone.
        _selectedSceneName = null;
        // The design stays on the pump, it just is not a saved screen anymore.
        if (_lcd != null && _lcd.Design.SceneName == name) _lcd.Design.SceneName = null;
        _scenes.Scenes.RemoveAll(x => x.Name == name);
        SceneNames.Remove(name);
        _selectedSceneName = null;
        OnChanged(nameof(SelectedSceneName)); OnChanged(nameof(SceneChoices));
        _scenes.Save();
    }

    public void Dispose()
    {
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
