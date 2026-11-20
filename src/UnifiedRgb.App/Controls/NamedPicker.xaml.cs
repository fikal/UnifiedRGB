using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace UnifiedRgb.App.Controls;

/// <summary>The item the user picked from a NamedPicker's list.</summary>
public sealed class NamedPickEventArgs(object item) : EventArgs
{
    public object Item { get; } = item;
}

/// <summary>A dropdown of named things with Save and a menu of New, Rename and
/// Delete (see NamedPicker.xaml for why it exists).
///
/// The control decides nothing. It reports what the user asked for and the page
/// that hosts it does it - asking about unsaved changes, opening the name
/// dialog, confirming a delete - because only the page knows its view model
/// and its window. SelectedItem is ONE-WAY on purpose: the list never writes a
/// pick into the view model behind the page's back. A bound two-way selection
/// is how a list rebuilt under a combo used to push null into the model, and
/// how "pick a profile" could not be told apart from "the list changed".</summary>
public partial class NamedPicker : UserControl
{
    public NamedPicker()
    {
        InitializeComponent();
        UpdateNoun();
        UpdateMenu();   // the DP callback never runs for a selection that starts out null
        List.SelectionChanged += List_SelectionChanged;
        // Opened as well as the ⋯ click: a right-click on the button opens the
        // menu directly, without going through More_Click.
        Menu.Opened += (_, _) => UpdateMenu();
    }

