using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App;

/// <summary>The Cooling pane's GPU power-connector card, fed by a WireView Pro
/// II on the 12V-2x6 cable. Hidden entirely on machines without one. The
/// Cooling refresh drives it (Refresh each tick, Release when the pane goes),
/// so the COM port is only held while the numbers are on screen.</summary>
public sealed class GpuPowerCardModel : INotifyPropertyChanged
{
    /// <summary>12V-2x6 contact rating: past this a pin is over spec.</summary>
    const double PinRatingA = 9.5;
    /// <summary>One pin's share of a full 600 W cable (600 / 12 V / 6 pins).
    /// A pin above it is carrying more than a balanced maximum load would.</summary>
    const double PinShareA = 600.0 / 12 / 6;
    /// <summary>Below this average the per-pin spread is sensor noise, not
    /// imbalance (idle pins at ~0.3 A read +/-20% of each other).</summary>
    const double BalanceMinAvgA = 2.0;
    public const double BarWidth = 150;

    public ObservableCollection<GpuPowerPinRow> Pins { get; } =
        new(Enumerable.Range(1, 6).Select(i => new GpuPowerPinRow($"Pin {i}")));
    public ObservableCollection<SensorRow> Temps { get; } = new();

    public bool IsVisible { get; private set; }
    public bool IsConnected { get; private set; }
    public string Message { get; private set; } = "";
    public string DeviceText { get; private set; } = "";
    public string WattsText { get; private set; } = "--";
    public string DetailText { get; private set; } = "";
    public string CapText { get; private set; } = "";
    public string BalanceText { get; private set; } = "";
    public bool HasFault { get; private set; }
    public string FaultText { get; private set; } = "";
    public bool HasFaultLog { get; private set; }
    public string FaultLogText { get; private set; } = "";

    /// <summary>One Cooling tick.</summary>
    public void Refresh()
    {
        _active = true;
        _quickTries = 0;
        Update();
    }

    /// <summary>The pane left the screen: hand the port back now rather than
    /// after the poller's idle timeout.</summary>
    public void Release()
    {
        _active = false;
        _soon?.Stop();
        WireView.Release();
    }

    // When the pane opens, the poller's first frame lands ~10 ms after Touch
    // but the next Cooling tick is 1.5 s away. Look again shortly (a bounded
    // number of times, so a device that has stopped answering cannot keep
    // this re-touching the port) instead of sitting on "Connecting…".
    DispatcherTimer? _soon;
    bool _active;
    int _quickTries;
    const int MaxQuickTries = 5;

    void RefreshSoon()
    {
        if (_quickTries >= MaxQuickTries) return;
        if (_soon == null)
        {
            _soon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _soon.Tick += (_, _) => { _soon.Stop(); if (_active) { _quickTries++; Update(); } };
        }
        if (!_soon.IsEnabled) _soon.Start();
    }

    void Update()
    {
        WireView.Touch();
        var info = WireView.Current;   // state, name and firmware as one value
        var state = info.State;
        var r = WireView.Latest;

        IsVisible = state != WireView.State.NotFound;
        IsConnected = r != null;
        Message = state switch
        {
            WireView.State.Busy => "The WireView is open in another program. Close Thermal Grizzly's app (or HWiNFO) to see it here.",
            WireView.State.Unsupported => $"Found a {info.Edition}, but UnifiedRGB can't read its sensors yet.",
            _ => "Connecting…",
        };
        DeviceText = state == WireView.State.Connected && info.Edition is string ed ? $"{ed} · FW v{info.Firmware}" : "";

        if (r != null) Show(r);
        else if (state == WireView.State.Connected || !WireView.HasScanned) RefreshSoon();
        foreach (var n in new[] { nameof(IsVisible), nameof(IsConnected), nameof(Message), nameof(DeviceText),
                                  nameof(WattsText), nameof(DetailText), nameof(CapText), nameof(BalanceText),
                                  nameof(HasFault), nameof(FaultText), nameof(HasFaultLog), nameof(FaultLogText) })
            OnChanged(n);
    }

    void Show(WireViewReading r)
    {
        WattsText = $"{r.Watts:0} W";
        DetailText = $"{r.Amps:0.00} A · {r.Volts:0.00} V";
        CapText = r.PsuCapWatts is int cap ? $"{cap} W cable" : "";

        for (int i = 0; i < Pins.Count && i < r.Pins.Length; i++)
        {
            var p = r.Pins[i];
            Pins[i].Update($"{p.Amps:0.00} A",
                $"{p.Volts:0.000} V · {p.Amps:0.00} A · {p.Watts:0.0} W",
                Math.Clamp(p.Amps / PinRatingA, 0, 1) * BarWidth,
                p.Amps >= PinRatingA ? "hot" : p.Amps > PinShareA ? "warn" : "ok");
        }

        double avg = r.Pins.Average(p => p.Amps);
        BalanceText = avg >= BalanceMinAvgA
            ? $"Pins within ±{r.Pins.Max(p => Math.Abs(p.Amps - avg)) / avg * 100:0}% of the average"
            : "";

        var temps = new List<SensorRow>();
        void Add(string label, double? c) { if (c is double v) temps.Add(new SensorRow(label, $"{v:0.0} °C")); }
        Add("Adapter in", r.OnboardInC);
        Add("Adapter out", r.OnboardOutC);
        Add("Probe 1", r.External1C);
        Add("Probe 2", r.External2C);
        CoolingViewModel.SyncReadoutRows(Temps, temps);

        HasFault = r.Faults != WireViewFault.None;
        FaultText = HasFault ? "Fault: " + string.Join(", ", WireView.Describe(r.Faults)) : "";
        // The log keeps every fault since it was last cleared; only worth a line
        // when it holds something that is not already showing as active.
        var past = r.FaultLog & ~r.Faults;
        HasFaultLog = past != WireViewFault.None;
        FaultLogText = HasFaultLog ? "Logged earlier: " + string.Join(", ", WireView.Describe(past)) : "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One pin's bar. Updated in place each refresh, so the six rows never
/// rebuild (no flicker, tooltips stay open).</summary>
public sealed class GpuPowerPinRow : INotifyPropertyChanged
{
    public GpuPowerPinRow(string label) => Label = label;

    public string Label { get; }
    public string AmpsText { get; private set; } = "--";
    public string Detail { get; private set; } = "";
    public double BarWidth { get; private set; }
    /// <summary>"ok", "warn" (above a balanced 600 W share) or "hot" (over the
    /// contact rating); the bar's colour.</summary>
    public string Level { get; private set; } = "ok";

    public void Update(string amps, string detail, double width, string level)
    {
        if (AmpsText != amps) { AmpsText = amps; OnChanged(nameof(AmpsText)); }
        if (Detail != detail) { Detail = detail; OnChanged(nameof(Detail)); }
        if (BarWidth != width) { BarWidth = width; OnChanged(nameof(BarWidth)); }
        if (Level != level) { Level = level; OnChanged(nameof(Level)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
