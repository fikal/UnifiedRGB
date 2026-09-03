using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace UnifiedRgb.App;

/// <summary>App-themed dialogs (the stock MessageBox clashes with the dark UI).
/// Built in code so they need no shared resource dictionary. The two save
/// prompts share one shell/button factory — they used to be ~85% copy-paste,
/// including a verbatim duplicate of the button builder.</summary>
public static class Dialogs
{
    /// <summary>Blur the owner window while a modal is up, so the dialog
    /// pops and the busy UI behind it recedes. Restores on close.</summary>
    public static void ShowBlurred(Window? owner, Window dialog)
    {
        // Never blur/ShowDialog against a window that's mid-close - WPF throws
        // "Cannot set Visibility... while a Window is closing". Fall back to a
        // plain modal (or nothing if there's no live owner).
        bool canBlur = owner != null && owner.IsLoaded && owner.IsVisible;
        var root = canBlur ? owner!.Content as UIElement : null;
        var prev = root?.Effect;
        bool prevPause = LedPreview.GlobalPause;
        if (root != null) root.Effect = new BlurEffect { Radius = 9 };
        // The previews' timers gate on IsVisible, which stays true behind a
        // modal: pause them so each tick doesn't re-run the blur over the whole
        // window for the dialog's lifetime.
        LedPreview.GlobalPause = true;
        try { dialog.ShowDialog(); }
        finally { if (root != null) root.Effect = prev; LedPreview.GlobalPause = prevPause; }
    }

    /*-----------------------------------------------------*\
    | Shared shell: chrome window + dark card + drag + keys |
    | (also the shell of HeaderConfigDialog - it used to    |
    | carry its own copies of this and Btn)                 |
    \*-----------------------------------------------------*/
    internal static (Window Win, StackPanel Body) MakeDialog(Window owner, Action onEscape, Action? onEnter = null)
    {
        var win = new Window
        {
            Owner = owner.IsVisible ? owner : null,
            WindowStartupLocation = owner.IsVisible
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Topmost = true,
        };

        var body = new StackPanel { MinWidth = 340 };
        win.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x27)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x31, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(16),   // room for the drop shadow
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            Child = body,
        };

        // Text boxes must keep their own mouse handling; anywhere else drags.
        win.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) try { win.DragMove(); } catch { } };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) onEscape();
            if (e.Key == Key.Enter) onEnter?.Invoke();
        };
        return (win, body);
    }

    internal static UIElement Btn(string text, bool accent, Action click)
    {
        var normal = accent ? Color.FromRgb(0x4C, 0x6F, 0xFF) : Color.FromRgb(0x3A, 0x3D, 0x48);
        var hover = accent ? Color.FromRgb(0x63, 0x83, 0xFF) : Color.FromRgb(0x4A, 0x4E, 0x5C);
        var b = new Border
        {
            Background = new SolidColorBrush(normal),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16, 9, 16, 9),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text, Foreground = Brushes.White, FontSize = 13,
                FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
            },
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(hover);
        b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(normal);
        // Preview (tunneling) + Handled: commits before the window-level
        // drag handler can capture the mouse.
        b.PreviewMouseLeftButtonDown += (_, e2) => { e2.Handled = true; click(); };
        return b;
    }

    static TextBlock Title(string text) => new()
    {
        Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
    };

    static TextBlock Message(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xAC, 0xB8)),
        Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
    };

    /// <summary>"Save changes to profile X?" — Yes = save, No = discard,
    /// Cancel = abort the close.</summary>
    public static MessageBoxResult AskSaveChanges(Window owner, string profileName)
    {
        var result = MessageBoxResult.Cancel;
        Window win = null!;
        void Done(MessageBoxResult r) { result = r; win.Close(); }

        (win, var body) = MakeDialog(owner,
            onEscape: () => Done(MessageBoxResult.Cancel),
            onEnter: () => Done(MessageBoxResult.Yes));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(Btn("Cancel", false, () => Done(MessageBoxResult.Cancel)));
        buttons.Children.Add(Btn("Don't Save", false, () => Done(MessageBoxResult.No)));
        buttons.Children.Add(Btn("Save", true, () => Done(MessageBoxResult.Yes)));

        body.Children.Add(Title("Save changes?"));
        body.Children.Add(Message($"Profile “{profileName}” has unsaved changes."));
        body.Children.Add(buttons);

        ShowBlurred(owner, win);
        return result;
    }

    /// <summary>First-run close guard: the lighting was customized but no
    /// profile exists, so closing would lose everything. Lets the user name
    /// and save a profile right in the prompt. Yes = save under the returned
    /// name, No = discard, Cancel = abort the close.</summary>
    public static (MessageBoxResult Result, string Name) AskSaveFirstProfile(Window owner)
    {
        var result = MessageBoxResult.Cancel;
        Window win = null!;
        void Done(MessageBoxResult r) { result = r; win.Close(); }

        (win, var body) = MakeDialog(owner,
            onEscape: () => Done(MessageBoxResult.Cancel),
            onEnter: () => Done(MessageBoxResult.Yes));

        var nameBox = new TextBox
        {
            Text = "My setup",
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x32)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x48)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(Btn("Cancel", false, () => Done(MessageBoxResult.Cancel)));
        buttons.Children.Add(Btn("Don't Save", false, () => Done(MessageBoxResult.No)));
        buttons.Children.Add(Btn("Save profile", true, () => Done(MessageBoxResult.Yes)));

        body.Children.Add(Title("Save your setup?"));
        body.Children.Add(Message("You haven't saved a profile yet. Closing now loses your colors and effects. Name it and hit Save profile:"));
        body.Children.Add(nameBox);
        body.Children.Add(buttons);

        win.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };

        ShowBlurred(owner, win);
        return (result, nameBox.Text);
    }
}
