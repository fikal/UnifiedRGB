using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Themes;

/// <summary>Code-behind for the style dictionary: the mouse-first policy's
/// EventSetter handlers live here (typing drives the reactive effects, so
/// focused controls must never react to keys; Tab still moves focus).</summary>
public partial class Styles : ResourceDictionary
{
    void MouseOnly_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) e.Handled = true;
    }

    void Combo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ComboBox cb && !cb.IsDropDownOpen && e.Key != Key.Tab)
            e.Handled = true;
    }
}
