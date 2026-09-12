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
    void MarkDirty(bool lightingChanged = true)
    {
        if (_initializing) return;
        _dirty = true;
        if (lightingChanged) _appliedProfile = null;
    }

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
                BaseColor = ch.BaseColor.ToHex(),
                // The ripple's color source rides the pattern fields (it uses
                // the same PatternColor enum and shares the target's palette).
                PatternColor = isPattern ? fx.Pattern?.Color.ToString()
                             : isRipple ? fx.Ripple?.Color.ToString() : null,
                PatternMotion = isPattern ? fx.Pattern?.Motion.ToString() : null,
                PatternDensity = fx.Pattern?.Density ?? 1.0,
                PatternReverse = fx.Pattern?.Reverse ?? false,
                PatternPalette = isPattern || isRipple || isPalette
                    ? fx.Palette.Select(c => c.ToHex()).ToArray() : null,
                Canvas = ch.Canvas,
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

    EffectChoice? ChoiceByName(string? name)
        => string.IsNullOrEmpty(name) ? null   // "Effect": null in a hand-edited profile (TryGetValue throws on null)
        : Effects.FirstOrDefault(e => e.Name == name)
        ?? (EffectAliases.TryGetValue(name, out var alias) ? Effects.FirstOrDefault(e => e.Name == alias) : null);

    /// <summary>Stop everything and start the profile's saved assignments.</summary>
    void RestoreEffects(List<EffectAssignment>? saved)
    {
        _engine.StopAll();
        foreach (var fx in _targetFx.Values) { fx.Channel = null; fx.Choice = Effects[0]; }

        // Null-tolerant throughout: this runs from the constructor for the
        // startup profile, and a well-formed profiles.json with a null entry
        // ("Effects": [null], "Device": null) must not stop the app launching.
        foreach (var a in saved ?? new())
        {
            if (a == null) continue;
            var dev = Devices.FirstOrDefault(d => d.Name == a.Device);
            var choice = ChoiceByName(a.Effect);
            if (dev == null || choice?.Effect == null) continue;
            if (a.Offset < 0 || a.Count <= 0 || (long)a.Offset + a.Count > dev.LedCount) continue;

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

            // With the desk switched on, everything renders across it: that is
            // what the switch says and what people expect from it. CanvasPositions
            // returns null when the desk is off or this device has no place on
            // it, so the effect then runs per-device exactly as before.
            fx.Channel = EffectBlockedByClient(dev) ? null
                       : _engine.Start(dev, a.Offset, a.Count, FrameFor(dev), effect,
                                       SignedSpeed(fx), baseColor,
                                       CanvasPositions(dev, a.Offset, a.Count));
        }
        NotifyModeChanged();
        RequestLianRebake();
    }

    /*-----------------------------------------------------*\
    | The screen in the case.                                |
    |                                                        |
    | A profile already carried the lights and the pump LCD.  |
    | This is the third panel, and it rides on the same       |
    | switch so "go to Night" means the whole desk.           |
    \*-----------------------------------------------------*/

    /// <summary>The picker's "do not touch it" row. A string rather than a null
    /// item because a WPF ComboBox of strings cannot show a null usefully, and
    /// because the row deserves to say what it does.</summary>
    public const string NoWallpaper = "Leave the wallpaper alone";

    public ObservableCollection<string> WallpaperProfiles { get; } = new();

    string _wallpaperChoice = NoWallpaper;
    public string WallpaperChoice
    {
        get => _wallpaperChoice;
        set
        {
            // A null or blank arriving here is NOT a choice. It is the combo box
            // saying its list changed underneath it: clearing a bound collection
            // makes a selector drop its selection and push null back through the
            // binding. Reading that as "leave the wallpaper alone" is the same
            // mistake that let a rename wipe the choice, one layer down - and
            // here it also left the box blank, because the source then held a
            // value the target had already abandoned.
            //
            // Choosing the "leave the wallpaper alone" row sends that row's
            // text. Nothing a person can do sends null.
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_wallpaperChoice == value) return;
            _wallpaperChoice = value;
            MarkDirty(lightingChanged: false);
            OnChanged();
        }
    }

    /// <summary>Both halves matter. Wallpaper Engine installed but with no
    /// profiles saved is the messy case: we would be offering to bind something
    /// the user has not made yet, and the only honest thing the picker could
    /// list is nothing. The card says what to do instead.</summary>
    public bool WallpaperAvailable
        => Services.WallpaperEngine.Installed &&
           (Services.WallpaperEngine.Profiles.Count > 0 || _wallpaperChoice != NoWallpaper);

    public bool WallpaperNeedsProfiles
        => Services.WallpaperEngine.Installed && Services.WallpaperEngine.Profiles.Count == 0;

    /// <summary>Re-read what Wallpaper Engine has and rebuild the picker. Cheap
    /// and timestamp-guarded underneath, so calling it when the settings pane is
    /// opened costs a file stat on the common path.</summary>
    public void RefreshWallpaperProfiles()
    {
        var found = Services.WallpaperEngine.Profiles.ToList();
        // A setup moved from another machine can name a wallpaper that is not
        // installed here. Keep that choice until the user explicitly clears it.
        if (_wallpaperChoice != NoWallpaper && !found.Contains(_wallpaperChoice, StringComparer.Ordinal))
            found.Add(_wallpaperChoice);

        // Rebuilt only when the contents actually DIFFER. This runs every time
        // the settings page is shown and the answer is nearly always the same
        // list, so the usual cost should be nothing - but more than that,
        // clearing a bound collection is not free: the combo box drops its
        // selection, and its own reset is posted rather than immediate, so it
        // can land after the notification that would have restored it and leave
        // the control blank. The version with no race in it is the one that does
        // not touch the collection.
        if (!Matches(found))
        {
            WallpaperProfiles.Clear();
            WallpaperProfiles.Add(NoWallpaper);
            foreach (string p in found) WallpaperProfiles.Add(p);
        }

        OnChanged(nameof(WallpaperAvailable));
        OnChanged(nameof(WallpaperNeedsProfiles));
        OnChanged(nameof(WallpaperChoice));
    }

    /// <summary>Whether the picker already lists exactly these profiles, in this
    /// order, behind the "leave it alone" row.</summary>
    bool Matches(IReadOnlyList<string> found)
    {
        if (WallpaperProfiles.Count != found.Count + 1) return false;
        if (WallpaperProfiles[0] != NoWallpaper) return false;
        for (int i = 0; i < found.Count; i++)
            if (!string.Equals(WallpaperProfiles[i + 1], found[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /*--- the pump panel, as a picker rather than a ritual ---*/

    /// <summary>A row in the pump picker. A typed item rather than a decorated
    /// string because a screen and a show are allowed to share a name, and
    /// parsing a suffix back out of display text to tell them apart is how that
    /// becomes a bug. A record, so a rebuilt list still contains an item EQUAL
    /// to the selected one and the combo box keeps its selection.</summary>
    /// <summary>A row in the pump-panel picker. A record, so a rebuilt list still
    /// contains an item EQUAL to the selected one and the combo box keeps its
    /// selection.</summary>
    public sealed record PumpRow(string Label, string? Screen)
    {
        public override string ToString() => Label;
    }

    public const string NoPump = "Leave the pump panel alone";
    public const string NoShow = "No show";
    static readonly PumpRow PumpNone = new(NoPump, null);

    public ObservableCollection<PumpRow> PumpRows { get; } = new();
    public ObservableCollection<string> ShowRows { get; } = new();

    PumpRow _pumpChoice = PumpNone;
    public PumpRow PumpChoice
    {
        get => _pumpChoice;
        set
        {
            // Null is the control saying its list moved, never a choice. Picking
            // the "leave it alone" row sends that row; nothing a person can do
            // sends null.
            if (value == null || _pumpChoice == value) return;
            _pumpChoice = value;
            MarkDirty(lightingChanged: false);
            OnChanged();
        }
    }

    string _showChoice = NoShow;
    public string ShowChoice
    {
        get => _showChoice;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _showChoice == value) return;
            _showChoice = value;
            MarkDirty(lightingChanged: false);
            OnChanged();
        }
    }

    public bool PumpAvailable => PumpRows.Count > 1;
    public bool ShowAvailable => ShowRows.Count > 1;

    /*--- Two pickers, not one.
          They were one exclusive control at first - a screen OR a show - on the
          grounds that a panel can hold a picture or run a sequence but not both.
          That is true of the panel and false of the PROFILE, and the difference
          showed up the first time somebody built the obvious thing: a profile
          that starts a show AND is a step inside it. Applied by hand it should
          start the show; reached as a step it should say what the panel shows.
          With one control it could only say one of those, so the step displayed
          nothing and the previous step's screen stayed up.
          They are two questions: what does this profile put on the panel, and
          does this profile start a show. ---*/

    public void RefreshPumpRows()
    {
        var wantScreens = new List<PumpRow> { PumpNone };
        foreach (string name in Lcd.SceneNames) wantScreens.Add(new PumpRow(name, name));

        var wantShows = new List<string> { NoShow };
        foreach (var seq in Shows.Shows) if (seq != null) wantShows.Add(seq.Name);

        if (_pumpChoice.Screen is string missingScreen && !wantScreens.Any(r =>
            string.Equals(r.Screen, missingScreen, StringComparison.OrdinalIgnoreCase)))
            wantScreens.Add(new PumpRow(missingScreen + " (unavailable)", missingScreen));
        if (_showChoice != NoShow && !wantShows.Contains(_showChoice, StringComparer.OrdinalIgnoreCase))
            wantShows.Add(_showChoice);

        // Rebuilt only when the contents differ: clearing a bound collection
        // makes the combo box drop its selection, and its reset is posted rather
        // than immediate, so it can land after the notification meant to restore
        // it and leave the control blank.
        if (!PumpRows.SequenceEqual(wantScreens))
        {
            PumpRows.Clear();
            foreach (var r in wantScreens) PumpRows.Add(r);
        }
        // OrdinalIgnoreCase, matching the "is it missing?" test above. With the
        // two disagreeing, a show whose name differed only in case counted as
        // present when deciding whether to add it and as different when deciding
        // whether to rebuild. It cancels out today only because SyncPumpChoice
        // always adopts the row's own spelling.
        if (!ShowRows.SequenceEqual(wantShows, StringComparer.OrdinalIgnoreCase))
        {
            ShowRows.Clear();
            foreach (var r in wantShows) ShowRows.Add(r);
        }

        // A name that has since been deleted would leave the control on a row
        // that is no longer in its list, which a ComboBox shows as blank.
        if (!PumpRows.Contains(_pumpChoice))
            _pumpChoice = PumpRows.FirstOrDefault(r => string.Equals(r.Screen, _pumpChoice.Screen, StringComparison.OrdinalIgnoreCase)) ?? PumpNone;
        if (!ShowRows.Contains(_showChoice)) _showChoice = NoShow;

        OnChanged(nameof(PumpAvailable));
        OnChanged(nameof(ShowAvailable));
        OnChanged(nameof(PumpChoice));
        OnChanged(nameof(ShowChoice));
    }

    void SyncPumpChoice(Profile? p)
    {
        if (p == null) return;   // a deselect says nothing about either
        // Preserve unavailable bindings until the user explicitly clears them.
        if (!string.IsNullOrWhiteSpace(p.Screen) && !PumpRows.Any(r =>
            string.Equals(r.Screen, p.Screen, StringComparison.OrdinalIgnoreCase)))
            PumpRows.Add(new PumpRow(p.Screen + " (unavailable)", p.Screen));
        if (!string.IsNullOrWhiteSpace(p.Show) && !ShowRows.Contains(p.Show, StringComparer.OrdinalIgnoreCase))
            ShowRows.Add(p.Show);
        _pumpChoice = PumpRows.FirstOrDefault(r =>
                          string.Equals(r.Screen, p.Screen, StringComparison.OrdinalIgnoreCase))
                      ?? PumpNone;
        _showChoice = ShowRows.FirstOrDefault(r =>
                          !string.Equals(r, NoShow, StringComparison.Ordinal)
                          && string.Equals(r, p.Show, StringComparison.OrdinalIgnoreCase))
                      ?? NoShow;
        // The rows just added are what makes each picker VISIBLE (both
        // Available flags are Count > 1, bound to Visibility). Without saying
        // so, a machine with no screens and no shows of its own - a setup
        // moved from another PC, the case this preservation exists for - left
        // both pickers hidden for the rest of the session, so the user could
        // not see or clear the binding this code just worked to keep. Nothing
        // else re-runs RefreshPumpRows on a selection change.
        // SyncWallpaperChoice already does this for the same reason.
        OnChanged(nameof(PumpAvailable));
        OnChanged(nameof(ShowAvailable));
        OnChanged(nameof(PumpChoice));
        OnChanged(nameof(ShowChoice));
    }

    /// <summary>What a save records for the panel and the show. Null when this
    /// machine has neither to offer, so a profile that came from one that did
    /// keeps what it carries.</summary>
    PumpTarget? PumpForSave
        => PumpRows.Count <= 1 && ShowRows.Count <= 1
           ? null
           : new PumpTarget(_pumpChoice.Screen, _showChoice == NoShow ? null : _showChoice);

    void SyncWallpaperChoice(Profile? p)
    {
        // A DESELECT says nothing about the wallpaper, so it must not reset the
        // picker. This is not hypothetical tidiness: a rename removes the old
        // profile from the bound collection before it reads what to save, and a
        // WPF selector whose selected item leaves its list pushes null back
        // through the binding. Treating that null as "this profile has no
        // wallpaper" wiped the user's choice and then saved the wipe. The name
        // box beside it already ignored null for the same reason.
        if (p == null) return;
        _wallpaperChoice = string.IsNullOrWhiteSpace(p.Wallpaper) ? NoWallpaper : p.Wallpaper!;
        if (!WallpaperProfiles.Contains(NoWallpaper)) WallpaperProfiles.Insert(0, NoWallpaper);
        if (!WallpaperProfiles.Contains(_wallpaperChoice)) WallpaperProfiles.Add(_wallpaperChoice);
        OnChanged(nameof(WallpaperAvailable));
        OnChanged(nameof(WallpaperChoice));
    }

    /// <summary>What a Save should store. Three answers, not two:
    ///
    /// NULL when Wallpaper Engine is not installed here - this machine has no
    /// opinion, so a profile that came from a machine that DID must keep its
    /// wallpaper rather than have it quietly stripped by a save on a laptop.
    ///
    /// EMPTY when the user picked "leave the wallpaper alone" on purpose, which
    /// has to be able to clear a name they set earlier.
    ///
    /// The name otherwise.</summary>
    string? WallpaperForSave
        => !Services.WallpaperEngine.Installed ? null
         : _wallpaperChoice == NoWallpaper ? ""
         : _wallpaperChoice;

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
    /// the close prompt — keeps the profile's existing name).
    ///
    /// While a SHOW is running the desk is not the user's edit: it is whatever
    /// step is up, and a step is somebody else's profile. Capturing it here wrote
    /// that step's colors into the profile the user had selected - on the close
    /// prompt, and with no prompt at all from Session_Ending, which is a logoff
    /// quietly rewriting a saved setup. So the lighting is left out of the capture
    /// and carries over from the profile itself, through the same "device absent
    /// right now" path BuildCapture already uses: every device is absent in the
    /// sense that matters, because none of them is showing this profile. The
    /// swatches and the screen/show/wallpaper choices ARE the user's and are
    /// still saved, which is what a step must not be allowed to cost them.</summary>
    public void SaveActiveProfile()
    {
        var active = SelectedProfile;
        if (active == null) return;
        bool showOwnsLighting = Shows.Running;
        var frames = showOwnsLighting
            ? Enumerable.Empty<(IRgbDevice, Rgb[])>()
            : Devices.Select(d => (d, FrameFor(d)));
        if (showOwnsLighting)
            UnifiedRgb.Core.Log.Info("profiles",
                $"saved '{active.Name}' without its colors: a show is running, so the lighting on the desk belongs to its current step");
        var p = _store.TryCapture(active.Name, frames, CustomColorsSnapshot(),
                                   showOwnsLighting ? null : CaptureEffects(), PumpForSave,
                                   wallpaper: WallpaperForSave);
        if (p == null) { NoteSaveFailed(active.Name); return; }
        int idx = Profiles.IndexOf(active);
        if (idx >= 0) Profiles[idx] = p; else Profiles.Add(p);
        // Through the setter: replacing the item made the bound combo push null
        // through it (raising IsStartupProfile/CanDeleteProfile for "nothing
        // selected"), and a direct field write afterwards left Delete greyed
        // and the startup checkbox stale until the profile was re-picked.
        SelectedProfile = p;
        _dirty = false;
        // The lighting now IS this profile - that is what saving means - so
        // say so rather than clearing it. Nulling it cost ApplyStep its "do not
        // re-apply what is already on" skip, so a show whose next step named the
        // just-saved profile visibly restarted every effect channel; and AddStep
        // defaulted a new step to the first profile in the list instead of the
        // lit one. The exception is a save taken while a SHOW is running: the
        // colors were not captured then (see above), so the lighting still
        // belongs to whatever step is up.
        if (!showOwnsLighting) _appliedProfile = p.Name;
    }

    void SaveProfile()
    {
        // Empty name + a selected profile = update that profile in place.
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            SaveActiveProfile();
            return;
        }

        // Everything this save depends on is read BEFORE the collections are
        // touched, because touching them re-enters through the bindings. The
        // name and the prior profile were already latched here for that reason;
        // the wallpaper was read at the call site below, after the rename had
        // removed the selected item, and a selector pushing null back over that
        // removal was enough to change the answer.
        string newName = ProfileName.Trim();
        var prior = SelectedProfile;
        string? wallpaper = WallpaperForSave;
        // Latched for the same reason as the wallpaper, rather than left as an
        // argument that happens to be evaluated before the first mutation. It is
        // correct today only because of that evaluation order; one reordering
        // breaks it silently.
        var pump = PumpForSave;

        var renamedFrom = prior != null && !newName.Equals(prior.Name, StringComparison.OrdinalIgnoreCase) ? prior : null;
        // Persist the new profile while the old name is still valid. No view or
        // saved reference changes when writing the replacement fails.
        var p = _store.TryCapture(newName, Devices.Select(d => (d, FrameFor(d))), CustomColorsSnapshot(), CaptureEffects(), pump,
                               carryFrom: renamedFrom, wallpaper: wallpaper);
        if (p == null) { NoteSaveFailed(newName); return; }
        var existing = Profiles.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Profiles[Profiles.IndexOf(existing)] = p;
        else Profiles.Add(p);
        // Both names are available while bound step/rule dropdowns move to the
        // replacement. Removing the old item first clears TwoWay selections.
        if (renamedFrom != null)
        {
            if (RenameProfileReferences(renamedFrom.Name, p.Name)
                && _store.TryRemoveRenamedProfile(renamedFrom.Name)) Profiles.Remove(renamedFrom);
            else
                // The new profile is written and the old one is still there, which
                // is the right way round to fail - nothing on disk is inconsistent.
                // But the user asked to RENAME and now sees two, and a Log.Warn is
                // not somewhere they will look.
                UnifiedRgb.Core.Automation.ActivityLog.Note(UnifiedRgb.Core.Automation.ActivityKind.Problem,
                    $"'{renamedFrom.Name}' could not be renamed to '{p.Name}' - the new profile was saved, "
                    + "but the old one is still there. Check that the settings folder is writable, then delete it.");
        }
        SelectedProfile = p;
        ProfileName = "";                       // saved: clear the name box
        _dirty = false;
        _appliedProfile = p.Name;   // the lighting IS this profile now
        OnChanged(nameof(IsStartupProfile));
    }

    /// <summary>A save that could not be written. Every one of these paths used
    /// to just return, so the button appeared to do nothing and the only record
    /// was a line in a log file the user has no reason to open. TrySave has
    /// already said WHY in the log; this says THAT, somewhere they can see.</summary>
    static void NoteSaveFailed(string name) =>
        UnifiedRgb.Core.Automation.ActivityLog.Note(UnifiedRgb.Core.Automation.ActivityKind.Problem,
            $"'{name}' could not be saved - profiles.json could not be written. "
            + "Check that the settings folder is writable and not locked by another program.");

    /// <summary>Create a NEW profile from the current lighting — never
    /// renames or replaces the selected one (Save does that). A clashing
    /// name gets a numeric suffix rather than silently overwriting.</summary>
    public void SaveProfileAsNew()
    {
        string baseName = string.IsNullOrWhiteSpace(ProfileName) ? "Profile" : ProfileName.Trim();
        string name = baseName;
        for (int n = 2; Profiles.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} {n}";

        var p = _store.TryCapture(name, Devices.Select(d => (d, FrameFor(d))), CustomColorsSnapshot(), CaptureEffects(), PumpForSave,
                               wallpaper: WallpaperForSave);
        if (p == null) { NoteSaveFailed(name); return; }
        Profiles.Add(p);
        SelectedProfile = p;
        ProfileName = "";
        _dirty = false;
        _appliedProfile = p.Name;   // the lighting IS this profile now
        OnChanged(nameof(IsStartupProfile));
    }

    /// <summary>Every profile apply funnels through here: the button, a
    /// hotkey, an automation rule, a scene step. It used to log nothing at all,
    /// so a bundle could not answer "which profile was on" for any moment in a
    /// three week log.</summary>
    /// <param name="fromShow">This apply is a step of a running show. The
    /// profile's own SHOW binding is ignored then, and a running show is not
    /// stopped by the profile's screen: the show is the thing driving, and a
    /// step must not be able to swap the show out from under itself.</param>
    void LoadProfile(Profile? p, bool fromShow = false)
    {
        if (p == null) return;
        _recoveryLighting.ApplyProfile(p);
        // Stop the channels FIRST: workers write devices directly, so a final
        // effect frame could land after the static write below and leave a
        // range frozen mid-effect until the next write.
        _engine.StopAll();
        foreach (var d in Devices)
        {
            if (!p.DeviceFrames.TryGetValue(d.Name, out var hex) || hex == null) continue;   // "Device": null keeps its current colors
            // An unparseable entry keeps that LED's current color (as before).
            var frame = FrameFor(d);
            var saved = new Rgb[Math.Min(frame.Length, hex.Length)];
            for (int i = 0; i < saved.Length; i++)
                saved[i] = Rgb.TryFromHex(hex[i], out var c) ? c : frame[i];
            if (_sdkHeld.Contains(d) || _lighting.IsClaimed(d))
            {
                Array.Copy(saved, frame, saved.Length);
                _engine.InvalidateBase(d);
            }
            else RestoreFrame(d, saved);
        }
        ApplyCustomColors(p.CustomColors);
        RestoreEffects(p.Effects);
        SyncWheelToSelection();     // wheel reflects what the profile applied
        // The pump screen too. Every profile apply comes through here - the
        // button, a hotkey, an app rule, a schedule, a show step - so this is
        // the one place that makes a profile mean the whole desk.
        // The profile OWNS the pump panel: a screen, a show, or nothing. Nothing
        // else starts a show any more, which is what stops the panel having two
        // owners that overwrite each other a second apart.
        // The panel and the show are INDEPENDENT. A profile that starts a show
        // usually also wants to say what the panel shows while it is itself a
        // step of that show - which is the ordinary thing to build, and which one
        // exclusive control could not express: the step displayed nothing and the
        // previous step's screen stayed up.
        bool screenShown = false, showStarted = false;

        // A profile that names no show STOPS whatever is running - including from
        // an app rule or a schedule, not just a button. That is what makes a
        // profile mean the whole desk rather than only the part it mentions, and
        // it is why CapturePlayback exists: the automation has to be able to put
        // the show back when the rule ends.
        if (!fromShow && string.IsNullOrWhiteSpace(p.Show)) Shows.Stop();
        if (!string.IsNullOrWhiteSpace(p.Screen))
        {
            // fromShow decides whether this screen becomes the user's canvas or
            // is shown on the show's behalf - see LcdDesignerViewModel.ShowScreen.
            // (The show-stopping that used to be explained here now lives above,
            // because it applies to screenless profiles too.)
            screenShown = Lcd.ShowScreen(p.Screen!, fromShow);
        }

        // Ignored when this apply IS a show step: a step's profile naming a
        // different show would have the show swap itself out mid-run, and one
        // naming its own show would restart it from step one on every pass.
        if (!fromShow && !string.IsNullOrWhiteSpace(p.Show))
            showStarted = Shows.Start(p.Show!);
        // And the screen in the case. Same reasoning as the pump panel: this is
        // the one door every apply comes through - the button, a hotkey, an app
        // rule, a schedule, a show step - so it is the only place that can make
        // a profile mean the whole desk rather than just the LEDs.
        bool wallpaperSent = ApplyWallpaperProfile(p.Wallpaper);
        // Shows change output while the editor remains on the user's selected
        // profile. Its unsaved choices must survive timed steps too.
        if (!fromShow) _dirty = false;
        _appliedProfile = p.Name;
        UnifiedRgb.Core.Log.Info("lighting",
            $"applied profile '{p.Name}': {p.Effects?.Count ?? 0} effect(s) on "
            + $"{p.DeviceFrames?.Count ?? 0} device(s)"
            + (p.Screen == null ? ""
               : screenShown ? $", pump screen '{p.Screen}'" : $", pump screen '{p.Screen}' not shown")
            + (p.Show == null || fromShow ? ""
               : showStarted ? $", show '{p.Show}'" : $", show '{p.Show}' could not start")
            // "asked for", never "showing": the control channel tells us
            // nothing about what the wallpaper did after we sent the request.
            + (p.Wallpaper == null ? ""
               : wallpaperSent ? $", asked for wallpaper '{p.Wallpaper}'"
                               : $", wallpaper '{p.Wallpaper}' could not be asked for"));
    }

    /// <summary>Everything that names this profile, each as a sentence a person
    /// can act on.
    ///
    /// A deleted profile does not break anything loudly: a show step or a rule
    /// that asks for a name nobody has any more is refused and logged, and the
    /// lighting is left alone. That is the problem. The show keeps running, one
    /// of its steps quietly does nothing, and the log is the only place that
    /// says why - which is no use to somebody who will notice weeks later that
    /// the panel skips a beat.
    ///
    /// Shows are named down to the STEP, because "the show Evening" is not
    /// enough to find it among twelve of them. Schedules and rules are listed
    /// too: the question "what breaks if this goes" does not stop at the pump
    /// panel, and answering half of it would send the reader away confident.</summary>
    public IReadOnlyList<string> WhatUsesProfile(string? name)
    {
        var uses = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) return uses;
        bool Is(string? s) => !string.IsNullOrWhiteSpace(s)
                              && s!.Trim().Equals(name!.Trim(), StringComparison.OrdinalIgnoreCase);

        foreach (var seq in Shows.Shows)
        {
            var steps = seq?.Actions;
            if (steps == null) continue;
            for (int i = 0; i < steps.Count; i++)
                if (Is(steps[i]?.Profile))
                    uses.Add($"the show “{seq!.Name}”, step {i + 1}");
        }

        var s = _store.Settings;
        foreach (var r in s.Schedules ?? new())
            if (Is(r?.Profile)) uses.Add($"the schedule {r!.Start}–{r.End}");
        foreach (var r in s.AutomationRules ?? new())
            if (Is(r?.Profile)) uses.Add($"the app rule for {r!.Process}");
        foreach (var r in s.SensorRules ?? new())
            if (Is(r?.Profile)) uses.Add($"the sensor rule on {r!.Source}");
        if (Is(s.StartupProfile)) uses.Add("your startup profile");

        return uses;
    }

    bool RenameProfileReferences(string oldName, string newName)
    {
        bool Matches(string? name) => string.Equals(name?.Trim(), oldName, StringComparison.OrdinalIgnoreCase);
        foreach (var show in Lcd.Scenes.Sequences.Concat(Shows.Shows).Distinct())
            foreach (var action in show.Actions)
                if (Matches(action.Profile)) action.Profile = newName;
        bool scenesSaved = ProfileStore.TrySave(UnifiedRgb.Core.AppPaths.Config("scenes.json"), Lcd.Scenes, "scenes.json");

        // The UI normally shares these objects with Settings, but include both
        // collections so an open editor cannot keep an obsolete reference.
        var settings = _store.Settings;
        foreach (var rule in (settings.AutomationRules ?? new()).Concat(AutoRules).Distinct())
            if (Matches(rule.Profile)) rule.Profile = newName;
        foreach (var rule in (settings.SensorRules ?? new()).Concat(SensorRules).Distinct())
            if (Matches(rule.Profile)) rule.Profile = newName;
        foreach (var rule in (settings.Schedules ?? new()).Concat(Schedules).Distinct())
            if (Matches(rule.Profile)) rule.Profile = newName;
        if (Matches(settings.StartupProfile)) settings.StartupProfile = newName;
        if (Matches(_appliedProfile)) _appliedProfile = newName;
        bool settingsSaved = ProfileStore.TrySave(UnifiedRgb.Core.AppPaths.Config("settings.json"), settings, "settings.json");
        Shows.NotifyProfilesChanged();
        return scenesSaved && settingsSaved;
    }

    public void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        if (!_store.Delete(SelectedProfile.Name))
        {
            // The file still has it, so the list keeps it: a row that vanished
            // and was back at the next launch was the old behaviour.
            UnifiedRgb.Core.Automation.ActivityLog.Note(UnifiedRgb.Core.Automation.ActivityKind.Problem,
                $"'{SelectedProfile.Name}' could not be deleted - profiles.json could not be written. "
                + "Check that the settings folder is writable and not locked by another program.");
            return;
        }
        Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        ProfileName = "";       // the deleted name lingering in the box invites a confusing re-save
        _dirty = false;
        OnChanged(nameof(IsStartupProfile));
    }

    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
