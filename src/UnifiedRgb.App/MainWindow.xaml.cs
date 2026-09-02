using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace UnifiedRgb.App;

/// <summary>The shell: custom title bar, window placement, global hotkeys, the
/// tray icon and the close prompt. Everything inside the two cards lives in the
/// pane UserControls under Views/ (each owns its own x:Names and handlers).</summary>
public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly Services.AutomationService _automation;
    WF.NotifyIcon? _tray;
    bool _wasMaximized;
    bool _closing;   // Window.Show() during Closing throws; tray/second-launch must not call it

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closing += Window_Closing;
        _automation = new Services.AutomationService(_vm);
        Closed += (_, _) => { _automation.Dispose(); UnregisterHotkeys(); _tray?.Dispose(); _vm.Dispose(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        StateChanged += Window_StateChanged;

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
        _closing = true;
        if (_vm.NeedsFirstProfilePrompt)
        {
            var (r, name) = Dialogs.AskSaveFirstProfile(this);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; _closing = false; return; }
            if (r == MessageBoxResult.Yes) _vm.SaveProfileAs(name);
        }
        else if (_vm.NeedsSavePrompt)
        {
            var r = Dialogs.AskSaveChanges(this, _vm.SelectedProfile?.Name ?? "");
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; _closing = false; return; }
            if (r == MessageBoxResult.Yes) _vm.SaveActiveProfile();
        }
        SaveWindowPlacement();
    }

    /*-----------------------------------------------------*\
    | Custom title bar + the shell-level buttons             |
    \*-----------------------------------------------------*/
    void EnableDevice_Click(object sender, RoutedEventArgs e) => _vm.EnableSelectedDevice();

    // Open-only: pressing the gear while already on Settings stays put
    // (Back and the nav list are the ways out) — the old toggle bounced
    // users back to the device view.
    void Settings_Click(object sender, RoutedEventArgs e) => _vm.IsSettingsOpen = true;

    void Update_Click(object sender, RoutedEventArgs e) => _vm.InstallUpdate();

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_StateChanged(object? sender, EventArgs e)
    {
        BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
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
        if (_closing) return;   // the save prompt is up mid-close; Show() would throw
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

    /// <summary>Window-level keys: Escape leaves Settings; Delete removes the
    /// selected LCD element (unless a TextBox has focus).</summary>
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
        if (_vm.IsLcdSelected && _vm.Lcd.HasElement)
        {
            _vm.Lcd.DeleteSelectedElement();
            e.Handled = true;
        }
    }
}
