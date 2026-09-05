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


/// <summary>A selectable apply-target: the whole device, or one zone.</summary>
public sealed class TargetItem
{
    public required string Name { get; init; }
    public RgbZone? Zone { get; init; }             // null = entire device
    public override string ToString() => Name;
}

/// <summary>A fan-editor part button: display name + the part id passed to
/// SelectLianPart (-1 all fans, 0 whole fan, 1..N the device's parts).</summary>
public readonly record struct LianPartButton(string Name, int Tag);


public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    readonly DeviceManager _manager = new();
    readonly ProfileStore _store = new();
    // Engine + static frames + applier live in the controller (it owns HOW a
    // write reaches the hardware; the partials decide WHAT). The aliases keep
    // the partials' engine/applier calls readable.
    readonly Services.LightingController _lighting = new();
    EffectEngine _engine => _lighting.Engine;
    CoalescingApplier _applier => _lighting.Applier;
    static object LaneOf(IRgbDevice d) => Services.LightingController.LaneOf(d);
    Rgb[] FrameFor(IRgbDevice d) => _lighting.FrameFor(d);
    Rgb[] ComposedFrame(IRgbDevice dev) => _lighting.ComposedFrame(dev);
    readonly Services.LianBakeService _bake;

    /*-----------------------------------------------------*\
    | Per-target effect state: every device/zone remembers  |
    | its own mode, speed, and pattern settings, and runs   |
    | its own engine channel — so the fans can rainbow      |
    | while the keyboard waves, simultaneously.             |
    \*-----------------------------------------------------*/
    sealed class TargetFx
    {
        public EffectChoice? Choice;
        public PatternEffect? Pattern;
        public KeyRipple? Ripple;
        public Taichi? Taichi;
        // Per-target instances of any other palette effect (Gradient, Confetti,
        // ...), so each target keeps its own colors instead of sharing the one
        // prototype in the effect list.
        public Dictionary<Type, IEffect> PaletteInstances = new();
        public ObservableCollection<Rgb> Palette { get; } = new() { new Rgb(255, 0, 96), new Rgb(0, 160, 255) };
        /// <summary>What the effect instances read: a render-thread-safe
        /// snapshot view of Palette (the UI keeps binding to Palette itself).</summary>
        public LivePalette PaletteView { get; }
        public TargetFx() => PaletteView = new LivePalette(Palette);
        public double Speed = 1.0;
        public bool Reverse;
        public EffectEngine.Channel? Channel;
    }

    // Direction is expressed as the sign of the speed the engine runs at: a
    // negated clock reverses motion for every time-based effect (sweeps,
    // rotations, hue flow) with no per-effect code.
    static double SignedSpeed(TargetFx fx) => fx.Speed * (fx.Reverse ? -1.0 : 1.0);
    readonly Dictionary<string, TargetFx> _targetFx = new();
    static readonly PatternEffect PatternMarker = new();   // pill identity only; never renders

    public IReadOnlyList<EffectChoice> Effects { get; }

    (IRgbDevice dev, int off, int count)? CurrentTarget()
    {
        var dev = SelectedDevice;
        if (dev == null) return null;
        var z = SelectedTarget?.Zone;
        return (dev, z?.Offset ?? 0, z?.Count ?? dev.LedCount);
    }

    TargetFx CurrentFx()
    {
        var t = CurrentTarget();
        return t == null ? FxForKey("?") : FxFor(t.Value.dev, t.Value.off, t.Value.count);
    }

    /// <summary>The per-range effect state for a device range, created on first
    /// use (was five hand-rolled key-format + TryGetValue copies).</summary>
    TargetFx FxFor(IRgbDevice dev, int off, int count) => FxForKey($"{dev.Name}|{off}|{count}");

    TargetFx FxForKey(string key)
    {
        if (!_targetFx.TryGetValue(key, out var fx)) _targetFx[key] = fx = new TargetFx();
        return fx;
    }

    /// <summary>The target's channel only while the engine still runs it. The
    /// engine's failure breaker and LightsOff stop channels without telling the
    /// VM, which then tinted a dead channel: static picks became silent no-ops.</summary>
    static EffectEngine.Channel? LiveChannel(TargetFx fx)
    {
        if (fx.Channel is { IsRunning: false }) fx.Channel = null;
        return fx.Channel;
    }

    EffectChoice ChoiceOf(TargetFx fx) => fx.Choice ?? Effects[0];

    /// <summary>Every per-range state the current selection stands for: one
    /// TargetFx per fan under "All fans + part" (fan 0's IS CurrentFx()), else
    /// just CurrentFx(). Setters write the whole set; getters read fan 0.</summary>
    IEnumerable<TargetFx> CurrentFxSet()
    {
        if (LianFanOut && SelectedDevice is { } dev)
            foreach (var (off, cnt) in LianApplyRanges()) yield return FxFor(dev, off, cnt);
        else
            yield return CurrentFx();
    }

    /// <summary>Carry speed, direction, the pattern settings (pattern effects)
    /// and the palette (palette-driven effects) from one target's state to
    /// another - All devices and the fan-out share it. Self-copy is a no-op:
    /// Clear()-then-copy from the SAME collection used to wipe the source.</summary>
    static void CopyTargetSettings(TargetFx src, TargetFx dst, EffectChoice choice)
    {
        if (ReferenceEquals(src, dst)) return;
        dst.Speed = src.Speed;
        dst.Reverse = src.Reverse;
        if (choice.Effect is PatternEffect)
        {
            var srcPat = PatternOf(src);
            var dstPat = PatternOf(dst);
            dstPat.Color = srcPat.Color; dstPat.Motion = srcPat.Motion;
            dstPat.Density = srcPat.Density; dstPat.Reverse = srcPat.Reverse;
        }
        if (choice.Effect is PatternEffect or IPaletteEffect) CopyPalette(src, dst);
    }

    /// <summary>Palette only (no speed/direction/pattern settings). Self-copy is
    /// a no-op for the same reason as CopyTargetSettings.</summary>
    static void CopyPalette(TargetFx src, TargetFx dst)
    {
        if (ReferenceEquals(src, dst)) return;
        var pal = src.Palette.ToArray();
        dst.Palette.Clear();
        foreach (var c in pal) dst.Palette.Add(c);
    }

    bool _pickingEffect;   // guards the pill ListBox's re-push while a pick notifies

    public EffectChoice? SelectedEffectChoice
    {
        get => Effects == null ? null : ChoiceOf(CurrentFx());
        set
        {
            if (value == null || _selectionChanging || _pickingEffect) return;
            // Re-entrancy guard: the ListBox re-pushes the current selection
            // whenever its ItemsSource refreshes; same value = nothing to do.
            if (ReferenceEquals(ChoiceOf(CurrentFx()), value)) return;
            CurrentFx().Choice = value;
            // Mode-first flow: remember the user's pick so clicking a fan
            // part right after carries the mode onto that part.
            _lianSel.PendingChoice = value;
            // Picking a NON-favorite makes VisibleEffects rebuild (to add its
            // pill); the pill ListBox then re-pushes its OLD selection (still in
            // the refreshed list) back into this setter, which would overwrite the
            // new pick and revert it. Block that re-push while we notify - the
            // final SelectedEffectChoice notify lands the correct highlight.
            _pickingEffect = true;
            try { NotifyModeChanged(); }
            finally { _pickingEffect = false; }
            ApplyFx();
        }
    }

    /*--- effect favorites: the pills show starred effects (plus Custom
          Pattern and whatever the current target is running); the full
          categorized library lives behind the All effects button ---*/
    static readonly string[] DefaultFavorites =
        { "Static", "Rainbow Wave", "Rainbow Cycle", "Breathing", "Wallpaper" };
    HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);

    public bool IsFavoriteEffect(string name) => _favorites.Contains(name);

    /*--- title row: the current effect's name + a star that favorites it
          (favorites are the pills; a non-favorite effect still runs and is
          edited here, it just doesn't occupy a pill) ---*/
    public string CurrentEffectName => Effects == null ? "" : ChoiceOf(CurrentFx()).Name;
    public string CurrentEffectStar => IsFavoriteEffect(CurrentEffectName) ? "\u2605" : "\u2606";
    public System.Windows.Media.Brush CurrentEffectStarBrush =>
        IsFavoriteEffect(CurrentEffectName) ? EffectRowVM.GoldBrush : EffectRowVM.GrayBrush;
    public void ToggleCurrentEffectFavorite() => ToggleFavoriteEffect(CurrentEffectName);
    /// <summary>Custom Pattern is always a pill - hide its pointless star.</summary>
    public bool ShowCurrentEffectStar => CurrentEffectName != "Custom Pattern";

    public void ToggleFavoriteEffect(string name)
    {
        if (!_favorites.Remove(name)) _favorites.Add(name);
        _store.Settings.FavoriteEffects = _favorites.ToList();
        _store.SaveSettings();
        NotifyModeChanged();
    }

    /// <summary>Categorized browser rows for the current device (keyboard-only
    /// effects hidden elsewhere - Key Ripple has no business on a case fan).</summary>
    // The menu's ~60-row tree is cached per (keyboard, lian) filter state and
    // only favorite flags are refreshed in place on reopen — the old rebuild
    // regenerated every EffectRowVM (and its templated Border/Buttons) per open.
    readonly Dictionary<(bool kb, bool lian), List<EffectCategoryVM>> _effectMenus = new();

    public List<EffectCategoryVM> BuildEffectMenu()
    {
        bool kb = SelectedDevice?.Type == DeviceType.Keyboard;
        bool lian = SelectedDevice is LianLiWireless;
        if (!_effectMenus.TryGetValue((kb, lian), out var menu))
        {
            menu = Effects
                .Where(e => kb || e.Category != "Keyboard")
                .Where(e => lian || e.Category != "Fan stack")   // stack effects need the stack
                .Where(e => e.Category != "Custom")   // Custom Pattern is always a pill
                .GroupBy(e => e.Category)
                .Select(g => new EffectCategoryVM(g.Key,
                    g.Select(e => new EffectRowVM { Choice = e, IsFavorite = _favorites.Contains(e.Name) }).ToList()))
                .ToList();
            _effectMenus[(kb, lian)] = menu;
        }
        else
        {
            foreach (var cat in menu)
                foreach (var row in cat.Items)
                    row.IsFavorite = _favorites.Contains(row.Name);
        }
        return menu;
    }

    /// <summary>Pills for the current device: favorites + Custom Pattern +
    /// the target's currently-assigned effect (so selection can always show),
    /// keyboard-only modes filtered elsewhere.
    /// MUST return a stable instance per filter state — a fresh enumerable
    /// every read makes the ListBox reset its ItemsSource on each notify,
    /// which re-pushes SelectedItem into the setter and recurses to a
    /// stack overflow.</summary>
    // A STABLE collection the pill ListBox binds to. We MUTATE it (add/remove)
    // rather than replace it: a fresh instance each rebuild made WPF reset the
    // ListBox's SelectedItem and drop the highlight for a just-picked effect.
    // Mutating keeps the containers, so the selection highlights reliably.
    public ObservableCollection<EffectChoice> VisibleEffects { get; } = new();
    string _visibleCacheKey = "";

    /// <summary>Bring VisibleEffects in line with the current device + favorites +
    /// the currently-assigned effect (which always earns a pill, even when it's
    /// not a favorite, so a non-favorite selection shows its own highlighted pill
    /// instead of a stale one). No-op when the desired set is unchanged.</summary>
    void RefreshVisibleEffects()
    {
        if (Effects == null) return;
        bool kb = SelectedDevice?.Type == DeviceType.Keyboard;
        bool lian = SelectedDevice is LianLiWireless;
        var curEffect = ChoiceOf(CurrentFx());
        string key = $"{kb}|{lian}|{curEffect.Name}|{string.Join(";", _favorites.OrderBy(x => x))}";
        if (key == _visibleCacheKey) return;
        _visibleCacheKey = key;

        bool Passes(EffectChoice e) => (kb || e.Category != "Keyboard") && (lian || e.Category != "Fan stack");

        // Order: favorites (in Effects order) → the current NON-favorite effect →
        // Custom Pattern always last.
        var desired = new List<EffectChoice>();
        foreach (var e in Effects)
            if (Passes(e) && _favorites.Contains(e.Name) && e.Name != "Custom Pattern") desired.Add(e);
        if (Passes(curEffect) && curEffect.Name != "Custom Pattern" && !_favorites.Contains(curEffect.Name))
            desired.Add(curEffect);
        if (Effects.FirstOrDefault(e => e.Name == "Custom Pattern") is { } custom && Passes(custom))
            desired.Add(custom);

        // Reconcile the STABLE collection to match desired (membership + order),
        // keeping item instances so the ListBox selection survives the update.
        for (int i = VisibleEffects.Count - 1; i >= 0; i--)
            if (!desired.Contains(VisibleEffects[i])) VisibleEffects.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            int at = VisibleEffects.IndexOf(desired[i]);
            if (at < 0) VisibleEffects.Insert(i, desired[i]);
            else if (at != i) VisibleEffects.Move(at, i);
        }
    }

    /// <summary>Refresh every mode-dependent binding for the current target.</summary>
    void NotifyModeChanged()
    {
        RefreshVisibleEffects();
        OnChanged(nameof(SelectedEffectChoice));
        OnChanged(nameof(CurrentEffectName)); OnChanged(nameof(CurrentEffectStar));
        OnChanged(nameof(CurrentEffectStarBrush)); OnChanged(nameof(ShowCurrentEffectStar));
        OnChanged(nameof(IsEffectRunning)); OnChanged(nameof(IsCustomPattern));
        OnChanged(nameof(IsStaticMode)); OnChanged(nameof(ShowColorControls)); OnChanged(nameof(ShowSpeedInMain));
        OnChanged(nameof(SpeedLabel));
        OnChanged(nameof(EffectSpeed));
        OnChanged(nameof(EffectReverse)); OnChanged(nameof(ShowDirection));
        OnChanged(nameof(ShowEffectPalette)); OnChanged(nameof(PatternPalette)); OnChanged(nameof(HasPalette));
        OnChanged(nameof(SelectedPatternColor)); OnChanged(nameof(SelectedPatternMotion));
        OnChanged(nameof(PatternMotions));
        OnChanged(nameof(PatternReverse)); OnChanged(nameof(PatternDensity));
        OnChanged(nameof(IsPaletteMode));   // (PatternPalette already raised above)
        OnChanged(nameof(IsKeyRipple)); OnChanged(nameof(SelectedRippleColor));
        OnChanged(nameof(IsRipplePalette));
    }

    public bool IsStaticMode => ChoiceOf(CurrentFx()).Effect == null;

    /*-----------------------------------------------------*\
    | Settings view (gear icon)                             |
    \*-----------------------------------------------------*/
    bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            _isSettingsOpen = value;
            OnChanged(); OnChanged(nameof(ShowLighting)); OnChanged(nameof(ShowLcdPanel));
            OnChanged(nameof(ShowDisabledPane));
            // Field bug: Cooling stayed visible under Settings — this
            // notify was missing, so the panel never re-evaluated.
            OnChanged(nameof(ShowCoolingPanel));
            // The gated cooling timer self-stops under Settings; closing
            // Settings with Cooling still selected must restart it.
            if (!value && IsCoolingSelected) StartCoolingRefresh();
        }
    }
    public bool ShowLcdPanel => _isLcdSelected && !_isSettingsOpen;

    string _uploadStatus = "";
    public string UploadStatus { get => _uploadStatus; set { _uploadStatus = value; OnChanged(); } }

    string _supportNote = "";
    public string SupportNote { get => _supportNote; set { _supportNote = value; OnChanged(); } }

    public string LogFilePath => Log.FilePath;

    public string AppVersion => AppInfo.VersionText;

    /*-----------------------------------------------------*\
    | Custom pattern (per-target instance)                  |
    \*-----------------------------------------------------*/
    public ICommand AddPaletteColorCommand { get; }
    public ICommand RemovePaletteColorCommand { get; }

    public IReadOnlyList<PatternColor> PatternColorModes { get; } = Enum.GetValues<PatternColor>();

    /// <summary>Motions that make sense for the current color source: Rotate
    /// spins colors around the ring, so it does nothing when every LED is the
    /// same color (Solid, Temp) - hide it there.</summary>
    static readonly IReadOnlyList<PatternMotion> AllMotions = Enum.GetValues<PatternMotion>();
    static readonly IReadOnlyList<PatternMotion> NoRotateMotions =
        Enum.GetValues<PatternMotion>().Where(m => m != PatternMotion.Rotate).ToArray();
    public IReadOnlyList<PatternMotion> PatternMotions =>
        PatternOf(CurrentFx()).Color is PatternColor.Solid or PatternColor.Temp
            ? NoRotateMotions : AllMotions;   // stable instances: no container regen per notify

    static PatternEffect PatternOf(TargetFx fx) => fx.Pattern ??= new PatternEffect { Palette = fx.PaletteView };
    static Taichi TaichiOf(TargetFx fx) => fx.Taichi ??= new Taichi { Palette = fx.PaletteView };

    static KeyRipple RippleOf(TargetFx fx) => fx.Ripple ??= new KeyRipple { Palette = fx.PaletteView };

    /// <summary>A per-target instance of a palette effect (Gradient, Confetti,
    /// Palette Cycle, ...), bound to THIS target's palette so its colors are
    /// independent. The one-off effects (Pattern/Ripple/Taichi) keep their own
    /// typed slots; this covers every other IPaletteEffect generically.</summary>
    static IEffect PaletteInstanceOf(TargetFx fx, EffectChoice choice)
    {
        var type = choice.Effect!.GetType();
        if (!fx.PaletteInstances.TryGetValue(type, out var inst))
        {
            inst = (IEffect)Activator.CreateInstance(type)!;
            ((IPaletteEffect)inst).Palette = fx.PaletteView;   // share this target's colors (snapshot view)
            fx.PaletteInstances[type] = inst;
        }
        return inst;
    }

    /// <summary>THE effect-instance resolution: which instance actually runs for
    /// a choice on a target. This used to exist as three separate copies
    /// (single-target apply, All-devices, profile restore) — the third,
    /// hand-rolled copy is exactly where the "All devices → white" bug lived.</summary>
    static IEffect ResolveEffect(TargetFx fx, EffectChoice choice) => choice.Effect switch
    {
        PatternEffect => PatternOf(fx),
        KeyRipple => RippleOf(fx),
        Taichi => TaichiOf(fx),
        IPaletteEffect => PaletteInstanceOf(fx, choice),   // Gradient, Confetti, Palette Cycle…
        _ => choice.Effect!,
    };

    /// <summary>Replace a target's palette from saved hex strings (no-op when
    /// absent). Was copy-pasted 4x inside RestoreEffects alone.</summary>
    static void LoadPalette(TargetFx fx, string[]? hex)
    {
        if (hex is not { Length: > 0 }) return;
        fx.Palette.Clear();
        foreach (var h in hex) { try { fx.Palette.Add(Rgb.FromHex(h)); } catch { } }
        if (fx.Palette.Count == 0) fx.Palette.Add(new Rgb(255, 255, 255));
    }

    /*-----------------------------------------------------*\
    | Key Ripple settings (per target, like the pattern)    |
    \*-----------------------------------------------------*/
    public bool IsKeyRipple => ChoiceOf(CurrentFx()).Effect is KeyRipple;

    public PatternColor SelectedRippleColor
    {
        get => RippleOf(CurrentFx()).Color;
        set
        {
            RippleOf(CurrentFx()).Color = value;
            MarkDirty(); OnChanged();
            OnChanged(nameof(IsRipplePalette)); OnChanged(nameof(ShowColorControls));
        }
    }

    public bool IsRipplePalette => RippleOf(CurrentFx()).Color == PatternColor.Gradient;

    // The pattern setters read fan 0's instance and write EVERY range of the
    // current selection (one per fan under "All fans + part"), so the stack
    // stays uniform instead of only the representative fan changing.
    public PatternColor SelectedPatternColor
    {
        get => PatternOf(CurrentFx()).Color;
        set
        {
            foreach (var fx in CurrentFxSet())
            {
                PatternOf(fx).Color = value;
                // Switching to Solid: the wheel color becomes the pattern color
                // right away (not whatever stale base the channel started with).
                if (value == PatternColor.Solid && fx.Channel is { } ch) ch.BaseColor = Current;
            }
            // The new color source may not offer the current motion.
            if (!PatternMotions.Contains(PatternOf(CurrentFx()).Motion))
            {
                foreach (var fx in CurrentFxSet()) PatternOf(fx).Motion = PatternMotion.Static;
                OnChanged(nameof(SelectedPatternMotion));
            }
            RequestLianRebake();
            MarkDirty(); OnChanged(); OnChanged(nameof(IsPaletteMode)); OnChanged(nameof(PatternMotions));
        }
    }
    public PatternMotion SelectedPatternMotion { get => PatternOf(CurrentFx()).Motion; set { foreach (var fx in CurrentFxSet()) PatternOf(fx).Motion = value; RequestLianRebake(); MarkDirty(); OnChanged(); } }
    public bool PatternReverse { get => PatternOf(CurrentFx()).Reverse; set { foreach (var fx in CurrentFxSet()) PatternOf(fx).Reverse = value; RequestLianRebake(); MarkDirty(); OnChanged(); } }
    public double PatternDensity { get => PatternOf(CurrentFx()).Density; set { foreach (var fx in CurrentFxSet()) PatternOf(fx).Density = value; RequestLianRebake(); MarkDirty(); OnChanged(); } }
    // Only Gradient uses the palette (Solid = wheel color, ScreenSync = the screen).
    public bool IsPaletteMode => PatternOf(CurrentFx()).Color == PatternColor.Gradient;

    public ObservableCollection<Rgb> PatternPalette => CurrentFx().Palette;
    public bool HasPalette => PatternPalette.Count > 0;

    /*-----------------------------------------------------*\
    | Palette Library: browse presets, apply, save, import  |
    \*-----------------------------------------------------*/
    /// <summary>Presets first, then the user's saved palettes.</summary>
    public IEnumerable<PaletteEntry> AllPalettes()
    {
        foreach (var p in PaletteLibrary.Presets) yield return p;
        foreach (var s in _store.Settings.SavedPalettes ?? new())
        {
            var cols = new List<Rgb>();
            foreach (var h in s.Colors) if (Rgb.TryFromHex(h, out var c)) cols.Add(c);
            if (cols.Count > 0) yield return new PaletteEntry(s.Name, cols.ToArray(), true);
        }
    }

    /// <summary>Replace the current target's palette with these colors, live.</summary>
    public void ApplyPaletteColors(IEnumerable<Rgb> colors)
    {
        var pal = CurrentFx().Palette;
        pal.Clear();
        foreach (var c in colors) pal.Add(c);
        if (pal.Count == 0) pal.Add(new Rgb(255, 255, 255));
        // The running effect holds a reference to this same collection, so it
        // repaints on the next frame; baked fans need an explicit rebake.
        OnChanged(nameof(HasPalette));
        SyncFanOutPalettes();
        RequestLianRebake();
        MarkDirty();
    }

    /// <summary>The palette strip binds to fan 0's palette; under "All fans +
    /// part" every other fan's range gets a copy after each edit so the stack
    /// keeps one palette (each range's LivePalette re-snapshots on change).
    /// Palette ONLY: a fan running its own speed/direction (mixed stack) must
    /// not have its stored Speed/Reverse silently overwritten by a palette
    /// edit while its channel keeps running the old values.</summary>
    void SyncFanOutPalettes()
    {
        if (!LianFanOut) return;
        var src = CurrentFx();
        foreach (var fx in CurrentFxSet()) CopyPalette(src, fx);
    }

    /// <summary>Save the current target's palette under a name for reuse.</summary>
    public void SaveCurrentPaletteAs(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        var colors = CurrentFx().Palette.Select(c => c.ToHex()).ToArray();
        if (colors.Length == 0) return;
        var list = _store.Settings.SavedPalettes ??= new();
        list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));   // overwrite same name
        list.Add(new SavedPalette { Name = name, Colors = colors });
        _store.SaveSettings();
    }

    public void DeleteSavedPalette(string name)
    {
        if (_store.Settings.SavedPalettes?.RemoveAll(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
            _store.SaveSettings();
    }

    /// <summary>Parse colors out of pasted text (coolors URL / hex list) and
    /// apply them; returns how many were found (0 = nothing usable).</summary>
    public int ImportPalette(string text)
    {
        var colors = PaletteLibrary.ParseColors(text);
        if (colors.Length > 0) ApplyPaletteColors(colors);
        return colors.Length;
    }

    /*-----------------------------------------------------*\
    | Custom colors: user swatches shown wherever the wheel |
    | is; stored globally in settings and inside profiles.  |
    \*-----------------------------------------------------*/
    public ObservableCollection<Rgb> CustomColors { get; } = new();
    public ICommand AddCustomColorCommand { get; }
    public ICommand AddCustomElementColorCommand { get; }
    public ICommand RemoveCustomColorCommand { get; }

    void AddCustomColor(Rgb c)
    {
        if (!CustomColors.Contains(c)) { CustomColors.Add(c); PersistCustomColors(); }
    }

    void PersistCustomColors()
    {
        var hex = CustomColors.Select(c => c.ToHex()).ToArray();
        // Every profile apply (scene steps, app rules) routes through here; only
        // touch settings.json when the swatches actually changed.
        if (_store.Settings.CustomColors is { } cur && cur.AsSpan().SequenceEqual(hex)) return;
        _store.Settings.CustomColors = hex;
        _store.SaveSettings();
    }

    string[] CustomColorsSnapshot() => CustomColors.Select(c => c.ToHex()).ToArray();

    void ApplyCustomColors(string[]? hex)
    {
        if (hex == null) return;
        CustomColors.Clear();
        foreach (var h in hex) { try { CustomColors.Add(Rgb.FromHex(h)); } catch { } }
        PersistCustomColors();
    }

    public ObservableCollection<IRgbDevice> Devices { get; } = new();
    public ObservableCollection<LeftItem> DeviceItems { get; } = new();

    /// <summary>Detection came up empty. A first-time user on unsupported
    /// hardware would otherwise face a blank sidebar with nothing to act on,
    /// so the list shows what to try next instead.</summary>
    public bool NoDevicesFound => DeviceItems.Count == 0;
    public ObservableCollection<LeftItem> SystemItems { get; } = new();
    /// <summary>Both nav sections flattened (selection lookups).</summary>
    IEnumerable<LeftItem> AllLeftItems => DeviceItems.Concat(SystemItems);
    public ObservableCollection<TargetItem> Targets { get; } = new();
    public ObservableCollection<Profile> Profiles { get; } = new();

    LeftItem? _selectedLeft;
    public LeftItem? SelectedLeftItem
    {
        get => _selectedLeft;
        set => SelectLeftItem(value, closeSettings: true);
    }

    /// <summary>Make a nav row the current view. A user pick closes Settings;
    /// a rebuild (Rescan re-matching the same row) must not - toggling OpenRGB
    /// or installing PawnIO FROM Settings used to swap the pane away from the
    /// status text the user was waiting on.</summary>
    void SelectLeftItem(LeftItem? value, bool closeSettings)
    {
        _selectedLeft = value; OnChanged(nameof(SelectedLeftItem));
        if (value == null) return;
        if (closeSettings) IsSettingsOpen = false;
        if (value.IsLcd) IsLcdSelected = true;
        else { IsLcdSelected = false; if (value.Device != null) SelectedDevice = value.Device; }
        // Start the refresh timer only while Cooling is on screen; leaving
        // stops it (it used to fire 40x/min for the process lifetime).
        if (value.IsCooling) StartCoolingRefresh();
        else Cooling.Stop();
        OnChanged(nameof(IsDisabledSelected)); OnChanged(nameof(ShowDisabledPane));
        OnChanged(nameof(IsCoolingSelected)); OnChanged(nameof(ShowCoolingPanel));
        OnChanged(nameof(ShowLighting)); OnChanged(nameof(ShowLcdPanel));
    }

    public bool IsDisabledSelected => _selectedLeft?.IsDisabled == true;
    public bool ShowDisabledPane => IsDisabledSelected && !_isSettingsOpen;

    /*-----------------------------------------------------*\
    | OpenRGB bridge: a managed, invisible OpenRGB instance |
    | covers devices we don't drive natively. Opt-in.       |
    \*-----------------------------------------------------*/
    string _openRgbStatus = "";
    public string OpenRgbStatus { get => _openRgbStatus; set { _openRgbStatus = value; OnChanged(); } }

    /*--- debounced settings save for slider-driven values: a drag used to do a
         full JSON serialize + file write PER MOUSE-MOVE DELTA ---*/
    DispatcherTimer? _settingsSaveTimer;
    void SaveSettingsDebounced()
    {
        if (_settingsSaveTimer == null)
        {
            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer!.Stop(); _store.SaveSettings(); };
        }
        _settingsSaveTimer.Stop(); _settingsSaveTimer.Start();
    }

    /*--- master brightness: scales every hardware write, stored unscaled ---*/
    public double MasterBrightness
    {
        get => UnifiedRgb.Core.Master.Brightness;
        set
        {
            double v = Math.Clamp(value, 0.1, 1.0);
            if (Math.Abs(UnifiedRgb.Core.Master.Brightness - v) < 0.001) return;
            UnifiedRgb.Core.Master.Brightness = v;
            _store.Settings.MasterBrightness = v;
            SaveSettingsDebounced();
            OnChanged(); OnChanged(nameof(MasterBrightnessText));
            ReapplyAllStatic();
        }
    }
    public string MasterBrightnessText => $"{UnifiedRgb.Core.Master.Brightness * 100:0}%";

    /// <summary>Live fan-animation speed calibration: the SL-INF fans loop in
    /// hardware at a rate we can't predict, so the user drags this until they
    /// match the other devices. Higher = slower fans. Persisted; applies live.</summary>
    public double LianSpeedScale
    {
        get => _store.Settings.LianSpeedScale;
        set
        {
            double v = Math.Clamp(value, 0.3, 4.0);
            if (Math.Abs(_store.Settings.LianSpeedScale - v) < 1e-4) return;
            _store.Settings.LianSpeedScale = v;
            SaveSettingsDebounced();               // was a full JSON write per drag delta
            foreach (var d in Devices) if (d is LianLiWireless lw) lw.IntervalScale = v;
            _bake.ForgetSignatures();             // force re-bake at the new speed
            RequestLianRebake();
            OnChanged(); OnChanged(nameof(LianSpeedText));
        }
    }
    public string LianSpeedText => $"{_store.Settings.LianSpeedScale:0.0}x";
    public bool ShowLianSpeed => SelectedDevice is LianLiWireless;

    /// <summary>Wired SL-Infinity is selected - show its channel + fan-count controls.</summary>
    public bool ShowLianUni => SelectedDevice is LianLiUniHub;
    public IReadOnlyList<int> LianUniFanOptions { get; } = new[] { 1, 2, 3, 4 };   // max 4 fans/connector
    LianLiUniHub? LianUniHub => SelectedDevice as LianLiUniHub;

    public sealed record LianUniChannelOption(int Channel, string Label);

    /// <summary>Channel dropdown = the connectors the hub reports as populated
    /// (falls back to all 4 if the tach read found none, so it's never empty).</summary>
    public IReadOnlyList<LianUniChannelOption> LianUniChannelOptions
    {
        get
        {
            var hub = LianUniHub;
            if (hub == null) return Array.Empty<LianUniChannelOption>();
            var chans = hub.PopulatedChannels.Count > 0
                ? hub.PopulatedChannels
                : Enumerable.Range(0, 4).ToList();
            return chans.Select(c => new LianUniChannelOption(c, $"Channel {c + 1}")).ToList();
        }
    }
    public bool ShowLianUniChannel => ShowLianUni && LianUniChannelOptions.Count > 0;

    /// <summary>Fans configured for one connector, clamped 1..4.</summary>
    int FansByChannel(int ch)
    {
        var list = _store.Settings.LianUniFansByChannel;
        return list != null && ch >= 0 && ch < list.Count ? Math.Clamp(list[ch], 1, 4) : 1;
    }
    void SetFansByChannel(int ch, int v)
    {
        var list = _store.Settings.LianUniFansByChannel;
        while (list.Count < 4) list.Add(1);
        if (ch >= 0 && ch < list.Count) list[ch] = Math.Clamp(v, 1, 4);
    }

    /// <summary>Active wired-hub connector. Selecting one rebuilds the hub on that
    /// connector and auto-loads its saved fan count (default 1 for a new channel).</summary>
    public int LianUniChannel
    {
        get => _store.Settings.LianUniChannel;
        set
        {
            int v = Math.Clamp(value, 0, 3);
            if (v == _store.Settings.LianUniChannel) return;
            _store.Settings.LianUniChannel = v;
            _store.SaveSettings();
            LianLiUniHub.ConfiguredChannel = v;
            LianLiUniHub.ConfiguredFanCount = FansByChannel(v);   // the count follows the channel
            Rescan();
            OnChanged(); OnChanged(nameof(LianUniFanCount));
        }
    }

    /// <summary>Fans on the ACTIVE connector (1..4). Saved per-channel, so each
    /// connector remembers its own count. The hub can't report this - chained
    /// fans share one tach - so it's set here, not auto-detected.</summary>
    public int LianUniFanCount
    {
        get => FansByChannel(_store.Settings.LianUniChannel);
        set
        {
            int ch = _store.Settings.LianUniChannel;
            int v = Math.Clamp(value, 1, 4);
            if (v == FansByChannel(ch)) return;
            SetFansByChannel(ch, v);
            _store.SaveSettings();
            LianLiUniHub.ConfiguredFanCount = v;
            Rescan();                       // rebuild the hub with the new fan count
            OnChanged();
        }
    }

    /// <summary>One-time: pad the per-channel list to 4 and migrate the old single
    /// count into channel 0. Also point the active channel at a populated one when
    /// the saved channel is empty, so it lights without the user hunting for it.</summary>
    void EnsureLianChannelSettings()
    {
        var s = _store.Settings;
        s.LianUniFansByChannel ??= new();
        while (s.LianUniFansByChannel.Count < 4) s.LianUniFansByChannel.Add(1);
        if (s.LianUniFanCount > 1 && s.LianUniFansByChannel.All(x => x == 1))
            s.LianUniFansByChannel[0] = Math.Clamp(s.LianUniFanCount, 1, 4);
    }

    /// <summary>After detection: if the saved channel has no fans but another
    /// connector does, jump to the first populated one (one extra rescan, only
    /// when the saved channel is actually empty).</summary>
    void SyncLianUniChannelToPopulated()
    {
        var hub = Devices.OfType<LianLiUniHub>().FirstOrDefault();
        if (hub == null || hub.PopulatedChannels.Count == 0) return;
        OnChanged(nameof(LianUniChannelOptions)); OnChanged(nameof(ShowLianUniChannel));
        if (!hub.PopulatedChannels.Contains(_store.Settings.LianUniChannel))
            LianUniChannel = hub.PopulatedChannels[0];   // saves + rescans once
    }

    void ApplyLianSpeed()
    {
        foreach (var d in Devices) if (d is LianLiWireless lw) lw.IntervalScale = _store.Settings.LianSpeedScale;
    }

    public void NudgeBrightness(double delta)
        => System.Windows.Application.Current.Dispatcher.Invoke(
            () => MasterBrightness = UnifiedRgb.Core.Master.Brightness + delta);

    /// <summary>Copy a saved frame over a device's stored frame (clamped to the
    /// shorter of the two, so a changed LED count never overruns) and push it.
    /// The one shape behind Rescan, RestoreState and LoadProfile - three pasted
    /// copies before.</summary>
    void RestoreFrame(IRgbDevice d, Rgb[] saved)
    {
        var frame = FrameFor(d);
        Array.Copy(saved, frame, Math.Min(saved.Length, frame.Length));
        _lighting.PushFrame(d);
    }

    /// <summary>Re-push every device's current static frame (after a
    /// brightness change; animated zones get overwritten next engine frame).</summary>
    void ReapplyAllStatic()
    {
        foreach (var d in Devices)
        {
            // The Lian Li handles brightness via re-baking (a direct SetColors
            // would interrupt a playing hardware animation). Only trust the flag
            // while a channel is actually running: a static "All devices" leaves
            // it stale, and the fans then missed the first brightness change.
            if (d is LianLiWireless { SuppressStreaming: true } && _engine.ChannelsFor(d).Count > 0) continue;
            _lighting.PushFrame(d);
        }
        // Brightness is applied inside the bake render, not in its dedup
        // signature - forget the last upload or the fans keep the old level.
        _bake.ForgetSignatures();
        RequestLianRebake();
    }

    /// <summary>Raised whenever a profile is applied; the automation uses it
    /// to stop protecting a stale snapshot after the user picks something new
    /// mid-override (its own applies are guarded out).</summary>
    public event Action? LightingApplied;

    /// <summary>Select + load a profile (the hotkey/automation entry point).</summary>
    public void ApplyProfile(Profile p)
    {
        SelectedProfile = p;
        LoadProfile(p);
        LightingApplied?.Invoke();
    }

    public void ApplyProfileByIndex(int i)
    {
        if (i < 0 || i >= Profiles.Count) return;
        System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyProfile(Profiles[i]));
    }

    public bool ApplyProfileByName(string name)
    {
        var p = Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p == null) return false;
        ApplyProfile(p);
        return true;
    }

    /*-----------------------------------------------------*\
    | Selection                                             |
    \*-----------------------------------------------------*/
    IRgbDevice? _selected;
    // Switching devices fires a synchronous cascade (targets rebuilt, the
    // effect-pill ItemsSource swapped, bindings re-pushed). A stale value
    // landing in an apply path mid-cascade briefly painted the NEW device
    // with the OLD view's color. While this flag is up, color/effect setters
    // update the UI but never touch hardware.
    bool _selectionChanging;

    public IRgbDevice? SelectedDevice
    {
        get => _selected;
        set
        {
            _selectionChanging = true;
            try
            {
                _selected = value;
                OnChanged(); OnChanged(nameof(HasSelection)); OnChanged(nameof(ShowPreview)); OnChanged(nameof(ShowGenericPreview)); OnChanged(nameof(ShowLianEditor)); OnChanged(nameof(IsGigabyteSelected)); OnChanged(nameof(IsRazerSelected)); OnChanged(nameof(ShowLianSpeed)); OnChanged(nameof(LianSpeedScale)); OnChanged(nameof(LianSpeedText)); OnChanged(nameof(ShowLianUni)); OnChanged(nameof(LianUniFanCount)); OnChanged(nameof(LianUniChannel)); OnChanged(nameof(LianUniChannelOptions)); OnChanged(nameof(ShowLianUniChannel));
                // Pills follow the device's fan list (wireless carries the
                // user's arranged stack order; wired is Fan 1..N).
                LianFanNames = (value as ILianFanDevice)?.LianFanNames.ToList() ?? new List<string>();
                _lianSel.Reset();
                OnChanged(nameof(LianFanNames)); OnChanged(nameof(LianFan)); OnChanged(nameof(IsLianLiSelected));
                OnChanged(nameof(LianParts));
                OnChanged(nameof(LianGroupNames)); OnChanged(nameof(HasLianGroups)); OnChanged(nameof(ShowArrangeFans));
                RebuildTargets();
                // Land on the target that's actually running an effect so the
                // mode selector matches what the fans are showing (e.g. an "All
                // fans" effect restored on launch shouldn't read as Static just
                // because we defaulted to Fan 1). Else land on Fan 1.
                if (value is ILianFanDevice && value is IRgbDevice rd)
                    SelectLianPart(_engine.ChannelsFor(rd).Any(c => c.Offset == 0 && c.Count == rd.LedCount)
                        ? -1 : 0);
            }
            finally { _selectionChanging = false; }
        }
    }
    public bool HasSelection => _selected != null;

    TargetItem? _selectedTarget;
    public TargetItem? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            _selectedTarget = value; OnChanged(); OnChanged(nameof(ShowPreview)); OnChanged(nameof(ShowGenericPreview)); OnChanged(nameof(ShowLianEditor));
            // Effects stay put; the pills and controls swap to show THIS
            // target's own assignment (per-target state, all simultaneous).
            NotifyModeChanged();
            if (value != null) SyncWheelToSelection();
        }
    }

    /*-----------------------------------------------------*\
    | Color state (all views kept in sync via SetColor)     |
    \*-----------------------------------------------------*/
    int _r = 255, _g = 0, _b = 255;
    string _hex = "FF00FF";
    int _brightness = 255;
    Color _wheelColor = Color.FromRgb(255, 0, 255);
    bool _syncing;

    public int R { get => _r; set { if (!_syncing) SetColor(new Rgb((byte)Clamp(value), (byte)_g, (byte)_b)); } }
    public int G { get => _g; set { if (!_syncing) SetColor(new Rgb((byte)_r, (byte)Clamp(value), (byte)_b)); } }
    public int B { get => _b; set { if (!_syncing) SetColor(new Rgb((byte)_r, (byte)_g, (byte)Clamp(value))); } }

    public string Hex
    {
        get => _hex;
        set
        {
            if (_syncing) return;
            _hex = value; OnChanged();
            if (Rgb.TryFromHex(value, out var c)) SetColor(c);
        }
    }

    public Color WheelColor
    {
        get => _wheelColor;
        set { if (!_syncing) SetColor(new Rgb(value.R, value.G, value.B)); }
    }

    public int Brightness
    {
        get => _brightness;
        set
        {
            if (_syncing) return;
            int v = Clamp(value);
            var (h, s, _) = ColorWheel.RgbToHsv(Color.FromRgb((byte)_r, (byte)_g, (byte)_b));
            var c = ColorWheel.HsvToRgb(h, s, v / 255.0);
            SetColor(new Rgb(c.R, c.G, c.B), fromBrightness: true);
        }
    }

    public Brush PreviewBrush { get; private set; } = Brushes.Magenta;

    /*-----------------------------------------------------*\
    | Profiles / startup                                    |
    \*-----------------------------------------------------*/
    string _profileName = "";
    public string ProfileName { get => _profileName; set { _profileName = value; OnChanged(); } }

    Profile? _selectedProfile;
    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            _selectedProfile = value;
            OnChanged(); OnChanged(nameof(IsStartupProfile));
            if (value != null) { _profileName = value.Name; OnChanged(nameof(ProfileName)); }
        }
    }

    public bool IsStartupProfile
    {
        get => _selectedProfile != null &&
               string.Equals(_store.Settings.StartupProfile, _selectedProfile.Name, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (_selectedProfile == null) return;
            _store.Settings.StartupProfile = value ? _selectedProfile.Name : null;
            _store.SaveSettings();
            OnChanged();
        }
    }

    // Cached + refreshed off-thread: the real check spawns `schtasks /Query`
    // with a 4-SECOND timeout, and the binding used to evaluate it on the UI
    // thread during window load (a guaranteed pre-first-paint stall).
    bool _startWithWindows;
    bool _startWithWindowsKnown;
    public bool StartWithWindows
    {
        get
        {
            if (!_startWithWindowsKnown)
            {
                _startWithWindowsKnown = true;   // one refresh in flight
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    bool on = ProfileStore.IsAutoStartEnabled();
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_startWithWindows != on) { _startWithWindows = on; OnChanged(nameof(StartWithWindows)); }
                    });
                });
            }
            return _startWithWindows;
        }
        set
        {
            _startWithWindows = value; OnChanged();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (ProfileStore.SetAutoStart(value)) return;
                // schtasks failed / timed out / UAC declined (logged there): snap
                // the checkbox back to the task's real state instead of leaving
                // the optimistic tick over a task that does not exist.
                bool on = ProfileStore.IsAutoStartEnabled();
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_startWithWindows != on) { _startWithWindows = on; OnChanged(nameof(StartWithWindows)); }
                });
            });
        }
    }

    /// <summary>Open straight to the tray on launch (like --autostart, but for a
    /// normal manual start too). Read once at startup by the window.</summary>
    public bool StartMinimized
    {
        get => _store.Settings.StartMinimized;
        set => SetSetting(_store.Settings.StartMinimized, value, v => _store.Settings.StartMinimized = v);
    }

    /*--- first-run welcome wizard ---*/
    public bool NeedsFirstRun => !_store.Settings.FirstRunDone;
    public void CompleteFirstRun() { _store.Settings.FirstRunDone = true; _store.SaveSettings(); }

    /// <summary>Paint a simple starting look across every device (the wizard's
    /// last step). "rainbow" = Rainbow Wave, "white" = solid white, "off" = dark.</summary>
    public void ApplyStarterLook(string kind)
    {
        _engine.StopAll();

        // Route through each device's whole-device TargetFx so the choice STICKS:
        // the pills highlight it, Save/startup-profile captures it, and revisiting
        // the device reads it back. Painting the engine directly (as before) left
        // every device's stored Choice on the old profile - so the main screen
        // still showed the previous effect after the wizard.
        var choice = kind == "rainbow"
            ? (Effects.FirstOrDefault(e => e.Name == "Rainbow Wave") ?? Effects[0])
            : Effects[0];                                   // Static (white/off via base color)
        var color = kind == "white" ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);

        foreach (var d in Devices)
        {
            var tfx = FxFor(d, 0, d.LedCount);
            tfx.Choice = choice;

            if (choice.Effect is { } fx)
            {
                tfx.Channel = _engine.Start(d, 0, d.LedCount, FrameFor(d), fx, 1.0, new Rgb(255, 255, 255));
            }
            else
            {
                tfx.Channel = null;                         // Static: paint a solid frame
                Array.Fill(FrameFor(d), color);
                _lighting.PushFrame(d);
            }
        }

        RequestLianRebake();
        MarkDirty();
        NotifyModeChanged();        // repaint pills/preview for the selected device
    }

    /*-----------------------------------------------------*\
    | Window placement persistence                          |
    \*-----------------------------------------------------*/
    public (double[]? Bounds, bool Maximized) GetWindowState()
        => (_store.Settings.WindowBounds, _store.Settings.WindowMaximized);

    public void SaveWindowState(double left, double top, double width, double height, bool maximized)
    {
        _store.Settings.WindowBounds = new[] { left, top, width, height };
        _store.Settings.WindowMaximized = maximized;
        _store.SaveSettings();
    }

    public ICommand ApplyToTargetCommand { get; }
    public ICommand ApplyToAllCommand { get; }
    public ICommand PickSwatchCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }


    public MainViewModel()
    {
        _bake = new Services.LianBakeService(_lighting, () => Devices.OfType<LianLiWireless>());
        Cooling = new CoolingViewModel(_store.Settings, _store.SaveSettings,
            isGigabyteBoard: () => Devices.OfType<GigabyteIt5711>().Any(),
            pawnIoMissing: () => PawnIoMissing,
            isOnScreen: () => ShowCoolingPanel);
        Lcd = new LcdDesignerViewModel(
            isOnScreen: () => ShowLcdPanel,   // visibility, not selection: Settings covers the pane
            lightsSuppressed: () => LightsSuppressed,
            applyProfile: ApplyProfileByName,
            profileNames: () => Profiles.Select(p => p.Name),
            currentProfile: () => SelectedProfile?.Name);
        ApplyToTargetCommand = new RelayCommand(_ => ApplyToTarget(), _ => HasSelection);
        ApplyToAllCommand    = new RelayCommand(_ => ApplyModeToAll(), _ => Devices.Count > 0);
        PickSwatchCommand    = new RelayCommand(o => { if (o is Rgb c) SetColor(c); });
        RescanCommand        = new RelayCommand(_ => Rescan());
        SaveProfileCommand   = new RelayCommand(_ => SaveProfile(),
            _ => !string.IsNullOrWhiteSpace(ProfileName) || SelectedProfile != null);
        LoadProfileCommand   = new RelayCommand(_ => LoadProfile(SelectedProfile), _ => SelectedProfile != null);
        DeleteProfileCommand = new RelayCommand(_ => DeleteProfile(), _ => SelectedProfile != null);
        AddPaletteColorCommand  = new RelayCommand(_ => { PatternPalette.Add(Current); OnChanged(nameof(HasPalette)); SyncFanOutPalettes(); RequestLianRebake(); MarkDirty(); });
        RemovePaletteColorCommand = new RelayCommand(o => { if (o is Rgb c && PatternPalette.Count > 1) { PatternPalette.Remove(c); OnChanged(nameof(HasPalette)); SyncFanOutPalettes(); RequestLianRebake(); MarkDirty(); } });
        AddCustomColorCommand = new RelayCommand(_ => AddCustomColor(Current));
        AddCustomElementColorCommand = new RelayCommand(_ =>
            AddCustomColor(new Rgb(Lcd.SelectedElementColor.R, Lcd.SelectedElementColor.G, Lcd.SelectedElementColor.B)));
        RemoveCustomColorCommand = new RelayCommand(o => { if (o is Rgb c) { CustomColors.Remove(c); PersistCustomColors(); } });
        ApplyCustomColors(_store.Settings.CustomColors);

        Effects = new[]
        {
            new EffectChoice { Name = "Static", Effect = null },
            new EffectChoice { Name = "Rainbow Wave",   Effect = new RainbowWave(),  Category = "Basics" },
            new EffectChoice { Name = "Rainbow Cycle",  Effect = new RainbowCycle(), Category = "Basics" },
            new EffectChoice { Name = "Rainbow Morph",  Effect = new RainbowMorph(), Category = "Basics" },
            new EffectChoice { Name = "Color Cycle",    Effect = new ColorCycle(),   Category = "Basics" },
            new EffectChoice { Name = "Breathing",      Effect = new Breathing(),    Category = "Basics" },
            new EffectChoice { Name = "Wave",           Effect = new Wave(),         Category = "Basics" },
            new EffectChoice { Name = "Spiral",         Effect = new Spiral(),       Category = "Basics" },
            new EffectChoice { Name = "Meteor",         Effect = new Meteor(),       Category = "Motion" },
            new EffectChoice { Name = "Meteor Rainbow", Effect = new MeteorRainbow(),Category = "Motion" },
            new EffectChoice { Name = "Colorful Meteor",Effect = new ColorfulMeteor(),Category = "Motion" },
            new EffectChoice { Name = "Double Meteor",  Effect = new DoubleMeteor(), Category = "Motion" },
            new EffectChoice { Name = "Boomerang",      Effect = new Boomerang(),    Category = "Motion" },
            new EffectChoice { Name = "Runway",         Effect = new Runway(),       Category = "Motion" },
            new EffectChoice { Name = "Scan",           Effect = new Scan(),         Category = "Motion" },
            new EffectChoice { Name = "Wing",           Effect = new Wing(),         Category = "Motion" },
            new EffectChoice { Name = "Taichi",         Effect = new Taichi(),       Category = "Motion" },
            new EffectChoice { Name = "Tide",           Effect = new TideFx(),       Category = "Motion" },
            new EffectChoice { Name = "Reflect",        Effect = new Reflect(),      Category = "Motion" },
            new EffectChoice { Name = "Gradient Ribbon",Effect = new GradientRibbon(),Category = "Motion" },
            new EffectChoice { Name = "Return Arc",     Effect = new ReturnArc(),    Category = "Motion" },
            new EffectChoice { Name = "Double Arc",     Effect = new DoubleArc(),    Category = "Motion" },
            new EffectChoice { Name = "Door",           Effect = new Door(),         Category = "Motion" },
            new EffectChoice { Name = "Mop Up",         Effect = new MopUp(),        Category = "Motion" },
            new EffectChoice { Name = "Meteor Mix",     Effect = new MeteorMix(),    Category = "Motion" },
            new EffectChoice { Name = "Heartbeat",      Effect = new Heartbeat(),    Category = "Motion" },
            new EffectChoice { Name = "Heartbeat Runway",Effect = new HeartbeatRunway(),Category = "Motion" },
            new EffectChoice { Name = "Drumming",       Effect = new Drumming(),     Category = "Motion" },
            new EffectChoice { Name = "Temp Glow",      Effect = new TempGlow(),     Category = "Ambient" },
            new EffectChoice { Name = "Aurora",         Effect = new Aurora(),       Category = "Ambient" },
            new EffectChoice { Name = "Plasma",         Effect = new Plasma(),       Category = "Ambient" },
            new EffectChoice { Name = "Candle",         Effect = new Candle(),       Category = "Ambient" },
            new EffectChoice { Name = "Rain",           Effect = new Rain(),         Category = "Motion" },
            new EffectChoice { Name = "Fire",           Effect = new Fire(),         Category = "Ambient" },
            new EffectChoice { Name = "Lava",           Effect = new Lava(),         Category = "Ambient" },
            new EffectChoice { Name = "Mixing",         Effect = new Mixing(),       Category = "Ambient" },
            new EffectChoice { Name = "Starfield",      Effect = new Starfield(),    Category = "Ambient" },
            new EffectChoice { Name = "Matrix",         Effect = new MatrixRain(),   Category = "Ambient" },
            new EffectChoice { Name = "Wallpaper",      Effect = new WallpaperSync(),Category = "Ambient" },
            new EffectChoice { Name = "Screen Ambient", Effect = new ScreenSync(),   Category = "Ambient" },
            new EffectChoice { Name = "Time Warmth",    Effect = new TimeWarmth(),   Category = "Ambient" },
            new EffectChoice { Name = "Chroma Sync",    Effect = new ChromaSync(),   Category = "Ambient" },
            new EffectChoice { Name = "Twinkle",        Effect = new Twinkle(),      Category = "Party" },
            new EffectChoice { Name = "Disco",          Effect = new Disco(),        Category = "Party" },
            new EffectChoice { Name = "Police",         Effect = new Police(),       Category = "Party" },
            new EffectChoice { Name = "Electric",       Effect = new Electric(),     Category = "Party" },
            new EffectChoice { Name = "Warning",        Effect = new Warning(),      Category = "Party" },
            new EffectChoice { Name = "Candy Box",      Effect = new CandyBox(),     Category = "Party" },
            new EffectChoice { Name = "Outline",        Effect = new StackOutline(), Category = "Fan stack" },
            new EffectChoice { Name = "Waterfall",      Effect = new Waterfall(),    Category = "Fan stack" },
            new EffectChoice { Name = "Orbit",          Effect = new Orbit(),        Category = "Fan stack" },
            new EffectChoice { Name = "Audio Bars",     Effect = new AudioBars(),    Category = "Audio" },
            new EffectChoice { Name = "Audio Pulse",    Effect = new AudioPulse(),   Category = "Audio" },
            new EffectChoice { Name = "Key Fade",       Effect = new KeyFade(),      Category = "Keyboard" },
            new EffectChoice { Name = "Key Ripple",     Effect = new KeyRipple(),    Category = "Keyboard" },
            new EffectChoice { Name = "Gradient",       Effect = new Gradient(),     Category = "Palette" },
            new EffectChoice { Name = "Palette Cycle",  Effect = new PaletteCycle(), Category = "Palette" },
            new EffectChoice { Name = "Confetti",       Effect = new Confetti(),     Category = "Palette" },
            new EffectChoice { Name = "Custom Pattern", Effect = PatternMarker,      Category = "Custom" },
        };
        _favorites = new HashSet<string>(
            (IEnumerable<string>?)_store.Settings.FavoriteEffects ?? DefaultFavorites,
            StringComparer.OrdinalIgnoreCase);
        // Migrate removed effects: Screen Sync -> Wallpaper (its successor),
        // drop Chroma Sync. Persist so the pills settle to valid effects.
        bool migrated = false;
        foreach (var (oldName, newName) in EffectAliases)
            if (_favorites.Remove(oldName)) { _favorites.Add(newName); migrated = true; }
        if (_favorites.Remove("Chroma Sync")) migrated = true;
        if (migrated) { _store.Settings.FavoriteEffects = _favorites.ToList(); _store.SaveSettings(); }

        foreach (var p in _store.Profiles) Profiles.Add(p);
        UnifiedRgb.Core.Master.Brightness = _store.Settings.MasterBrightness;
        EnsureLianChannelSettings();                                        // pad list + migrate old count
        LianLiUniHub.ConfiguredChannel = _store.Settings.LianUniChannel;    // before detection
        LianLiUniHub.ConfiguredFanCount = FansByChannel(_store.Settings.LianUniChannel);
        // Purge half-filled rules: the old inline editor could create blank
        // rows and they persisted. They can't be created anymore; drop any
        // stragglers on load so they never show or match again.
        int purged = _store.Settings.AutomationRules?.RemoveAll(
            r => string.IsNullOrWhiteSpace(r.Process) || string.IsNullOrWhiteSpace(r.Profile)) ?? 0;
        if (purged > 0) _store.SaveSettings();
        foreach (var r in _store.Settings.AutomationRules ?? new()) AutoRules.Add(r);
        // Rules whose profile has since been deleted stay in the list: the
        // dialog shows them blank so they can be repointed, and the automation
        // skips them meanwhile.
        foreach (var r in _store.Settings.SensorRules ?? new()) SensorRules.Add(r);
        Lcd.Start();
        UnifiedRgb.Core.Effects.ChromaFeed.Start();      // DLL-shim pipe (Wallpaper Engine)
        UnifiedRgb.Core.Net.ChromaRestServer.Start();    // Chroma REST API :54235 (CS2 and modern games)
        Rescan();
        SyncLianUniChannelToPopulated();      // land on a connector that actually has fans
        SetColor(new Rgb(255, 0, 255));

        /*-------------------------------------------------*\
        | Apply + select the startup profile on every       |
        | launch (autostart and manual alike).              |
        \*-------------------------------------------------*/
        if (_store.Settings.StartupProfile is string sp)
        {
            var prof = Profiles.FirstOrDefault(p => p.Name.Equals(sp, StringComparison.OrdinalIgnoreCase));
            if (prof != null) { SelectedProfile = prof; LoadProfile(prof); LandOnRunningLianTarget(); }
        }
        _initializing = false;

        CheckForUpdate();
        if (_store.Settings.UseOpenRgb) StartOpenRgbBridge();
        Lcd.InitScenes();
        // Every profile-name list in the UI is computed from Profiles (Show tab
        // lights dropdowns, app-rule pickers); without this they stay frozen at
        // whatever existed at launch.
        Profiles.CollectionChanged += (_, _) => { Lcd.NotifyProfilesChanged(); OnChanged(nameof(ProfileNames)); };
    }

    bool _initializing = true;

    Rgb Current => new((byte)_r, (byte)_g, (byte)_b);

    /*-----------------------------------------------------*\
    | Central color update: sync every view of the color,   |
    | then live-apply if enabled.                           |
    \*-----------------------------------------------------*/
    /// <summary>Full white is every channel at maximum, which is the heaviest
    /// current an ARGB header can be asked for, and it is the easiest thing in
    /// the app to hit by accident: one swatch, or the middle of the wheel,
    /// followed by "All devices". Choosing white lands at 60% instead. The tint
    /// is preserved so a warm white stays warm.</summary>
    const double WhiteSafeScale = 0.6;

    static Rgb SoftenWhite(Rgb c) =>
        c.R < 240 || c.G < 240 || c.B < 240
            ? c
            : new Rgb((byte)(c.R * WhiteSafeScale), (byte)(c.G * WhiteSafeScale), (byte)(c.B * WhiteSafeScale));

    /// <param name="fromBrightness">The brightness slider is an explicit choice
    /// about level, so it is never capped. Every other path (wheel, hex, R/G/B,
    /// swatches) is picking a colour, and gets the white guard.</param>
    void SetColor(Rgb c, bool fromBrightness = false)
    {
        if (!fromBrightness) c = SoftenWhite(c);
        UpdateColorViews(c);
        if (_selectionChanging) return;   // sync views only — never write mid-switch

        if (LiveChannel(CurrentFx()) != null)
        {
            // Tint the running effect live - on every fan of an "All fans +
            // part" fan-out, not just the representative fan 0.
            foreach (var fx in CurrentFxSet())
                if (LiveChannel(fx) is { } liveCh) liveCh.BaseColor = c;
            // Lian Li fans play a BAKED animation (streaming suppressed), so a
            // live BaseColor change only moves the preview - re-bake to push the
            // new tint to the hardware.
            RequestLianRebake();
            MarkDirty();
        }
        else if (!_initializing) ApplyToTarget();   // static picking is always live
    }

    /// <summary>Sync every color view (wheel/hex/RGB/brightness) with NO side
    /// effects — no hardware writes, no effect tinting.</summary>
    void UpdateColorViews(Rgb c)
    {
        _syncing = true;
        _r = c.R; _g = c.G; _b = c.B;
        _hex = c.ToHex();
        _wheelColor = Color.FromRgb(c.R, c.G, c.B);
        _brightness = (int)(ColorWheel.RgbToHsv(_wheelColor).V * 255);
        var pb = new SolidColorBrush(_wheelColor); pb.Freeze();   // frozen: no per-color-change change-tracking overhead
        PreviewBrush = pb;
        OnChanged(nameof(R)); OnChanged(nameof(G)); OnChanged(nameof(B));
        OnChanged(nameof(Hex)); OnChanged(nameof(WheelColor));
        OnChanged(nameof(Brightness)); OnChanged(nameof(PreviewBrush));
        _syncing = false;
    }

    /// <summary>Show the selected target's actual current color in the wheel:
    /// a running effect's tint, else the target's first stored LED color.</summary>
    void SyncWheelToSelection()
    {
        var t = CurrentTarget();
        if (t == null) return;
        var (dev, off, _) = t.Value;
        var fx = CurrentFx();
        Rgb c;
        // Only mirror a running channel's tint when the effect actually USES
        // it. Rainbow-style effects ignore their base color — showing that
        // stale, never-chosen tint made Apply paint a color the user never
        // picked (field case: rainbow keyboard, wheel silently pink).
        if (LiveChannel(fx) is { } ch && ch.Effect.UsesBaseColor) c = ch.BaseColor;
        else
        {
            var frame = FrameFor(dev);
            c = off < frame.Length ? frame[off] : Rgb.Black;
        }
        UpdateColorViews(c);
    }

    /// <summary>Start/stop the current target's channel per its assigned mode.</summary>
    void ApplyFx()
    {
        if (_selectionChanging) return;
        var dev = SelectedDevice;
        if (dev == null) return;
        var choice = ChoiceOf(CurrentFx());

        // "All fans + a part": run the chosen mode on that part of EVERY fan,
        // each fan's zone getting its own effect channel + state.
        if (LianFanOut)
        {
            var src = CurrentFx();   // fan 0's range: the state the editor shows
            foreach (var (off, cnt) in LianApplyRanges())
            {
                var rfx = FxFor(dev, off, cnt);
                rfx.Choice = choice;
                // Speed/direction/pattern/palette too, not just the mode -
                // fans 2..N otherwise started on their own defaults.
                CopyTargetSettings(src, rfx, choice);
                ApplyFxRange(dev, off, cnt, rfx, choice);
            }
            return;
        }

        var t = CurrentTarget();
        if (t == null) return;
        ApplyFxRange(t.Value.dev, t.Value.off, t.Value.count, CurrentFx(), choice);
    }

    /// <summary>Start (or stop, for Static) one effect channel on a single device
    /// range, replacing any overlapping channel. Shared by the single-target and
    /// all-fans fan-out paths.</summary>
    void ApplyFxRange(IRgbDevice dev, int off, int count, TargetFx fx, EffectChoice choice)
    {
        // Assignments this range replaces (overlap on the same device) fall
        // back to Static so their pills read correctly when revisited.
        foreach (var other in _targetFx.Values)
        {
            if (other == fx || other.Channel == null) continue;
            var c = other.Channel;
            if (ReferenceEquals(c.Device, dev) && off < c.Offset + c.Count && c.Offset < off + count)
            {
                other.Choice = Effects[0];
                other.Channel = null;
            }
        }

        if (choice.Effect == null)
        {
            _engine.StopRange(dev, off, count);
            fx.Channel = null;
            _lighting.RestoreStatics(dev, off, count);
            RequestLianRebake();
            return;
        }

        // Presets are stateless and shared; the custom pattern and key ripple
        // use this target's own instances so their settings are independent.
        var effect = ResolveEffect(fx, choice);
        // A base-color effect started on black is invisible (Twinkle on
        // 000000 = nothing): swap in white and show it on the wheel.
        var bc = Current;
        if (effect.UsesBaseColor && bc.R < 16 && bc.G < 16 && bc.B < 16)
        {
            bc = new Rgb(255, 255, 255);
            UpdateColorViews(bc);
        }
        fx.Channel = _engine.Start(dev, off, count, FrameFor(dev), effect, SignedSpeed(fx), bc);
        RequestLianRebake();
        MarkDirty();
    }


    /// <summary>"All devices" applies the CURRENT MODE everywhere: the static
    /// color in Static mode, or the running effect (with its speed, tint and
    /// pattern settings) as a whole-device channel on every device.</summary>
    void ApplyModeToAll()
    {
        var srcFx = CurrentFx();
        var choice = ChoiceOf(srcFx);
        if (choice.Effect == null) { ApplyToAll(); return; }

        foreach (var dev in Devices)
        {
            // Keyboard-only modes stay on keyboards.
            if (choice.Category == "Keyboard" && dev.Type != DeviceType.Keyboard) continue;

            var fx = FxFor(dev, 0, dev.LedCount);
            fx.Choice = choice;
            // Speed, direction, pattern settings and palette follow the source
            // (self-copy safe: with the default "Entire device" target the
            // source IS this device's whole-device state, and the old
            // Clear()-then-copy wiped its palette).
            CopyTargetSettings(srcFx, fx, choice);
            // The ONE start path: replaced sub-targets fall back to Static, a
            // black base swaps to white, the channel starts.
            ApplyFxRange(dev, 0, dev.LedCount, fx, choice);
        }
        // The whole-device channel now owns every fan; a fan/part selection's
        // own sub-target was just retired to Static, so the pill would read
        // Static while the fans animate. Land on "All fans, whole fan", whose
        // state IS the running channel.
        LandOnWholeLianDevice();
        // Synchronized restart: everything jumps to the top of the cycle together
        // (red for rainbow), including devices already running it, so All devices
        // reads as one coordinated start rather than a continuation.
        _engine.RestartClock();
        NotifyModeChanged();
        _bake.ForgetSignatures();  // All devices is an explicit sync: re-align the fans to the clock
        RequestLianRebake();
        MarkDirty();
    }

    void ApplyToAll()
    {
        // "All devices" means ALL of it: any running effect channel would
        // repaint over the static within one engine frame (~16ms), making
        // the click look like it did nothing. Stop every channel and reset
        // every target's mode to Static first, then paint.
        _engine.StopAll();
        foreach (var fx in _targetFx.Values) { fx.Channel = null; fx.Choice = Effects[0]; }
        NotifyModeChanged();

        foreach (var d in Devices)
        {
            Array.Fill(FrameFor(d), Current);
            _lighting.PushFrame(d);
        }
        RequestLianRebake();   // zero channels: clears the fans' SuppressStreaming flag
        MarkDirty();
    }

    void Rescan()
    {
        // Carry the current lighting across the rescan: static frames by device
        // name, and effect assignments via the same snapshot profiles use.
        var savedEffects = CaptureEffects();
        var savedFrames = Devices.ToDictionary(d => d.Name, d => (Rgb[])FrameFor(d).Clone());

        _lighting.StopAndDrain();   // queued static writes must not land on disposed handles
        _targetFx.Clear();          // device instances are replaced
        _manager.Dispose();
        _lighting.ForgetFrames();
        Devices.Clear();
        _manager.DetectAll(IsFamilyDisabled);
        foreach (var d in _manager.Devices)
        {
            // Per-device families (OpenRGB proxies) can't be skipped at the
            // factory level; filter them here instead.
            if (_manager.FamilyOf.TryGetValue(d, out var fam) && IsFamilyDisabled(fam)) continue;
            Devices.Add(d);
        }

        // Repaint statics on the fresh device instances.
        foreach (var d in Devices)
            if (savedFrames.TryGetValue(d.Name, out var old)) RestoreFrame(d, old);

        ApplyLianSpeed();                                      // carry the saved fan-speed calibration
        BuildLeftItems();                                      // devices + pump LCD row
        RestoreEffects(savedEffects);
        LandOnRunningLianTarget();
        SyncWheelToSelection();
    }

    /// <summary>Rescan and launch select the device (landing on Fan 1 / whole
    /// fan - no channel exists yet) BEFORE RestoreEffects/LoadProfile bring an
    /// "All fans" effect back, so the pills read Static while the fans animate.
    /// Once the channels exist, move a still-default selection to All fans.</summary>
    void LandOnRunningLianTarget()
    {
        if (SelectedDevice is ILianFanDevice and IRgbDevice dev && _lianSel is { Fan: 0, Part: 0 }
            && _engine.ChannelsFor(dev).Any(c => c.Offset == 0 && c.Count == dev.LedCount))
            SelectLianPart(-1);
    }

    /// <summary>"All devices" just started a whole-device channel on the
    /// selected Lian Li device: whatever fan/part was selected, its own
    /// sub-target no longer runs anything, so move the selection to
    /// "All fans, whole fan" — the target whose state is that channel — and
    /// the pill shows the effect the fans are actually playing.</summary>
    void LandOnWholeLianDevice()
    {
        if (SelectedDevice is ILianFanDevice and IRgbDevice dev
            && (_lianSel.Fan != -1 || _lianSel.Part != 0)
            && _engine.ChannelsFor(dev).Any(c => c.Offset == 0 && c.Count == dev.LedCount))
        {
            _lianSel.Part = 0;
            SelectLianPart(-1);
        }
    }

    public void Dispose()
    {
        // Fans first: hand every controlled header back to the BIOS before
        // anything else can fail on the way down — but keep the saved profiles
        // so the next launch re-applies them. A slider value still on its
        // debounce is dropped here (Cooling.Stop below would flush it AFTER
        // the handback).
        Cooling.DiscardPendingDuties();
        if (UnifiedRgb.Core.Sensors.SensorHub.AnyControlledFan)
            UnifiedRgb.Core.Sensors.SensorHub.RestoreAllFans("app exit", keepConfig: true);
        // Wireless fans: optionally follow the SYS fan header's hardware curve
        // while the app is away (must run before the device is disposed).
        if (_store.Settings.LianHandoffOnExit)
            try { LianLiWireless.Instance?.FollowPwmLine(); } catch { }
        Cooling.Stop(); _bake.Stop();
        // Flush a pending debounced settings save so a drag right before exit sticks.
        if (_settingsSaveTimer?.IsEnabled == true) { _settingsSaveTimer.Stop(); _store.SaveSettings(); }
        // Each step isolated: one throw here used to skip the rest (port 54235
        // stayed bound, device handles stayed open).
        static void Safely(Action a) { try { a(); } catch (Exception ex) { UnifiedRgb.Core.Log.Warn("shutdown", ex.Message); } }
        Safely(() => _lighting.StopAndDrain());   // the exit-restore writes must finish before the handles go
        Safely(_manager.Dispose);
        Safely(Lcd.Dispose);   // saves the design, releases the panel + the PawnIO temp reader
        Safely(UnifiedRgb.Core.Sensors.SensorHub.Shutdown);   // closes LHM (unloads its ring0 service) + the PawnIO sensor handles
        Safely(UnifiedRgb.Core.Net.ChromaRestServer.Stop);   // release :54235 cleanly
        Safely(OpenRgbLink.Shutdown);
        Safely(OpenRgbManager.Stop);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}








