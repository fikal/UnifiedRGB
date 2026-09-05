using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.App;

/// <summary>Editor for sensor threshold rules: pick a sensor, a number and a
/// profile. Every part of an existing rule is editable in place, because a
/// threshold is a number people tune rather than get right first time.
///
/// Reordering is by arrow buttons rather than the app-rules dialog's drag: the
/// list is short, order is priority, and buttons say so without a gesture.</summary>
public partial class SensorRulesWindow : Window
{
    readonly MainViewModel _vm;

    /// <summary>A sensor the user can pick: the stable id that gets saved, and
    /// the readable name shown in the dropdown.</summary>
    public sealed record SourceChoice(string Id, string Label);

    public IReadOnlyList<SourceChoice> SourceChoices { get; }

    public SensorRulesWindow(MainViewModel vm)
    {
        _vm = vm;
        SourceChoices = vm.SensorSourceChoices
            .Select(id => new SourceChoice(id, SensorSources.Label(id)))
            .ToList();
        InitializeComponent();
        DataContext = vm;
        // The window exposes SourceChoices itself, so the rows can reach it
        // through the Window ancestor the same way they reach ProfileNames.
        SourcePick.ItemsSource = SourceChoices;
        SourcePick.SelectedIndex = 0;
        UpdateHints();
        // Named handler + unhook: SensorRules lives on the long-lived VM, so an
        // anonymous subscription would root this window for the app's lifetime.
        vm.SensorRules.CollectionChanged += RulesChanged;
        Closed += (_, _) => vm.SensorRules.CollectionChanged -= RulesChanged;
    }

    void RulesChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateHints();

    void UpdateHints()
    {
        NoRulesText.Visibility = _vm.SensorRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // A rule whose profile was deleted shows an empty dropdown; say why
        // rather than leaving a blank box the user has to guess at.
        bool anyMissing = _vm.SensorRules.Any(
            r => string.IsNullOrWhiteSpace(r.Profile) || !_vm.HasProfile(r.Profile));
        MissingProfileText.Visibility = anyMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Every inline edit persists. Guarded on IsLoaded because each
    /// row's bindings raise SelectionChanged as its container is generated,
    /// which would otherwise be one settings write per rule per open with
    /// nothing actually changed.</summary>
    void Rule_Persist(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.PersistAutomation();
        UpdateHints();
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (SourcePick.SelectedValue is not string source)
        {
            AddHint.Text = "Pick a sensor first.";
            return;
        }
        if (!double.TryParse(ThresholdBox.Text.Trim(), out double threshold))
        {
            AddHint.Text = "That threshold is not a number.";
            return;
        }
        if (ProfilePick.SelectedItem is not string profile || profile.Length == 0)
        {
            AddHint.Text = "Choose which profile to apply. Save one first if the list is empty.";
            return;
        }

        bool above = DirPick.SelectedIndex == 0;
        _vm.AddSensorRule(new SensorRule
        {
            Source = source,
            Above = above,
            Threshold = threshold,
            Profile = profile,
        });
        AddHint.Text = $"Added: {SensorSources.Label(source)} {(above ? "at or above" : "at or below")} " +
                       $"{threshold:0.#}{SensorSources.Unit(source)} applies '{profile}'.";
        if (!_vm.SensorRulesEnabled)
            AddHint.Text += " Tick the box above to start watching.";
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SensorRule r) _vm.RemoveSensorRule(r);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not SensorRule r) return;
        int i = _vm.SensorRules.IndexOf(r);
        if (i < 0) return;
        _vm.MoveSensorRule(r, i + delta);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}
