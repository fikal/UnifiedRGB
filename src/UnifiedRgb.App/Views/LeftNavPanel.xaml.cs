using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>The DEVICES / SYSTEM navigation card (left column).</summary>
public partial class LeftNavPanel : UserControl
{
    public LeftNavPanel() => InitializeComponent();

    MainViewModel VM => (MainViewModel)DataContext;

    /// <summary>The DEVICES and SYSTEM lists share one SelectedLeftItem, and
    /// a ListBox keeps its old highlight when the shared selection moves to an
    /// item it doesn't contain. A later click on that stale row changes
    /// nothing ("already selected" — no event), so the view never switched.
    /// Same story for re-clicking the current item while Settings is open.
    /// Cure both by making the click itself authoritative: the clicked row
    /// becomes the view, Settings closes, and the sibling list's stale
    /// highlight is cleared.</summary>
    void LeftNav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var item = ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is not LeftItem li) return;
        var sibling = ReferenceEquals(lb, DeviceNav) ? SystemNav : DeviceNav;
        sibling.SelectedItem = null;      // pushes null through the shared binding first
        VM.SelectedLeftItem = li;         // then the clicked row wins
        VM.IsSettingsOpen = false;
    }
}
