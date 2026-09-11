using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.App.Services;
using UnifiedRgb.App.Views;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/*-----------------------------------------------------------*\
| Per-device and per-zone color calibration.                   |
|                                                              |
| The window is deliberately not "six sliders and good luck".  |
| The thing that makes this job possible is the comparison     |
| aid, so it sits ABOVE the sliders: the same flat patch on    |
| every device at once, because the eye has no absolute memory |
| for white and a user trimming one device at a time is        |
| comparing what is in front of them against a remembered      |
| color, which is always wrong.                                |
|                                                              |
| THE LIST IS TWO LEVELS. A motherboard is one device object   |
| and seven unrelated lights: a 30-LED ribbon strip on one     |
| header beside a single accent LED under the chipset. So a    |
| device with more than one zone lists its zones underneath    |
| it, indented, and either level can be selected and trimmed.  |
| A device with only one zone does NOT, because a child row    |
| meaning exactly what its parent means only makes the list    |
| longer.                                                      |
|                                                              |
| A zone with no trim of its own FOLLOWS its device, and the   |
| screen says so in as many words: its sliders open showing    |
| what the device trim is doing to it, the note under them     |
| says it is following, and the moment a slider moves both the |
| note and the row change to say the zone now has its own.     |
| Silence about that fallback would make the sliders look like |
| they had simply failed to save.                              |
|                                                              |
| All state lives in Core's Calibration, a static store keyed  |
| by device name and, for zones, by zone name under it. This   |
| window is therefore a thin editor over it: read with         |
| Effective(), write with Set() or SetZone() on every tick,    |
| and Save() only when a drag ends. That split is not a        |
| micro-optimisation - Set() rebuilds a lookup table and is    |
| cheap, Save() writes a file and a single drag would be a few |
| hundred disk writes.                                         |
|                                                              |
| Nothing here is bound to MainViewModel. Like ExitBehaviorWin |
| this takes plain arguments, because what it needs is a list  |
| of devices and a way to light them, not the whole app state. |
\*-----------------------------------------------------------*/
public partial class CalibrationWindow : Window
{
    readonly CalibrationAid _aid;

    /// <summary>Suppresses the write-back handlers while WE are the ones
    /// moving a control. Every slider and both lists raise the same events
    /// whether a human dragged them or LoadSelected pushed a stored value in,
    /// and without this a mere click on a device would write that device's
    /// own values back over itself.</summary>
    bool _loading;

    /// <summary>Something has been changed since the last Save(). Tracked
    /// rather than saving unconditionally so that opening the window, looking,
    /// and closing it does not rewrite calibration.json.</summary>
    bool _dirty;

    public ObservableCollection<CalDeviceRow> Rows { get; } = new();

    /// <summary>The patch choices, in the order Core cycles them.</summary>
    public IReadOnlyList<RefChoice> References { get; }

    /// <summary>The normal entry point: the devices to offer, and the aid that
    /// will light them. The caller owns the aid because the caller owns the
    /// LightingController it writes through.</summary>
    public CalibrationWindow(IEnumerable<IRgbDevice>? devices, CalibrationAid aid)
    {
        _aid = aid;

        // The purpose rides on the pill itself rather than in a tooltip. Which
        // patch to pick IS the question a user has at this point, and an answer
        // that only appears on hover is an answer most people never see.
        References = CalibrationReferences.Cycle
            .Select(r => new RefChoice(r,
                $"{CalibrationReferences.LabelOf(r)}, {CalibrationReferences.PurposeOf(r)}"))
            .ToList();

        // A device with no LEDs cannot show a patch and cannot be trimmed, so
        // it is dropped here rather than listed as a row that does nothing.
        // This mirrors what CalibrationAid.Start does with the same list.
        foreach (var d in devices ?? Enumerable.Empty<IRgbDevice>())
        {
            if (d == null || d.LedCount <= 0) continue;
            Rows.Add(new CalDeviceRow(d, null));

            // Zone rows only where there is more than one zone. A mouse whose
            // single zone covers the whole device would otherwise grow a child
            // row that is a second name for the row above it, and the user
            // would have two places to set one thing.
            var zones = d.Zones;
            if (zones == null || zones.Count < 2) continue;
            // Keyed, not named. Two zones can be called the same thing, and a
            // row that wrote the shared name would edit its twin as well as
            // itself - see Calibration.ZoneKeys. The key is also what the row
            // DISPLAYS, because two identically labelled rows that behave
            // differently are worse than a suffix.
            var keys = Calibration.ZoneKeys(zones);
            for (int i = 0; i < zones.Count; i++)
                if (zones[i] is RgbZone z && z.Count > 0) Rows.Add(new CalDeviceRow(d, z, keys[i]));
        }

        InitializeComponent();

        // Items are handed over in code rather than bound. There is no view
        // model behind this window, and a DataContext of "this" would buy one
        // shorter line of XAML at the cost of every binding path in the file
        // failing silently at runtime instead of loudly at compile time.
        DeviceList.ItemsSource = Rows;
        RefList.ItemsSource = References;

        // Escape has to be caught at the WINDOW, tunnelling, because the
        // themed Slider and ListBox styles enforce a mouse-first policy and
        // mark every key handled once they have focus. PreviewKeyDown reaches
        // us before they get the chance.
        PreviewKeyDown += Keys;

        _loading = true;
        RefList.SelectedIndex = IndexOfReference(_aid.Reference);
        AidCheck.IsChecked = _aid.Active;
        if (Rows.Count > 0) DeviceList.SelectedIndex = 0;
        _loading = false;

        NoDevicesText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadSelected();
    }

