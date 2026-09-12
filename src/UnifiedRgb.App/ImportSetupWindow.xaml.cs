using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>The window between a .urgb file and the user's setup.
///
/// Importing a bundle rewrites profiles, screens, palettes and the desk
/// layout, which is most of what a person has ever built in this app. So this
/// screen exists to make sure nothing is lost by accident: SetupBundle.Preview
/// has already read and validated the whole file WITHOUT writing anything, and
/// what it worked out is shown here, item by item, before Apply is called.
///
/// The conflicts are the reason the window exists. An item that would replace
/// something the user already has gets a red badge, a red border and a red
/// wash, because a subtle tint is exactly the sort of thing a person clicking
/// through a dialog does not see. The safe preset leaves every one of them
/// unticked, so the careless path adds and never destroys.
///
/// The window OWNS nothing else: the file dialog that picked the bundle and
/// the in-memory refresh that has to follow a successful import both belong to
/// the caller, which is why the result is handed back as a property rather
/// than acted on here.</summary>
public partial class ImportSetupWindow : Window
{
    /*--- Shared and frozen. One template binds these for every row in the
          list, so a brush built per row would be pure garbage, and an unfrozen
          brush touched from a template is a lock the render thread does not
          need to take. Internal because the row view models below are what
          actually bind them. ---*/
    internal static readonly Brush NewBrush = Frozen(0x3E, 0x9B, 0x62);
    internal static readonly Brush SameBrush = Frozen(0x50, 0x55, 0x63);
    internal static readonly Brush ConflictBrush = Frozen(0xC0, 0x30, 0x40);
    internal static readonly Brush RowPlainBrush = Frozen(0x26, 0x29, 0x32);
    internal static readonly Brush RowConflictBrush = Frozen(0x3A, 0x26, 0x30);
    internal static readonly Brush BorderConflictBrush = Frozen(0xC0, 0x30, 0x40);
    internal static readonly Brush BorderPlainBrush = Frozen(0x2E, 0x31, 0x40);
    internal static readonly Brush DeviceOkBrush = Frozen(0x9A, 0xA3, 0xB2);
    internal static readonly Brush DeviceRemappedBrush = Frozen(0x8F, 0xA3, 0xFF);
    internal static readonly Brush DeviceMissingBrush = Frozen(0xE0, 0xC1, 0x69);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>What Preview() made of the file. Public because a caller that
    /// wants to log or report the refusal should not have to re-read the
    /// bundle to find out why.</summary>
    public ImportPreview Preview { get; }
    readonly Action<ImportResult>? _afterApply;

    /// <summary>What Apply() did, or null when the user cancelled or the
    /// bundle was refused before anything could be applied. NON-NULL DOES NOT
    /// MEAN SUCCESS: check Result.Ok, and then the ProfilesChanged /
    /// SettingsChanged / ScenesChanged / CanvasChanged flags to know what has
    /// to be reloaded in memory. The running app is still holding what it read
    /// at startup, and its next routine save would put the old data straight
    /// back over the import.</summary>
    public ImportResult? Result { get; private set; }

    /*--- bound collections; DataContext is this window ---*/
    public ObservableCollection<string> SummaryLines { get; } = new();
    public ObservableCollection<string> WarningLines { get; } = new();
    public ObservableCollection<ImportSection> Sections { get; } = new();
    public ObservableCollection<DeviceRemapRow> DeviceRows { get; } = new();
    public ObservableCollection<string> AppliedLines { get; } = new();

    /*--- the rows, kept by category so the ticks can be turned back into an
          ImportChoices without walking the section tree ---*/
    readonly List<ImportRow> _profiles = new();
    readonly List<ImportRow> _screens = new();
    readonly List<ImportRow> _sequences = new();
    readonly List<ImportRow> _palettes = new();
    ImportRow? _currentScreen, _canvas, _rules, _preferences, _machine;

