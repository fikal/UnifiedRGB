using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace UnifiedRgb.App;

/// <summary>First-run welcome: shows detected devices, offers the PawnIO driver,
/// and applies a starter look - so a friend's first launch guides them past the
/// snags (no devices, missing PawnIO, blank canvas) instead of dropping them in.</summary>
public partial class WizardWindow : Window
{
    readonly MainViewModel _vm;
    int _step;

    public WizardWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        ShowStep(0);
    }

    void ShowStep(int s)
    {
        _step = s;
        Step1.Visibility = s == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = s == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = s == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = s > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = s == 2 ? "Finish" : "Next";
        StepLabel.Text = $"Step {s + 1} of 3";
        if (s == 0) LoadDevices();
        else if (s == 1) LoadSensors();
    }

    public sealed record DevRow(string Name, string Subtitle);

    void LoadDevices()
    {
        DeviceList.ItemsSource = _vm.Devices
            .Select(d => new DevRow(d.Name, $"{d.Type} • {d.LedCount} LEDs")).ToList();
        NoDevices.Visibility = _vm.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _vm.RescanCommand.Execute(null);
        LoadDevices();
    }

    void LoadSensors()
    {
        bool have = !_vm.PawnIoMissing;
        SensorHead.Text = have ? "✓ Sensor driver is installed" : "Optional: install the sensor driver";
        SensorBody.Text = have
            ? "PawnIO is installed, so CPU temperature, RAM lighting and motherboard fans are all available."
            : "PawnIO is a small signed driver that unlocks CPU temperature, RAM lighting and motherboard fan monitoring/control. Without it those show as dashes. Install it now, or later in Settings.";
        PawnRow.Visibility = have ? Visibility.Collapsed : Visibility.Visible;
    }

    async void InstallPawn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        PawnBar.Visibility = Visibility.Visible;
        PawnStatus.Text = "installing…";
        // async void: an escaping exception (the post-install rescan can throw)
        // is the app-wide error dialog, and the step used to stay stuck with the
        // busy bar up and the button disabled. Same guard as SettingsPane.
        try { await _vm.InstallPawnIoAsync(); }
        catch (Exception ex) { UnifiedRgb.Core.Log.Error("pawnio", ex); }
        finally
        {
            PawnBar.Visibility = Visibility.Collapsed;
            InstallBtn.IsEnabled = true;
        }
        PawnStatus.Text = _vm.PawnIoMissing ? "not installed — try again or skip" : "installed ✓";
        LoadSensors();
    }

    void Look_Click(object sender, RoutedEventArgs e)
    {
        string? tag = (sender as FrameworkElement)?.Tag as string;
        if (tag is not null and not "skip") _vm.ApplyStarterLook(tag);
        LookStatus.Text = tag == "skip"
            ? "No problem — pick effects per device on the main screen."
            : "Applied. Tweak it any time from the main screen.";
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 2) ShowStep(_step + 1);
        else Finish();
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (_step > 0) ShowStep(_step - 1); }
    void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    void Finish()
    {
        _vm.CompleteFirstRun();
        Close();
    }

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
