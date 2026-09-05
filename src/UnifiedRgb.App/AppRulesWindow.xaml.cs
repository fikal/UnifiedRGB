using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.App;

/// <summary>Dedicated editor for foreground-app profile rules: pick a
/// RUNNING program (or browse to an .exe), pick a profile, add. No
/// half-filled rules can ever be created here.</summary>
public partial class AppRulesWindow : Window
{
    readonly MainViewModel _vm;

    public sealed record RunningApp(string Name, string Display);

    public AppRulesWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        RefreshApps();
        UpdateEmptyText();
        // Named handler + unhook on close: AutoRules lives on the long-lived
        // VM, so an anonymous subscription rooted this whole window (and its
        // visual tree) once per open — a real, unbounded leak.
        vm.AutoRules.CollectionChanged += RulesChanged;
        Closed += (_, _) => vm.AutoRules.CollectionChanged -= RulesChanged;
        // A row press that never became a drag must not block window-move forever.
        PreviewMouseLeftButtonUp += (_, _) => { _pressRule = null; _pressRow = null; };
    }

    void RulesChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateEmptyText();

    void UpdateEmptyText() =>
        NoRulesText.Visibility = _vm.AutoRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void RefreshApps()
    {
        var self = Environment.ProcessId;
        var apps = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                string title = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title) || !seen.Add(p.ProcessName)) continue;
                if (title.Length > 42) title = title[..42] + "…";
                apps.Add(new RunningApp(p.ProcessName, $"{p.ProcessName} - {title}"));
            }
            catch { }
            finally { p.Dispose(); }
        }
        RunningApps.ItemsSource = apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshApps();

    void RunningApp_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RunningApps.SelectedItem is RunningApp a) ProcName.Text = a.Name;
    }

    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick the program",
            Filter = "Programs|*.exe|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
            ProcName.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        string proc = ProcName.Text.Trim();
        string? profile = ProfilePick.SelectedItem as string;
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc[..^4];
        if (proc.Length == 0) { AddHint.Text = "Pick or type a program name first."; return; }
        if (string.IsNullOrEmpty(profile)) { AddHint.Text = "Choose which profile to apply."; return; }
        _vm.AddAutoRuleExplicit(proc, profile);
        ProcName.Text = "";
        RunningApps.SelectedItem = null;
        AddHint.Text = $"Added: {proc} → {profile}. Switch to that program to try it.";
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AutomationRule r) _vm.RemoveAutoRule(r);
    }

    // Selector raises SelectionChanged when each row's SelectedItem binding
    // first applies (container generation, before Loaded): that was one full
    // settings.json write per rule on every open with nothing changed. Only a
    // change made in the live window persists.
    void Rule_Persist(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _vm.PersistAutomation();
    }

    /*-----------------------------------------------------*\
    | Drag-to-reorder with real feedback: grab anywhere on   |
    | a row (interactive controls excluded), a translucent   |
    | ghost of the row follows the cursor, a blue line marks |
    | the insertion point, and the source row dims while in  |
    | flight. The top matching rule wins, so order is the    |
    | priority the user is editing here.                     |
    \*-----------------------------------------------------*/
    System.Windows.Point _pressPos;
    AutomationRule? _pressRule;
    Border? _pressRow;
    Border? _indicated;
    DragGhostAdorner? _ghost;

    static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ComboBox or Button or TextBox) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void Row_Down(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        _pressRow = sender as Border;
        _pressRule = _pressRow?.DataContext as AutomationRule;
        _pressPos = e.GetPosition(this);
    }

    void Row_Move(object sender, MouseEventArgs e)
    {
        if (_pressRule == null || _pressRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPos.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var rule = _pressRule;
        var row = _pressRow;
        _pressRule = null; _pressRow = null;

        // Snapshot the row at full brightness BEFORE recoloring it — a live
        // VisualBrush would inherit the dimmed "in flight" look and the ghost
        // becomes nearly invisible.
        var snapshot = SnapshotOf(row);
        var prevBg = row.Background;
        row.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2C, 0x35, 0x52));
        row.Opacity = 0.55;
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(RootPanel);
        if (layer != null)
        {
            _ghost = new DragGhostAdorner(RootPanel, snapshot,
                new Size(row.ActualWidth, row.ActualHeight), e.GetPosition(RootPanel));
            layer.Add(_ghost);
        }
        try
        {
            DragDrop.DoDragDrop(row, new DataObject(typeof(AutomationRule), rule), DragDropEffects.Move);
        }
        finally
        {
            row.Opacity = 1;
            row.Background = prevBg;
            ClearIndicator();
            if (_ghost != null && layer != null) { layer.Remove(_ghost); _ghost = null; }
        }
    }

    static System.Windows.Media.ImageSource SnapshotOf(FrameworkElement el)
    {
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(el), null,
                new Rect(new Size(el.ActualWidth, el.ActualHeight)));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    void Root_PreviewDragOver(object sender, DragEventArgs e)
        => _ghost?.SetPosition(e.GetPosition(RootPanel));

    void SetIndicator(Border row, bool below)
    {
        if (!ReferenceEquals(_indicated, row)) ClearIndicator();
        _indicated = row;
        var accent = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0x6F, 0xFF));
        row.BorderBrush = accent;
        row.BorderThickness = below ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    void ClearIndicator()
    {
        if (_indicated == null) return;
        _indicated.BorderBrush = System.Windows.Media.Brushes.Transparent;
        _indicated.BorderThickness = new Thickness(0, 2, 0, 2);
        _indicated = null;
    }

    void Rule_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(AutomationRule));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Border row)
            SetIndicator(row, e.GetPosition(row).Y > row.ActualHeight / 2);
        e.Handled = true;
    }

    void Rule_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(_indicated, sender)) ClearIndicator();
    }

    void Rule_Drop(object sender, DragEventArgs e)
    {
        ClearIndicator();
        if (e.Data.GetData(typeof(AutomationRule)) is not AutomationRule dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not AutomationRule target
            || ReferenceEquals(dragged, target)) return;
        int idx = _vm.AutoRules.IndexOf(target);
        if (idx < 0) return;
        // Lower half of the row = insert after it.
        if (sender is FrameworkElement fe && e.GetPosition(fe).Y > fe.ActualHeight / 2) idx++;
        if (_vm.AutoRules.IndexOf(dragged) < idx) idx--;   // removal shifts targets up
        _vm.MoveAutoRule(dragged, idx);
        e.Handled = true;
    }

    /// <summary>Bright snapshot card of the dragged row — drop shadow + accent
    /// outline — following the cursor.</summary>
    sealed class DragGhostAdorner : System.Windows.Documents.Adorner
    {
        static readonly System.Windows.Media.Pen Outline = new(
            new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4C, 0x6F, 0xFF)), 2);
        static readonly System.Windows.Media.Brush Shadow =
            new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x66, 0, 0, 0));

        static DragGhostAdorner()
        {
            Outline.Freeze();
            Shadow.Freeze();
        }

        readonly System.Windows.Media.ImageSource _snapshot;
        readonly Size _size;
        System.Windows.Point _pos;

        public DragGhostAdorner(UIElement adorned, System.Windows.Media.ImageSource snapshot,
            Size size, System.Windows.Point start) : base(adorned)
        {
            _snapshot = snapshot;
            _size = size;
            _pos = start;
            IsHitTestVisible = false;
        }

        public void SetPosition(System.Windows.Point p)
        {
            _pos = p;
            InvalidateVisual();
        }

        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            var r = new Rect(_pos.X - 14, _pos.Y - _size.Height / 2, _size.Width, _size.Height);
            var shadow = r; shadow.Offset(3, 4);
            dc.DrawRoundedRectangle(Shadow, null, shadow, 8, 8);
            dc.DrawImage(_snapshot, r);
            dc.DrawRoundedRectangle(null, Outline, r, 8, 8);
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        // A press that began on a rule row belongs to row-reorder, not window-move.
        if (_pressRule != null) return;
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}
