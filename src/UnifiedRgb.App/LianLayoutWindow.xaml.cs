using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.App;

/// <summary>Drag the fans into physical top-to-bottom order (the chain order
/// rarely matches the case), optionally splitting the stack into groups that
/// each get their own effect canvas. Saves lianli-layout.json and rescans.</summary>
public partial class LianLayoutWindow : Window
{
    public sealed class FanSlot : INotifyPropertyChanged
    {
        public int Chain { get; init; }
        public string Label => $"Fan {Chain + 1}";
        bool _newGroup;
        public bool NewGroup { get => _newGroup; set { _newGroup = value; Notify(nameof(NewGroup)); } }
        bool _canBreak = true;
        public bool CanBreak { get => _canBreak; set { _canBreak = value; Notify(nameof(CanBreak)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
    }

    readonly System.Collections.ObjectModel.ObservableCollection<FanSlot> _slots = new();
    readonly Action _onSaved;

    public LianLayoutWindow(int fanNum, Action onSaved)
    {
        _onSaved = onSaved;
        InitializeComponent();
        var (order, breaks) = LianLiWireless.LoadLayout(fanNum);
        for (int s = 0; s < order.Length; s++)
            _slots.Add(new FanSlot { Chain = order[s], NewGroup = breaks.Contains(s) });
        RefreshBreakables();
        RowsList.ItemsSource = _slots;
        // A click without a drag left _pressSlot set: the title bar stopped
        // moving the window and the next mouse-down anywhere could start
        // dragging the stale row. (AppRulesWindow already had this reset.)
        PreviewMouseLeftButtonUp += (_, _) => { _pressSlot = null; _pressRow = null; };
    }

    void RefreshBreakables()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].CanBreak = i > 0;      // a group can't start above the first fan
            if (i == 0) _slots[i].NewGroup = false;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var order = _slots.Select(s => s.Chain).ToArray();
        var breaks = _slots.Select((s, i) => (s, i)).Where(x => x.i > 0 && x.s.NewGroup).Select(x => x.i).ToArray();
        var json = JsonSerializer.Serialize(new { order, breaks });
        try
        {
            // Atomic like every other store: a crash mid-write used to leave a
            // truncated file, and LoadLayout then silently fell back to chain order.
            SafeFile.WriteAllText(AppPaths.Config("lianli-layout.json"), json);
        }
        catch (Exception ex)
        {
            Log.Warn("LianLi", $"layout save failed: {ex.Message}");
        }
        Close();
        _onSaved();
    }

    /*--- drag-to-reorder (same feel as the app-rules dialog) ---*/
    System.Windows.Point _pressPos;
    FanSlot? _pressSlot;
    Border? _pressRow;
    Border? _indicated;

    void Row_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
        _pressRow = sender as Border;
        _pressSlot = _pressRow?.DataContext as FanSlot;
        _pressPos = e.GetPosition(this);
    }

    static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is CheckBox or Button) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void Row_Move(object sender, MouseEventArgs e)
    {
        if (_pressSlot == null || _pressRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPos.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var slot = _pressSlot;
        var row = _pressRow;
        _pressSlot = null; _pressRow = null;
        row.Opacity = 0.5;
        try { DragDrop.DoDragDrop(row, new DataObject(typeof(FanSlot), slot), DragDropEffects.Move); }
        finally { row.Opacity = 1; ClearIndicator(); }
    }

    void SetIndicator(Border row, bool below)
    {
        if (!ReferenceEquals(_indicated, row)) ClearIndicator();
        _indicated = row;
        row.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0x6F, 0xFF));
        row.BorderThickness = below ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    void ClearIndicator()
    {
        if (_indicated == null) return;
        _indicated.BorderBrush = System.Windows.Media.Brushes.Transparent;
        _indicated.BorderThickness = new Thickness(0, 2, 0, 2);
        _indicated = null;
    }

    void Row_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(FanSlot));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Border row)
            SetIndicator(row, e.GetPosition(row).Y > row.ActualHeight / 2);
        e.Handled = true;
    }

    void Row_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(_indicated, sender)) ClearIndicator();
    }

    void Row_Drop(object sender, DragEventArgs e)
    {
        ClearIndicator();
        if (e.Data.GetData(typeof(FanSlot)) is not FanSlot dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not FanSlot target
            || ReferenceEquals(dragged, target)) return;
        int idx = _slots.IndexOf(target);
        if (idx < 0) return;
        if (sender is FrameworkElement fe && e.GetPosition(fe).Y > fe.ActualHeight / 2) idx++;
        if (_slots.IndexOf(dragged) < idx) idx--;
        _slots.Remove(dragged);
        _slots.Insert(Math.Clamp(idx, 0, _slots.Count), dragged);
        RefreshBreakables();
        e.Handled = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (_pressSlot != null) return;
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
    }
}
