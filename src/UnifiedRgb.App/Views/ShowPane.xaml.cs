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

    Window? Owner => Window.GetWindow(this);

    /// <summary>Picking a show only opens it for editing; Run plays it.</summary>
    void Show_Picked(object? sender, Controls.NamedPickEventArgs e)
    {
        if (e.Item is SceneSequence show) Shows.SelectedShow = show;
    }

    void ShowNew_Click(object? sender, EventArgs e)
    {
        if (Owner is not Window owner) return;
        string? name = Dialogs.AskName(owner, "New show",
            "Starts empty. Add steps below; each one waits, then goes to a profile.",
            Shows.SuggestShowName(), "Create", n => Shows.ShowNameProblem(n));
        if (name != null) Shows.Create(name);
    }

    void ShowRename_Click(object? sender, EventArgs e)
    {
        if (Shows.SelectedShow is not SceneSequence show || Owner is not Window owner) return;
        string? name = Dialogs.AskName(owner, "Rename show",
            "Profiles that start it follow the new name.",
            show.Name, "Rename", n => Shows.ShowNameProblem(n, show));
        if (name != null) Shows.Rename(show, name);
    }

    /// <summary>Delete, asking first only when profiles still start it.</summary>
    void ShowDelete_Click(object? sender, EventArgs e)
    {
        if (Shows.SelectedShow is not SceneSequence show) return;
        var uses = VM.WhatUsesShow(show.Name);
        if (uses.Count > 0 && Owner is Window owner)
        {
            string list = string.Join("\n", uses.Select(u => "   • " + u));
            if (!Dialogs.Confirm(owner, $"Delete “{show.Name}”?",
                    $"{uses.Count} profile{(uses.Count == 1 ? "" : "s")} still start it:\n\n{list}\n\n"
                    + "They keep the name and start nothing until you pick another show for them.",
                    "Delete Anyway"))
                return;
        }
        Shows.Delete();
    }
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
