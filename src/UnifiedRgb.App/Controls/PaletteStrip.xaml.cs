using System.Windows;
using System.Windows.Controls;

namespace UnifiedRgb.App.Controls;

/// <summary>Removable swatch strip bound to the view model's PatternPalette.</summary>
public partial class PaletteStrip : UserControl
{
    public PaletteStrip() => InitializeComponent();

    public static readonly DependencyProperty SwatchSizeProperty =
        DependencyProperty.Register(nameof(SwatchSize), typeof(double), typeof(PaletteStrip), new PropertyMetadata(28.0));

    public static readonly DependencyProperty SwatchMarginProperty =
        DependencyProperty.Register(nameof(SwatchMargin), typeof(Thickness), typeof(PaletteStrip), new PropertyMetadata(new Thickness(3)));

    /// <summary>Swatch edge in DIPs (28 in the side columns, 34 in the palette editor).</summary>
    public double SwatchSize
    {
        get => (double)GetValue(SwatchSizeProperty);
        set => SetValue(SwatchSizeProperty, value);
    }

    public Thickness SwatchMargin
    {
        get => (Thickness)GetValue(SwatchMarginProperty);
        set => SetValue(SwatchMarginProperty, value);
    }
}
