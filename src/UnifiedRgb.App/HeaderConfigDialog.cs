using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>Configure which motherboard ARGB headers host addressable devices
/// (fans/strips): per header — enabled, name, LED count, wire color order —
/// with a Test button that lights the header white so the user can see which
/// physical fans respond. Saves to hardware.json and rebuilds devices.</summary>
public sealed class HeaderConfigDialog
{
    static readonly string[] Orders = { "GRB", "RGB", "BGR", "RBG", "GBR", "BRG" };

    /// <summary>Builds the dialog on the shared Dialogs shell and shows it
    /// modally over a blurred owner.</summary>
    public static void Show(Window owner, MainViewModel vm)
    {
        var cfg = HardwareConfig.Load();
        Window win = null!;
        (win, var body) = Dialogs.MakeDialog(owner, onEscape: () => win.Close());
        win.Topmost = false;   // an owned config dialog; only the close prompts float
        body.MinWidth = 470;

        var fgMain = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        var fgDim = new SolidColorBrush(Color.FromRgb(0xA8, 0xAC, 0xB8));
        var boxBg = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x32));

        TextBox Tb(string text, double width) => new()
        {
            Text = text, Width = width, Padding = new Thickness(6, 6, 6, 6),
            Background = boxBg, Foreground = fgMain, BorderThickness = new Thickness(0),
            CaretBrush = fgMain, VerticalAlignment = VerticalAlignment.Center,
        };

        var rows = new List<(CheckBox On, TextBox Name, TextBox Leds, ComboBox Order)>();
        var grid = new StackPanel();

        for (int header = 1; header <= 4; header++)
        {
            var existing = cfg.GigabyteArgbHeaders.FirstOrDefault(h => h.Header == header);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };

            var on = new CheckBox
            {
                Content = $"Header {header}", IsChecked = existing != null, Width = 92,
                Foreground = fgMain, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            var name = Tb(existing?.Name ?? $"Fans (Header {header})", 190);
            name.Margin = new Thickness(8, 0, 0, 0);
            var leds = Tb((existing?.Leds ?? 8).ToString(), 46);
            leds.Margin = new Thickness(8, 0, 0, 0);
            var order = new ComboBox
            {
                // Case-insensitive like the device's NormalizeOrder: a hand-edited
                // "rgb" must pre-select RGB, or Save would silently rewrite it to GRB.
                ItemsSource = Orders, SelectedItem = existing?.ColorOrder?.Trim().ToUpperInvariant() is string o && Orders.Contains(o) ? o : "GRB",
                Width = 68, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };

            int h = header;
            var test = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x48)), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "Test", Foreground = fgMain },
                VerticalAlignment = VerticalAlignment.Center,
            };
            test.PreviewMouseLeftButtonDown += (_, e2) =>
            {
                e2.Handled = true;
                vm.TestHeader(h, int.TryParse(leds.Text, out int n) ? Math.Clamp(n, 1, 64) : 12);
            };

            row.Children.Add(on); row.Children.Add(name); row.Children.Add(leds); row.Children.Add(order); row.Children.Add(test);
            grid.Children.Add(row);
            rows.Add((on, name, leds, order));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Dialogs.Btn("Cancel", false, () => win.Close()));
        buttons.Children.Add(Dialogs.Btn("Save && Rescan", true, () =>
        {
            var newCfg = new HardwareConfig { GigabyteArgbHeaders = new() };
            for (int i = 0; i < rows.Count; i++)
            {
                var (on, name, leds, order) = rows[i];
                if (on.IsChecked != true) continue;
                newCfg.GigabyteArgbHeaders.Add(new ArgbHeaderConfig
                {
                    Header = i + 1,
                    Name = string.IsNullOrWhiteSpace(name.Text) ? $"ARGB Header {i + 1}" : name.Text.Trim(),
                    Leds = int.TryParse(leds.Text, out int n) ? Math.Clamp(n, 1, 256) : 8,
                    ColorOrder = order.SelectedItem as string ?? "GRB",
                });
            }
            vm.ApplyHeaderConfig(newCfg);
            win.Close();
        }));

        body.Children.Add(new TextBlock { Text = "ARGB Headers", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fgMain });
        body.Children.Add(new TextBlock
        {
            Text = "Check the headers that have addressable fans/strips. 'Test' lights that header white so you can see which fans respond.",
            Foreground = fgDim, Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 470,
        });
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock { Text = "", Width = 100 });
        head.Children.Add(new TextBlock { Text = "Name", Width = 198, Foreground = fgDim, FontSize = 11 });
        head.Children.Add(new TextBlock { Text = "LEDs", Width = 54, Foreground = fgDim, FontSize = 11 });
        head.Children.Add(new TextBlock { Text = "Order", Foreground = fgDim, FontSize = 11 });
        body.Children.Add(head);
        body.Children.Add(grid);
        body.Children.Add(buttons);

        // Every close path (Cancel, the X button, Save before its Rescan)
        // repaints the header from the static frame: with no effect channel
        // covering it nothing else would, and the Test's white ring stayed up.
        win.Closed += (_, _) => vm.EndHeaderTest();
        Dialogs.ShowBlurred(owner, win);
    }
}
