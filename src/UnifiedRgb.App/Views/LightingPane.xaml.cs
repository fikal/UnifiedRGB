using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedRgb.App.Views;

/// <summary>The lighting editor: effect pills + browser, color column, custom
/// pattern / ripple columns, the LED preview and the Lian Li fan model.</summary>
public partial class LightingPane : UserControl
{
    MainViewModel? _vm;
    MainViewModel VM => _vm ??= (MainViewModel)DataContext;

    public LightingPane()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => { if (e.NewValue is MainViewModel vm && _vm == null) Attach(vm); };
    }

    void Attach(MainViewModel vm)
    {
        _vm = vm;
        PreviewView.Source = vm.CurrentTargetView;
        PreviewView.LedClicked += vm.PaintLed;
        PreviewView.LedRightClicked += vm.ClearLed;
        LianFanView.Source = vm.LianLiView;
        LianFanView.Clicked += vm.LianClicked;
        // Reshape the fan model to the selected device's parts (wireless 8/20/16,
        // wired 8/12/0). Part buttons under the model route via SelectLianPart.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LianParts)) ApplyFanParts();
            else if (e.PropertyName == nameof(MainViewModel.SelectedEffectChoice)) SyncPillSelection();
        };
        // Initial state: the VM set the effect and raised LianParts before this
        // pane existed, so both are applied explicitly once.
        SyncPillSelection();
        ApplyFanParts();
    }

    void ApplyFanParts()
    {
        var (c, o, s, sio) = VM.LianFanPartCounts;
        LianFanView.SetParts(c, o, s, sio);
    }

    Window? Owner => Window.GetWindow(this);

    void ConfigureHeaders_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) HeaderConfigDialog.Show(owner, VM);   // blurs + pauses the previews itself
    }

    void ConfigureRazer_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) RazerLayoutDialog.Show(owner, VM);
    }

    void DisableDevice_Click(object sender, RoutedEventArgs e) => VM.DisableSelectedDevice();

    void WakeLights_Click(object sender, RoutedEventArgs e) => VM.WakeLights();

    /*--- All-effects browser: pick applies, star pins to the pills ---*/
    void AllEffects_Click(object sender, RoutedEventArgs e)
    {
        EffectMenuList.ItemsSource = VM.BuildEffectMenu();
        EffectsPopup.IsOpen = true;
    }

    void EffectPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EffectRowVM row)
        {
            VM.SelectedEffectChoice = row.Choice;
            EffectsPopup.IsOpen = false;
        }
    }

    // Pill selection is driven from code-behind (not a SelectedItem binding) so
    // we control WHEN the highlight is applied: a just-picked non-favorite adds a
    // fresh pill, and setting its selection before WPF generates the container
    // silently no-ops. Deferring to Background priority runs after layout, so the
    // container exists and the IsSelected visual actually paints.
    bool _syncingPills;

    void Pills_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPills) return;                       // our own sync, not a user click
        if (PillsList.SelectedItem is EffectChoice ec) VM.SelectedEffectChoice = ec;
    }

    void SyncPillSelection() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _syncingPills = true;
        try { PillsList.SelectedItem = VM.SelectedEffectChoice; }
        finally { _syncingPills = false; }
    }), System.Windows.Threading.DispatcherPriority.Background);

    void Pills_PreviewKeyDown(object sender, KeyEventArgs e) => KeyPolicy.MouseFirst(e);

    void TitleStar_Click(object sender, RoutedEventArgs e) => VM.ToggleCurrentEffectFavorite();

    void PaletteLibrary_Click(object sender, RoutedEventArgs e)
        => new PaletteLibraryWindow(VM) { Owner = Owner }.ShowDialog();

    // "+ color" opens the compact picker pop-up (StaysOpen=False: any click
    // outside dismisses it, so no explicit close button is needed).
    void AddPaletteColor_Click(object sender, RoutedEventArgs e) => AddColorPopup.IsOpen = true;

    void EffectStar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EffectRowVM row)
        {
            VM.ToggleFavoriteEffect(row.Name);
            row.IsFavorite = VM.IsFavoriteEffect(row.Name);
        }
    }

    void LianGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string name) VM.SelectLianGroup(name);
    }

    void ArrangeFans_Click(object sender, RoutedEventArgs e)
    {
        int fans = (VM.SelectedDevice as UnifiedRgb.Core.Devices.LianLiWireless)?.LedCount / 44 ?? 0;
        if (fans <= 0) return;
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new LianLayoutWindow(fans, () => VM.RescanCommand.Execute(null)) { Owner = owner });
    }

    void LianPart_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag;
        int part;
        if (tag is int i) part = i;
        else if (tag is string s && int.TryParse(s, out int p)) part = p;
        else return;
        VM.SelectLianPart(part, carryPending: true);
    }
}
