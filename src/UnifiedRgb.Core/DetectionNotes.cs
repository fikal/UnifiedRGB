namespace UnifiedRgb.Core;

/// <summary>Why something we can SEE is not something we can drive.</summary>
public enum BlockReason
{
    /// <summary>Another program has the device open and will not share it.</summary>
    HeldByOtherSoftware,
    /// <summary>The work needs the PawnIO kernel driver, which needs elevation.</summary>
    NeedsAdministrator,
    /// <summary>A driver we depend on is not installed.</summary>
    DriverMissing,
    /// <summary>We have it, but not everything works.</summary>
    PartlyWorking,
    /// <summary>It is there and the attempt failed for a reason we can name.</summary>
    Failed,
    /// <summary>It was here on the last scan and it is not here now.
    ///
    /// The one reason that is about a CHANGE rather than a state, and the one
    /// the user is most likely to have caused a second ago. Before this, an
    /// unplugged device simply vanished from the list with nothing anywhere
    /// saying it had ever been there, which reads identically to "the app
    /// stopped supporting my keyboard".</summary>
    WentAway,
}

/// <summary>One thing the app can see but cannot fully drive, and what to do
/// about it.</summary>
/// <param name="Family">The detector it came from, matching the device-family
/// names used elsewhere (log lines, the per-device disable list).</param>
/// <param name="What">What the user would call it.</param>
/// <param name="Detail">The specific thing that went wrong.</param>
/// <param name="Remedy">What would fix it, or null when we do not know.</param>
public sealed record BlockedDevice(string Family, string What, BlockReason Reason, string Detail, string? Remedy)
{
    public string ReasonText => Reason switch
    {
        BlockReason.HeldByOtherSoftware => "another program has it",
        BlockReason.NeedsAdministrator => "needs administrator",
        BlockReason.DriverMissing => "a driver is missing",
        BlockReason.PartlyWorking => "partly working",
        BlockReason.WentAway => "it has gone",
        _ => "failed",
    };

    /// <summary>The same fact in the vocabulary of DeviceHealth, so the two
    /// channels the user sees - the health badge on a device we hold, and this
    /// list of things we could not open - are never two different words for
    /// the same situation.
    ///
    /// HeldByOtherSoftware IS "controlled by another app"; that is not a
    /// coincidence, it is the reason this maps at all rather than a parallel
    /// enum being invented next to it. Everything else here is hardware the
    /// app cannot currently drive, which is what "not responding" means to a
    /// person.</summary>
    public DeviceHealthState Health => Reason switch
    {
        BlockReason.HeldByOtherSoftware => DeviceHealthState.ControlledElsewhere,
        _ => DeviceHealthState.NotResponding,
    };

    public override string ToString() =>
        $"{What}: {ReasonText} - {Detail}" + (Remedy != null ? $" ({Remedy})" : "");
}

/// <summary>Collects the reasons a detection pass could not use something.
///
/// A detector returns an IRgbDevice or null, and null has always meant every
/// one of "no such hardware", "the hardware is here but another program owns
/// it", "it needs a driver that is not installed" and "it needs elevation we
/// do not have". Those look identical to the user - the device is simply
/// missing from the list - and identical in a support bundle, which is why
/// "my mouse is not detected" has taken days to answer more than once.
///
/// This is the side channel for the difference. It is deliberately OPT-IN: a
/// detector that says nothing behaves exactly as before, so drivers can start
/// reporting one at a time rather than all thirteen changing signature at
/// once.</summary>
public static class DetectionNotes
{
    static readonly object _lock = new();
    static readonly List<BlockedDevice> _notes = new();

    /// <summary>Record something we could see but could not fully use. Safe to
    /// call from anywhere; duplicates for the same thing are collapsed.</summary>
    public static void Report(string family, string what, BlockReason reason, string detail, string? remedy = null)
    {
        var note = new BlockedDevice(family, what, reason, detail, remedy);
        lock (_lock)
        {
            if (_notes.Any(n => n.Family == family && n.What == what && n.Reason == reason)) return;
            _notes.Add(note);
        }
    }

    /// <summary>Everything recorded since the last Clear.</summary>
    public static IReadOnlyList<BlockedDevice> Current
    {
        get { lock (_lock) return _notes.ToList(); }
    }

    /// <summary>True when anything on the list is a device that was working a
    /// moment ago. That is the difference between "this rig has always had a
    /// blocked device" - which is a settings-screen fact - and "something just
    /// changed", which is worth saying out loud.</summary>
    public static bool AnythingWentAway
    {
        get { lock (_lock) return _notes.Any(n => n.Reason == BlockReason.WentAway); }
    }

    /// <summary>Start a fresh detection pass. Notes describe what happened on
    /// the LAST scan, so a rescan that fixes something must clear the old
    /// reason rather than leaving it on screen.</summary>
    public static void Clear()
    {
        lock (_lock) _notes.Clear();
    }
}
