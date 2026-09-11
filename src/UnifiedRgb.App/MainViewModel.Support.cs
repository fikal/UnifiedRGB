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
        set => SetSetting(_store.Settings.GithubUpdateCheck, value, v => _store.Settings.GithubUpdateCheck = v);
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
    /// <summary>What the app is doing, for the diagnostic bundle. The report
    /// beside it is a hardware survey: it says a keyboard exists, never what
    /// color the keyboard was told to be. Every "my lighting is wrong" thread
    /// needs this half, and it did not exist.
    ///
    /// Runs on the UI thread, so it reads collections directly.</summary>
    public string DescribeState()
    {
        var sb = new System.Text.StringBuilder();
        void Say(string line = "") => sb.AppendLine(line);

        Say($"profile: {SelectedProfile?.Name ?? "(none selected)"}"
            + (_dirty ? "  (unsaved changes)" : ""));
        Say($"startup profile: {_store.Settings.StartupProfile ?? "(none)"}");
        Say($"master brightness: {MasterBrightness:P0}");
        Say($"lights suppressed: {LightsSuppressed}");
        Say($"desk canvas: {(Canvas.Enabled ? $"on, {Canvas.Items.Count} device(s) placed" : "off")}");
        Say();

        Say("devices and what they are showing:");
        foreach (var d in Devices)
        {
            var channels = _engine.ChannelsFor(d);
            string what = channels.Count == 0
                ? "static"
                : string.Join(", ", channels.Select(c =>
                    $"{c.Effect.Name} on [{c.Offset}..{c.Offset + c.Count})"
                    + $" speed {c.Speed:0.##}{(c.Canvas ? " desk-mapped" : "")}"));
            var frame = _lighting.ComposedFrame(d);
            string first = frame.Length > 0 ? frame[0].ToHex() : "------";
            Say($"  {d.Name} ({d.Vendor}, {d.Type}, {d.LedCount} LEDs): {what}; led0 #{first}");
        }
        Say();

        // How each device is ANSWERING, which the list above does not say: a
        // device can be enumerated, be assigned an effect, and be refusing every
        // frame we send it. That is the exact shape of most "my lighting is
        // wrong" reports and the bundle carried nothing about it.
        Say("how devices are answering:");
        var health = UnifiedRgb.Core.DeviceHealth.Shared;
        foreach (var d in Devices)
        {
            int refusals = health.RefusalsOf(d);
            Say($"  {d.Name}: {UnifiedRgb.Core.DeviceHealth.Describe(health.StateOf(d))}"
                + (health.DetailOf(d) is string why ? $" ({why})" : "")
                + (refusals > 0 ? $"; {refusals} refused frame(s) in a row" : ""));
        }
        // And anything health knows about that is not in the list any more. A
        // device that went away mid-session leaves its last state behind, and
        // that state is usually the answer.
        var known = new HashSet<IRgbDevice>(Devices);
        foreach (var r in health.Snapshot())
        {
            if (known.Contains(r.Device)) continue;
            Say($"  {r.Device.Name} (no longer listed): {UnifiedRgb.Core.DeviceHealth.Describe(r.State)}"
                + (r.Detail is string d2 ? $" ({d2})" : ""));
        }
        Say();

        // The devices we could see and could NOT use. This is the half of the
        // picture a bundle never carried: "not detected" was indistinguishable
        // from "held by Synapse" and from "needs administrator", and answering
        // that question has taken days of back and forth more than once.
        var blocked = UnifiedRgb.Core.DetectionNotes.Current;
        Say($"seen but not usable: {(blocked.Count == 0 ? "nothing" : blocked.Count + " item(s)")}");
        foreach (var b in blocked)
        {
            Say($"  {b.What} [{b.Family}]: {b.ReasonText}");
            Say($"      {b.Detail}");
            if (b.Remedy != null) Say($"      fix: {b.Remedy}");
        }
        Say();

        Say("automation:");
        Say($"  by app: {(_store.Settings.AppSwitchEnabled ? "on" : "off")}, "
            + $"{_store.Settings.AutomationRules?.Count ?? 0} rule(s)");
        Say($"  by sensor: {(_store.Settings.SensorRulesEnabled ? "on" : "off")}, "
            + $"{_store.Settings.SensorRules?.Count ?? 0} rule(s)");
        Say($"  schedules: {_store.Settings.Schedules?.Count ?? 0}");
        Say($"  return to startup profile: {_store.Settings.ReturnToStartupProfile}");
        Say($"  lights off when locked: {_store.Settings.LockLightsOff}");
        Say();

        Say("integrations:");
        Say($"  OpenRGB bridge: {(_store.Settings.UseOpenRgb ? "on" : "off")} - {OpenRgbStatus}");
        Say($"  SDK server: {SdkServerStatus}");
        Say($"  CS2 game state: {Cs2Status}");
        Say($"  Chroma sync: {(ChromaSyncEnabled ? "on" : "off")} - {ChromaSyncStatus}");
        Say($"  disabled device families: "
            + ((_store.Settings.DisabledDevices?.Count ?? 0) == 0
               ? "none"
               : string.Join(", ", _store.Settings.DisabledDevices!.Select(e => e.Name))));

        return sb.ToString();
    }

    public async void SendToSupport()
    {
        UploadStatus = "collecting hardware report (~15s)...";
        try
        {
            var (ok, msg) = await Support.SendBundleAsync(SupportNote, s => UploadStatus = s, DescribeState);
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

    // Speed/direction write every range of the selection (one per fan under
    // "All fans + part"); fan 0 alone used to change and the stack desynced.
    public double EffectSpeed
    {
        get => CurrentFx().Speed;
        set
        {
            foreach (var fx in CurrentFxSet())
            {
                fx.Speed = value;
                if (fx.Channel != null) fx.Channel.Speed = SignedSpeed(fx);
            }
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
            foreach (var fx in CurrentFxSet())
            {
                fx.Reverse = value;
                if (fx.Channel != null) fx.Channel.Speed = SignedSpeed(fx);
            }
            RequestLianRebake();
            MarkDirty();
            OnChanged();
        }
    }
}
