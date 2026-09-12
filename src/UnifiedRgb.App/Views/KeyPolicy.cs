using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>Mouse-first policy shared by the panes: typing drives the reactive
/// effects, so focused list controls must never react to keys. Tab still moves
/// focus; TextBoxes nested in a list item still type (PreviewKeyDown tunnels
/// ahead of them - marking their keys handled blocked TextInput entirely).</summary>
/// <summary>A ListBox's template ScrollViewer handles the mouse wheel and marks
/// it handled even when it has nothing to scroll, so a page ScrollViewer behind
/// a list never moved while the pointer sat over the list (most of the Cooling
/// page's left column). Wired as PreviewMouseWheel on such lists: the tunnelling
/// event is claimed, and a fresh bubbling one starts from the list itself, which
/// its own viewer (a child) never sees and the page's viewer (an ancestor) does.</summary>
static class WheelPolicy
{
    public static void Bubble(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el) return;
        e.Handled = true;
        el.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }
}

static class KeyPolicy
{
    public static void MouseFirst(KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        // Key.System = Alt chord (Alt+F4); must reach DefWindowProc - see
        // Styles.MouseOnly_PreviewKeyDown.
        if (e.Key is Key.Tab or Key.System) return;
        e.Handled = true;
    }
}
