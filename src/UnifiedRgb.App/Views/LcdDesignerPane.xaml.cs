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

    void Design_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        if (sender is ListBox lb) lb.ReleaseMouseCapture();
        GuideLayer.Children.Clear();
        VM.EndGesture();
        VM.TouchLcd();
    }

    /*-----------------------------------------------------*\
    | Alignment guides, the way a forms designer does it:   |
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

    void DrawGuide(double x1, double y1, double x2, double y2) =>
        GuideLayer.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = GuideBrush, StrokeThickness = 1, SnapsToDevicePixels = true,
        });

    // Magenta, the color every forms designer uses for this, and nothing else
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
        if (sender is not FrameworkElement fe) return;
        // After the guard: an early return would leave a gesture open forever,
        // and every later edit would fold into a step nobody can reach.
        VM.BeginGesture();
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

    /*--- scenes & sequences ---*/
    // The show handlers left with the show: see ShowPane.

    /*--- the Screens tab (see NamedPicker) ---*/

    Window? Owner => Window.GetWindow(this);
    /// <summary>The profiles live on the main view model, which is the
    /// window's; this pane's own DataContext is the designer.</summary>
    MainViewModel? Main => Owner?.DataContext as MainViewModel;

    /// <summary>Picking a screen puts it on the canvas, replacing what is
    /// there, so edits to the screen being left are asked about first.</summary>
    void Screen_Picked(object? sender, Controls.NamedPickEventArgs e)
    {
        if (e.Item is not string next) return;
        if (VM.ScreenHasChanges && VM.SelectedSceneName is string current && Owner is Window owner)
        {
            var answer = Dialogs.AskSaveChanges(owner, current, "Screen");
            if (answer == MessageBoxResult.Cancel) return;
            // Not kept (the file could not be written): stay on this screen
            // rather than replace the canvas holding the only copy.
            if (answer == MessageBoxResult.Yes && !VM.SaveScreen()) return;
        }
        VM.SelectedSceneName = next;
    }

    void ScreenSave_Click(object? sender, EventArgs e)
    {
        if (VM.SelectedSceneName == null) { ScreenNew_Click(sender, e); return; }
        VM.SaveScreen();
    }

    /// <summary>Back to the picked screen as saved. Undo still has the edits.</summary>
    void ScreenRevert_Click(object? sender, EventArgs e) => VM.RevertScreen();

    void ScreenNew_Click(object? sender, EventArgs e)
    {
        if (Owner is not Window owner) return;
        // A screen is made from the canvas the panel is showing; with no panel
        // there is nothing to take, and accepting a name to then do nothing
        // was the silent failure.
        if (!VM.PanelConnected)
        {
            Dialogs.Info(owner, "New screen", "The pump panel is not connected, so there is no canvas to save as a screen. Plug it in and try again.");
            return;
        }
        string? name = Dialogs.AskName(owner, "New screen",
            "Saves what is on the canvas now as a screen of its own.",
            VM.SuggestScreenName(), "Create", n => VM.ScreenNameProblem(n));
        if (name != null) VM.CreateScreen(name);
    }

    void ScreenRename_Click(object? sender, EventArgs e)
    {
        if (VM.SelectedSceneName is not string current || Owner is not Window owner) return;
        string? name = Dialogs.AskName(owner, "Rename screen",
            "Profiles that show it follow the new name.",
            current, "Rename", n => VM.ScreenNameProblem(n, current));
        if (name != null) VM.RenameScreen(current, name);
    }

    /// <summary>Delete, asking first only when profiles still show it: they
    /// would put nothing on the panel from then on, and say so only in the log.</summary>
    void ScreenDelete_Click(object? sender, EventArgs e)
    {
        if (VM.SelectedSceneName is not string current) return;
        var uses = Main?.WhatUsesScreen(current) ?? Array.Empty<string>();
        if (uses.Count > 0 && Owner is Window owner)
        {
            string list = string.Join("\n", uses.Select(u => "   • " + u));
            if (!Dialogs.Confirm(owner, $"Delete “{current}”?",
                    $"{uses.Count} profile{(uses.Count == 1 ? "" : "s")} still show it:\n\n{list}\n\n"
                    + "They keep the name and leave the pump panel alone until you pick another screen for them.",
                    "Delete Anyway"))
                return;
        }
        VM.DeleteScene();
    }

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