    /// <summary>With the list closed nothing the user does is a pick, so a
    /// selection that moved anyway (a wheel over the closed box, the list
    /// rebuilt under it) goes straight back to what the page says. Left alone,
    /// the box would show one thing while another was picked, and the next
    /// open-and-close without choosing would "pick" what it was showing.
    /// Posted, because changing a selection from inside its own change event
    /// is not something a Selector takes well.</summary>
    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.IsDropDownOpen || Same(List.SelectedItem, SelectedItem)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!List.IsDropDownOpen && !Same(List.SelectedItem, SelectedItem)) Resync();
        });
    }

    static bool Same(object? a, object? b) => ReferenceEquals(a, b) || Equals(a, b);

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(NamedPicker));
    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(NamedPicker),
            new PropertyMetadata(null, (d, _) => ((NamedPicker)d).OnSelectedItemChanged()));
    public static readonly DependencyProperty DisplayMemberPathProperty =
        DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(NamedPicker), new PropertyMetadata(""));
    public static readonly DependencyProperty NounProperty =
        DependencyProperty.Register(nameof(Noun), typeof(string), typeof(NamedPicker),
            new PropertyMetadata("item", (d, _) => ((NamedPicker)d).UpdateNoun()));
    public static readonly DependencyProperty ShowSaveProperty =
        DependencyProperty.Register(nameof(ShowSave), typeof(bool), typeof(NamedPicker), new PropertyMetadata(true));
    public static readonly DependencyProperty SaveEnabledProperty =
        DependencyProperty.Register(nameof(SaveEnabled), typeof(bool), typeof(NamedPicker), new PropertyMetadata(true));
    public static readonly DependencyProperty HasChangesProperty =
        DependencyProperty.Register(nameof(HasChanges), typeof(bool), typeof(NamedPicker));
    public static readonly DependencyProperty ShowRevertProperty =
        DependencyProperty.Register(nameof(ShowRevert), typeof(bool), typeof(NamedPicker),
            new PropertyMetadata(false, (d, _) => ((NamedPicker)d).UpdateMenu()));
    public static readonly DependencyProperty PickerWidthProperty =
        DependencyProperty.Register(nameof(PickerWidth), typeof(double), typeof(NamedPicker), new PropertyMetadata(220.0));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>What the page says is picked. Bind it one-way to the view model.</summary>
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public string DisplayMemberPath { get => (string)GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }
    /// <summary>"profile", "screen", "show": the menu reads "New profile…".</summary>
    public string Noun { get => (string)GetValue(NounProperty); set => SetValue(NounProperty, value); }
    /// <summary>False for things that save themselves as they are edited (a show).</summary>
    public bool ShowSave { get => (bool)GetValue(ShowSaveProperty); set => SetValue(ShowSaveProperty, value); }
    public bool SaveEnabled { get => (bool)GetValue(SaveEnabledProperty); set => SetValue(SaveEnabledProperty, value); }
    /// <summary>Shows "Unsaved changes" beside the buttons.</summary>
    public bool HasChanges { get => (bool)GetValue(HasChangesProperty); set => SetValue(HasChangesProperty, value); }
    /// <summary>Offer "Revert to saved" (for things with a saved version that
    /// the live one can drift from: a profile, a screen).</summary>
    public bool ShowRevert { get => (bool)GetValue(ShowRevertProperty); set => SetValue(ShowRevertProperty, value); }
    public double PickerWidth { get => (double)GetValue(PickerWidthProperty); set => SetValue(PickerWidthProperty, value); }

    /// <summary>The user picked a different item. The page loads it, or asks
    /// first; if it does neither, the list goes back to what is picked.</summary>
    public event EventHandler<NamedPickEventArgs>? Picked;
    public event EventHandler? SaveRequested;
    public event EventHandler? NewRequested;
    public event EventHandler? RenameRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? RevertRequested;

    void UpdateNoun()
    {
        if (NewItem == null) return;   // the callback can run before InitializeComponent
        NewItem.Header = $"New {Noun}…";
    }

    void OnSelectedItemChanged()
    {
        UpdateMenu();
        Resync();
    }

    /// <summary>Rename, Revert and Delete act on the picked item, so with
    /// nothing picked they have nothing to act on.</summary>
    void UpdateMenu()
    {
        if (RenameItem == null) return;   // a DP callback can run before InitializeComponent
        bool any = SelectedItem != null;
        RenameItem.IsEnabled = any;
        DeleteItem.IsEnabled = any;
        RevertItem.IsEnabled = any;
        RevertItem.Visibility = ShowRevert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Put the list back on what the page says is picked. SetCurrentValue,
    /// never SetValue: a local value would replace the one-way binding and the
    /// list would stop following the page for good.</summary>
    void Resync() => List.SetCurrentValue(Selector.SelectedItemProperty, SelectedItem);

    /// <summary>Acted on when the list CLOSES, not on every selection change: a
    /// list rebuilt underneath the combo (an item saved, renamed or deleted)
    /// changes the selection too, and none of that is somebody picking.
    ///
    /// One dispatcher turn later, not inside the event, for two reasons. A
    /// pick made with the keyboard (open, arrow, Enter) closes the list FIRST
    /// and commits the highlighted item AFTER, so read at once the selection is
    /// still the old one and the pick was lost (and the closed-list resync then
    /// put the old one back). And the app's combo template closes its popup
    /// after this event, so a save prompt opened from inside it came up with
    /// the list still drawn on top of everything.</summary>
    void List_DropDownClosed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() =>
        {
            var item = List.SelectedItem;
            if (item != null && !Same(item, SelectedItem))
                Picked?.Invoke(this, new NamedPickEventArgs(item));
            // Whatever the page did: accepted (the binding already moved
            // SelectedItem), asked and was cancelled, or ignored it.
            Resync();
        });

    void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    void More_Click(object sender, RoutedEventArgs e)
    {
        OnSelectedItemChanged();
        Menu.PlacementTarget = MoreButton;
        Menu.Placement = PlacementMode.Bottom;
        Menu.IsOpen = true;
    }

    void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke(this, EventArgs.Empty);
    void Rename_Click(object sender, RoutedEventArgs e) => RenameRequested?.Invoke(this, EventArgs.Empty);
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
    void Revert_Click(object sender, RoutedEventArgs e) => RevertRequested?.Invoke(this, EventArgs.Empty);
}
