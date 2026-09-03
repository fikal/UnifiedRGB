using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.App;

/// <summary>Razer devices whose shape the firmware won't tell us (the HyperFlux
/// V2 pad's strip) get their LED count here, and every Razer device gets a
/// Test chase — LEDs light one after another so the user can see how many
/// exist and in what order. Saves to hardware.json (RazerLedCounts) and
/// rebuilds devices, exactly like the ARGB header dialog.</summary>
public sealed class RazerLayoutDialog
{
    public static void Show(Window owner, MainViewModel vm)
    {
        var cfg = HardwareConfig.Load();
        var devices = vm.Devices.OfType<RazerHid>().ToList();
        Window win = null!;
        (win, var body) = Dialogs.MakeDialog(owner, onEscape: () => win.Close());
        win.Topmost = false;
        body.MinWidth = 480;

        var fgMain = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        var fgDim = new SolidColorBrush(Color.FromRgb(0xA8, 0xAC, 0xB8));
        var boxBg = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x32));

        var status = new TextBlock { Foreground = fgDim, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
        bool busy = false;
        var rows = new List<(RazerHid Dev, TextBox? Leds)>();
        var grid = new StackPanel();

        foreach (var dev in devices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = dev.Name, Width = 250, Foreground = fgMain, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"pid {dev.ProductId:X4}  transaction 0x{dev.TransactionId:X2}  fw {dev.Firmware}  serial {dev.Serial}",
            });
            TextBox? leds = null;
            if (dev.IsPad)
            {
                leds = new TextBox
                {
                    Text = dev.LedCount.ToString(), Width = 46, Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(8, 0, 0, 0),
                    Background = boxBg, Foreground = fgMain, BorderThickness = new Thickness(0), CaretBrush = fgMain,
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Current count is {dev.CountSource}",
                };
                row.Children.Add(leds);
            }
            else row.Children.Add(new TextBlock { Text = $"{dev.LedCount} LEDs", Width = 54, Margin = new Thickness(8, 0, 0, 0), Foreground = fgDim, VerticalAlignment = VerticalAlignment.Center });

            var test = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x48)), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "Test", Foreground = fgMain }, VerticalAlignment = VerticalAlignment.Center,
            };
            var d = dev; var l = leds;
            test.PreviewMouseLeftButtonDown += (_, e2) =>
            {
                e2.Handled = true;
                if (busy) return;
                int count = l != null && int.TryParse(l.Text, out int n) ? Math.Clamp(n, 1, RazerHid.MaxLeds) : d.LedCount;
                busy = true;
                status.Text = $"Chasing {count} LEDs on {d.Name}… watch which ones light and in what order.";
                // The chase holds the device's write lock for count × 180 ms;
                // off the dispatcher so the dialog stays responsive.
                Task.Run(() => d.TestChase(count)).ContinueWith(t =>
                {
                    busy = false;
                    status.Text = t.IsFaulted ? $"Test failed: {t.Exception?.GetBaseException().Message}" : $"{d.Name}: {t.Result}";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            };
            row.Children.Add(test);
            grid.Children.Add(row);
            rows.Add((dev, leds));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Dialogs.Btn("Cancel", false, () => win.Close()));
        buttons.Children.Add(Dialogs.Btn("Save && Rescan", true, () =>
        {
            if (busy) { status.Text = "Wait for the chase to finish."; return; }
            foreach (var (dev, leds) in rows)
            {
                if (leds == null || !int.TryParse(leds.Text, out int n)) continue;
                cfg.RazerLedCounts[$"{dev.ProductId:X4}"] = Math.Clamp(n, 1, RazerHid.MaxLeds);
            }
            vm.ApplyHeaderConfig(cfg);   // saves hardware.json, rebuilds devices
            win.Close();
        }));

        body.Children.Add(new TextBlock { Text = "Razer devices", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = fgMain });
        body.Children.Add(new TextBlock
        {
            Text = "'Test' lights the LEDs one at a time. For the mouse the order should be scroll wheel, logo, then the underglow " +
                   "around the base. For the charging pad, raise or lower the count until the chase reaches the last LED and stops there, then Save.",
            Foreground = fgDim, Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 480,
        });
        if (devices.Count == 0)
            body.Children.Add(new TextBlock { Text = "No Razer device is claimed right now.", Foreground = fgDim });
        body.Children.Add(grid);
        body.Children.Add(status);
        body.Children.Add(buttons);

        win.Closed += (_, _) => vm.EndRazerTest();
        Dialogs.ShowBlurred(owner, win);
    }
}
