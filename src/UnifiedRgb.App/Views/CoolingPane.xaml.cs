using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>Cooling: temperature gauges, the fan list and the curve editor.</summary>
public partial class CoolingPane : UserControl
{
    CoolingViewModel? _vm;
    CoolingViewModel VM => _vm ??= (CoolingViewModel)DataContext;

    public CoolingPane()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => { if (e.NewValue is CoolingViewModel vm && _vm == null) Attach(vm); };
    }

    void Attach(CoolingViewModel vm)
    {
        _vm = vm;
        WireFanCurveEditor();
    }

    void IdentifyFan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FanRowModel fan) fan.Identify();
    }

    void ApplyToAllFans_Click(object sender, RoutedEventArgs e) => VM.ApplyToAllFans();

    void Pills_PreviewKeyDown(object sender, KeyEventArgs e) => KeyPolicy.MouseFirst(e);

    /*-----------------------------------------------------*\
    | Fan curve editor: keep the graph in sync with the     |
    | edited fan, push edits back, and animate the live     |
    | marker each cooling refresh.                          |
    \*-----------------------------------------------------*/
    FanRowModel? _hookedFan;

    void WireFanCurveEditor()
    {
        FanCurveGraph.CurveChanged += _ =>
        {
            VM.EditingFan?.OnCurveEdited();
        };
        VM.EditingFanChanged += f => Dispatcher.Invoke(() => HookEditingFan(f));
        VM.CoolingTick += () => Dispatcher.Invoke(UpdateCurveMarker);
        HookEditingFan(VM.EditingFan);
    }

    void HookEditingFan(FanRowModel? f)
    {
        if (_hookedFan != null) _hookedFan.PropertyChanged -= EditFan_PropertyChanged;
        _hookedFan = f;
        if (f != null)
        {
            f.PropertyChanged += EditFan_PropertyChanged;
            FanCurveGraph.SetFloor(f.CurveFloor);
            FanCurveGraph.SetCurve(f.Curve);
            UpdateCurveMarker();
        }
    }

    void EditFan_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanRowModel.Curve) && VM.EditingFan is { } f)
            FanCurveGraph.SetCurve(f.Curve);
    }

    void UpdateCurveMarker()
    {
        var f = VM.EditingFan;
        if (f != null && f.IsCurve)
            FanCurveGraph.SetLive(UnifiedRgb.Core.Sensors.SensorHub.CurrentTemp(f.Curve.Source), null);
        else
            FanCurveGraph.SetLive(null, null);
    }

    /// <summary>The name box handles its own mouse-down (the TextEditor marks it
    /// handled), so the ListBoxItem never saw the click and the row was not
    /// selected: the editor on the right kept editing the PREVIOUS fan while the
    /// caret sat in this one. Focus in the box = this fan is the edited fan.</summary>
    void CoolingLabel_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FanRowModel f }) VM.EditingFan = f;
    }

    /// <summary>Cooling fan-label editor: Enter commits, Escape reverts —
    /// both hand focus away so the LostFocus binding settles the text.</summary>
    void CoolingLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }
}
