using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Controls;

/// <summary>Wrap of clickable color swatches (see SwatchGrid.xaml).</summary>
public partial class SwatchGrid : UserControl
{
    public SwatchGrid() => InitializeComponent();

    public static readonly DependencyProperty ColorsProperty =
        DependencyProperty.Register(nameof(Colors), typeof(IEnumerable), typeof(SwatchGrid));
    public static readonly DependencyProperty SwatchSizeProperty =
        DependencyProperty.Register(nameof(SwatchSize), typeof(double), typeof(SwatchGrid), new PropertyMetadata(28.0));
    public static readonly DependencyProperty SwatchMarginProperty =
        DependencyProperty.Register(nameof(SwatchMargin), typeof(Thickness), typeof(SwatchGrid), new PropertyMetadata(new Thickness(3)));
    public static readonly DependencyProperty PickCommandProperty =
        DependencyProperty.Register(nameof(PickCommand), typeof(ICommand), typeof(SwatchGrid));
    public static readonly DependencyProperty RemoveCommandProperty =
        DependencyProperty.Register(nameof(RemoveCommand), typeof(ICommand), typeof(SwatchGrid));

    /// <summary>The Rgb values to show.</summary>
    public IEnumerable? Colors { get => (IEnumerable?)GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }
    public double SwatchSize { get => (double)GetValue(SwatchSizeProperty); set => SetValue(SwatchSizeProperty, value); }
    public Thickness SwatchMargin { get => (Thickness)GetValue(SwatchMarginProperty); set => SetValue(SwatchMarginProperty, value); }
    /// <summary>Left click: receives the clicked Rgb.</summary>
    public ICommand? PickCommand { get => (ICommand?)GetValue(PickCommandProperty); set => SetValue(PickCommandProperty, value); }
    /// <summary>Right click: receives the clicked Rgb. Leave unset for read-only grids.</summary>
    public ICommand? RemoveCommand { get => (ICommand?)GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }
}
