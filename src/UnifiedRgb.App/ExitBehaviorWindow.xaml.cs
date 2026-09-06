using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>Per-device "when the app is closed, show…".
///
/// One row per detected device. What a row offers comes from the device
/// itself, so a driver that grows the ability to hold a colour appears here
/// with no change to this window; a device that cannot do it says so plainly
/// rather than offering an option that would quietly do nothing.</summary>
public partial class ExitBehaviorWindow : Window
{
    readonly MainViewModel _vm;

    public ObservableCollection<ExitRow> Rows { get; } = new();

    public ExitBehaviorWindow(MainViewModel vm)
    {
        _vm = vm;
        var saved = HardwareConfig.Load().ExitBehaviors;
        foreach (var device in vm.Devices)
            Rows.Add(new ExitRow(device, saved.GetValueOrDefault(device.Name)));
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Any edit persists at once. There is no OK button: the rows are
    /// each one setting, and a dialog that loses your choice because you closed
    /// it with the X is a dialog nobody trusts.</summary>
    void Row_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;                       // container generation, not a user edit
        if ((sender as FrameworkElement)?.DataContext is not ExitRow row) return;

        var config = HardwareConfig.Load();
        var behavior = row.Current;
        if (behavior.Mode == ExitMode.KeepLast) config.ExitBehaviors.Remove(row.Name);
        else config.ExitBehaviors[row.Name] = behavior;
        config.Save();
        row.Refresh();
    }

    void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExitRow row) return;
        _vm.ApplyExitBehaviorNow(row.Device, row.Current);
    }

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>One device's row.</summary>
public sealed class ExitRow : INotifyPropertyChanged
{
    public sealed record Choice(string Label, ExitBehavior Behavior);

    public IRgbDevice Device { get; }
    public string Name => Device.Name;
    public IReadOnlyList<Choice> Choices { get; }

    /// <summary>False when the device can only keep its last colors: the one
    /// choice is still listed, so the row reads as an answer rather than a
    /// gap, but there is nothing to pick.</summary>
    public bool Supported => Choices.Count > 1;

    public string Note => Supported
        ? Device.Vendor
        : $"{Device.Vendor} · nothing to hand it back to";

    public bool CanApplyNow => Supported && Current.Mode != ExitMode.KeepLast;
    public bool NeedsColor => HardwareExit.NeedsColor(Current);

    int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (_selectedIndex == value) return; _selectedIndex = value; Refresh(); }
    }

    string _colorHex;
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            // Keep what renders and what is saved in step: an unparseable hex
            // would leave the swatch showing the old colour while the config
            // took the typo.
            string clean = Rgb.TryFromHex(value, out var c) ? c.ToHex() : _colorHex;
            if (_colorHex == clean) return;
            _colorHex = clean;
            Notify(nameof(ColorHex));
        }
    }

    /// <summary>The behavior this row currently describes.</summary>
    public ExitBehavior Current
    {
        get
        {
            var pick = Choices[Math.Clamp(_selectedIndex, 0, Choices.Count - 1)].Behavior;
            return new ExitBehavior { Mode = pick.Mode, Effect = pick.Effect, ColorHex = _colorHex };
        }
    }

    public ExitRow(IRgbDevice device, ExitBehavior? saved)
    {
        Device = device;
        Choices = HardwareExit.Choices(device)
            .Select(b => new Choice(HardwareExit.Label(b), b))
            .ToList();
        _colorHex = saved?.ColorHex is string hex && Rgb.TryFromHex(hex, out var c) ? c.ToHex() : "FFFFFF";
        _selectedIndex = IndexOf(saved);
    }

    /// <summary>Where a saved choice sits in this device's list now. A behavior
    /// naming an effect the device no longer has falls back to keeping its last
    /// colors rather than silently becoming a different effect.</summary>
    int IndexOf(ExitBehavior? saved)
    {
        if (saved == null) return 0;
        for (int i = 0; i < Choices.Count; i++)
        {
            var b = Choices[i].Behavior;
            if (b.Mode != saved.Mode) continue;
            if (b.Mode == ExitMode.Effect
                && !string.Equals(b.Effect, saved.Effect, StringComparison.OrdinalIgnoreCase)) continue;
            return i;
        }
        return 0;
    }

    public void Refresh()
    {
        Notify(nameof(SelectedIndex));
        Notify(nameof(NeedsColor));
        Notify(nameof(CanApplyNow));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}