    /// <summary>Convenience for a caller that has a LightingController but no
    /// aid: the window builds its own. It still calls Stop() on the way out,
    /// so standalone callers should supply restoration callbacks on the aid
    /// when they need to resume effects as well as static colors.</summary>
    public CalibrationWindow(IEnumerable<IRgbDevice>? devices, LightingController lighting)
        : this(devices, new CalibrationAid(lighting)) { }

    CalDeviceRow? Current => DeviceList.SelectedItem as CalDeviceRow;

    CalibrationReference CurrentReference =>
        (RefList.SelectedItem as RefChoice)?.Value ?? _aid.Reference;

    int IndexOfReference(CalibrationReference r)
    {
        for (int i = 0; i < References.Count; i++)
            if (References[i].Value == r) return i;
        return 0;
    }

    /*-----------------------------------------------------*\
    | Reading the store into the controls.                   |
    \*-----------------------------------------------------*/

    /// <summary>Pull the selected row's trim into the sliders. For a zone with
    /// no trim of its own that is the DEVICE's trim, because that is what the
    /// zone is really doing right now: opening a following zone on a row of
    /// 1.0s would state, wrongly, that nothing is happening to it.
    ///
    /// Note the gamma slider's floor is 0.3 while the store allows 0.25. The
    /// slider template draws its own value to one decimal place, so the tick
    /// spacing has to divide the range cleanly or the number above the thumb
    /// would disagree with the value actually held. Starting at 0.3 gives
    /// exact tenths all the way up; the 0.25 to 0.3 sliver it gives up is a
    /// difference nobody can see and nobody asks for. A hand-edited 0.25 in
    /// the file therefore shows as 0.3 here, and is only written back if the
    /// user touches something.</summary>
    void LoadSelected()
    {
        var row = Current;
        EditorPanel.IsEnabled = row != null;

        if (row == null)
        {
            SelectedName.Text = Rows.Count == 0 ? "Nothing to calibrate" : "Pick a device or one of its zones";
            SelectedDetail.Text = "";
            ScopeNote.Text = "";
            RefreshSwatches();
            return;
        }

        SelectedName.Text = row.Name;
        SelectedDetail.Text = row.Context;
        ResetOneButton.Content = row.IsZone ? "Reset this zone" : "Reset this device";
        ResetOneButton.ToolTip = row.IsZone
            ? "Drop this zone's own trim so it follows the whole-device trim again"
            : "Back to untrimmed: every LED on this device, zones included, is sent exactly the color you picked";

        var cal = Calibration.Effective(row.Device.Name, row.ZoneName);
        _loading = true;
        GainRSlider.Value = cal.GainR;
        GainGSlider.Value = cal.GainG;
        GainBSlider.Value = cal.GainB;
        GammaSlider.Value = cal.Gamma;
        CapSlider.Value = cal.MaxBrightness;
        _loading = false;

        UpdateScopeNote();
        UpdateRelevance();
        RefreshSwatches();
    }

