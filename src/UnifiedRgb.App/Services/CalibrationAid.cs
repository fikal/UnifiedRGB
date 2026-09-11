using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------------*\
| The thing that makes per-device calibration usable.          |
|                                                              |
| Sliders on their own are useless here, and that is not a UI  |
| nicety - it is the whole reason a "color calibration" pane   |
| either works or does not. Nobody can trim a keyboard to      |
| match fans they cannot see at the same moment: the eye has   |
| no absolute memory for white point, so a user adjusting one  |
| device at a time is comparing the fans in front of them with |
| a remembered keyboard, and remembered whites are always      |
| wrong. Every real display calibration works the same way     |
| this one does: put the SAME requested value on everything    |
| at once and let the difference be visible side by side.      |
|                                                              |
| So the aid drives every selected device to one reference     |
| patch - white first, then mid grey - through the ordinary    |
| write path, trim included. That last part matters: the user  |
| must be looking at TRIMMED output while they move the        |
| sliders, or they would be matching devices that are still    |
| mismatched the instant the pane closes.                      |
|                                                              |
| Why white AND grey. White finds the balance error: every     |
| channel is at maximum, so what you see is the emitter's own  |
| color and nothing else. But white cannot show a gamma error  |
| at all, because every device is pinned at its ceiling there  |
| - two devices can agree perfectly on white and still         |
| disagree completely at 40%, which is where most lighting     |
| actually lives. Mid grey is where that shows up. The         |
| primaries are in the cycle because once white looks wrong,   |
| the next question is WHICH channel, and a red-only patch     |
| answers it in a second.                                      |
|                                                              |
| This class owns no UI. It is the logic a view model drives:  |
| Start, Show/Next, Refresh after every slider move, Stop.     |
\*-----------------------------------------------------------*/
public sealed class CalibrationAid
{
    readonly LightingController _lighting;

    /// <summary>The devices the aid is currently driving. Its own list rather
    /// than a live reference to the caller's, because a rescan can replace the
    /// caller's collection underneath a running session and the aid still has
    /// to be able to put back what it took over.</summary>
    readonly List<IRgbDevice> _devices = new();

    readonly Action? _capture, _restore, _refresh;
    public CalibrationAid(LightingController lighting, Action? capture = null, Action? restore = null, Action? refresh = null)
    {
        _lighting = lighting;
        _capture = capture;
        _restore = restore;
        _refresh = refresh;
    }

    /// <summary>True while reference patches are on the hardware. The view
    /// model shows the patch controls on this and, more importantly, knows
    /// that what the user is looking at is NOT their lighting.</summary>
    public bool Active { get; private set; }

    /// <summary>The patch currently displayed.</summary>
    public CalibrationReference Reference { get; private set; } = CalibrationReference.White;

    /// <summary>The devices being driven, for the view model's list.</summary>
    public IReadOnlyList<IRgbDevice> Devices => _devices;

    /// <summary>Take over the given devices and put the first patch up.
    ///
    /// Effects on those devices are STOPPED. They have to be: an effect worker
    /// streams at up to 60 fps and would repaint the patch between one glance
    /// and the next, so a calibration screen with an effect running behind it
    /// is a calibration screen that shows nothing usable. The capture callback
    /// runs before stopping effects; the restore callback puts that exact live
    /// state back on Stop. Standalone callers without callbacks get a static
    /// frame restore.</summary>
    public void Start(IEnumerable<IRgbDevice>? devices, CalibrationReference reference = CalibrationReference.White)
    {
        if (devices == null) return;
        Stop();   // never leave a previous session's devices lit and forgotten

        foreach (var d in devices)
            if (d != null && d.LedCount > 0) _devices.Add(d);
        if (_devices.Count == 0) return;
        _capture?.Invoke();

        Active = true;
        Reference = reference;
        foreach (var d in _devices)
            _lighting.Engine.StopRange(d, 0, d.LedCount);
        Log.Info("calibration", $"calibration aid started on {_devices.Count} device(s): {string.Join(", ", _devices.Select(d => d.Name))}");
        Push();
    }

    /// <summary>Show a specific patch.</summary>
    public void Show(CalibrationReference reference)
    {
        Reference = reference;
        if (Active) Push();
    }

    /// <summary>Advance to the next patch in the cycle and show it. Returns
    /// what is now displayed, so a button handler is one line.</summary>
    public CalibrationReference Next()
    {
        Show(CalibrationReferences.Next(Reference));
        return Reference;
    }

    /// <summary>Re-send the current patch. The view model calls this after
    /// every calibration change (Calibration.Set), because the trim is applied
    /// on the way out: the patch already on the hardware was transformed by
    /// the OLD table and will not update itself. It is cheap enough to call on
    /// every slider tick - the applier coalesces per device, so a fast drag
    /// collapses into one write per device per lane pass, and the drivers
    /// dedup an unchanged result on top of that.</summary>
    public void Refresh()
    {
        if (Active) Push();
        else _refresh?.Invoke();
    }

    /// <summary>End the session and restore its captured state, or stored static
    /// colors for standalone callers. Safe to call when nothing is running, which makes it
    /// usable as the first line of Start and from a window-closed handler that
    /// cannot know whether the pane was ever opened.</summary>
    public void Stop()
    {
        bool wasActive = Active;
        if (_devices.Count > 0 && _restore == null)
        {
            foreach (var d in _devices)
            {
                // The whole device, not a zone: the aid painted the whole
                // device, so a zone restore would leave the patch lit outside
                // whatever zone the caller happened to have selected.
                try { _lighting.PushFrame(d); }
                catch (Exception ex) { Log.Warn("calibration", $"{d.Name}: could not restore statics after calibration: {ex.Message}"); }
            }
            Log.Info("calibration", "calibration aid stopped; statics restored");
        }
        _devices.Clear();
        Active = false;
        if (wasActive) _restore?.Invoke();
    }

    /// <summary>What the user's device would show for the current patch, per
    /// device, WITHOUT writing anything. For an on-screen swatch row beside the
    /// sliders: it is the same number the hardware is being sent, so a user on
    /// a device they cannot see (a GPU logo inside a closed case) still has
    /// something to trim against.
    ///
    /// Master brightness is deliberately NOT included. The swatch answers "what
    /// is this device's trim doing to the color", and folding a global dimmer
    /// into it would make every swatch on the screen move together whenever the
    /// master slider did, which tells the user nothing about the thing they are
    /// calibrating.</summary>
    public Rgb SwatchFor(IRgbDevice dev) => SwatchFor(dev, null);

    /// <summary>The same, for one ZONE of a device: what that part of the
    /// device would show, which is its own trim where it has one and the
    /// device's where it does not. The editor lists zones beside devices, and
    /// a zone row painted with its device's color would hide the very
    /// difference the user opened this screen to fix. <paramref name="zone"/>
    /// null asks about the whole device.</summary>
    public Rgb SwatchFor(IRgbDevice dev, string? zone)
        => Calibration.Apply(dev.Name, zone, CalibrationReferences.ColorOf(Reference));

    void Push()
    {
        var color = CalibrationReferences.ColorOf(Reference);
        foreach (var d in _devices) _lighting.PushReference(d, color);
    }
}
