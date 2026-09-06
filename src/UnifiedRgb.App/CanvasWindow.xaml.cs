using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App;

/// <summary>Arrange the devices the way they sit on the desk, so one effect can
/// run across all of them as a single image.
///
/// Drawn by hand into a Canvas rather than with an ItemsControl: every device is
/// a rectangle plus a scatter of live LED dots, the dots move when the layout
/// does, and rebuilding that from a template on every mouse-move would cost
/// more than redrawing it.</summary>
public partial class CanvasWindow : Window
{
    readonly MainViewModel _vm;
    readonly CanvasLayout _layout;
    readonly UndoStack<string> _history = new(50);

    CanvasItem? _selected;
    CanvasItem? _drag;
    bool _resizing;
    Point _grabOffset;
    bool _suppressShapeEvents;

    /// <summary>The desk is drawn to fit the panel, so everything below works
    /// in canvas units and converts only when drawing or reading the mouse.</summary>
    double _scale = 1;

    public CanvasWindow(MainViewModel vm)
    {
        _vm = vm;
        _layout = vm.Canvas;
        InitializeComponent();
        DataContext = vm;

        // Anything attached since the layout was last saved needs a place, or
        // it would be invisible here and unaffected by a desk effect.
        _layout.AutoArrange(vm.Devices);

        Loaded += (_, _) => { Redraw(); UpdateSelectionUi(); };
        SizeChanged += (_, _) => Redraw();
        PreviewKeyDown += Keys;
    }

    /*--- drawing ---*/

