using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>Browse built-in palettes and the user's saved ones, apply any to the
/// current effect in one click, save the current colors, or paste-import from a
/// coolors.co URL / hex list. Cards are built in code so a palette's color strip
/// draws directly - no per-item DataTemplate/converter plumbing.</summary>
public partial class PaletteLibraryWindow : Window
{
    readonly MainViewModel _vm;
    Border? _active;

    // Shared card brushes (frozen: one instance serves every card).
    static readonly SolidColorBrush NormalBg = Frozen(Color.FromRgb(0x20, 0x23, 0x2B));
    static readonly SolidColorBrush HoverBg = Frozen(Color.FromRgb(0x2A, 0x2E, 0x3B));
    static readonly SolidColorBrush HoverBorder = Frozen(Color.FromArgb(0x66, 0x4C, 0x6F, 0xFF));
    static readonly SolidColorBrush AccentBrush = Frozen(Color.FromRgb(0x4C, 0x6F, 0xFF));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public PaletteLibraryWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        BuildCards();
    }

    void BuildCards()
    {
        Cards.Children.Clear();
        _active = null;
        foreach (var entry in _vm.AllPalettes())
            Cards.Children.Add(MakeCard(entry));
    }

    Border MakeCard(PaletteEntry entry)
    {
        var strip = new UniformGrid { Rows = 1, Columns = Math.Max(1, entry.Colors.Length), Height = 34 };
        foreach (var c in entry.Colors)
            strip.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) });

        var name = new TextBlock
        {
            Text = entry.Name, FontWeight = FontWeights.SemiBold, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var titleRow = new Grid { Margin = new Thickness(2, 8, 0, 0) };
        titleRow.Children.Add(name);
        if (entry.Custom)
        {
            var del = new Button
            {
                Content = "✕", Padding = new Thickness(6, 1, 6, 1), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.6,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "Delete this saved palette",
            };
            del.Click += (_, e) =>
            {
                e.Handled = true;
                _vm.DeleteSavedPalette(entry.Name);
                BuildCards();
            };
            titleRow.Children.Add(del);
        }

        var body = new StackPanel();
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = strip,
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
        });
        body.Children.Add(titleRow);

        var card = new Border
        {
            Width = 196, Margin = new Thickness(6), Padding = new Thickness(10),
            CornerRadius = new CornerRadius(9), Background = NormalBg,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
            Child = body,
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;                       // don't let the click drag the window
            _vm.ApplyPaletteColors(entry.Colors);
            if (_active != null) _active.BorderBrush = Brushes.Transparent;
            card.BorderBrush = AccentBrush;
            _active = card;
            ImportStatus.Text = $"Applied “{entry.Name}” ({entry.Colors.Length} colors).";
        };
        // Hover feedback: lift the card (lighter fill + soft accent outline);
        // the applied card keeps its solid accent border either way.
        card.MouseEnter += (_, _) =>
        {
            card.Background = HoverBg;
            if (_active != card) card.BorderBrush = HoverBorder;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = NormalBg;
            if (_active != card) card.BorderBrush = Brushes.Transparent;
        };
        return card;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = SaveName.Text?.Trim() ?? "";
        if (name.Length == 0) { ImportStatus.Text = "Type a name to save the current palette."; return; }
        _vm.SaveCurrentPaletteAs(name);
        SaveName.Text = "";
        BuildCards();
        ImportStatus.Text = $"Saved “{name}”.";
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        int n = _vm.ImportPalette(ImportText.Text ?? "");
        ImportStatus.Text = n > 0
            ? $"Imported {n} color{(n == 1 ? "" : "s")} and applied them."
            : "No hex colors found in that text.";
        if (_active != null) { _active.BorderBrush = Brushes.Transparent; _active = null; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
