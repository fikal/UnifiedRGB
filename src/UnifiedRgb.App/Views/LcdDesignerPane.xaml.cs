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
        var item = ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is LcdElement el)
        {
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
            _drag.X = Clamp(_startX + (p.X - _dragOrigin.X), 0, 312);
            _drag.Y = Clamp(_startY + (p.Y - _dragOrigin.Y), 0, 232);
        }
        else if (_bgDrag)
        {
            var p = e.GetPosition(lb);
            // Generous clamp: allow dragging mostly off-screen for framing.
            VM.LcdBgX = Clamp(_bgStartX + (p.X - _dragOrigin.X), -VM.LcdBgW + 24, 296);
            VM.LcdBgY = Clamp(_bgStartY + (p.Y - _dragOrigin.Y), -VM.LcdBgH + 24, 216);
        }
    }

    void Design_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        if (sender is ListBox lb) lb.ReleaseMouseCapture();
        VM.TouchLcd();
    }

    /// <summary>Alt+Tab / a popup mid-drag: capture leaves without a MouseUp,
    /// and the next hover used to keep dragging with no button down.</summary>
    void Design_LostCapture(object sender, MouseEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        VM.TouchLcd();
    }

    /*--- background resize grip (bottom-right corner) ---*/
    bool _gripDrag;
    Point _gripOrigin;
    double _gripStartW, _gripStartH;

    void BgGrip_Down(object sender, MouseButtonEventArgs e)
    {
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
        if (VM.LcdBgAspectLock)
            VM.LcdBgW = _gripStartW + dx;              // setter derives H
        else
        {
            VM.LcdBgW = _gripStartW + dx;
            VM.LcdBgH = _gripStartH + dy;
        }
        e.Handled = true;
    }

    void BgGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        VM.TouchLcd();
        e.Handled = true;
    }

    void BgGrip_LostCapture(object sender, MouseEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
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
