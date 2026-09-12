using System.Windows;
using System.Windows.Controls;

namespace UnifiedRgb.App.Views;

/// <summary>The Shows page: a timeline of profiles. Its own top-level page
/// rather than a tab inside the pump LCD designer, because a show drives the
/// lights, the pump panel and the screen in the case, and living under one of
/// the three was filing it under the smallest.</summary>
public partial class ShowPane : UserControl
{
    public ShowPane() => InitializeComponent();

    MainViewModel VM => (MainViewModel)DataContext;
    ShowViewModel Shows => VM.Shows;

    void Back_Click(object sender, RoutedEventArgs e) => VM.IsShowOpen = false;

    void NewShow_Click(object sender, RoutedEventArgs e) => Shows.New();
    void DeleteShow_Click(object sender, RoutedEventArgs e) => Shows.Delete();
    void AddStep_Click(object sender, RoutedEventArgs e) => Shows.AddStep();
    void RunShow_Click(object sender, RoutedEventArgs e) => Shows.Toggle();
    void PauseShow_Click(object sender, RoutedEventArgs e) => Shows.TogglePaused();

    void StepRemove_Click(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is SceneAction a) Shows.RemoveStep(a);
    }

    void StepUp_Click(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is SceneAction a) Shows.MoveStep(a, -1);
    }

    void StepDown_Click(object sender, RoutedEventArgs e)
    {
        if (StepOf(sender) is SceneAction a) Shows.MoveStep(a, +1);
    }

    /// <summary>The step a row's button belongs to. Read from the button's own
    /// DataContext rather than from the list's selection: these rows have no
    /// selection, and reaching for one would act on the wrong step.</summary>
    static SceneAction? StepOf(object sender) => (sender as FrameworkElement)?.DataContext as SceneAction;
}
