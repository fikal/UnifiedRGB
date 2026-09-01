using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace UnifiedRgb.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly Services.AutomationService _automation;
    WF.NotifyIcon? _tray;
    bool _wasMaximized;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closing += Window_Closing;
        _automation = new Services.AutomationService(_vm);
        Closed += (_, _) => { _automation.Dispose(); UnregisterHotkeys(); _tray?.Dispose(); _vm.Dispose(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        StateChanged += Window_StateChanged;

        PreviewView.Source = _vm.CurrentTargetView;
        PreviewView.LedClicked += _vm.PaintLed;
        PreviewView.LedRightClicked += _vm.ClearLed;
        LianFanView.Source = _vm.LianLiView;
        LianFanView.Clicked += _vm.LianClicked;
        LianFanView.LedRightClicked += _vm.LianRightClicked;
        // Reshape the fan model to the selected device's parts (wireless 8/20/16,
        // wired 8/12/0). Part buttons under the model route via SelectLianPart.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.LianParts))
            {
                var (c, o, s, sio) = _vm.LianFanPartCounts;
                LianFanView.SetParts(c, o, s, sio);
            }
            else if (e.PropertyName == nameof(_vm.SelectedEffectChoice))
                SyncPillSelection();
        };
        SyncPillSelection();   // initial highlight (the VM set the effect before this handler existed)
        WireFanCurveEditor();

        RestoreWindowPlacement();
        InitTray();

        // Launched at login (--autostart) OR the user chose "Start minimized":
        // start in the tray, not on screen — and once detection + the startup
        // profile settle, trim the working set.
        if (Environment.GetCommandLineArgs().Contains("--autostart") || _vm.StartMinimized)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Loaded += (_, _) =>
            {
                Hide();
                Task.Delay(30_000).ContinueWith(_ =>
                {
                    if (!Dispatcher.Invoke(() => IsVisible)) UnifiedRgb.Core.MemoryTrimmer.Trim();
                });
            };
        }

        // First time the window is actually shown (a fresh manual launch, or the
        // user opening it from the tray after an autostart) run the welcome wizard
        // once - guiding a friend past no-devices / missing PawnIO / blank canvas.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _wizardShown || !_vm.NeedsFirstRun) return;
            _wizardShown = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_vm.NeedsFirstRun) new WizardWindow(_vm) { Owner = this }.ShowDialog();
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    bool _wizardShown;

    /*-----------------------------------------------------*\
    | Drag smoothness: freeze the per-frame preview while   |
    | the window is being moved/resized, so the move loop   |
    | isn't starved by constant invalidation (drag judder). |
    \*-----------------------------------------------------*/
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
        {
            src.AddHook(WndProc);
            RegisterHotkeys(src.Handle);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_HOTKEY = 0x0312;
        if (msg == WM_ENTERSIZEMOVE) LedPreview.GlobalPause = true;
        else if (msg == WM_EXITSIZEMOVE) LedPreview.GlobalPause = false;
        else if (msg == WM_HOTKEY) { OnHotkey(wParam.ToInt32()); handled = true; }
        return IntPtr.Zero;
    }

    /*-----------------------------------------------------*\
    | Global hotkeys: Ctrl+Alt+1..9 = apply profile N,       |
    | Ctrl+Alt+Up/Down = master brightness. Registered on    |
    | our (elevated) window, so they work over games too.    |
    \*-----------------------------------------------------*/
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    const int HkProfileBase = 0x5230;        // +0..8 = profiles 1..9
    const int HkBrightUp = 0x5240, HkBrightDown = 0x5241;
    IntPtr _hotkeyHwnd;

    void RegisterHotkeys(IntPtr hwnd)
    {
        _hotkeyHwnd = hwnd;
        for (int i = 0; i < 9; i++)
            RegisterHotKey(hwnd, HkProfileBase + i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)('1' + i));
        RegisterHotKey(hwnd, HkBrightUp, MOD_CONTROL | MOD_ALT, 0x26);     // VK_UP
        RegisterHotKey(hwnd, HkBrightDown, MOD_CONTROL | MOD_ALT, 0x28);   // VK_DOWN
    }

    void UnregisterHotkeys()
    {
        if (_hotkeyHwnd == IntPtr.Zero) return;
        for (int i = 0; i < 9; i++) UnregisterHotKey(_hotkeyHwnd, HkProfileBase + i);
        UnregisterHotKey(_hotkeyHwnd, HkBrightUp);
        UnregisterHotKey(_hotkeyHwnd, HkBrightDown);
    }

    void OnHotkey(int id)
    {
        if (id >= HkProfileBase && id < HkProfileBase + 9)
            _vm.ApplyProfileByIndex(id - HkProfileBase);
        else if (id == HkBrightUp) _vm.NudgeBrightness(+0.10);
        else if (id == HkBrightDown) _vm.NudgeBrightness(-0.10);
    }

    /*-----------------------------------------------------*\
    | Window placement: restore on open, save on close.     |
    \*-----------------------------------------------------*/
    void RestoreWindowPlacement()
    {
        var (b, max) = _vm.GetWindowState();
        _wasMaximized = max;
        if (b is { Length: 4 } && b[2] >= 500 && b[3] >= 350)
        {
            // Only restore a position that's still on some screen.
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var r = new Rect(b[0], b[1], b[2], b[3]);
            if (r.IntersectsWith(vs))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = b[0]; Top = b[1]; Width = b[2]; Height = b[3];
            }
        }
        if (max && !Environment.GetCommandLineArgs().Contains("--autostart"))
            WindowState = WindowState.Maximized;
    }

    void SaveWindowPlacement()
    {
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (r.Width > 0 && r.Height > 0)
            _vm.SaveWindowState(r.Left, r.Top, r.Width, r.Height, _wasMaximized);
    }

    /// <summary>Unsaved work: ask before closing (themed dialogs). Two cases —
    /// no profile exists at all (offer to create one, name box included), or
    /// the active profile has unsaved changes.</summary>
    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // NOTE: never Show()/RestoreFromTray() here - the window is mid-close
        // (WmClose) and that throws "while a Window is closing". The prompts
        // center on screen when the owner isn't visible, so a tray-exit still
        // shows them fine.
        if (_vm.NeedsFirstProfilePrompt)
        {
            var (r, name) = Dialogs.AskSaveFirstProfile(this);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _vm.SaveProfileAs(name);
        }
        else if (_vm.NeedsSavePrompt)
        {
            var r = Dialogs.AskSaveChanges(this, _vm.SelectedProfile?.Name ?? "");
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) _vm.SaveActiveProfile();
        }
        SaveWindowPlacement();
    }

    /*-----------------------------------------------------*\
    | Custom title bar                                       |
    \*-----------------------------------------------------*/
    void ConfigureHeaders_Click(object sender, RoutedEventArgs e)
    {
        var prev = ((UIElement)Content).Effect;
        ((UIElement)Content).Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9 };
        try { HeaderConfigDialog.Show(this, _vm); }
        finally { ((UIElement)Content).Effect = prev; }
    }

    void DisableDevice_Click(object sender, RoutedEventArgs e) => _vm.DisableSelectedDevice();

    void EnableDevice_Click(object sender, RoutedEventArgs e) => _vm.EnableSelectedDevice();

    // Open-only: pressing the gear while already on Settings stays put
    // (Back and the nav list are the ways out) — the old toggle bounced
    // users back to the device view.
    void Settings_Click(object sender, RoutedEventArgs e) => _vm.IsSettingsOpen = true;

    void SettingsBack_Click(object sender, RoutedEventArgs e) => _vm.IsSettingsOpen = false;

    /// <summary>The DEVICES and SYSTEM lists share one SelectedLeftItem, and
    /// a ListBox keeps its old highlight when the shared selection moves to an
    /// item it doesn't contain. A later click on that stale row changes
    /// nothing ("already selected" — no event), so the view never switched.
    /// Same story for re-clicking the current item while Settings is open.
    /// Cure both by making the click itself authoritative: the clicked row
    /// becomes the view, Settings closes, and the sibling list's stale
    /// highlight is cleared.</summary>
    void LeftNav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var item = ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is not LeftItem li || li.IsHeader) return;
        var sibling = ReferenceEquals(lb, DeviceNav) ? SystemNav : DeviceNav;
        sibling.SelectedItem = null;      // pushes null through the shared binding first
        _vm.SelectedLeftItem = li;        // then the clicked row wins
        _vm.IsSettingsOpen = false;
    }

    void IdentifyFan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FanRowModel fan) fan.Identify();
        else if ((sender as FrameworkElement)?.DataContext is SensorRow row) row.Identify();
    }

    void ApplyToAllFans_Click(object sender, RoutedEventArgs e) => _vm.ApplyToAllFans();

    void WakeLights_Click(object sender, RoutedEventArgs e) => _vm.WakeLights();

    /*--- All-effects browser: pick applies, star pins to the pills ---*/
    void AllEffects_Click(object sender, RoutedEventArgs e)
    {
        EffectMenuList.ItemsSource = _vm.BuildEffectMenu();
        EffectsPopup.IsOpen = true;
    }

    void EffectPick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EffectRowVM row)
        {
            _vm.SelectedEffectChoice = row.Choice;
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
        if (PillsList.SelectedItem is EffectChoice ec) _vm.SelectedEffectChoice = ec;
    }

    void SyncPillSelection() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _syncingPills = true;
        try { PillsList.SelectedItem = _vm.SelectedEffectChoice; }
        finally { _syncingPills = false; }
    }), System.Windows.Threading.DispatcherPriority.Background);

    void TitleStar_Click(object sender, RoutedEventArgs e) => _vm.ToggleCurrentEffectFavorite();

    void PaletteLibrary_Click(object sender, RoutedEventArgs e)
        => new PaletteLibraryWindow(_vm) { Owner = this }.ShowDialog();

    // "+ color" opens the compact picker pop-up (StaysOpen=False: any click
    // outside dismisses it, so no explicit close button is needed).
    void AddPaletteColor_Click(object sender, RoutedEventArgs e) => AddColorPopup.IsOpen = true;

    void EffectStar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EffectRowVM row)
        {
            _vm.ToggleFavoriteEffect(row.Name);
            row.IsFavorite = _vm.IsFavoriteEffect(row.Name);
        }
    }

    void LianGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string name) _vm.SelectLianGroup(name);
    }

    void ArrangeFans_Click(object sender, RoutedEventArgs e)
    {
        int fans = (_vm.SelectedDevice as UnifiedRgb.Core.Devices.LianLiWireless)?.LedCount / 44 ?? 0;
        if (fans <= 0) return;
        Dialogs.ShowBlurred(this, new LianLayoutWindow(fans, () => _vm.RescanCommand.Execute(null)) { Owner = this });
    }

    void LianPart_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag;
        int part;
        if (tag is int i) part = i;
        else if (tag is string s && int.TryParse(s, out int p)) part = p;
        else return;
        _vm.SelectLianPart(part, carryPending: true);
    }

    void InstallPawnIo_Click(object sender, RoutedEventArgs e) => _ = _vm.InstallPawnIoAsync();

    void SaveProfileAsNew_Click(object sender, RoutedEventArgs e) => _vm.SaveProfileAsNew();

    void ManageRules_Click(object sender, RoutedEventArgs e)
        => Dialogs.ShowBlurred(this, new AppRulesWindow(_vm) { Owner = this });

    void FanDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.EditingFan is { } f) f.Mode = "Auto";
    }

    /*-----------------------------------------------------*\
    | Fan curve editor: keep the graph in sync with the     |
    | edited fan, push edits back, and animate the live     |
    | marker each cooling refresh.                          |
    \*-----------------------------------------------------*/
    FanRowModel? _hookedFan;

    void WireFanCurveEditor()
    {
        FanCurveGraph.CurveChanged += _ =>
        {
            _vm.EditingFan?.OnCurveEdited();
        };
        _vm.EditingFanChanged += f => Dispatcher.Invoke(() => HookEditingFan(f));
        _vm.CoolingTick += () => Dispatcher.Invoke(UpdateCurveMarker);
        HookEditingFan(_vm.EditingFan);
    }

    void HookEditingFan(FanRowModel? f)
    {
        if (_hookedFan != null) _hookedFan.PropertyChanged -= EditFan_PropertyChanged;
        _hookedFan = f;
        if (f != null)
        {
            f.PropertyChanged += EditFan_PropertyChanged;
            FanCurveGraph.SetFloor(f.CurveFloor);
            FanCurveGraph.SetCurve(f.Curve);
            UpdateCurveMarker();
        }
    }

    void EditFan_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FanRowModel.Curve) && _vm.EditingFan is { } f)
            FanCurveGraph.SetCurve(f.Curve);
    }

    void UpdateCurveMarker()
    {
        var f = _vm.EditingFan;
        if (f != null && f.IsCurve)
            FanCurveGraph.SetLive(UnifiedRgb.Core.Sensors.SensorHub.CurrentTemp(f.Curve.Source), null);
        else
            FanCurveGraph.SetLive(null, null);
    }

    /// <summary>Cooling fan-label editor: Enter commits, Escape reverts —
    /// both hand focus away so the LostFocus binding settles the text.</summary>
    void CoolingLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    /*-----------------------------------------------------*\
    | Mouse-first policy: typing drives the reactive        |
    | effects, so focused controls must never react to      |
    | keys. Tab still moves focus; TextBoxes still type.    |
    \*-----------------------------------------------------*/
    void Pills_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) e.Handled = true;
    }

    /// <summary>Buttons / sliders / checkboxes: no space-activate, no arrow-nudge.</summary>
    void MouseOnly_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) e.Handled = true;
    }

    /// <summary>Closed combo: dead to keys. Open popup keeps arrows/Enter/Esc.</summary>
    void Combo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ComboBox cb && !cb.IsDropDownOpen && e.Key != Key.Tab)
            e.Handled = true;
    }

    void SendSupport_Click(object sender, RoutedEventArgs e) => _vm.SendToSupport();

    void AdminRefresh_Click(object sender, RoutedEventArgs e) => _vm.RefreshAdminReports();

    void AdminCopy_Click(object sender, RoutedEventArgs e) => _vm.CopySelectedAdminReport();

    void AdminDelete_Click(object sender, RoutedEventArgs e) => _vm.DeleteSelectedAdminReport();

    void Update_Click(object sender, RoutedEventArgs e) => _vm.InstallUpdate();

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_StateChanged(object? sender, EventArgs e)
    {
        BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (WindowState == WindowState.Maximized) _wasMaximized = true;
        else if (WindowState == WindowState.Normal) _wasMaximized = false;
        if (WindowState == WindowState.Minimized)
        {
            // Minimize-to-tray, then hand idle pages back to the OS — the app
            // lives hidden most of its life and the UI faults back in cheaply.
            Hide();
            ShowInTaskbar = false;
            Task.Delay(2000).ContinueWith(_ =>
            {
                if (!Dispatcher.Invoke(() => IsVisible)) UnifiedRgb.Core.MemoryTrimmer.Trim();
            });
        }
    }

    /*-----------------------------------------------------*\
    | Tray icon                                              |
    \*-----------------------------------------------------*/
    void InitTray()
    {
        _tray = new WF.NotifyIcon { Text = "UnifiedRGB", Icon = MakeTrayIcon(), Visible = true };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open UnifiedRGB", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    /// <summary>A second launch signaled us instead of starting: surface.</summary>
    internal void SurfaceFromSecondLaunch() => RestoreFromTray();

    /// <summary>RGB gradient dot, drawn at runtime (no asset files).</summary>
    static SD.Icon MakeTrayIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var brush = new SD.Drawing2D.LinearGradientBrush(
                new SD.Rectangle(0, 0, 32, 32),
                SD.Color.FromArgb(255, 0, 96), SD.Color.FromArgb(0, 160, 255), 45f);
            g.FillEllipse(brush, 2, 2, 28, 28);
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.IsSettingsOpen)
        {
            _vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Delete) return;
        if (Keyboard.FocusedElement is TextBox) return;   // don't hijack text editing
        if (_vm.IsLcdSelected && _vm.HasElement)
        {
            _vm.DeleteSelectedElement();
            e.Handled = true;
        }
    }

    /*-----------------------------------------------------*\
    | Pump LCD designer: drag elements to place them.       |
    \*-----------------------------------------------------*/
    LcdElement? _drag;
    Point _dragOrigin;
    double _startX, _startY;

    bool _bgDrag;                 // empty-canvas drag moves the background
    double _bgStartX, _bgStartY;

    void Design_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var item = ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is LcdElement el)
        {
            _vm.SelectedElement = el;
            _drag = el;
            _dragOrigin = e.GetPosition(lb);
            _startX = el.X; _startY = el.Y;
            lb.CaptureMouse();
            return;
        }
        // Pressed empty canvas: drag the background itself.
        if (_vm.LcdHasBackground)
        {
            _bgDrag = true;
            _dragOrigin = e.GetPosition(lb);
            _bgStartX = _vm.LcdBgX; _bgStartY = _vm.LcdBgY;
            lb.CaptureMouse();
        }
    }

    void Design_Move(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (_drag != null)
        {
            var p = e.GetPosition(lb);
            _drag.X = Clamp(_startX + (p.X - _dragOrigin.X), 0, 312);
            _drag.Y = Clamp(_startY + (p.Y - _dragOrigin.Y), 0, 232);
        }
        else if (_bgDrag)
        {
            var p = e.GetPosition(lb);
            // Generous clamp: allow dragging mostly off-screen for framing.
            _vm.LcdBgX = Clamp(_bgStartX + (p.X - _dragOrigin.X), -_vm.LcdBgW + 24, 296);
            _vm.LcdBgY = Clamp(_bgStartY + (p.Y - _dragOrigin.Y), -_vm.LcdBgH + 24, 216);
        }
    }

    void Design_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null && !_bgDrag) return;
        _drag = null; _bgDrag = false;
        if (sender is ListBox lb) lb.ReleaseMouseCapture();
        _vm.TouchLcd();
    }

    /*--- background resize grip (bottom-right corner) ---*/
    bool _gripDrag;
    Point _gripOrigin;
    double _gripStartW, _gripStartH;

    void BgGrip_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _gripDrag = true;
        _gripOrigin = e.GetPosition(this);
        _gripStartW = _vm.LcdBgW; _gripStartH = _vm.LcdBgH;
        fe.CaptureMouse();
        e.Handled = true;
    }

    void BgGrip_Move(object sender, MouseEventArgs e)
    {
        if (!_gripDrag) return;
        var p = e.GetPosition(this);
        double dx = p.X - _gripOrigin.X, dy = p.Y - _gripOrigin.Y;
        if (_vm.LcdBgAspectLock)
            _vm.LcdBgW = _gripStartW + dx;              // setter derives H
        else
        {
            _vm.LcdBgW = _gripStartW + dx;
            _vm.LcdBgH = _gripStartH + dy;
        }
        e.Handled = true;
    }

    void BgGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDrag) return;
        _gripDrag = false;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        _vm.TouchLcd();
        e.Handled = true;
    }

    void BgFill_Click(object sender, RoutedEventArgs e) => _vm.BgFill();
    void BgFit_Click(object sender, RoutedEventArgs e) => _vm.BgFit();
    void BgCenter_Click(object sender, RoutedEventArgs e) => _vm.BgCenter();

    /*--- scenes & sequences ---*/
    void SaveScene_Click(object sender, RoutedEventArgs e) => _vm.SaveScene();
    void DeleteScene_Click(object sender, RoutedEventArgs e) => _vm.DeleteScene();
    void NewSequence_Click(object sender, RoutedEventArgs e) => _vm.NewSequence();
    void DeleteSequence_Click(object sender, RoutedEventArgs e) => _vm.DeleteSequence();
    void AddAction_Click(object sender, RoutedEventArgs e) => _vm.AddSequenceAction();
    void RunSequence_Click(object sender, RoutedEventArgs e) => _vm.ToggleSequence();

    void ActionRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) _vm.RemoveSequenceAction(a);
    }

    void ActionUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) _vm.MoveSequenceAction(a, -1);
    }

    void ActionDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SceneAction a) _vm.MoveSequenceAction(a, +1);
    }

    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
