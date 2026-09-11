using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.App;

/*-----------------------------------------------------------*\
| The panel side of ActivityLog: "why did my lights just       |
| change", answered as a list of plain sentences.              |
|                                                              |
| Two things live here rather than in the log, because both    |
| are presentation decisions:                                  |
|                                                              |
| 1. The pause is at the TOP, above the list. The list is why  |
|    someone opens this window, but the pause is what they     |
|    actually want when a rule is fighting them, and burying   |
|    it under a scrolling list means they never find it.       |
|                                                              |
| 2. The whole list is replaced on every change instead of     |
|    being diffed into an observable collection. The ring      |
|    folds repeats INTO its newest entry, so "what changed"    |
|    is not always an insert at the head, and a diff that has  |
|    to spot an in-place mutation is more code and more bugs   |
|    than simply handing WPF the new snapshot. At a capacity   |
|    of 200 rows, on a list that only moves when the lighting  |
|    actually changes, the swap costs nothing worth having.    |
\*-----------------------------------------------------------*/
public partial class ActivityWindow : Window
{
    /// <summary>The one shared history. Cached in a field so the subscribe and
    /// the unsubscribe can never end up talking about different objects.</summary>
    readonly ActivityLog _log = ActivityLog.Shared;

    /// <summary>Set once a refresh is sitting in the dispatcher queue, so a
    /// burst of writes (a sensor parked on its threshold, an SDK client
    /// churning) collapses into one repaint instead of one per entry.</summary>
    int _refreshQueued;

    /// <summary>True once this window is gone. A refresh can already be queued
    /// at that point, and running it would touch the controls of a window that
    /// no longer exists for the benefit of nobody.</summary>
    bool _closed;

    // Frozen brushes: the pause card repaints on every toggle and every log
    // entry, and there is no reason to allocate a brush each time. Frozen so
    // WPF can share one instance without cloning it per use.
    static readonly SolidColorBrush RunningDot = Frozen(0xFF, 0x4C, 0xD0, 0x7A);
    static readonly SolidColorBrush PausedDot = Frozen(0xFF, 0xE0, 0xA8, 0x3A);
    static readonly SolidColorBrush IdleDot = Frozen(0xFF, 0x6E, 0x74, 0x84);
    static readonly SolidColorBrush CardBg = Frozen(0xFF, 0x14, 0x16, 0x1C);
    static readonly SolidColorBrush CardEdge = Frozen(0xFF, 0x2A, 0x2E, 0x38);
    // While paused the card itself goes amber. State that is only a word in a
    // sentence is state people miss; a card that changes color is not.
    static readonly SolidColorBrush PausedBg = Frozen(0x2E, 0xE0, 0xA8, 0x3A);
    static readonly SolidColorBrush PausedEdge = Frozen(0x99, 0xE0, 0xA8, 0x3A);

    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>No arguments: everything this window shows reaches it through
    /// statics that outlive it (the shared log, and the automation service's
    /// Current). Taking the view model would only be ceremony, and it would
    /// suggest a lifetime relationship that is not there.</summary>
    public ActivityWindow()
    {
        InitializeComponent();

        // Subscribed here rather than on Loaded: entries can land between
        // construction and the first layout pass, and a window that opens
        // already one entry stale is exactly the confusion it exists to fix.
        _log.Changed += OnLogChanged;
        Closed += OnWindowClosed;
        // Window level and tunnelling, because the themed Button style swallows
        // key presses on the focused control (see MouseOnly_PreviewKeyDown in
        // Styles.xaml). A bubbling handler would never see Escape once a button
        // has focus, which is most of the time on a window this size.
        PreviewKeyDown += OnPreviewKeyDown;

        Refresh();
        UpdatePauseUi();
    }

    /*---------------------------------------------------*\
    | Live updates                                        |
    \*---------------------------------------------------*/

    /// <summary>Raised on WHICHEVER THREAD WROTE the entry: the automation
    /// timer is on the UI thread, but the OpenRGB socket threads and the sensor
    /// hub's own thread are not. So nothing here may touch a control directly;
    /// everything goes through the dispatcher first. Getting this wrong is a
    /// hard cross-thread failure, not a cosmetic one.</summary>
    void OnLogChanged()
    {
        // If a refresh is already queued, that refresh will read the newest
        // snapshot anyway, so a second one would repaint identical rows.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Dispatcher.InvokeAsync(RefreshFromQueue);
    }

    /// <summary>The queued repaint, now safely on the UI thread.</summary>
    void RefreshFromQueue()
    {
        // Cleared BEFORE the snapshot is taken: an entry written during the
        // refresh must be able to queue the next one, or it would sit unshown
        // until something else happened to change.
        Volatile.Write(ref _refreshQueued, 0);
        if (_closed) return;   // queued before the close, delivered after it

        Refresh();
        // The pause is refreshed here too, not just on the button click,
        // because the pause writes its own history entries and this is the
        // cheapest way to stay honest if anything else ever toggles it.
        UpdatePauseUi();
    }

    void Refresh()
    {
        // Snapshot is already a newest-first copy, so it is safe to hand
        // straight to the binding engine without any further defence.
        var entries = _log.Snapshot();
        Entries.ItemsSource = entries;

        // The empty state carries real information: it says the automation has
        // not touched anything yet, which is a different fact from "this
        // feature is broken". A blank box says the second one.
        EmptyText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = entries.Length switch
        {
            0 => "",
            1 => "1 change, newest first",
            _ => $"{entries.Length} changes, newest first",
        };
    }

