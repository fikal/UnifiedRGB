using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.App;

/// <summary>Editor for timed windows: lights off, or a profile, on chosen days.
/// Night mode is one of these, migrated on first run.</summary>
public partial class SchedulesWindow : Window
{
    readonly MainViewModel _vm;

    public SchedulesWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        UpdateEmptyText();
        // Named handler + unhook: Schedules lives on the long-lived VM, so an
        // anonymous subscription would root this window for the app's lifetime.
        vm.Schedules.CollectionChanged += RulesChanged;
        Closed += (_, _) => vm.Schedules.CollectionChanged -= RulesChanged;
    }

    void RulesChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateEmptyText();

    void UpdateEmptyText() =>
        NoRulesText.Visibility = _vm.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Every inline edit persists. Guarded on IsLoaded because each
    /// row's bindings raise SelectionChanged as its container is generated,
    /// which would be one settings write per row per open with nothing changed.</summary>
    void Rule_Persist(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _vm.PersistAutomation();
    }

    void AddOff_Click(object sender, RoutedEventArgs e) =>
        _vm.AddSchedule(new ScheduleRule { Action = ScheduleAction.LightsOff, Start = "23:00", End = "07:00" });

    void AddProfile_Click(object sender, RoutedEventArgs e) =>
        _vm.AddSchedule(new ScheduleRule
        {
            Action = ScheduleAction.Profile,
            Start = "18:00",
            End = "22:00",
            Profile = _vm.ProfileNames.FirstOrDefault(),
        });

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ScheduleRule r) _vm.RemoveSchedule(r);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}
