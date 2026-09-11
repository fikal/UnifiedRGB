using System.Windows;
using System.Windows.Controls;

namespace UnifiedRgb.App.Views;

/// <summary>The gear page: profiles, startup, OpenRGB, PawnIO, Chroma,
/// brightness, automation and support.</summary>
public partial class SettingsPane : UserControl
{
    public SettingsPane()
    {
        InitializeComponent();
        // Ask Wallpaper Engine again whenever this page appears. Someone who
        // reads the hint, alt-tabs over there and makes a profile should find it
        // in the picker when they come back, not after restarting this app. The
        // read underneath is guarded by the config file's timestamp, so the
        // usual answer costs one file stat.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true || DataContext is not MainViewModel vm) return;
            vm.RefreshWallpaperProfiles();
            // Screens and shows are made on another page, so this list can be
            // stale by the time somebody comes back here to bind one.
            vm.RefreshPumpRows();
        };
    }

    MainViewModel VM => (MainViewModel)DataContext;
    Window? Owner => Window.GetWindow(this);

    void SettingsBack_Click(object sender, RoutedEventArgs e) => VM.IsSettingsOpen = false;

    /// <summary>Open a link in the user's browser. UseShellExecute, because a
    /// bare Process.Start of a URL does not launch anything on .NET Core - and
    /// caught, because a machine with no default browser must not take the app
    /// down over a link somebody clicked out of curiosity.</summary>
    void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { UnifiedRgb.Core.Log.Warn("ui", $"could not open {e.Uri}: {ex.Message}"); }
        e.Handled = true;
    }

    void SaveProfileAsNew_Click(object sender, RoutedEventArgs e) => VM.SaveProfileAsNew();

    /// <summary>Delete, but say what the name is still holding up first.
    ///
    /// Nothing breaks loudly when a profile goes: a show step or a rule asking
    /// for a name nobody has is refused, logged, and the lighting is left as it
    /// was. So the show keeps running with one step quietly doing nothing, and
    /// the only record is a log line. Listing what points at it is the whole
    /// difference between noticing now and noticing in a month.
    ///
    /// No prompt when nothing points at it - a confirmation on every delete
    /// trains people to click through the one that mattered.</summary>
    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = VM.SelectedProfile;
        if (profile == null) return;

        var uses = VM.WhatUsesProfile(profile.Name);
        if (uses.Count > 0 && Owner is Window owner)
        {
            string list = string.Join("\n", uses.Select(u => "   • " + u));
            if (!Dialogs.Confirm(owner, $"Delete “{profile.Name}”?",
                    $"{uses.Count} thing{(uses.Count == 1 ? "" : "s")} still ask for it:\n\n{list}\n\n"
                    + "They keep the name and do nothing until you point them somewhere else.",
                    "Delete Anyway"))
                return;
        }
        VM.DeleteProfile();
    }

    async void InstallPawnIo_Click(object sender, RoutedEventArgs e)
    {
        // Awaited: a discarded Task swallowed install failures silently.
        try { await VM.InstallPawnIoAsync(); }
        catch (Exception ex) { UnifiedRgb.Core.Log.Error("pawnio", ex); }
    }

    /// <summary>The recent-decisions history and the automation pause. Takes no
    /// arguments: it reads the one shared activity log and the live automation
    /// service directly, the same way they are written.</summary>
    void Activity_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new ActivityWindow { Owner = owner });
    }

    /// <summary>Per-device color trim. The window drives the hardware itself
    /// with a reference patch. Capture the live state before stopping effects
    /// and restore it, including unsaved edits, when the aid stops.</summary>
    void Calibration_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        MainViewModel.LightState? beforeAid = null;
        var aid = new Services.CalibrationAid(VM.Lighting,
            capture: () => beforeAid = VM.CaptureState(),
            restore: () => { if (beforeAid != null) VM.RestoreState(beforeAid, honorSuppression: true); beforeAid = null; },
            refresh: VM.RefreshCalibrationLighting);
        var win = new CalibrationWindow(VM.Devices, aid) { Owner = owner };
        Dialogs.ShowBlurred(owner, win);
    }

    /// <summary>Write the whole setup to one portable file.</summary>
    void ExportSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save your setup",
            Filter = Services.SetupBundle.FileFilter,
            DefaultExt = Services.SetupBundle.FileExtension,
            FileName = $"UnifiedRGB setup {DateTime.Now:yyyy-MM-dd}{Services.SetupBundle.FileExtension}",
        };
        if (dlg.ShowDialog(Owner) != true) return;

        var result = Services.SetupBundle.Export(dlg.FileName, VM.Devices);
        Dialogs.Info(Owner, "Backup",
            result.Ok
                ? $"Saved {result.FileCount} settings file(s) and {result.AssetCount} image(s) to\n{result.Path}"
                : $"That did not work: {result.Problem}");
    }

    /// <summary>Read a setup file back. The window previews every change and
    /// writes nothing until the user picks, so this only has to reload what the
    /// import replaced - without that, our in-memory copy would be saved back
    /// over the imported files the next time anything changed.</summary>
    void ImportSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a setup file",
            Filter = Services.SetupBundle.FileFilter,
            DefaultExt = Services.SetupBundle.FileExtension,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Owner) != true) return;

        var owner = Owner;
        var win = new ImportSetupWindow(dlg.FileName, VM.Devices, VM.ReloadAfterImport) { Owner = owner };
        Dialogs.ShowBlurred(owner, win);
    }

    void ManageRules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new AppRulesWindow(VM) { Owner = owner });
    }

    void ManageSchedules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new SchedulesWindow(VM) { Owner = owner });
        VM.RefreshScheduleSummary();
    }

    void ManageSensorRules_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new SensorRulesWindow(VM) { Owner = owner });
    }

    void ArrangeDesk_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new CanvasWindow(VM) { Owner = owner });
    }

    void InstallCs2_Click(object sender, RoutedEventArgs e)
        => Dialogs.Info(Owner, "Counter-Strike 2", VM.InstallCs2Config());

    /// <summary>For when writing the file failed, or the game lives somewhere
    /// we cannot write: show exactly what to save and where.</summary>
    void ShowCs2Config_Click(object sender, RoutedEventArgs e)
    {
        var folders = UnifiedRgb.Core.Games.GsiConfig.Cs2CfgFolders();
        string where = folders.Count > 0
            ? string.Join(Environment.NewLine, folders)
            : @"<Steam library>\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg";
        string body = $"Save this as {UnifiedRgb.Core.Games.GsiConfig.FileName} in:"
                    + Environment.NewLine + where
                    + Environment.NewLine + Environment.NewLine
                    + VM.Cs2ConfigText();
        Dialogs.Info(Owner, "gamestate_integration_unifiedrgb.cfg", body, preformatted: true);
    }

    void ManageExitBehavior_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Dialogs.ShowBlurred(owner, new ExitBehaviorWindow(VM) { Owner = owner });
    }

    void SendSupport_Click(object sender, RoutedEventArgs e) => VM.SendToSupport();
}
