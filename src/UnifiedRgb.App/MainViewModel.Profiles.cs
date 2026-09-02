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

// Profiles: capture/restore/save/load — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*\
    | Profiles                                              |
    \*-----------------------------------------------------*/
    // True once colors changed since the selected profile was loaded/saved.
    bool _dirty;
    void MarkDirty() { if (!_initializing) _dirty = true; }

    /// <summary>Snapshot every running effect assignment for saving.</summary>
    List<EffectAssignment> CaptureEffects()
    {
        var list = new List<EffectAssignment>();
        foreach (var fx in _targetFx.Values)
        {
            var ch = fx.Channel;
            if (ch == null || ChoiceOf(fx).Effect == null) continue;
            bool isPattern = ChoiceOf(fx).Effect is PatternEffect;
            bool isRipple = ChoiceOf(fx).Effect is KeyRipple;
            bool isPalette = ChoiceOf(fx).Effect is IPaletteEffect;   // Taichi et al.
            list.Add(new EffectAssignment
            {
                Device = ch.Device.Name, Offset = ch.Offset, Count = ch.Count,
                Effect = ChoiceOf(fx).Name, Speed = fx.Speed, Reverse = fx.Reverse,
                BaseColor = ch.BaseColor.ToString().TrimStart('#'),
                // The ripple's color source rides the pattern fields (it uses
                // the same PatternColor enum and shares the target's palette).
                PatternColor = isPattern ? fx.Pattern?.Color.ToString()
                             : isRipple ? fx.Ripple?.Color.ToString() : null,
                PatternMotion = isPattern ? fx.Pattern?.Motion.ToString() : null,
                PatternDensity = fx.Pattern?.Density ?? 1.0,
                PatternReverse = fx.Pattern?.Reverse ?? false,
                PatternPalette = isPattern || isRipple || isPalette
                    ? fx.Palette.Select(c => c.ToString().TrimStart('#')).ToArray() : null,
            });
        }
        return list;
    }

    /// <summary>Renamed/removed effects, old name -> successor. Consulted for
    /// saved profiles AND the favorites migration (only the pills were migrated
    /// before, so a profile saved under the old name silently lost its effect).</summary>
    static readonly Dictionary<string, string> EffectAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Screen Sync"] = "Wallpaper",
    };

    EffectChoice? ChoiceByName(string name)
        => Effects.FirstOrDefault(e => e.Name == name)
        ?? (EffectAliases.TryGetValue(name, out var alias) ? Effects.FirstOrDefault(e => e.Name == alias) : null);

    /// <summary>Stop everything and start the profile's saved assignments.</summary>
    void RestoreEffects(List<EffectAssignment>? saved)
    {
        _engine.StopAll();
        foreach (var fx in _targetFx.Values) { fx.Channel = null; fx.Choice = Effects[0]; }

        foreach (var a in saved ?? new())
        {
            var dev = Devices.FirstOrDefault(d => d.Name == a.Device);
            var choice = ChoiceByName(a.Effect);
            if (dev == null || choice?.Effect == null) continue;
            if (a.Offset < 0 || a.Count <= 0 || a.Offset + a.Count > dev.LedCount) continue;

            var fx = FxFor(dev, a.Offset, a.Count);
            fx.Choice = choice;
            fx.Speed = a.Speed;
            fx.Reverse = a.Reverse;

            Rgb baseColor = Current;
            try { if (a.BaseColor != null) baseColor = Rgb.FromHex(a.BaseColor); } catch { }

            // Restore per-target settings, then resolve through the ONE shared
            // instance-resolution path (ResolveEffect) — no third copy.
            if (choice.Effect is PatternEffect)
            {
                var pat = PatternOf(fx);
                if (Enum.TryParse<PatternColor>(a.PatternColor, out var pc)) pat.Color = pc;
                if (Enum.TryParse<PatternMotion>(a.PatternMotion, out var pm)) pat.Motion = pm;
                pat.Density = a.PatternDensity;
                pat.Reverse = a.PatternReverse;
                LoadPalette(fx, a.PatternPalette);
            }
            else if (choice.Effect is KeyRipple)
            {
                var rip = RippleOf(fx);
                if (Enum.TryParse<PatternColor>(a.PatternColor, out var rc)) rip.Color = rc;
                LoadPalette(fx, a.PatternPalette);
            }
            else if (choice.Effect is IPaletteEffect)
            {
                LoadPalette(fx, a.PatternPalette);
            }
            var effect = ResolveEffect(fx, choice);

            fx.Channel = _engine.Start(dev, a.Offset, a.Count, FrameFor(dev), effect, SignedSpeed(fx), baseColor);
        }
        NotifyModeChanged();
        RequestLianRebake();
    }

    /// <summary>Prompt-on-close is warranted only when a profile is active and
    /// the colors have drifted from it.</summary>
    public bool NeedsSavePrompt => SelectedProfile != null && _dirty;

    /// <summary>The other close hazard: the lighting was customized but NO
    /// profile exists at all — closing would silently lose everything.</summary>
    public bool NeedsFirstProfilePrompt => Profiles.Count == 0 && _dirty;

    /// <summary>Save the current state as a new profile under the given name
    /// (first-profile close prompt).</summary>
    public void SaveProfileAs(string name)
    {
        ProfileName = string.IsNullOrWhiteSpace(name) ? "My setup" : name.Trim();
        SaveProfile();
    }

    /// <summary>Save the current state back into the active profile (used by
    /// the close prompt — keeps the profile's existing name).</summary>
    public void SaveActiveProfile()
    {
        var active = SelectedProfile;
        if (active == null) return;
        var p = _store.Capture(active.Name, Devices.Select(d => (d, FrameFor(d))), CustomColorsSnapshot(), CaptureEffects());
        int idx = Profiles.IndexOf(active);
        if (idx >= 0) Profiles[idx] = p; else Profiles.Add(p);
        _selectedProfile = p; OnChanged(nameof(SelectedProfile));
        _dirty = false;
    }

    void SaveProfile()
    {
        // Empty name + a selected profile = update that profile in place.
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            SaveActiveProfile();
            return;
        }

        string newName = ProfileName.Trim();
        var prior = SelectedProfile;

        // Renaming the selected profile: replace it instead of duplicating,
        // and carry the startup-profile setting to the new name.
        if (prior != null && !newName.Equals(prior.Name, StringComparison.OrdinalIgnoreCase))
        {
            bool wasStartup = string.Equals(_store.Settings.StartupProfile, prior.Name, StringComparison.OrdinalIgnoreCase);
            _store.Delete(prior.Name);
            Profiles.Remove(prior);
            if (wasStartup) { _store.Settings.StartupProfile = newName; _store.SaveSettings(); }
        }

        var p = _store.Capture(newName, Devices.Select(d => (d, FrameFor(d))), CustomColorsSnapshot(), CaptureEffects());
        var existing = Profiles.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Profiles.Remove(existing);
        Profiles.Add(p);
        SelectedProfile = p;
        ProfileName = "";                       // saved: clear the name box
        _dirty = false;
        OnChanged(nameof(IsStartupProfile));
    }

    /// <summary>Create a NEW profile from the current lighting — never
    /// renames or replaces the selected one (Save does that). A clashing
    /// name gets a numeric suffix rather than silently overwriting.</summary>
    public void SaveProfileAsNew()
    {
        string baseName = string.IsNullOrWhiteSpace(ProfileName) ? "Profile" : ProfileName.Trim();
        string name = baseName;
        for (int n = 2; Profiles.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} {n}";

        var p = _store.Capture(name, Devices.Select(d => (d, FrameFor(d))), CustomColorsSnapshot(), CaptureEffects());
        Profiles.Add(p);
        SelectedProfile = p;
        ProfileName = "";
        _dirty = false;
        OnChanged(nameof(IsStartupProfile));
    }

    void LoadProfile(Profile? p)
    {
        if (p == null) return;
        // Stop the channels FIRST: workers write devices directly, so a final
        // effect frame could land after the static write below and leave a
        // range frozen mid-effect until the next write.
        _engine.StopAll();
        foreach (var d in Devices)
        {
            if (!p.DeviceFrames.TryGetValue(d.Name, out var hex)) continue;
            var frame = FrameFor(d);
            for (int i = 0; i < frame.Length && i < hex.Length; i++)
            {
                try { frame[i] = Rgb.FromHex(hex[i]); } catch { }
            }
            _lighting.PushFrame(d);
        }
        ApplyCustomColors(p.CustomColors);
        RestoreEffects(p.Effects);
        SyncWheelToSelection();     // wheel reflects what the profile applied
        _dirty = false;
    }

    void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        _store.Delete(SelectedProfile.Name);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        ProfileName = "";       // the deleted name lingering in the box invites a confusing re-save
        _dirty = false;
        OnChanged(nameof(IsStartupProfile));
    }

    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
