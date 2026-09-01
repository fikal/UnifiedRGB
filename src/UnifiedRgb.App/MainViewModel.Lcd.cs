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

// Pump LCD designer — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*\
    | Pump LCD designer                                     |
    \*-----------------------------------------------------*/
    LcdController? _lcd;
    PawnIoCpuTempProvider? _cpuTemp;
    public bool LcdAvailable => _lcd != null;

    /// <summary>True when CPU temp is live (PawnIO reachable — needs elevation).</summary>
    public bool CpuTempAvailable => _cpuTemp?.Available == true;
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

    bool _isLcdSelected;
    public bool IsLcdSelected
    {
        get => _isLcdSelected;
        set { _isLcdSelected = value; OnChanged(); OnChanged(nameof(ShowLighting)); OnChanged(nameof(ShowPreview)); OnChanged(nameof(ShowGenericPreview)); OnChanged(nameof(ShowLianEditor)); }
    }
    public bool ShowLighting => !_isLcdSelected && !_isSettingsOpen && !IsDisabledSelected && !IsCoolingSelected;

    readonly DispatcherTimer _lcdSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    void StartLcd()
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
        OnChanged(nameof(LcdAvailable));
        OnChanged(nameof(CpuTempAvailable)); OnChanged(nameof(CpuTempUnavailable));
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

    void LcdElementChanged(object? s, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // Live editor pushes are NOT user edits. ClockImage especially: it is
        // set from RefreshDisplays on every render tick, and reacting with
        // TouchLcd() -> Refresh() -> Ticked -> RefreshDisplays -> ClockImage
        // would recurse to a stack overflow the moment an analog clock was on
        // screen with the designer open.
        if (args.PropertyName is nameof(LcdElement.Display) or nameof(LcdElement.ClockImage)) return;
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
        if (!IsLcdSelected) return;
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

    /*-----------------------------------------------------*    | Background placement: one rect, edited here, rendered |
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
        var src = LcdBackground as System.Windows.Media.Imaging.BitmapSource;
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

    /// <summary>Cover the whole panel (crops the overflow), centered.</summary>
    public void BgFill()
    {
        if (_lcd == null) return;
        var d = _lcd.Design;
        double scale = Math.Max(320.0 / _bgNatW, 240.0 / _bgNatH);
        d.BgW = Math.Round(_bgNatW * scale); d.BgH = Math.Round(_bgNatH * scale);
        d.BgX = Math.Round((320 - d.BgW) / 2); d.BgY = Math.Round((240 - d.BgH) / 2);
        NotifyBgRect(); TouchLcd();
    }

    /// <summary>Fit the whole image on the panel (letterboxed), centered.</summary>
    public void BgFit()
    {
        if (_lcd == null) return;
        var d = _lcd.Design;
        double scale = Math.Min(320.0 / _bgNatW, 240.0 / _bgNatH);
        d.BgW = Math.Round(_bgNatW * scale); d.BgH = Math.Round(_bgNatH * scale);
        d.BgX = Math.Round((320 - d.BgW) / 2); d.BgY = Math.Round((240 - d.BgH) / 2);
        NotifyBgRect(); TouchLcd();
    }

    /// <summary>Center the image at its current size.</summary>
    public void BgCenter()
    {
        if (_lcd == null) return;
        var d = _lcd.Design;
        d.BgX = Math.Round((320 - d.BgW) / 2); d.BgY = Math.Round((240 - d.BgH) / 2);
        NotifyBgRect(); TouchLcd();
    }
}
