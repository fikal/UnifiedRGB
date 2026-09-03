using System.Windows;

namespace UnifiedRgb.App;

/// <summary>Whether the main window is actually on screen. The pane view models
/// gate their editor-only refresh work on this as well as on navigation:
/// minimize-to-tray hides the window but leaves the nav selection in place,
/// which used to keep the Cooling sensor sweep and the LCD designer's per-tick
/// pushes running for as long as the app sat in the tray.</summary>
static class MainWindowState
{
    /// <summary>True while the main window is shown - and before it exists, so
    /// startup paths are not gated by it. UI thread only.</summary>
    public static bool Visible => Application.Current?.MainWindow?.IsVisible != false;
}
