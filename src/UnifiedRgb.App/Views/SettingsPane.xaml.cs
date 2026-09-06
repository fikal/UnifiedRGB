using System.Windows;
using System.Windows.Controls;

namespace UnifiedRgb.App.Views;

/// <summary>The gear page: profiles, startup, OpenRGB, PawnIO, Chroma,
/// brightness, automation and support.</summary>
public partial class SettingsPane : UserControl
{
    public SettingsPane() => InitializeComponent();

    MainViewModel VM => (MainViewModel)DataContext;
    Window? Owner => Window.GetWindow(this);

    void SettingsBack_Click(object sender, RoutedEventArgs e) => VM.IsSettingsOpen = false;

    void SaveProfileAsNew_Click(object sender, RoutedEventArgs e) => VM.SaveProfileAsNew();

    async void InstallPawnIo_Click(object sender, RoutedEventArgs e)
    {
        // Awaited: a discarded Task swallowed install failures silently.
        try { await VM.InstallPawnIoAsync(); }
        catch (Exception ex) { UnifiedRgb.Core.Log.Error("pawnio", ex); }
    }

    void ManageRules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new AppRulesWindow(VM) { Owner = owner });
    }

    void ManageSchedules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new SchedulesWindow(VM) { Owner = owner });
        VM.RefreshScheduleSummary();
    }

    void ManageSensorRules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new SensorRulesWindow(VM) { Owner = owner });
    }

    void ManageExitBehavior_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new ExitBehaviorWindow(VM) { Owner = owner });
    }

    void SendSupport_Click(object sender, RoutedEventArgs e) => VM.SendToSupport();
}
