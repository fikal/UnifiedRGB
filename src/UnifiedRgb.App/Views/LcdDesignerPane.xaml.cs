using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>The pump LCD designer: WYSIWYG canvas (drag to place, background
/// move/resize) plus the Design / Background / Screens / Show tabs.</summary>
public partial class LcdDesignerPane : UserControl
{
    public LcdDesignerPane() => InitializeComponent();

    LcdDesignerViewModel VM => (LcdDesignerViewModel)DataContext;

    /*-----------------------------------------------------*\
    | Drag elements to place them.                          |
    \*-----------------------------------------------------*/
    LcdElement? _drag;
    Point _dragOrigin;
    double _startX, _startY;

    bool _bgDrag;                 // empty-canvas drag moves the background
    double _bgStartX, _bgStartY;

    void Design_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        // Take focus for the canvas. Capturing the mouse from a Preview handler
        // stops the ListBox focusing itself the way a click normally would, so
        // focus stayed in whichever box was last used: selecting a Text element
        // opens one for its content, and Ctrl+Z then went to that box's own text
        // undo instead of the design. Clicking the canvas means working on the
        // canvas.
        lb.Focus();
        var item = ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is LcdElement el)
        {
            // Before the first pixel of movement: undo returns to where the
            // element was when the drag began, not part way through it.
            VM.BeginGesture();
            VM.SelectedElement = el;
            _drag = el;
            _dragOrigin = e.GetPosition(lb);
            _startX = el.X; _startY = el.Y;
            lb.CaptureMouse();
            return;
        }
        // Pressed empty canvas: drag the background itself.
        if (VM.LcdHasBackground)
        {
            VM.BeginGesture();
            _bgDrag = true;
            _dragOrigin = e.GetPosition(lb);
            _bgStartX = VM.LcdBgX; _bgStartY = VM.LcdBgY;
            lb.CaptureMouse();
        }
    }

    void Design_Move(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (_drag != null)
        {
            var p = e.GetPosition(lb);
            double x = Clamp(_startX + (p.X - _dragOrigin.X), 0, 312);
            double y = Clamp(_startY + (p.Y - _dragOrigin.Y), 0, 232);
            (x, y) = SnapToGuides(lb, _drag, x, y);
            // One notification (= one panel render) per move, not X then Y.
            _drag.MoveTo(x, y);
        }
        else if (_bgDrag)
        {
            var p = e.GetPosition(lb);
            // Generous clamp: allow dragging mostly off-screen for framing.
            VM.MoveBg(Clamp(_bgStartX + (p.X - _dragOrigin.X), -VM.LcdBgW + 24, 296),
                      Clamp(_bgStartY + (p.Y - _dragOrigin.Y), -VM.LcdBgH + 24, 216));
        }
    }

    void Undo_Click(object sender, RoutedEventArgs e) => VM.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => VM.Redo();

    void Design_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        if (sender is ListBox lb) lb.ReleaseMouseCapture();
        GuideLayer.Children.Clear();
        VM.EndGesture();
        VM.TouchLcd();
    }

    /*-----------------------------------------------------*    | Alignment guides, the way a forms designer does it:   |
    | while an item is dragged, its edges and centre are    |
    | compared against every other item and against the     |
    | screen itself. Anything within a few pixels snaps,    |
    | and the line it snapped to is drawn.                  |
    \*-----------------------------------------------------*/

    const double SnapDistance = 4;      // how close before it grabs
    const double ScreenW = 320, ScreenH = 240;

    /// <summary>Rendered bounds of an element on the canvas. Size comes from the
    /// container because it depends on the text and font, which the element
    /// itself does not know.</summary>
    static Rect? BoundsOf(ListBox lb, LcdElement el)
    {
        if (lb.ItemContainerGenerator.ContainerFromItem(el) is not FrameworkElement fe) return null;
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return null;
        return new Rect(el.X, el.Y, fe.ActualWidth, fe.ActualHeight);
    }

    /// <summary>Snap a proposed position to nearby edges/centres and draw the
    /// guides for whatever it lined up with. Returns the adjusted position.</summary>
    (double X, double Y) SnapToGuides(ListBox lb, LcdElement drag, double x, double y)
    {
        GuideLayer.Children.Clear();
        if (BoundsOf(lb, drag) is not { } self) return (x, y);
        double w = self.Width, h = self.Height;

        // Everything else on the screen, as (start, size) on each axis. The
        // snapping itself is shared with the desk canvas: see Core/SnapGuides.
        var acrossX = new List<(double, double)>();
        var acrossY = new List<(double, double)>();
        foreach (var other in VM.LcdElements)
        {
            if (ReferenceEquals(other, drag)) continue;
            if (BoundsOf(lb, other) is not { } r) continue;
            acrossX.Add((r.Left, r.Width));
            acrossY.Add((r.Top, r.Height));
        }

        var (sx, lineX) = UnifiedRgb.Core.SnapGuides.Snap(
            x, w, UnifiedRgb.Core.SnapGuides.Lines(ScreenW, acrossX), SnapDistance);
        var (sy, lineY) = UnifiedRgb.Core.SnapGuides.Snap(
            y, h, UnifiedRgb.Core.SnapGuides.Lines(ScreenH, acrossY), SnapDistance);

        if (lineX is { } vx) DrawGuide(vx, 0, vx, ScreenH);
        if (lineY is { } vy) DrawGuide(0, vy, ScreenW, vy);
        return (sx, sy);
    }

    /// <summary>Closest line to any of the item's three anchors, if one is within
    /// SnapDistance. `delta` is how far the item must move to sit on it.</summary>

    void DrawGuide(double x1, double y1, double x2, double y2) =>
        GuideLayer.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = GuideBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
        });

    // Magenta, the colour every forms designer uses for this, and nothing else
    // on this dark canvas looks like it.
    static readonly System.Windows.Media.Brush GuideBrush = CreateGuideBrush();

    static System.Windows.Media.Brush CreateGuideBrush()
    {
        var b = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x3C, 0xE0));
        b.Freeze();
        return b;
    }

    /// <summary>Alt+Tab / a popup mid-drag: capture leaves without a MouseUp,
    /// and the next hover used to keep dragging with no button down.</summary>
    void Design_LostCapture(object sender, MouseEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        GuideLayer.Children.Clear();
        VM.EndGesture();
        VM.TouchLcd();
    }

    /*--- background resize grip (bottom-right corner) ---*/
    bool _gripDrag;
    Point _gripOrigin;
    double _gripStartW, _gripStartH;

    void BgGrip_Down(object sender, MouseButtonEventArgs e)
    {
        VM.BeginGesture();
        if (sender is not FrameworkElement fe) return;
        _gripDrag = true;
        _gripOrigin = e.GetPosition(this);
        _gripStartW = VM.LcdBgW; _gripStartH = VM.LcdBgH;
        fe.CaptureMouse();
        e.Handled = true;
    }

    void BgGrip_Move(object sender, MouseEventArgs e)
    {
        if (!_gripDrag) return;
        var p = e.GetPosition(this);
        double dx = p.X - _gripOrigin.X, dy = p.Y - _gripOrigin.Y;
        VM.SetBgSize(_gripStartW + dx, _gripStartH + dy);   // honors the aspect lock; one render per move
        e.Handled = true;
    }

    void BgGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        VM.EndGesture();
        VM.TouchLcd();
        e.Handled = true;
    }

    void BgGrip_LostCapture(object sender, MouseEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
        VM.EndGesture();
        VM.TouchLcd();
    }

    void BgFill_Click(object sender, RoutedEventArgs e) => VM.BgFill();
    void BgFit_Click(object sender, RoutedEventArgs e) => VM.BgFit();
    void BgCenter_Click(object sender, RoutedEventArgs e) => VM.BgCenter();

    /*--- scenes & sequences ---*/
    void SaveScene_Click(object sender, RoutedEventArgs e) => VM.SaveScene();
    void DeleteScene_Click(object sender, RoutedEventArgs e) => VM.DeleteScene();
    void NewSequence_Click(object sender, RoutedEventArgs e) => VM.NewSequence();
    void DeleteSequence_Click(object sender, RoutedEventArgs e) => VM.DeleteSequence();
    void AddAction_Click(object sender, RoutedEventArgs e) => VM.AddSequenceAction();
    void RunSequence_Click(object sender, RoutedEventArgs e) => VM.ToggleSequence();

    void ActionRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) VM.RemoveSequenceAction(a);
    }

    void ActionUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) VM.MoveSequenceAction(a, -1);
    }

    void ActionDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) VM.MoveSequenceAction(a, +1);
    }

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