    /// <param name="bundlePath">The .urgb file the caller's open dialog
    /// produced.</param>
    /// <param name="devices">The devices attached right now. Passing them is
    /// what lets the same keyboard be recognised under a different name on
    /// this machine; without them the bundle can only be matched by name and
    /// the preview says so.</param>
    public ImportSetupWindow(string bundlePath, IEnumerable<IRgbDevice>? devices = null, Action<ImportResult>? afterApply = null)
    {
        // Read and validate before the window is built. Preview writes
        // nothing, so a bad file costs the user a sentence rather than a
        // half-applied setup, and the whole window can be laid out from a
        // result that is already known.
        _afterApply = afterApply;
        Preview = SetupBundle.Preview(bundlePath, devices);
        var local = (devices ?? Enumerable.Empty<IRgbDevice>())
            .Select(d => d.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        InitializeComponent();
        DataContext = this;

        PathLine.Text = bundlePath;
        foreach (string line in Preview.Summary) SummaryLines.Add(line);
        foreach (string line in Preview.Warnings) WarningLines.Add(line);
        if (WarningLines.Count > 0) WarningsCard.Visibility = Visibility.Visible;

        if (!Preview.Ok)
        {
            // Nothing here can be chosen, so nothing that implies a choice is
            // shown: the file name, the reason, and a way out.
            SourceLine.Text = "This file could not be read.";
            ProblemText.Text = Preview.Problem ?? "the bundle could not be read";
            ProblemCard.Visibility = Visibility.Visible;
            BodyScroll.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = false;
            return;
        }

        SourceLine.Text = DescribeSource();
        BuildSections();
        BuildDevices(local);

        // The safe set is what the window opens on, so the default answer to
        // "I clicked import without reading" is "you gained things and lost
        // nothing".
        ApplyPreset(Preview.DefaultChoices());
    }

    /// <summary>Where the bundle came from, in one line. The manifest can be
    /// missing a date, and "0001-01-01" reads as a bug rather than as "not
    /// recorded", so it is spelled out instead.</summary>
    string DescribeSource()
    {
        var m = Preview.Manifest;
        if (m == null) return "";
        string when = m.CreatedUtc == default
            ? "an unrecorded date"
            : m.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy, HH:mm");
        string machine = string.IsNullOrWhiteSpace(m.MachineName) ? "an unnamed machine" : m.MachineName;
        string version = string.IsNullOrWhiteSpace(m.AppVersion) ? "UnifiedRGB" : $"UnifiedRGB {m.AppVersion}";
        return $"Made by {version} on {machine}, {when}"
             + (Preview.SameMachine ? " · that is this machine, so this is your own backup" : "");
    }

    /*==========================================================*\
    |  Building the list                                         |
    \*==========================================================*/

    void BuildSections()
    {
        AddSection("PROFILES", "lighting saved per device", Preview.Profiles, _profiles);
        AddSection("SCREENS", "LCD designs", Preview.Screens, _screens);
        AddSection("SHOWS", "timed sequences of screens", Preview.Sequences, _sequences);
        AddSection("PALETTES", "saved color sets", Preview.Palettes, _palettes);

        // The whole-file sections. Each is all or nothing by nature (a desk
        // layout is one arrangement, not a set of independent rows), which is
        // why they are single rows rather than a list.
        var whole = new ImportSection("EVERYTHING ELSE", "each of these is all or nothing");
        _currentScreen = AddSingle(whole, Preview.CurrentScreen);
        _canvas = AddSingle(whole, Preview.Canvas);
        _rules = AddSingle(whole, Preview.Rules);
        _preferences = AddSingle(whole, Preview.Preferences);
        _machine = AddSingle(whole, Preview.MachineHardware);
        if (whole.Rows.Count > 0) Sections.Add(whole);

        // The swatches have no preview row of their own because they cannot
        // conflict: Apply merges them into what is already there. Offered only
        // when the bundle actually carries settings, which is exactly when the
        // preview produced a Preferences row.
        if (Preview.Preferences != null) SwatchCard.Visibility = Visibility.Visible;
    }

    void AddSection(string title, string note, List<ImportItem> items, List<ImportRow> into)
    {
        if (items.Count == 0) return;
        var section = new ImportSection(title, note);
        foreach (var item in items)
        {
            var row = new ImportRow(item);
            into.Add(row);
            section.Rows.Add(row);
        }
        Sections.Add(section);
    }

    ImportRow? AddSingle(ImportSection section, ImportItem? item)
    {
        if (item == null) return null;
        var row = new ImportRow(item);
        section.Rows.Add(row);
        return row;
    }

    void BuildDevices(List<string> localNames)
    {
        if (Preview.Devices.Count == 0) return;
        foreach (var mapping in Preview.Devices)
            DeviceRows.Add(new DeviceRemapRow(mapping, localNames));
        DeviceSection.Visibility = Visibility.Visible;
    }

    /*==========================================================*\
    |  Ticks in, choices out                                     |
    \*==========================================================*/

    /// <summary>Push a preset's answer onto the rows. The device mapping is
    /// deliberately left alone: which keyboard is which is a fact about this
    /// machine, not a preference about what to import, and silently undoing a
    /// mapping the user just corrected because they then pressed "take
    /// everything" would be its own small betrayal.</summary>
    void ApplyPreset(ImportChoices choices)
    {
        Tick(_profiles, choices.Profiles);
        Tick(_screens, choices.Screens);
        Tick(_sequences, choices.Sequences);
        Tick(_palettes, choices.Palettes);
        if (_currentScreen != null) _currentScreen.IsSelected = choices.CurrentScreen;
        if (_canvas != null) _canvas.IsSelected = choices.Canvas;
        if (_rules != null) _rules.IsSelected = choices.Rules;
        if (_preferences != null) _preferences.IsSelected = choices.Preferences;
        if (_machine != null) _machine.IsSelected = choices.MachineHardware;
        SwatchBox.IsChecked = choices.CustomColors;
        UpdateFooter();
    }

    static void Tick(List<ImportRow> rows, HashSet<string> chosen)
    {
        foreach (var row in rows) row.IsSelected = chosen.Contains(row.Name);
    }

    /// <summary>The ticks as SetupBundle understands them.</summary>
    ImportChoices BuildChoices()
    {
        var choices = new ImportChoices();
        foreach (var row in _profiles) if (row.IsSelected) choices.Profiles.Add(row.Name);
        foreach (var row in _screens) if (row.IsSelected) choices.Screens.Add(row.Name);
        foreach (var row in _sequences) if (row.IsSelected) choices.Sequences.Add(row.Name);
        foreach (var row in _palettes) if (row.IsSelected) choices.Palettes.Add(row.Name);
        choices.CurrentScreen = _currentScreen?.IsSelected == true;
        choices.Canvas = _canvas?.IsSelected == true;
        choices.Rules = _rules?.IsSelected == true;
        choices.Preferences = _preferences?.IsSelected == true;
        choices.MachineHardware = _machine?.IsSelected == true;
        choices.CustomColors = SwatchBox.IsChecked == true;

        foreach (var row in DeviceRows)
        {
            string? to = row.ChosenLocalName;
            if (to == null || string.Equals(to, row.BundleName, StringComparison.OrdinalIgnoreCase)) continue;
            // Indexer rather than Add: two identical devices share one name in
            // the bundle, so the same key can legitimately come round twice and
            // Add would throw on the second.
            choices.DeviceRemap[row.BundleName] = to;
        }
        return choices;
    }

    /// <summary>Keep the Import button and the count honest after every tick.
    /// Importing nothing is not a failure, but it is never what the user meant,
    /// so the button says so by being unavailable rather than by succeeding at
    /// doing nothing.</summary>
    void UpdateFooter()
    {
        var choices = BuildChoices();
        bool empty = ImportPreview.IsEmpty(choices);
        ImportButton.IsEnabled = Preview.Ok && !empty;

        if (empty)
        {
            // "Select the safe set" ticking nothing is not a broken button: the
            // safe set is everything this machine is MISSING, and a backup of
            // your own setup is missing nothing. Saying only "nothing selected"
            // made that read as a failure, so distinguish the two cases: the
            // user cleared the list, or there was never anything to add.
            bool anythingToDo = AllRows().Any(r => r.Item.Status != ImportStatus.Same);
            SelectionCount.Text = anythingToDo
                ? "nothing selected"
                : "this backup matches what you already have, so there is nothing to add";
            return;
        }
        int selected = AllRows().Count(r => r.IsSelected);
        int overwrites = AllRows().Count(r => r.IsSelected && r.Item.Status == ImportStatus.Differs);
        SelectionCount.Text = overwrites == 0
            ? $"{selected} selected, nothing of yours is replaced"
            : $"{selected} selected, {overwrites} would REPLACE something of yours";
    }

    IEnumerable<ImportRow> AllRows() => Sections.SelectMany(s => s.Rows);

    /*==========================================================*\
    |  Handlers                                                  |
    \*==========================================================*/

    void Selection_Changed(object sender, RoutedEventArgs e)
    {
        // Guarded on IsLoaded because every row's IsChecked binding fires as
        // its container is generated, which would run the whole recount once
        // per row before the window is even on screen.
        if (IsLoaded) UpdateFooter();
    }

    void SafeSet_Click(object sender, RoutedEventArgs e) => ApplyPreset(Preview.DefaultChoices());

    void Everything_Click(object sender, RoutedEventArgs e) => ApplyPreset(Preview.EverythingChoices());

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var choices = BuildChoices();
        if (!Preview.Ok || ImportPreview.IsEmpty(choices)) return;

        // Apply is synchronous file work on a handful of small JSON files and
        // it is all-or-nothing, so it runs on the UI thread rather than being
        // handed to a task whose failure would have to be marshalled back.
        // The button goes down first so an impatient second click cannot start
        // a second import over the top of the first.
        ImportButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        ImportResult result;
        try
        {
            result = SetupBundle.Apply(Preview, choices);
        }
        catch (Exception ex)
        {
            // Apply reports its failures rather than throwing them, so this
            // should be unreachable. It is here because the alternative to an
            // unreachable catch on a click handler is an unhandled exception
            // that takes the whole app down right after the user pressed the
            // button that rewrites their setup.
            result = new ImportResult { Problem = ex.Message };
        }
        finally
        {
            Cursor = null;
        }

        Result = result;
        // Reload in the same dispatcher turn as Apply, before any old scene
        // timer or debounced save can overwrite the imported files.
        string? reloadProblem = null;
        if (result.Ok)
        {
            try { _afterApply?.Invoke(result); }
            catch (Exception ex)
            {
                reloadProblem = ex.Message;
                Log.Warn("import", $"files imported, but live reload failed: {ex}");
            }
        }
        ShowResult(result);
        if (reloadProblem != null)
        {
            ResultHeading.Text = "Files imported; restart required";
            ResultSubHeading.Text = "The files were saved, but the running setup could not reload. Restart before editing settings or screens.";
            ResultProblemText.Text = reloadProblem;
            ResultProblemCard.Visibility = Visibility.Visible;
            RestartCard.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Swap the chooser for the report. Everything the user needs to
    /// trust what just happened: what changed, where their old files went, and
    /// whether the app has to be restarted for part of it to take.</summary>
    void ShowResult(ImportResult result)
    {
        foreach (string line in result.Applied) AppliedLines.Add(line);

        if (result.Ok)
        {
            bool didSomething = AppliedLines.Count > 0;
            ResultHeading.Text = didSomething ? "Import complete" : "Nothing changed";
            ResultSubHeading.Text = didSomething
                ? "Your setup has been updated from the bundle."
                : result.Problem ?? "Nothing was selected, so nothing was written.";
            AppliedCard.Visibility = didSomething ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ResultHeading.Text = result.RollbackIncomplete ? "Import failed; recovery needed" : "Import failed";
            ResultSubHeading.Text = result.RollbackIncomplete
                ? "Some changes could not be undone. Review the affected files and any saved originals below before editing your setup."
                : "Nothing was changed. Anything already written was put back.";
            ResultProblemText.Text = result.Problem ?? "the import could not be completed";
            ResultProblemCard.Visibility = Visibility.Visible;
            AppliedCard.Visibility = AppliedLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(result.BackupDirectory))
        {
            BackupPathText.Text = result.BackupDirectory;
            BackupCard.Visibility = Visibility.Visible;
        }
        if (result.RestartRecommended) RestartCard.Visibility = Visibility.Visible;

        ChooserPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
    }

    void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        string? dir = Result?.BackupDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return;
        // A folder that will not open is not worth an error dialog on top of a
        // successful import; the path is on screen either way.
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch { }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Done_Click(object sender, RoutedEventArgs e) => Close();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tunnelling, not bubbling: the app's mouse-first button and combo
        // styles mark PreviewKeyDown handled, so a KeyDown handler here would
        // never see Escape once anything had been clicked.
        if (e.Key != Key.Escape) return;
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;   // let the list close first
        e.Handled = true;
        Close();
    }

    void Drag_Down(object sender, MouseButtonEventArgs e)
    {
        // DragMove throws if the button is already up by the time it runs,
        // which happens with a fast click on a busy UI thread.
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}

/*==============================================================*\
|  Row view models                                               |
\*==============================================================*/

/// <summary>One named group of rows.</summary>
public sealed class ImportSection
{
    public string Title { get; }
    public string Note { get; }
    public ObservableCollection<ImportRow> Rows { get; } = new();

    public ImportSection(string title, string note)
    {
        Title = title;
        Note = note;
    }
}

/// <summary>One tickable thing, wrapping the preview's own item.
///
/// The tick lives on the ImportItem rather than beside it, so the row and the
/// thing it describes cannot drift apart.</summary>
public sealed class ImportRow : INotifyPropertyChanged
{
    public ImportItem Item { get; }

    public ImportRow(ImportItem item) => Item = item;

    public string Name => Item.Name;
    public string Detail => Item.Detail;

    public bool IsSelected
    {
        get => Item.Selected;
        set
        {
            if (Item.Selected == value) return;
            Item.Selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>Names the CONSEQUENCE, not the state. "Differs" is accurate and
    /// tells the user nothing; "overwrites yours" is the thing they need to
    /// know before they tick it.</summary>
    public string StatusText => Item.Status switch
    {
        ImportStatus.New => "NEW",
        ImportStatus.Same => "ALREADY HAVE",
        _ => "OVERWRITES",
    };

    public string CheckHint => Item.Status switch
    {
        ImportStatus.New => "Tick to add this. You have nothing of this name, so nothing can be lost.",
        ImportStatus.Same => "You already have this, identical. Importing it changes nothing.",
        _ => "TICK ONLY IF YOU MEAN IT: this replaces what you already have under this name. The old file is backed up first.",
    };

    public Brush StatusBrush => Item.Status switch
    {
        ImportStatus.New => ImportSetupWindow.NewBrush,
        ImportStatus.Same => ImportSetupWindow.SameBrush,
        _ => ImportSetupWindow.ConflictBrush,
    };

    public Brush RowBackground => Item.Status == ImportStatus.Differs
        ? ImportSetupWindow.RowConflictBrush : ImportSetupWindow.RowPlainBrush;

    public Brush RowBorder => Item.Status == ImportStatus.Differs
        ? ImportSetupWindow.BorderConflictBrush : ImportSetupWindow.BorderPlainBrush;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One device in the bundle and where its lighting should land here.
///
/// The whole point of the row is that the user can override it. Identity
/// matching gets this right almost always, but "almost" is not good enough
/// when the cost of being wrong is a keyboard lit with a fan hub's colors.</summary>
public sealed class DeviceRemapRow : INotifyPropertyChanged
{
    /// <summary>Not a device name: the choice to import under whatever name
    /// the bundle used. That is not the same as dropping the data, which is
    /// why it says "keep" rather than "skip".</summary>
    public const string NoMatch = "(keep the bundle's own name)";

    public DeviceMapping Mapping { get; }
    public IReadOnlyList<string> LocalChoices { get; }

    public DeviceRemapRow(DeviceMapping mapping, IReadOnlyList<string> localNames)
    {
        Mapping = mapping;
        var choices = new List<string> { NoMatch };
        choices.AddRange(localNames);
        LocalChoices = choices;
        // Start on what the preview worked out. A local name the bundle
        // matched to something no longer in the list would leave the combo
        // blank, so it falls back to the honest answer instead.
        _selectedLocal = mapping.LocalName != null && choices.Contains(mapping.LocalName, StringComparer.OrdinalIgnoreCase)
            ? choices.First(c => string.Equals(c, mapping.LocalName, StringComparison.OrdinalIgnoreCase))
            : NoMatch;
    }

    public string BundleName => Mapping.BundleName;
    public string BundleDescription => Mapping.BundleDescription;
    public string Detail => Mapping.Detail;

    public Brush DetailBrush => Mapping.LocalName == null
        ? ImportSetupWindow.DeviceMissingBrush
        : Mapping.IsRemap ? ImportSetupWindow.DeviceRemappedBrush : ImportSetupWindow.DeviceOkBrush;

    string _selectedLocal;
    public string SelectedLocal
    {
        get => _selectedLocal;
        set
        {
            // The combo hands back nothing while its items are being rebuilt;
            // taking that would silently clear a mapping the user set.
            string next = string.IsNullOrEmpty(value) ? NoMatch : value;
            if (_selectedLocal == next) return;
            _selectedLocal = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLocal)));
        }
    }

    /// <summary>The local device name to remap onto, or null for "leave it
    /// under the bundle's name".</summary>
    public string? ChosenLocalName => _selectedLocal == NoMatch ? null : _selectedLocal;

    public event PropertyChangedEventHandler? PropertyChanged;
}
