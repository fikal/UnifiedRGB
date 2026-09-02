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

// Lian Li: animation baking, ARGB header config, fan editor — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /// <summary>Re-bake the wireless fans' hardware animation (debounced; see
    /// LianBakeService).</summary>
    public void RequestLianRebake() => _bake.Request();

    // Only worth showing the "Apply to" list when the device has real sub-zones.
    public bool HasMultipleTargets => Targets.Count > 1;

    /*-----------------------------------------------------*\
    | ARGB header configuration (Gigabyte board)            |
    \*-----------------------------------------------------*/
    public bool IsGigabyteSelected => SelectedDevice is GigabyteIt5711;

    /// <summary>Light a header white so the user can see which fans answer
    /// (works regardless of the header's configured state or color order).</summary>
    public void TestHeader(int header, int leds)
    {
        var dev = Devices.OfType<GigabyteIt5711>().FirstOrDefault();
        if (dev == null) return;
        var white = Enumerable.Repeat(Rgb.White, Math.Clamp(leds, 1, 64)).ToArray();
        _applier.Post(LaneOf(dev), ("hdrtest", header), () => dev.SetHeaderLeds(header, white));
    }

    /// <summary>Persist the new header layout and rebuild devices from it.</summary>
    public void ApplyHeaderConfig(HardwareConfig cfg)
    {
        cfg.Save();
        Rescan();
    }

    void RebuildTargets()
    {
        Targets.Clear();
        if (_selected == null) { SelectedTarget = null; OnChanged(nameof(HasMultipleTargets)); OnChanged(nameof(ShowZonePicker)); return; }
        Targets.Add(new TargetItem { Name = "Entire device", Zone = null });
        if (_selected.Zones.Count > 1)
        {
            foreach (var z in _selected.Zones)
                Targets.Add(new TargetItem { Name = z.Name, Zone = z });
        }
        SelectedTarget = Targets[0];
        OnChanged(nameof(HasMultipleTargets)); OnChanged(nameof(ShowZonePicker));
    }

    void ApplyToTarget()
    {
        if (_selectionChanging) return;
        var dev = SelectedDevice;
        if (dev == null) return;
        var frame = FrameFor(dev);
        var zone = SelectedTarget?.Zone;

        // "All fans + a part" writes that part on every fan (non-contiguous, so
        // the single selected zone can't describe it). Otherwise: the selected
        // zone, or the whole device.
        var ranges = LianFanOut ? LianApplyRanges()
            : new List<(int off, int cnt)> { (zone?.Offset ?? 0, zone?.Count ?? dev.LedCount) };
        foreach (var (off, cnt) in ranges)
            for (int i = 0; i < cnt && off + i < frame.Length; i++) frame[off + i] = Current;

        // Lian Li with baked effects running: a direct static write would
        // interrupt the hardware animation. Fold the new static base into the
        // bake instead.
        if (dev is LianLiWireless)
        {
            var chans = _engine.ChannelsFor(dev);
            if (chans.Count > 0 && chans.All(c => c.Effect.Bakeable))
            { RequestLianRebake(); MarkDirty(); return; }
        }

        // Write only the targeted zone(s) when the device supports it, so setting
        // one zone never disturbs an effect running on another zone. Whole-device
        // (zone == null, no fan-out) writes the full frame.
        if (dev is IZoneWritable zw && (LianFanOut || zone != null))
            foreach (var (off, cnt) in ranges) _lighting.PushZone(zw, dev, off, cnt);
        else
            _lighting.PushFrame(dev);
        RequestLianRebake();
        MarkDirty();
    }

    /*-----------------------------------------------------*\
    | Lian Li fan editor: pick a fan, click a part on the    |
    | model, edit it. Parts map onto the device's zones, so  |
    | the whole existing pipeline (wheel, effects, profiles) |
    | drives whatever part is selected.                      |
    | Parts: 0 whole fan, 1 center, 2 inner ring,            |
    |        3 outer (infinity) ring.                        |
    \*-----------------------------------------------------*/
    public bool IsLianLiSelected => SelectedDevice is ILianFanDevice;
    ILianFanDevice? LianDev => SelectedDevice as ILianFanDevice;
    public bool ShowGenericPreview => ShowPreview && !IsLianLiSelected;
    // The editor IS the interface for this device - always visible with it,
    // regardless of which zone is selected (ShowPreview gates on zone kind).
    public bool ShowLianEditor => IsLianLiSelected && !_isLcdSelected;

    // Selection state + geometry math live in LianFanSelection (pure, no UI).
    readonly LianFanSelection _lianSel = new();

    public IReadOnlyList<LianPartButton> LianParts => LianFanSelection.Parts(LianDev);
    public (int Center, int Outer, int Side, bool SideInOuter) LianFanPartCounts => LianFanSelection.PartCounts(LianDev);

    // The fan pills + clickable model replace the Zone dropdown here.
    public bool ShowZonePicker => HasMultipleTargets && !IsLianLiSelected;

    public List<string> LianFanNames { get; private set; } = new();

    public int LianFan
    {
        get => _lianSel.Fan;
        set
        {
            if (value < 0) return;   // ListBox clears to -1 while All-fans scope is active
            _lianSel.Fan = value;    // pick a single fan; keep the current part
            _lianSel.PendingChoice = null;   // fan switches never spread modes
            if (SelectedDevice is ILianFanDevice dev) UpdateLianTarget(dev);
            OnChanged(nameof(LianFan)); OnChanged(nameof(LianSelectionLabel));
        }
    }

    /// <summary>Model click: in Custom Pattern mode a click on a segment of
    /// the CURRENT target paints that LED (like the keyboard preview) instead
    /// of re-targeting - re-targeting mid-pattern yanked the pills back to
    /// the new part's mode (Static) and read as losing the pattern.</summary>
    /// <summary>Model click = select that piece, ALWAYS. If the user picked a
    /// mode just before (mode-first flow), it carries onto the clicked part.</summary>
    public void LianClicked(int part, int led) => SelectLianPart(part, carryPending: true);

    /// <summary>User-defined fan groups (from the Arrange fans dialog): each
    /// is its own effect canvas spanning just its fans.</summary>
    public List<string> LianGroupNames => (SelectedDevice as LianLiWireless)?.Zones
        .Where(z => z.Name.StartsWith("Group ")).Select(z => z.Name).ToList() ?? new();
    public bool HasLianGroups => LianGroupNames.Count > 0;

    /// <summary>Arrange only matters with 2+ fans to order.</summary>
    // Arrange (re-order + group) is a wireless feature; the wired chain is fixed.
    public bool ShowArrangeFans => SelectedDevice is LianLiWireless && LianFanNames.Count > 1;

    /*--- Chroma sync: opt-in install of the interop shim (Wallpaper Engine /
          Chroma games -> UnifiedRGB). Gear-safe; backs up a real Razer SDK ---*/
    public bool ChromaShimAvailable => UnifiedRgb.Core.Net.ChromaShimInstaller.ShimAvailable;
    string _chromaStatus = "";
    public string ChromaSyncStatus { get => _chromaStatus; set { _chromaStatus = value; OnChanged(); } }
    public bool ChromaSyncEnabled
    {
        get => UnifiedRgb.Core.Net.ChromaShimInstaller.Installed;
        set
        {
            var err = value ? UnifiedRgb.Core.Net.ChromaShimInstaller.Install()
                            : UnifiedRgb.Core.Net.ChromaShimInstaller.Uninstall();
            ChromaSyncStatus = err ?? (value
                ? "Enabled. Restart Wallpaper Engine to connect."
                : "Disabled.");
            OnChanged();
        }
    }

    public void SelectLianGroup(string name)
    {
        if (SelectedDevice is not LianLiWireless) return;
        var t = Targets.FirstOrDefault(x => x.Zone != null && x.Zone.Name == name);
        if (t != null)
        {
            bool was = _selectionChanging;
            _selectionChanging = true;            // sync-only: never repaint on select
            try { SelectedTarget = t; }
            finally { _selectionChanging = was; }
        }
        CarryPendingChoice();
    }

    /// <summary>Mode brush (mode-first flow): if the user picked an EFFECT just
    /// before, carry it onto the part they just selected so a mode can be
    /// painted across parts. Never carries a color, and never brushes Static -
    /// selecting a part must not repaint it.</summary>
    void CarryPendingChoice()
    {
        var pending = _lianSel.PendingChoice;
        if (pending?.Effect != null && !ReferenceEquals(ChoiceOf(CurrentFx()), pending))
        {
            CurrentFx().Choice = pending;
            NotifyModeChanged();
            ApplyFx();
        }
    }

    public void SelectLianPart(int part, bool carryPending = false)
    {
        if (SelectedDevice is not ILianFanDevice dev) return;
        // "All fans" (-1) sets only the FAN scope and keeps the current part;
        // a part (0..N) sets only the part and keeps the fan scope.
        if (part == -1) _lianSel.Fan = -1;
        else if (part >= 0) _lianSel.Part = part;
        UpdateLianTarget(dev);
        OnChanged(nameof(LianFan)); OnChanged(nameof(LianSelectionLabel));
        if (carryPending) CarryPendingChoice();
    }

    /// <summary>Point SelectedTarget at the representative zone for the current
    /// fan-scope × part (fan 0 stands in when the scope is "all"), so the wheel
    /// and mode selector show that selection's state. Sync-only: guarded so it
    /// never writes hardware (the wheel round-trip would otherwise repaint).</summary>
    void UpdateLianTarget(ILianFanDevice dev)
    {
        var rep = _lianSel.RepresentativeRange(dev);
        var t = rep is var (off, cnt) && rep != null
            ? Targets.FirstOrDefault(x => x.Zone != null && x.Zone.Offset == off && x.Zone.Count == cnt)
            : Targets.FirstOrDefault(x => x.Zone == null);          // all fans + whole = whole device
        if (t != null)
        {
            bool was = _selectionChanging;
            _selectionChanging = true;
            try { SelectedTarget = t; }
            finally { _selectionChanging = was; }
        }
    }

    List<(int off, int cnt)> LianApplyRanges()
        => LianDev is { } dev ? _lianSel.ApplyRanges(dev) : new();

    bool LianFanOut => IsLianLiSelected && _lianSel.FanOut;

    public string LianSelectionLabel => _lianSel.Label(LianDev);

    /// <summary>Live per-fan LED slice + which part is selected (kept in sync
    /// with the target both ways). Works for wireless (44 LEDs, 3 parts) and the
    /// wired hub (20 LEDs, 2 parts) via ILianFanDevice.</summary>
    public (Rgb[] Colors, int SelectedPart) LianLiView()
    {
        if (SelectedDevice is not ILianFanDevice lf || SelectedDevice is not IRgbDevice dev)
            return (Array.Empty<Rgb>(), -1);
        int per = lf.LianLedsPerFan;

        // Zone picked from the dropdown for a different fan: follow it - but NOT
        // while the All-fans scope is active (Fan < 0), or the representative
        // fan-0 zone would silently collapse the scope back to a single fan.
        var z = SelectedTarget?.Zone;
        if (z != null && _lianSel.Fan >= 0)
        {
            int f = z.Offset / per;
            if (f != _lianSel.Fan && f < LianFanNames.Count)
            { _lianSel.Fan = f; OnChanged(nameof(LianFan)); }
        }

        // All fans: draw fan 0 as the representative (the selected part still
        // highlights, and it applies to every fan).
        int viewFan = _lianSel.Fan < 0 ? 0 : _lianSel.Fan;
        var frame = ComposedFrame(dev);
        var slice = new Rgb[per];
        int b = viewFan * per;
        for (int i = 0; i < per && b + i < frame.Length; i++) slice[i] = frame[b + i];

        // Highlight the selected part on the drawn fan. Derived from the part
        // selection so it's correct even in all-fans mode (where the zone points at fan 0).
        int sel = _lianSel.Part >= 0 && _lianSel.Part <= lf.LianFanParts.Count ? _lianSel.Part : -1;
        return (slice, sel);
    }

    /// <summary>Live view of the selected target for the preview: per-LED colors
    /// (with any running effect composited over its range), positions normalized
    /// to the target's bounds, plus the render style and layout aspect.</summary>
    public (Rgb[] Colors, LedPos[] Pos, PreviewStyle Style, double Aspect, LedRect[]? Rects) CurrentTargetView()
    {
        var dev = SelectedDevice;
        if (dev == null || _isLcdSelected)
            return (Array.Empty<Rgb>(), Array.Empty<LedPos>(), PreviewStyle.Dots, 1.6, null);
        var zone = SelectedTarget?.Zone;
        int off = zone?.Offset ?? 0;
        int count = zone?.Count ?? dev.LedCount;

        var frame = ComposedFrame(dev);

        var colors = new Rgb[count];
        for (int i = 0; i < count; i++)
            colors[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;

        // Positions normalized to the target's bounding box.
        LedPos[] src;
        if (dev.LedPositions is { Count: > 0 } p && p.Count == dev.LedCount)
            src = p.ToArray();
        else
        {
            src = new LedPos[dev.LedCount];
            for (int i = 0; i < src.Length; i++)
                src[i] = new LedPos(dev.LedCount <= 1 ? 0.5f : i / (float)(dev.LedCount - 1), 0.5f);
        }
        var pos = new LedPos[count];
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            var q = src[Math.Min(off + i, src.Length - 1)];
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        float rx = maxX - minX, ry = maxY - minY;
        for (int i = 0; i < count; i++)
        {
            var q = src[Math.Min(off + i, src.Length - 1)];
            pos[i] = new LedPos(rx > 1e-4f ? (q.X - minX) / rx : 0.5f,
                                ry > 1e-4f ? (q.Y - minY) / ry : 0.5f);
        }

        var style = zone?.IsFan == true ? PreviewStyle.Fan
                  : dev.Type == DeviceType.Keyboard ? PreviewStyle.Keys
                  : PreviewStyle.Dots;
        double aspect = style == PreviewStyle.Fan ? 1.0
                      : dev.PreviewAspect ?? (style == PreviewStyle.Keys ? 3.2 : 1.6);

        // Exact per-LED footprints, only when the target is the whole device
        // (zone slices fall back to the position-based heuristic).
        LedRect[]? rects = null;
        if (zone == null && dev.LedGeometry is { } g && g.Count == dev.LedCount)
            rects = g.ToArray();
        return (colors, pos, style, aspect, rects);
    }

    /// <summary>Click-to-paint one LED of the selected target with the current color.</summary>
    public void PaintLed(int i) => SetLed(i, Current);

    /// <summary>Right-click clears one LED (off/black).</summary>
    public void ClearLed(int i) => SetLed(i, Rgb.Black);

    void SetLed(int i, Rgb color)
    {
        var dev = SelectedDevice;
        if (dev == null) return;
        var zone = SelectedTarget?.Zone;
        int off = zone?.Offset ?? 0;
        int count = zone?.Count ?? dev.LedCount;
        if (i < 0 || i >= count) return;

        // An effect is animating this LED: a static write would only blink
        // (overwritten next frame), so send nothing.
        int abs = off + i;
        if (_engine.ChannelsFor(dev).Any(ch => abs >= ch.Offset && abs < ch.Offset + ch.Count)) return;

        var frame = FrameFor(dev);
        if (off + i < frame.Length) frame[off + i] = color;
        if (dev is IZoneWritable zw && zone != null) _lighting.PushZone(zw, dev, off, count);
        else _lighting.PushFrame(dev);
        MarkDirty();
    }
}