    /*---------------------------------------------------*\
    | The pause                                           |
    \*---------------------------------------------------*/

    /// <summary>Repaint the pause card from the service's actual state, so the
    /// card is never a claim about the pause that the pause disagrees with.</summary>
    void UpdatePauseUi()
    {
        var service = AutomationService.Current;
        if (service is null)
        {
            // Null before the main window builds the service and after it is
            // disposed. It should not be possible to reach this window in
            // either state, but a disabled button that explains itself beats
            // an enabled one that silently does nothing.
            StateDot.Fill = IdleDot;
            StateText.Text = "Automation is not running";
            ConsequenceText.Text = "There are no rules or schedules active right now, so there is nothing to pause.";
            PauseButton.Content = "Pause automation";
            PauseButton.IsEnabled = false;
            PauseCard.Background = CardBg;
            PauseCard.BorderBrush = CardEdge;
            return;
        }

        PauseButton.IsEnabled = true;
        bool paused = service.Paused;

        StateDot.Fill = paused ? PausedDot : RunningDot;
        PauseCard.Background = paused ? PausedBg : CardBg;
        PauseCard.BorderBrush = paused ? PausedEdge : CardEdge;
        StateText.Text = paused ? "Automation is paused" : "Automation is running";
        PauseButton.Content = paused ? "Resume automation" : "Pause automation";

        // Deliberately spelled out both ways round. Before the click the user
        // needs to know what they are about to lose, and after it they need to
        // know it is temporary, or they will pause once and never trust it
        // again. Kept free of em dashes to match the rest of the automation
        // copy, which the test harness asserts on.
        ConsequenceText.Text = paused
            ? "Rules and schedules will not change your lighting until you resume. The pause lasts for this session only, so closing the app clears it."
            : "Pausing freezes the lighting you have now. Rules and schedules stop changing it until you resume, and the pause lasts for this session only.";
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        var service = AutomationService.Current;
        if (service is null) return;

        service.Paused = !service.Paused;

        // The setter takes effect immediately and writes its own history entry,
        // which comes back through Changed. That callback only queues work on
        // the dispatcher though, so repaint now: a pause button that stays on
        // its old caption for a frame reads as a pause button that failed.
        UpdatePauseUi();
        Refresh();
    }

    /*---------------------------------------------------*\
    | Plumbing                                            |
    \*---------------------------------------------------*/

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        // Same reasoning as the pause: Clear raises Changed, but that lands as
        // queued work, and the list should empty on the click.
        Refresh();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    /// <summary>The log is a process-wide singleton and this window is not, so
    /// a handler left attached would keep the closed window (and its whole
    /// visual tree) alive for the rest of the session and eventually queue work
    /// against a dispatcher that has nothing left to draw. Named handlers
    /// throughout for exactly this reason: an anonymous one cannot be removed.</summary>
    void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _log.Changed -= OnLogChanged;
        PreviewKeyDown -= OnPreviewKeyDown;
        Closed -= OnWindowClosed;
    }

    /// <summary>Anywhere that is not a text box drags the window, since this
    /// window draws its own caption and has no system chrome to grab.</summary>
    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}

/// <summary>One accent color per kind of entry, for the row's left stripe and
/// its category label. The point is scanning, not decoration: a user looking
/// for the moment a rule took over should be able to find it by shape before
/// they read a single sentence. Colors are muted on purpose, because a list
/// where every row shouts is a list where nothing stands out.</summary>
public sealed class ActivityKindBrushConverter : System.Windows.Data.IValueConverter
{
    // Built once and frozen: there is one brush per kind for the life of the
    // process, and rows are regenerated on every refresh.
    static readonly SolidColorBrush RuleActivated = Frozen(0x7F, 0xB4, 0xFF);
    static readonly SolidColorBrush Precedence = Frozen(0xC9, 0x8F, 0xFF);
    static readonly SolidColorBrush BackToBase = Frozen(0x6E, 0xC7, 0x9B);
    static readonly SolidColorBrush LightsOff = Frozen(0x8A, 0x90, 0xA2);
    static readonly SolidColorBrush SdkClient = Frozen(0x5F, 0xC6, 0xD8);
    static readonly SolidColorBrush Failsafe = Frozen(0xE0, 0x6C, 0x6C);
    static readonly SolidColorBrush UserOverride = Frozen(0xF0, 0xB4, 0x5A);
    static readonly SolidColorBrush Paused = Frozen(0xE0, 0xA8, 0x3A);
    static readonly SolidColorBrush Unknown = Frozen(0x6E, 0x74, 0x84);

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c)
        => v is ActivityKind kind
            ? kind switch
            {
                ActivityKind.RuleActivated => RuleActivated,
                ActivityKind.Precedence => Precedence,
                ActivityKind.BackToBase => BackToBase,
                ActivityKind.LightsOff => LightsOff,
                ActivityKind.SdkClient => SdkClient,
                ActivityKind.Failsafe => Failsafe,
                ActivityKind.UserOverride => UserOverride,
                ActivityKind.Paused => Paused,
                _ => Unknown,
            }
            : Unknown;

    public object ConvertBack(object v, Type t, object? p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}
