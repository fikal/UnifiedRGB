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
    /*-----------------------------------------------------*\
    | Lian Li animation baking: the wireless fans loop a     |
    | multi-frame animation in HARDWARE. Streaming single    |
    | frames over RF is capped at ~8 fps (the lag). Instead  |
    | we bake one loop of all the device's bakeable effects  |
    | into N frames and upload ONCE; the receiver plays it    |
    | smoothly. Live effects (audio/temp/wallpaper) can't be |
    | baked, so those fall back to streaming.                |
    \*-----------------------------------------------------*/
    DispatcherTimer? _lianRebakeTimer;
    readonly Dictionary<LianLiWireless, string> _lastBakeSig = new();   // skip redundant re-bakes

    public void RequestLianRebake()
    {
        _lianRebakeTimer ??= CreateLianRebakeTimer();
        _lianRebakeTimer.Stop();
        _lianRebakeTimer.Start();
    }

    DispatcherTimer CreateLianRebakeTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) => { t.Stop(); foreach (var d in Devices) if (d is LianLiWireless lw) RebakeLian(lw); };
        return t;
    }

    void RebakeLian(LianLiWireless dev)
    {
        var channels = _engine.ChannelsFor(dev);
        if (channels.Count == 0) { dev.SuppressStreaming = false; UnifiedRgb.Core.Log.Info("LianBake", "no channels - streaming/static"); return; }
        if (!channels.All(c => c.Effect.Bakeable)) { dev.SuppressStreaming = false; UnifiedRgb.Core.Log.Info("LianBake", $"live effect present ({channels.First(c => !c.Effect.Bakeable).Effect.Name}) - streaming"); return; }

        // Skip the re-bake if nothing about the effect actually changed. Phase
        // alignment re-samples the clock every bake, so without this a redundant
        // re-apply (pressing All devices again) would re-upload a phase-shifted
        // copy and visibly reset the fans mid-loop while streamed devices flow on.
        string sig = string.Join("|", channels.OrderBy(c => c.Offset).Select(c =>
            $"{c.Offset}:{c.Count}:{c.Effect.Name}:{c.Speed}:{c.BaseColor}:" +
            (c.Effect is UnifiedRgb.Core.Effects.IPaletteEffect pe ? string.Join(",", pe.Palette) : "")));
        if (dev.SuppressStreaming && _lastBakeSig.TryGetValue(dev, out var prev) && prev == sig) return;
        _lastBakeSig[dev] = sig;

        dev.SuppressStreaming = true;
        // Up to 12s so full-hue-turn effects (Rainbow Cycle 9s, Color Cycle 12s)
        // bake a complete loop instead of being clipped into a seam.
        double T = Math.Clamp(channels.Max(c => c.Effect.LoopSeconds(c.Speed)), 1.5, 12.0);
        // Frame count is chosen so the per-frame interval lands in the hardware's
        // honored range (L-Connect never exceeds ~77ms = SpeedType 7 x 11). A
        // large interval (e.g. 64 frames over 9s = 140ms) gets clamped by the
        // receiver and plays too fast, out of sync with the streamed devices. So
        // pick N to target ~60ms/frame: more frames, smaller interval, same loop.
        int N = (int)Math.Clamp(Math.Round(T * 1000.0 / 60.0), 32, 160);
        UnifiedRgb.Core.Log.Info("LianBake", $"baking {channels.Count} channel(s) [{string.Join(",", channels.Select(c => c.Effect.Name))}], T={T:0.0}s, N={N}");
        // Snapshot the statics under the UI thread; the render loop itself runs
        // on a WORKER — 28k+ LED evaluations per device was a visible dispatcher
        // hitch. A generation stamp makes a superseded bake's upload a no-op
        // (a slower older bake can otherwise finish after a newer one).
        var baseFrame = (Rgb[])FrameFor(dev).Clone();
        int myGen = _bakeGen.AddOrUpdate(dev, 1, (_, g) => g + 1);
        // Bake from the clock's current phase so the fans' frame 0 is the same
        // point in the cycle the streamed devices are on (red right after an All-
        // devices restart). No look-ahead: on a restart we want them to START on
        // that color, not where the keyboard will have drifted to by upload time.
        double baseTime = _engine.ClockSeconds;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var frames = new Rgb[N][];
            // One scratch buffer per channel, reused across all N frames (the
            // old per-frame allocation threw away ~half the bake's memory).
            var bufs = new Rgb[channels.Count][];
            for (int c = 0; c < channels.Count; c++) bufs[c] = new Rgb[channels[c].Count];
            for (int f = 0; f < N; f++)
            {
                var frame = (Rgb[])baseFrame.Clone();
                double time = baseTime + T * f / N;
                for (int c = 0; c < channels.Count; c++)
                {
                    var ch = channels[c];
                    var buf = bufs[c];
                    if (_engine.RenderChannelAt(ch, buf, time))
                    {
                        UnifiedRgb.Core.Master.Scale(buf);
                        for (int i = 0; i < ch.Count && ch.Offset + i < frame.Length; i++)
                            frame[ch.Offset + i] = buf[i];
                    }
                }
                frames[f] = frame;
            }
            // No seam crossfade: effects bake exactly one period (their real
            // LoopSeconds), so frame N-1 -> frame 0 is already continuous.
            // The upload is paced (many RF packets, sleeps between them) - run it
            // on the device lane. Keyed so a fresh bake supersedes a still-queued
            // one; the generation check drops an out-of-date bake entirely.
            double frameMs = T * 1000.0 / N;
            _applier.Post(LaneOf(dev), (dev, "anim"), () =>
            {
                if (_bakeGen.TryGetValue(dev, out int cur) && cur != myGen) return;   // superseded
                dev.UploadAnimation(frames, frameMs);
            });
        });
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<LianLiWireless, int> _bakeGen = new();

    /// <summary>Repaint a range with its stored static colors.</summary>
    void RestoreStatics(IRgbDevice dev, int off, int count)
    {
        var frame = FrameFor(dev);
        if (dev is IZoneWritable zw)
        {
            var slice = new Rgb[count];
            for (int i = 0; i < count; i++) slice[i] = off + i < frame.Length ? frame[off + i] : Rgb.Black;
            _applier.Post(LaneOf(dev), (dev, off), () => { UnifiedRgb.Core.Master.Scale(slice); zw.SetZone(off, slice); });
        }
        else
        {
            var snap = (Rgb[])frame.Clone();
            _applier.Post(LaneOf(dev), dev, () => { UnifiedRgb.Core.Master.Scale(snap); dev.SetColors(snap); });
        }
    }

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

    Rgb[] FrameFor(IRgbDevice d)
    {
        if (!_frames.TryGetValue(d, out var f))
        {
            f = new Rgb[d.LedCount];
            _frames[d] = f;
        }
        return f;
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
        {
            foreach (var (off, cnt) in ranges)
            {
                var slice = new Rgb[cnt];
                Array.Fill(slice, Current);
                int o = off;
                _applier.Post(LaneOf(dev), (dev, o), () => { UnifiedRgb.Core.Master.Scale(slice); zw.SetZone(o, slice); });
            }
        }
        else
        {
            var snapshot = (Rgb[])frame.Clone();
            _applier.Post(LaneOf(dev), dev, () => { UnifiedRgb.Core.Master.Scale(snapshot); dev.SetColors(snapshot); });
        }
        RequestLianRebake();
        MarkDirty();
    }

    /// <summary>Full device frame = static colors with every running channel
    /// composited in (what the hardware is actually showing).</summary>
    Rgb[] ComposedFrame(IRgbDevice dev)
    {
        var frame = (Rgb[])FrameFor(dev).Clone();
        foreach (var ch in _engine.ChannelsFor(dev))
        {
            var buf = new Rgb[ch.Count];
            if (_engine.RenderChannel(ch, buf))
                for (int i = 0; i < buf.Length && ch.Offset + i < frame.Length; i++)
                    frame[ch.Offset + i] = buf[i];
        }
        return frame;
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

    /// <summary>Part buttons for the fan editor: All fans, Whole fan, then each
    /// of the selected device's parts (center/inner, outer, and side if it has
    /// one). Data-driven so both the 3-part wireless and 2-part wired fans work.</summary>
    public IReadOnlyList<LianPartButton> LianParts
    {
        get
        {
            var list = new List<LianPartButton> { new("All fans", -1), new("Whole fan", 0) };
            if (LianDev is { } d)
                for (int i = 0; i < d.LianFanParts.Count; i++) list.Add(new(d.LianFanParts[i].Name, i + 1));
            return list;
        }
    }

    /// <summary>Part LED counts for the fan-view drawing: (center/inner, outer,
    /// side, sideInOuter). Side 0 = no side strips. sideInOuter = the side
    /// rectangles are cosmetic and mirror the outer ring (wired SL-Infinity).</summary>
    public (int Center, int Outer, int Side, bool SideInOuter) LianFanPartCounts
    {
        get
        {
            var p = LianDev?.LianFanParts;
            if (p == null || p.Count == 0) return (8, 20, 16, false);
            int center = p[0].Count;
            int outer = p.Count > 1 ? p[1].Count : 0;
            if (p.Count > 2) return (center, outer, p[2].Count, false);   // wireless: real side part
            // Wired SL-Infinity has one outer group (12) that lights the ring AND
            // the side glow (L-Connect's SLInfinityOuter). Draw cosmetic side
            // rectangles that mirror the outer ring so it reads like the wireless.
            return (center, outer, 8, true);
        }
    }
    // The fan pills + clickable model replace the Zone dropdown here.
    public bool ShowZonePicker => HasMultipleTargets && !IsLianLiSelected;

    public List<string> LianFanNames { get; private set; } = new();
    int _lianFan, _lianPart;

    // Two independent dimensions: which fan(s) - _lianFan (-1 = All fans) - and
    // which part - _lianPart (0 = whole, 1..N = the device's parts). "All fans"
    // is a FAN scope that combines with the current part (All + Outer = every
    // fan's outer ring), NOT a part that collapses to the whole device.
    public int LianFan
    {
        get => _lianFan;
        set
        {
            if (value < 0) return;   // ListBox clears to -1 while All-fans scope is active
            _lianFan = value;        // pick a single fan; keep the current part
            _pendingChoice = null;   // fan switches never spread modes
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

    public void LianRightClicked(int led) { }

    /// <summary>User-defined fan groups (from the Arrange fans dialog): each
    /// is its own effect canvas spanning just its fans.</summary>
    public List<string> LianGroupNames => (SelectedDevice as LianLiWireless)?.Zones
        .Where(z => z.Name.StartsWith("Group ")).Select(z => z.Name).ToList() ?? new();
    public bool HasLianGroups => LianGroupNames.Count > 0;

    /// <summary>Arrange only matters with 2+ fans to order.</summary>
    // Arrange (re-order + group) is a wireless feature; the wired chain is fixed.
    public bool ShowArrangeFans => SelectedDevice is LianLiWireless && LianFanNames.Count > 1;

    /*--- exit handoff: wireless fans follow their SYS-fan sync wire while
          the app is away, so a hardware curve stays in charge ---*/
    public bool ShowLianHandoff => LianLiWireless.Instance != null;
    public bool LianHandoffOnExit
    {
        get => _store.Settings.LianHandoffOnExit;
        set { _store.Settings.LianHandoffOnExit = value; _store.SaveSettings(); OnChanged(); }
    }

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
        if (_pendingChoice?.Effect != null && !ReferenceEquals(ChoiceOf(CurrentFx()), _pendingChoice))
        {
            CurrentFx().Choice = _pendingChoice;
            NotifyModeChanged();
            ApplyFx();
        }
    }

    // The mode the user picked most recently, waiting to be carried onto the
    // next part they click. One-shot; cleared on device/fan switches. This is
    // what makes BOTH orders work: part-then-mode (the app's native flow) and
    // mode-then-part (how people actually think).
    EffectChoice? _pendingChoice;

    public void SelectLianPart(int part, bool carryPending = false)
    {
        if (SelectedDevice is not ILianFanDevice dev) return;
        // "All fans" (-1) sets only the FAN scope and keeps the current part;
        // a part (0..N) sets only the part and keeps the fan scope.
        if (part == -1) _lianFan = -1;
        else if (part >= 0) _lianPart = part;
        UpdateLianTarget(dev);
        OnChanged(nameof(LianFan)); OnChanged(nameof(LianSelectionLabel));

        // Mode brush (mode-first flow): if the user picked an EFFECT just before,
        // carry it onto the clicked part so a mode can be painted across parts.
        // Never carries a color, and never brushes Static - selecting a part must
        // not repaint it. Cleared on fan/device switches.
        if (carryPending && _pendingChoice?.Effect != null
            && !ReferenceEquals(ChoiceOf(CurrentFx()), _pendingChoice))
        {
            CurrentFx().Choice = _pendingChoice;
            NotifyModeChanged();
            ApplyFx();
        }
    }

    /// <summary>Point SelectedTarget at the representative zone for the current
    /// fan-scope × part (fan 0 stands in when the scope is "all"), so the wheel
    /// and mode selector show that selection's state. Sync-only: guarded so it
    /// never writes hardware (the wheel round-trip would otherwise repaint).</summary>
    void UpdateLianTarget(ILianFanDevice dev)
    {
        int per = dev.LianLedsPerFan;
        int repFan = _lianFan < 0 ? 0 : _lianFan;
        TargetItem? t;
        if (_lianFan < 0 && _lianPart == 0)
            t = Targets.FirstOrDefault(x => x.Zone == null);          // all fans + whole = whole device
        else
        {
            (int off, int cnt) = _lianPart >= 1 && _lianPart <= dev.LianFanParts.Count
                ? (repFan * per + dev.LianFanParts[_lianPart - 1].Offset, dev.LianFanParts[_lianPart - 1].Count)
                : (repFan * per, per);
            t = Targets.FirstOrDefault(x => x.Zone != null && x.Zone.Offset == off && x.Zone.Count == cnt);
        }
        if (t != null)
        {
            bool was = _selectionChanging;
            _selectionChanging = true;
            try { SelectedTarget = t; }
            finally { _selectionChanging = was; }
        }
    }

    /// <summary>The device ranges an apply (color/effect) writes for the current
    /// fan-scope × part: one range per fan (all fans, or just the selected one),
    /// covering the whole fan or just the chosen part.</summary>
    List<(int off, int cnt)> LianApplyRanges()
    {
        var list = new List<(int, int)>();
        if (LianDev is not { } dev) return list;
        int per = dev.LianLedsPerFan;
        var fans = _lianFan < 0 ? Enumerable.Range(0, dev.LianFanCount) : Enumerable.Range(_lianFan, 1);
        foreach (int f in fans)
            list.Add(_lianPart >= 1 && _lianPart <= dev.LianFanParts.Count
                ? (f * per + dev.LianFanParts[_lianPart - 1].Offset, dev.LianFanParts[_lianPart - 1].Count)
                : (f * per, per));
        return list;
    }

    /// <summary>True when an apply must fan out to a specific part on EVERY fan
    /// (All fans + inner/outer) - those zones aren't contiguous, so the single
    /// selected zone can't cover them. "All fans + whole" is contiguous and rides
    /// the whole-device zone, so it's not a fan-out.</summary>
    bool LianFanOut => IsLianLiSelected && _lianFan < 0 && _lianPart > 0;

    /// <summary>Human label for the current fan-scope × part (status line).</summary>
    public string LianSelectionLabel
    {
        get
        {
            if (LianDev is not { } d) return "";
            string fan = _lianFan < 0 ? "All fans" : $"Fan {_lianFan + 1}";
            string part = _lianPart == 0 ? "Whole fan"
                : _lianPart - 1 < d.LianFanParts.Count ? d.LianFanParts[_lianPart - 1].Name : "";
            return $"{fan}  ·  {part}";
        }
    }

    /// <summary>Live per-fan LED slice + which part is selected (kept in sync
    /// with the target both ways). Works for wireless (44 LEDs, 3 parts) and the
    /// wired hub (20 LEDs, 2 parts) via ILianFanDevice.</summary>
    public (Rgb[] Colors, int SelectedPart) LianLiView()
    {
        if (SelectedDevice is not ILianFanDevice lf || SelectedDevice is not IRgbDevice dev)
            return (Array.Empty<Rgb>(), -1);
        int per = lf.LianLedsPerFan;

        // Zone picked from the dropdown for a different fan: follow it - but NOT
        // while the All-fans scope is active (_lianFan < 0), or the representative
        // fan-0 zone would silently collapse the scope back to a single fan.
        var z = SelectedTarget?.Zone;
        if (z != null && _lianFan >= 0)
        {
            int f = z.Offset / per;
            if (f != _lianFan && f < LianFanNames.Count)
            { _lianFan = f; OnChanged(nameof(LianFan)); }
        }

        // All fans: draw fan 0 as the representative (the selected part still
        // highlights, and it applies to every fan).
        int viewFan = _lianFan < 0 ? 0 : _lianFan;
        var frame = ComposedFrame(dev);
        var slice = new Rgb[per];
        int b = viewFan * per;
        for (int i = 0; i < per && b + i < frame.Length; i++) slice[i] = frame[b + i];

        // Highlight the selected part on the drawn fan. Derived from _lianPart so
        // it's correct even in all-fans mode (where the zone points at fan 0).
        int sel = _lianPart >= 0 && _lianPart <= lf.LianFanParts.Count ? _lianPart : -1;
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
        if (dev is IZoneWritable zw && zone != null)
        {
            var slice = new Rgb[count];
            for (int k = 0; k < count; k++)
                slice[k] = off + k < frame.Length ? frame[off + k] : Rgb.Black;
            _applier.Post(LaneOf(dev), (dev, off), () => { UnifiedRgb.Core.Master.Scale(slice); zw.SetZone(off, slice); });
        }
        else
        {
            var snap = (Rgb[])frame.Clone();
            _applier.Post(LaneOf(dev), dev, () => { UnifiedRgb.Core.Master.Scale(snap); dev.SetColors(snap); });
        }
        MarkDirty();
    }
}
