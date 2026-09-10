using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Core;

/// <summary>Central registry: detects every supported device across all
/// transports and exposes them as a single list for the UI. New device types
/// are added by registering a factory here.</summary>
public sealed class DeviceManager : IDisposable
{
    readonly List<IRgbDevice> _devices = new();
    readonly Dictionary<IRgbDevice, string> _family = new();

    /// <summary>What the last COMPLETED pass found, by name and family.
    ///
    /// Deliberately not cleared by Dispose. Rescan's order is dispose, then
    /// detect, so anything Dispose cleared would be gone by the time the new
    /// pass wanted to compare against it - and the comparison is the whole
    /// point: a device that was in this map and is not in the new list is a
    /// device the user just unplugged, which is the single most useful thing
    /// a scan can notice and the thing the app said nothing about before.</summary>
    readonly Dictionary<string, string> _lastSeen = new(StringComparer.Ordinal);

    public IReadOnlyList<IRgbDevice> Devices => _devices;

    /// <summary>Driver-family name (factory type) that produced each device —
    /// the unit the disable feature skips so hardware is never even opened.</summary>
    public IReadOnlyDictionary<IRgbDevice, string> FamilyOf => _family;

    /// <summary>Factories for each device family. Each returns a device if its
    /// hardware is present, or null. Add new devices (mobo, LNP, mouse, fans,
    /// RAM, GPU) here as they are implemented.</summary>
    internal static readonly Func<IRgbDevice?>[] Factories =
    {
        CorsairStrafeMk2.TryOpen,
        SteelSeriesApex.TryOpen,
        GigabyteIt5711.TryOpen,
        LogitechG403.TryOpen,
        MsiGpu.TryOpen,
        SayoDevice.TryOpen,
        LianLiWireless.TryOpen,
        LianLiUniHub.TryOpen,
    };

    /// <summary>Families that can yield several devices at once (DRAM sticks,
    /// the OpenRGB bridge).</summary>
    internal static readonly Func<List<IRgbDevice>>[] MultiFactories =
    {
        EneDram.DetectAll,
        RazerHid.DetectAll,
        Net.OpenRgbLink.DetectAll,
    };

    /// <summary>Detect everything. skipFamily (by factory type name) lets the
    /// app honor user-disabled devices WITHOUT ever opening the hardware —
    /// some inits write packets, and the whole point of disabling is to leave
    /// the device to other software.</summary>
    public void DetectAll(Func<string, bool>? skipFamily = null)
    {
        // Absent detectors are collected and reported as ONE line. A line each
        // was 47% of a real user's three week log, which buries everything a
        // bundle is read for.
        // Notes describe the pass we are about to run, not the last one.
        DetectionNotes.Clear();
        // Same for the Logitech claim set the OpenRGB bridge consults: TryOpen
        // clears it itself, but a pass that SKIPS the family (the user disabled
        // it) never calls TryOpen, and the bridge would then keep hiding a mouse
        // nobody drives - the very case the claim set exists to fix.
        LogitechG403.ClearClaimed();
        var absent = new List<string>();
        // Families the user turned off are not "missing" - they were never
        // asked for. Without this a disable would report every device in the
        // family as having gone away, once, which is a lie with an alarming
        // remedy attached to it.
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var factory in Factories)
        {
            string name = factory.Method.DeclaringType?.Name ?? "?";
            if (skipFamily?.Invoke(name) == true) { skipped.Add(name); Log.Info("detect", $"{name}: skipped (disabled)"); continue; }
            try
            {
                var dev = factory();
                if (dev != null)
                {
                    _devices.Add(dev);
                    _family[dev] = name;
                    Log.Info("detect", $"{name}: FOUND '{dev.Name}' ({dev.LedCount} LEDs)");
                }
                else absent.Add(name);
            }
            catch (Exception ex)
            {
                Log.Error("detect", $"{name} threw: {ex}");
                Console.Error.WriteLine($"[DeviceManager] {name} failed: {ex.Message}");
            }
        }
        foreach (var factory in MultiFactories)
        {
            string name = factory.Method.DeclaringType?.Name ?? "?";
            if (skipFamily?.Invoke(name) == true) { skipped.Add(name); Log.Info("detect", $"{name}: skipped (disabled)"); continue; }
            try
            {
                var found = factory();
                _devices.AddRange(found);
                // OpenRGB proxies get per-device families so one can be
                // disabled without disabling the whole bridge.
                foreach (var d in found)
                    _family[d] = d is OpenRgbDevice ? $"OpenRgb:{d.Name}" : name;
                Log.Info("detect", $"{name}: {found.Count} device(s)" +
                    (found.Count > 0 ? " - " + string.Join(", ", found.Select(d => d.Name)) : ""));
            }
            catch (Exception ex)
            {
                Log.Error("detect", $"{name} threw: {ex}");
                Console.Error.WriteLine($"[DeviceManager] {name} failed: {ex.Message}");
            }
        }

        NoteDevicesThatWentAway(skipped);

        if (absent.Count > 0) Log.Info("detect", "not present: " + string.Join(", ", absent));
        // Said individually and at WARN: each of these is a device the user
        // owns and expects to see, and the reason is the whole answer to the
        // question they are about to ask.
        foreach (var b in DetectionNotes.Current)
            Log.Warn("detect", $"{b.What}: {b.ReasonText} - {b.Detail}"
                             + (b.Remedy != null ? $" -> {b.Remedy}" : ""));
        Log.Info("detect", $"{_devices.Count} device(s): "
            + (_devices.Count == 0 ? "none" : string.Join(", ", _devices.Select(d => $"{d.Name} ({d.LedCount})"))));
    }

    /// <summary>Compare this pass against the last one and say, in the same
    /// channel every other "seen but not usable" reason goes to, which devices
    /// have stopped being here.
    ///
    /// This is the detection half of the health signal. The write verdict
    /// catches a device that is still enumerated but has stopped answering; a
    /// device that has been physically pulled stops being enumerated at all,
    /// and there is no write left to refuse. Only a scan can see that, and
    /// only by remembering what it saw last time.
    ///
    /// Keyed by NAME rather than instance, because the instances of the last
    /// pass are exactly the ones that have just been disposed. Two devices
    /// sharing a name collapse into one entry here, which is acceptable for a
    /// message whose whole content is "one of your devices is gone".</summary>
    void NoteDevicesThatWentAway(HashSet<string> skippedFamilies)
    {
        if (_lastSeen.Count > 0)
        {
            var present = new HashSet<string>(_devices.Select(d => d.Name), StringComparer.Ordinal);
            foreach (var (name, family) in _lastSeen)
            {
                if (present.Contains(name) || skippedFamilies.Contains(family)) continue;
                DetectionNotes.Report(family, name, BlockReason.WentAway,
                    "it answered on the last scan and is not on the bus now",
                    "check the cable or the dongle; it comes back on its own when it does");
            }
        }

        _lastSeen.Clear();
        foreach (var d in _devices)
            _lastSeen[d.Name] = _family.TryGetValue(d, out var f) ? f : "?";
    }

    public void Dispose()
    {
        // One driver throwing (native teardown after a yanked dongle) must not
        // strand the others' handles, and the lists must clear regardless -
        // otherwise Rescan's DetectAll appends fresh devices onto dead ones.
        try
        {
            foreach (var d in _devices)
            {
                try { d.Dispose(); }
                catch (Exception ex) { Log.Error("detect", $"{d.Name} dispose threw: {ex.Message}"); }
            }
        }
        finally
        {
            _devices.Clear();
            _family.Clear();
        }
    }
}