    /// <summary>Say, in one line, what these five sliders are about to reach
    /// and what is currently reaching the selected row.
    ///
    /// This is the whole override rule in prose, and it has to be on screen
    /// rather than in a tooltip. A zone that is following its device shows
    /// non-default sliders it did not itself set: without the note, moving one
    /// of them would look like it had merely failed to stick, and a device
    /// whose zone is overriding it would look like a device whose sliders had
    /// stopped working on part of itself.</summary>
    void UpdateScopeNote()
    {
        var row = Current;
        if (row == null) { ScopeNote.Text = ""; return; }

        if (row.IsZone)
        {
            int leds = row.Zone!.Count;
            ScopeNote.Text = row.HasOwnTrim
                ? $"This zone has its own trim. It REPLACES the whole-device trim for these {leds} LED(s) rather than stacking on top of it."
                : $"These {leds} LED(s) are following the whole-device trim. Move any slider to give this zone a trim of its own, which then replaces the device trim here.";
            return;
        }

        // On a device row, what matters is the reverse: which parts of it
        // these sliders will NOT reach.
        // From the store rather than by counting rows: the store is what the
        // write path actually consults, and a trim left behind for a zone this
        // device no longer declares still deserves to be mentioned.
        int overridden = Calibration.CalibratedZones(row.Device.Name).Count;
        ScopeNote.Text = overridden == 0
            ? "This trims every LED on the device."
            : $"This trims every LED on the device except the {overridden} zone(s) below that have a trim of their own.";
    }

    /// <summary>Dim the sliders that cannot move anything for the patch that is
    /// up, and say why underneath.
    ///
    /// Dimmed rather than disabled, deliberately. Red gain on a blue patch is
    /// still a setting the user may legitimately want to change while looking
    /// at blue; it just will not show until they switch patches. Disabling it
    /// would trade one confusion for a worse one, so the control stays live and
    /// the screen explains itself instead.</summary>
    void UpdateRelevance()
    {
        var r = CurrentReference;

        Dim(GainRLabel, GainRSlider, CalibrationControl.GainR);
        Dim(GainGLabel, GainGSlider, CalibrationControl.GainG);
        Dim(GainBLabel, GainBSlider, CalibrationControl.GainB);
        Dim(GammaLabel, GammaSlider, CalibrationControl.Gamma);
        Dim(CapLabel, CapSlider, CalibrationControl.Cap);

        GainNote.Text = NoteFor(CalibrationControl.GainR, CalibrationControl.GainG, CalibrationControl.GainB);
        CurveNote.Text = NoteFor(CalibrationControl.Gamma, CalibrationControl.Cap);

        void Dim(System.Windows.Controls.TextBlock label, System.Windows.Controls.Slider slider, CalibrationControl c)
        {
            double o = CalibrationReferences.Affects(r, c) ? 1.0 : 0.4;
            label.Opacity = o;
            slider.Opacity = o;
        }

        // One line per group rather than one per row: three notes stacked under
        // three sliders is more words than the controls they describe.
        string NoteFor(params CalibrationControl[] controls)
        {
            var reasons = controls
                .Select(c => CalibrationReferences.InertBecause(r, c))
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
            return reasons.Count == 0 ? "" : "Dimmed: " + string.Join("; ", reasons) + ".";
        }
    }

    /// <summary>Repaint every swatch from the live store. Called after any
    /// change at all, because a trim on the selected device changes its row in
    /// the list as well as the big preview, and a Reset all changes every row
    /// at once.</summary>
    void RefreshSwatches()
    {
        foreach (var row in Rows) row.Update(_aid.SwatchFor(row.Device, row.ZoneName));

        var asked = CalibrationReferences.ColorOf(CurrentReference);
        PatchSwatch.Background = BrushFor(asked);

        var row2 = Current;
        if (row2 == null)
        {
            TrimSwatch.Background = Brushes.Transparent;
            TrimHex.Text = "";
        }
        else
        {
            // Through the ROW's trim, which for a zone is its own where it has
            // one and its device's where it does not: row2.Name is the zone's
            // name, not a device key, so the device has to be named explicitly.
            var sent = Calibration.Apply(row2.Device.Name, row2.ZoneName, asked);
            TrimSwatch.Background = BrushFor(sent);
            // The hex is here for the device nobody can actually see - a logo
            // inside a closed case - where the swatch on screen is the only
            // feedback there is.
            TrimHex.Text = sent.ToHex();
        }

        StatusText.Text = _aid.Active
            ? "Test patch is on your hardware. Your effects are stopped until this window closes."
            : "Trims are saved per device and survive every profile switch.";
    }

    static Brush BrushFor(Rgb c) => RgbToBrushConverter.Cached(c.R, c.G, c.B);

    /*-----------------------------------------------------*\
    | Writing the controls back into the store.              |
    \*-----------------------------------------------------*/

