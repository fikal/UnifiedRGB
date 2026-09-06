using System;
using System.Collections.Generic;
using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App.Services;

/// <summary>Charge for wireless gear: the left list's subtitle, and a sensor
/// source so a rule can act on it ("battery at or below 15% -> Low battery").
///
/// Slow and lazy on purpose. A battery query is a round trip to a device that
/// may be asleep, so it runs once a minute, only while something wireless is
/// actually attached, and on the device's own applier lane so it can never
/// interleave with a lighting write on the same transport. Nothing here runs
/// per frame.</summary>
public sealed class BatteryMonitor
{
    /// <summary>At or below this, and not charging, the subtitle turns amber.</summary>
    public const int LowPercent = 15;

    readonly CoalescingApplier _applier;
    readonly Func<IReadOnlyList<IRgbDevice>> _devices;
    readonly Action _changed;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly object _lock = new();
    /// <summary>Keyed by the device itself, not by its name. Two devices can
    /// share a name (a Razer mouse and its own dongle both report the model
    /// name), and one shared slot made them overwrite each other every poll.</summary>
    readonly Dictionary<IRgbDevice, SensorHub.BatteryLevel> _levels = new();

    public BatteryMonitor(CoalescingApplier applier, Func<IReadOnlyList<IRgbDevice>> devices, Action changed)
    {
        _applier = applier;
        _devices = devices;
        _changed = changed;
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Call after a detect. Readings are dropped (the device instances
    /// they came from are gone) and the timer only runs if anything wireless
    /// turned up, so an all-wired rig pays nothing at all.</summary>
    public void Rescan()
    {
        bool any = false;
        foreach (var d in _devices())
            if (d is IBatteryDevice) { any = true; break; }

        lock (_lock) _levels.Clear();
        Publish();

        _timer.Stop();
        if (!any) { _changed(); return; }
        _timer.Start();
        Poll();                      // don't make the user wait a minute for the first one
    }

    /// <summary>Stop polling for good. Called on the way out, BEFORE the device
    /// handles close: a tick that got through afterwards would read from a
    /// disposed handle.</summary>
    public void Stop()
    {
        _timer.Stop();
        lock (_lock) _levels.Clear();
    }

    /// <summary>Latest charge for a device, or null when it has no battery or
    /// has not answered yet.</summary>
    public SensorHub.BatteryLevel? Of(IRgbDevice d)
    {
        lock (_lock) return _levels.TryGetValue(d, out var l) ? l : null;
    }

    void Poll()
    {
        foreach (var d in _devices())
        {
            if (d is not IBatteryDevice batt) continue;
            var dev = d;
            // Latest-wins per device: a poll queued behind a slow lane replaces
            // the one already waiting rather than piling up.
            _applier.Post(LightingController.LaneOf(dev), ("battery", dev), () =>
            {
                var reading = batt.ReadBattery();
                bool changed;
                lock (_lock)
                {
                    _levels.TryGetValue(dev, out var was);
                    if (reading is BatteryReading b)
                    {
                        var now = new SensorHub.BatteryLevel(dev.Name, b.Percent, b.Charging);
                        changed = was != now;
                        _levels[dev] = now;
                    }
                    else
                    {
                        // No answer: keep nothing rather than a stale number a
                        // rule would keep acting on.
                        changed = _levels.Remove(dev);
                    }
                }
                if (!changed) return;
                Publish();                                   // rules read this
                _timer.Dispatcher.BeginInvoke(_changed);     // the UI reads it on its own thread
            });
        }
    }

    void Publish()
    {
        SensorHub.BatteryLevel[] snapshot;
        lock (_lock)
        {
            snapshot = new SensorHub.BatteryLevel[_levels.Count];
            _levels.Values.CopyTo(snapshot, 0);
        }
        SensorHub.PublishBatteries(snapshot);
    }
}
