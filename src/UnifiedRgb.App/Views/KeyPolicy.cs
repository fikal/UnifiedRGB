using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>Mouse-first policy shared by the panes: typing drives the reactive
/// effects, so focused list controls must never react to keys. Tab still moves
/// focus; TextBoxes nested in a list item still type (PreviewKeyDown tunnels
/// ahead of them - marking their keys handled blocked TextInput entirely).</summary>
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