    /// <summary>Install what the sliders currently say.
    ///
    /// Refresh() on the aid is not optional and not a nicety: calibration is
    /// applied on the way OUT to the hardware, so the patch already sitting on
    /// the device was transformed by the previous lookup table and will not
    /// update itself. Without this the user would drag a slider and see
    /// nothing move.</summary>
    void PushToStore(bool save)
    {
        var row = Current;
        if (row == null) return;

        var cal = new DeviceCalibration
        {
            GainR = GainRSlider.Value,
            GainG = GainGSlider.Value,
            GainB = GainBSlider.Value,
            Gamma = GammaSlider.Value,
            MaxBrightness = CapSlider.Value,
        };
        // A zone row writes a zone entry, which takes over from the device
        // trim for that zone alone. Dragging every slider back to default
        // drops the entry again and the zone goes back to following, which is
        // why the note below is recomputed on every tick and not just on
        // selection.
        if (row.IsZone) Calibration.SetZone(row.Device.Name, row.ZoneName!, cal);
        else Calibration.Set(row.Device.Name, cal);
        _dirty = true;

        _aid.Refresh();
        UpdateScopeNote();
        RefreshSwatches();
        if (save) SaveIfDirty();
    }

    void SaveIfDirty()
    {
        if (!_dirty) return;
        Calibration.Save();
        _dirty = false;
    }

    /*-----------------------------------------------------*\
    | Handlers.                                              |
    \*-----------------------------------------------------*/

    /// <summary>Every tick of a drag. Set() only, never Save(): the table
    /// rebuild is what the eye needs, and the file can wait for the mouse to
    /// come up.</summary>
    void Trim_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        PushToStore(save: false);
    }

    /// <summary>The mouse came up. This is the moment the file is written.
    /// Thumb.DragCompleted rather than a mouse-up on the slider because the
    /// themed slider has IsMoveToPointEnabled, so even a single click on the
    /// track becomes a drag and arrives here.</summary>
    void Trim_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_loading) return;
        PushToStore(save: true);
    }

    void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Leaving a device commits it, so a user who drags and then clicks
        // straight onto the next device never loses the first one.
        SaveIfDirty();
        LoadSelected();
    }

    void Reference_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Show() sets the aid's reference whether or not it is running, which
        // is what lets the on-screen swatches follow the patch selection even
        // with the hardware aid switched off.
        _aid.Show(CurrentReference);
        UpdateRelevance();
        RefreshSwatches();
    }

    void NextPatch_Click(object sender, RoutedEventArgs e)
    {
        var next = _aid.Next();
        // The list has to follow the aid rather than drive it here, and moving
        // its selection would re-enter Reference_Changed and push the patch a
        // second time.
        _loading = true;
        RefList.SelectedIndex = IndexOfReference(next);
        _loading = false;
        UpdateRelevance();
        RefreshSwatches();
    }

    /// <summary>Turn the hardware aid on or off. Starting it stops the effect
    /// channels on every listed device, which is why the flag that tells the
    /// caller to put the profile back is latched here and never cleared.</summary>
    void Aid_Click(object sender, RoutedEventArgs e)
    {
        if (AidCheck.IsChecked == true)
        {
            // Distinct, because a multi-zone device appears in Rows once for
            // itself and once per zone, and the aid takes a list of DEVICES:
            // handing it the same device seven times would stop and repaint it
            // seven times per patch.
            _aid.Start(Rows.Select(r => r.Device).Distinct(), CurrentReference);
        }
        else
        {
            _aid.Stop();
        }
        RefreshSwatches();
    }

    void ResetOne_Click(object sender, RoutedEventArgs e)
    {
        var row = Current;
        if (row == null) return;
        // On a device row this clears its zone trims too. "Reset this device"
        // has to mean the device is sent exactly the color the user picked,
        // and a surviving zone override would leave part of it trimmed by a
        // control they just cleared.
        if (row.IsZone) Calibration.ResetZone(row.Device.Name, row.ZoneName!);
        else Calibration.Reset(row.Device.Name);
        _dirty = true;
        SaveIfDirty();
        _aid.Refresh();
        LoadSelected();
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        // Every device and every zone on the machine, gone from memory AND from
        // calibration.json in one click, with no undo - and this button sits a
        // short distance from the one that resets a single row. Everything else
        // in the app that destroys work at this scale asks first.
        if (!Dialogs.Confirm(this, "Reset every device?",
                "Every color trim you have made, on every device and every zone, "
                + "goes back to untouched. This cannot be undone.",
                "Reset Everything")) return;
        Calibration.ResetAll();
        _dirty = true;
        SaveIfDirty();
        _aid.Refresh();
        LoadSelected();
    }

    /// <summary>Escape closes, which routes through OnClosing like every other
    /// way out. There is no separate teardown path anywhere in this class on
    /// purpose: the aid holding the hardware is exactly the kind of thing that
    /// gets left running by the one exit route somebody forgot.</summary>
    void Keys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    /// <summary>The single teardown. Reached by the X, by Escape, by Done and
    /// by Alt+F4 alike.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        SaveIfDirty();
        // Unconditional, and safe when the aid was never started: leaving a
        // flat white patch burning on somebody's desk because a code path
        // skipped this would be the worst bug this window could have.
        _aid.Stop();
    }

    void List_Keys(object sender, KeyEventArgs e) => KeyPolicy.MouseFirst(e);

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>One row in the list: either a whole device, or one zone of a
/// device, with the color that row currently turns the test patch into.
///
/// One class for both levels rather than two, because the list is a plain
/// ListBox and not a TreeView. The indent is a property on the row, which
/// costs one Thickness and buys a themed list that behaves exactly like every
/// other list in the app; a TreeView here would need its own template, its own
/// selection plumbing and its own scrollbar styling to stop looking like a
/// Windows dialog dropped onto a dark card.
///
/// The swatch is a stored value rather than a computed property because it has
/// to change when the TRIM changes, and a trim lives in a static store that
/// raises nothing.</summary>
public sealed class CalDeviceRow : INotifyPropertyChanged
{
    public IRgbDevice Device { get; }

