using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.App;

// Auto-update + support upload + admin inbox — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*\
    | Auto-update: check the feed at startup; the title bar |
    | shows an install badge when a newer build exists.     |
    \*-----------------------------------------------------*/
    bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; set { _updateAvailable = value; OnChanged(); } }

    string _updateText = "";
    public string UpdateText { get => _updateText; set { _updateText = value; OnChanged(); } }

    Services.UpdateService? _updateService;
    Services.UpdateService Updates => _updateService ??= new Services.UpdateService(
        t => UpdateText = t, a => UpdateAvailable = a);

    async void CheckForUpdate()
    {
        try { await Updates.CheckAsync(_store.Settings.GithubUpdateCheck); }
        catch (Exception ex) { Log.Error("update", ex); }
    }

    /// <summary>Settings toggle for the public-build GitHub release check.
    /// Only shown when no private feed is configured (feed builds don't use it).</summary>
    public bool GithubUpdateCheck
    {
        get => _store.Settings.GithubUpdateCheck;
        set { _store.Settings.GithubUpdateCheck = value; _store.SaveSettings(); OnChanged(); }
    }
    public bool ShowGithubUpdateToggle => !Backend.Configured;

    public async void InstallUpdate()
    {
        try { await Updates.InstallAsync(); }
        catch (Exception ex) { Log.Error("update", ex); UpdateText = $"update failed: {ex.Message}"; }
    }

    Services.SupportService? _supportService;
    Services.SupportService Support => _supportService ??= new Services.SupportService();

    /// <summary>One-shot support send: full hardware survey + session log +
    /// note, bundled into one report (see SupportService).</summary>
    public async void SendToSupport()
    {
        UploadStatus = "collecting hardware report (~15s)...";
        try
        {
            var (ok, msg) = await Support.SendBundleAsync(SupportNote, s => UploadStatus = s);
            UploadStatus = msg;
            if (ok) SupportNote = "";
        }
        catch (Exception ex)
        {
            Log.Error("support", ex);
            UploadStatus = $"send failed: {ex.Message}";
        }
    }

    /// <summary>Speed lives in the main column for preset effects; the custom
    /// pattern hosts its own speed control in the pattern column.</summary>
    public bool ShowSpeedInMain => IsEffectRunning && !IsCustomPattern
        && (ChoiceOf(CurrentFx()).Effect?.HasSpeed ?? true);
    // Direction rides alongside speed: any animated preset can run in reverse
    // (Custom Pattern keeps its own PatternReverse toggle).
    public bool ShowDirection => ShowSpeedInMain;

    // Palette effects (Taichi) pick their colors from the target's palette, so
    // the palette editor shows in the main effect panel instead of the wheel
    // driving a single base color.
    public bool ShowEffectPalette => IsEffectRunning && ChoiceOf(CurrentFx()).Effect is IPaletteEffect;

    /// <summary>Audio effects react to the music's own tempo, so the slider
    /// controls sensitivity there, not speed — label it honestly.</summary>
    public string SpeedLabel => ChoiceOf(CurrentFx()).Effect switch
    {
        AudioBars or AudioPulse => "Punch",
        TempGlow => "Pulse",
        _ => "Speed",
    };

    /// <summary>Preview only where it's meaningful: the keyboard replica and
    /// fan-zone discs. Generic dot scatter (mobo/mouse) adds nothing.</summary>
    public bool ShowPreview => !_isLcdSelected &&
        (SelectedTarget?.Zone?.IsFan == true || SelectedDevice?.Type == DeviceType.Keyboard);

    /// <summary>Color controls matter for static mode, base-color-tinted effects,
    /// and the custom pattern (palette picking) — not for rainbow generators.</summary>
    public bool ShowColorControls =>
        IsStaticMode
        // Palette effects (Taichi, Gradient, Confetti…) get their colors from the
        // palette strip + pop-up picker instead - the big inline wheel would just
        // duplicate it and bury the palette at the bottom of a busy column.
        || (ChoiceOf(CurrentFx()).Effect is { UsesBaseColor: true } and not IPaletteEffect)
        || IsCustomPattern
        || (IsKeyRipple && RippleOf(CurrentFx()).Color != PatternColor.Rainbow);   // Solid tints, Gradient feeds the palette
    public bool IsEffectRunning => ChoiceOf(CurrentFx()).Effect != null;
    public bool IsCustomPattern => ChoiceOf(CurrentFx()).Effect is PatternEffect;

    public double EffectSpeed
    {
        get => CurrentFx().Speed;
        set
        {
            var fx = CurrentFx();
            fx.Speed = value;
            if (fx.Channel != null) fx.Channel.Speed = SignedSpeed(fx);
            RequestLianRebake();
            MarkDirty();
            OnChanged();
        }
    }

    /// <summary>Reverse the direction of the current effect (all effects).</summary>
    public bool EffectReverse
    {
        get => CurrentFx().Reverse;
        set
        {
            var fx = CurrentFx();
            fx.Reverse = value;
            if (fx.Channel != null) fx.Channel.Speed = SignedSpeed(fx);
            RequestLianRebake();
            MarkDirty();
            OnChanged();
        }
    }
}