    void Redraw()
    {
        if (Desk.ActualWidth <= 0) return;
        Desk.Children.Clear();

        // Fit the desk into the panel, keeping its proportions: a device's
        // position only means something relative to the whole surface.
        _scale = Math.Min(Desk.ActualWidth / _layout.Width, Desk.ActualHeight / _layout.Height);
        double deskW = _layout.Width * _scale, deskH = _layout.Height * _scale;

        Desk.Children.Add(new Rectangle
        {
            Width = deskW, Height = deskH,
            Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x21)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)),
            StrokeThickness = 1,
        });

        foreach (var item in _layout.Items)
        {
            var device = _vm.Devices.FirstOrDefault(d => d.Name == item.Device);
            // A device in the file that is not attached right now keeps its
            // place but is drawn faintly, so it reads as remembered, not broken.
            DrawItem(item, device);
        }
    }

    void DrawItem(CanvasItem item, IRgbDevice? device)
    {
        bool present = device != null;
        bool selected = ReferenceEquals(item, _selected);
        double x = item.X * _scale, y = item.Y * _scale;
        double w = Math.Max(6, item.W * _scale), h = Math.Max(6, item.H * _scale);

        var box = new Rectangle
        {
            Width = w, Height = h, RadiusX = 4, RadiusY = 4,
            Fill = new SolidColorBrush(Color.FromArgb(present ? (byte)0x26 : (byte)0x12, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(selected ? Color.FromRgb(0x4C, 0x6F, 0xFF)
                                                  : Color.FromRgb(0x3A, 0x3D, 0x48)),
            StrokeThickness = selected ? 2 : 1,
            Tag = item,
        };
        Canvas.SetLeft(box, x); Canvas.SetTop(box, y);
        Desk.Children.Add(box);

        // The LEDs, where they will actually be once an effect runs. This is the
        // whole point of the window: you are arranging light, not boxes.
        if (device != null) DrawLeds(item, device, x, y, w, h);

        var label = new TextBlock
        {
            Text = present ? item.Device : item.Device + " (away)",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(present ? (byte)0xDD : (byte)0x77, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, x + 4); Canvas.SetTop(label, y + 3);
        Desk.Children.Add(label);

        if (!selected) return;

        // Resize grip, bottom-right, matching the LCD designer's.
        var grip = new Rectangle
        {
            Width = 12, Height = 12, RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x6F, 0xFF)),
            Cursor = Cursors.SizeNWSE,
            Tag = "grip",
        };
        Canvas.SetLeft(grip, x + w - 6); Canvas.SetTop(grip, y + h - 6);
        Desk.Children.Add(grip);
    }

    void DrawLeds(CanvasItem item, IRgbDevice device, double x, double y, double w, double h)
    {
        // Where each LED sits inside the device, through the same mapping the
        // engine uses, so the dots are exactly where the light will be.
        var local = EffectEngine.ZonePositions(device, 0, device.LedCount);
        var colors = _vm.ComposedFrameFor(device);

        // A dot per LED gets unreadable past a few dozen on a small rectangle;
        // thin them out rather than drawing a solid blob.
        int step = Math.Max(1, local.Length / 64);
        double dot = Math.Clamp(Math.Min(w, h) / 10, 2, 5);

        for (int i = 0; i < local.Length; i += step)
        {
            var mapped = CanvasMapper.Map(local[i], item, _layout.Width, _layout.Height);
            // Back into panel space. Map gives desk-normalized coordinates, so
            // this is the same transform the effect sees, drawn.
            double px = mapped.X * _layout.Width * _scale;
            double py = mapped.Y * _layout.Height * _scale;

            var c = i < colors.Length ? colors[i] : new Rgb(60, 60, 70);
            var ellipse = new Ellipse
            {
                Width = dot, Height = dot,
                Fill = new SolidColorBrush(Color.FromRgb(
                    Math.Max(c.R, (byte)24), Math.Max(c.G, (byte)24), Math.Max(c.B, (byte)28))),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ellipse, px - dot / 2);
            Canvas.SetTop(ellipse, py - dot / 2);
            Desk.Children.Add(ellipse);
        }
    }

    /*--- dragging ---*/

    void Desk_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Desk);
        var hit = Desk.InputHitTest(p) as FrameworkElement;

        if (hit?.Tag as string == "grip" && _selected != null)
        {
            PushUndo();
            _resizing = true;
            _drag = _selected;
            Desk.CaptureMouse();
            return;
        }

        // Topmost first: later children are drawn over earlier ones.
        CanvasItem? picked = null;
        for (int i = Desk.Children.Count - 1; i >= 0; i--)
            if (Desk.Children[i] is FrameworkElement fe && fe.Tag is CanvasItem ci
                && Inside(ci, p)) { picked = ci; break; }

        _selected = picked;
        UpdateSelectionUi();

        if (picked == null) { Redraw(); return; }

        PushUndo();
        _drag = picked;
        _grabOffset = new Point(p.X - picked.X * _scale, p.Y - picked.Y * _scale);
        Desk.CaptureMouse();
        Redraw();
    }

    bool Inside(CanvasItem item, Point p) =>
        p.X >= item.X * _scale && p.X <= (item.X + item.W) * _scale &&
        p.Y >= item.Y * _scale && p.Y <= (item.Y + item.H) * _scale;

    void Desk_Move(object sender, MouseEventArgs e)
    {
        if (_drag == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Desk);

        if (_resizing)
        {
            _drag.W = Math.Clamp(p.X / _scale - _drag.X, 20, _layout.Width - _drag.X);
            _drag.H = Math.Clamp(p.Y / _scale - _drag.Y, 20, _layout.Height - _drag.Y);
        }
        else
        {
            double nx = (p.X - _grabOffset.X) / _scale;
            double ny = (p.Y - _grabOffset.Y) / _scale;

            // Snap to the desk's edges and centre and to everything else on it,
            // through the helper the LCD designer uses.
            var others = _layout.Items.Where(i => !ReferenceEquals(i, _drag));
            var (sx, _) = SnapGuides.Snap(nx, _drag.W,
                SnapGuides.Lines(_layout.Width, others.Select(i => (i.X, i.W))));
            var (sy, _) = SnapGuides.Snap(ny, _drag.H,
                SnapGuides.Lines(_layout.Height, others.Select(i => (i.Y, i.H))));

            _drag.X = Math.Clamp(sx, 0, Math.Max(0, _layout.Width - _drag.W));
            _drag.Y = Math.Clamp(sy, 0, Math.Max(0, _layout.Height - _drag.H));
        }
        Redraw();
        DrawGuidesFor(_drag);
    }

    /// <summary>Show what the dragged item lined up with. Drawn after the
    /// redraw so the lines sit over everything.</summary>
    void DrawGuidesFor(CanvasItem item)
    {
        var others = _layout.Items.Where(i => !ReferenceEquals(i, item));
        var (_, lineX) = SnapGuides.Snap(item.X, item.W,
            SnapGuides.Lines(_layout.Width, others.Select(i => (i.X, i.W))));
        var (_, lineY) = SnapGuides.Snap(item.Y, item.H,
            SnapGuides.Lines(_layout.Height, others.Select(i => (i.Y, i.H))));

        if (lineX is { } gx) Guide(gx * _scale, 0, gx * _scale, _layout.Height * _scale);
        if (lineY is { } gy) Guide(0, gy * _scale, _layout.Width * _scale, gy * _scale);
    }

    void Guide(double x1, double y1, double x2, double y2) => Desk.Children.Add(new Line
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x6F, 0xFF)),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 3, 3 },
        IsHitTestVisible = false,
    });

    void Desk_Up(object sender, MouseEventArgs e)
    {
        if (_drag == null) return;
        _drag = null;
        _resizing = false;
        Desk.ReleaseMouseCapture();
        Commit();
        Redraw();
    }

    /*--- buttons ---*/

    void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        // Clear first: this is "put it back", not "fill the gaps".
        _layout.Items.RemoveAll(i => _vm.Devices.Any(d => d.Name == i.Device));
        _layout.AutoArrange(_vm.Devices);
        _selected = null;
        Commit();
        Redraw();
        UpdateSelectionUi();
    }

    void Rotate_Click(object sender, RoutedEventArgs e) => Edit(i => i.Rotation = (i.Rotation + 90) % 360);
    void FlipX_Click(object sender, RoutedEventArgs e) => Edit(i => i.FlipX = !i.FlipX);
    void FlipY_Click(object sender, RoutedEventArgs e) => Edit(i => i.FlipY = !i.FlipY);

    void Edit(Action<CanvasItem> change)
    {
        if (_selected == null) return;
        PushUndo();
        change(_selected);
        Commit();
        Redraw();
        UpdateSelectionUi();
    }

    void Shape_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressShapeEvents || _selected == null || !IsLoaded) return;
        PushUndo();

        int pick = ShapePick.SelectedIndex;
        if (pick <= 0) _selected.LedLayout = null;
        else
        {
            int.TryParse(ColsBox.Text, out int cols);
            int.TryParse(RowsBox.Text, out int rows);
            _selected.LedLayout = new LedLayoutOverride
            {
                Shape = pick == 1 ? "strip" : pick == 2 ? "ring" : "grid",
                Cols = Math.Max(1, cols),
                Rows = Math.Max(1, rows),
                Serpentine = SerpentineBox.IsChecked == true,
            };
        }
        Commit();
        Redraw();
        UpdateSelectionUi();
    }

    void UpdateSelectionUi()
    {
        _suppressShapeEvents = true;
        try
        {
            if (_selected == null)
            {
                SelectionText.Text = "Pick a device to move, rotate or reshape it.";
                ShapePick.SelectedIndex = 0;
                ColsBox.Text = ""; RowsBox.Text = ""; SerpentineBox.IsChecked = false;
                return;
            }

            var device = _vm.Devices.FirstOrDefault(d => d.Name == _selected.Device);
            string leds = device == null ? "not attached" : $"{device.LedCount} LEDs";
            string turned = _selected.Rotation == 0 ? "" : $", turned {_selected.Rotation}";
            string flipped = (_selected.FlipX ? ", flipped across" : "")
                           + (_selected.FlipY ? ", flipped down" : "");
            SelectionText.Text = $"{_selected.Device} ({leds}){turned}{flipped}.";

            var layout = _selected.LedLayout;
            ShapePick.SelectedIndex = layout?.Shape switch
            {
                "strip" => 1, "ring" => 2, "grid" => 3, _ => 0,
            };
            ColsBox.Text = layout?.Cols.ToString() ?? "";
            RowsBox.Text = layout?.Rows.ToString() ?? "";
            SerpentineBox.IsChecked = layout?.Serpentine == true;
        }
        finally { _suppressShapeEvents = false; }
    }

    /*--- undo ---*/

    string Snapshot() => JsonSerializer.Serialize(_layout);

    void PushUndo() => _history.Push(Snapshot());

    /// <summary>Save and, if the desk is live, restart the running effects so
    /// the change is visible immediately rather than at the next apply.</summary>
    void Commit()
    {
        _layout.Save();
        if (_layout.Enabled) _vm.ReapplyEffects();
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Step(undo: true);
    void Redo_Click(object sender, RoutedEventArgs e) => Step(undo: false);

    void Step(bool undo)
    {
        string current = Snapshot();
        string? next = undo ? _history.Undo(current) : _history.Redo(current);
        if (next == null) return;

        CanvasLayout? restored;
        try { restored = JsonSerializer.Deserialize<CanvasLayout>(next); }
        catch (Exception ex) { Log.Warn("canvas", $"undo snapshot unreadable: {ex.Message}"); return; }
        if (restored == null) return;

        // Mutate in place: the engine and the view model hold this instance.
        _layout.Enabled = restored.Enabled;
        _layout.Width = restored.Width;
        _layout.Height = restored.Height;
        _layout.Items = restored.Items;
        _selected = null;

        Commit();
        Redraw();
        UpdateSelectionUi();
    }

    void Keys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Step(true); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Step(false); e.Handled = true; }
        else if (e.Key == Key.Escape) Close();
    }

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