    /// <summary>The zone this row edits, or null for the whole device.</summary>
    public RgbZone? Zone { get; }

    public bool IsZone => Zone != null;

    /// <summary>The zone's STORE KEY, or null for a device row. This is the
    /// exact shape Calibration's zone-aware calls want, so the window never has
    /// to branch to build an argument. It is the zone's name wherever that name
    /// is unique on the device, which is the ordinary case; where it is not,
    /// Calibration.ZoneKeys has numbered it, and that number is the only thing
    /// separating this row's trim from its twin's.</summary>
    public string? ZoneName { get; }

    public string Name => ZoneName ?? Device.Name;

    public CalDeviceRow(IRgbDevice device, RgbZone? zone, string? zoneKey = null)
    {
        Device = device;
        Zone = zone;
        ZoneName = zone == null ? null : zoneKey ?? zone.Name;
        _swatch = Calibration.Apply(device.Name, ZoneName, Rgb.White);
    }

    Rgb _swatch;
    public Rgb Swatch => _swatch;

    /// <summary>Zone rows are indented under their device. Bound rather than
    /// set in the template because the container style is shared with every
    /// other list in the app and carries its own fixed margin.</summary>
    public Thickness Indent => IsZone ? new Thickness(18, 0, 0, 0) : default;

    /// <summary>Device names lead the list, zone names hang off them.</summary>
    public FontWeight NameWeight => IsZone ? FontWeights.Normal : FontWeights.SemiBold;

    /// <summary>True when this row has a trim stored against it in its own
    /// right, as opposed to inheriting one. On a zone row that is the
    /// difference between "its own" and "following the device".</summary>
    public bool HasOwnTrim => IsZone
        ? Calibration.HasZoneTrim(Device.Name, Zone!.Name)
        : !Calibration.For(Device.Name).IsIdentity;

    /// <summary>The second line of the row. For a device: the vendor, plus a
    /// plain word for "this one has been trimmed", because a user coming back
    /// a month later needs to see what they already touched without clicking
    /// through everything. For a zone: its size, and whether it is following
    /// the device or has broken away from it. The size is there because it is
    /// how somebody recognises which physical thing a name like "Header 2"
    /// refers to: 30 LEDs is the ribbon strip, 1 is an accent.</summary>
    public string Detail
    {
        get
        {
            if (!IsZone)
            {
                string self = HasOwnTrim ? $"{Device.Vendor} · trimmed" : Device.Vendor;
                int zones = Calibration.CalibratedZones(Device.Name).Count;
                return zones == 0 ? self : $"{self} · {zones} zone(s) trimmed";
            }
            string size = Zone!.Count == 1 ? "1 LED" : $"{Zone.Count} LEDs";
            return HasOwnTrim ? $"{size} · own trim" : $"{size} · following the device";
        }
    }

    /// <summary>What the editor puts under the selected row's name: the vendor
    /// for a device, and for a zone the device it belongs to, so the header
    /// never reads as a bare "Chipset Accent" with no clue whose it is.</summary>
    public string Context => IsZone ? $"Zone of {Device.Name}" : Device.Vendor;

    public void Update(Rgb swatch)
    {
        _swatch = swatch;
        Notify(nameof(Swatch));
        // Detail is recomputed from the store on every read, so it only needs
        // the nudge; there is nothing to assign.
        Notify(nameof(Detail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>One entry in the test-patch picker. A record so the pills can bind
/// straight to Label with no converter.</summary>
public sealed record RefChoice(CalibrationReference Value, string Label);
