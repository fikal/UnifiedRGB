using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Themes;

/// <summary>Code-behind for the style dictionary: the mouse-first policy's
/// EventSetter handlers live here (typing drives the reactive effects, so
/// focused controls must never react to keys; Tab still moves focus).</summary>
public partial class Styles : ResourceDictionary
{
    // Key.System is an Alt chord (Alt+F4, Alt+Space). It has to reach
    // DefWindowProc, which is what turns it into SC_CLOSE / the system menu:
    // marking it handled here killed Alt+F4 for as long as a button, pill,
    // slider or combo had keyboard focus - i.e. after any click on one.
    void MouseOnly_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.System) return;
        e.Handled = true;
    }

    // Same pass-through for the combo, except Alt+Up/Alt+Down: ComboBox's own
    // handler substitutes SystemKey for Key.System and treats those as
    // "toggle drop-down", i.e. a keyboard reaction this style exists to
    // suppress. Only the chords DefWindowProc needs (Alt+F4, Alt+Space, F10)
    // go through.
    void Combo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsDropDownOpen || e.Key == Key.Tab) return;
        bool windowChord = e.Key == Key.System && e.SystemKey is not (Key.Up or Key.Down);
        if (!windowChord) e.Handled = true;
    }
}
