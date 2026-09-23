using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.App.Services;

/// <summary>Bridges the SDK server to our lighting.
///
/// The socket threads never touch WPF. Everything that reads or writes view
/// model state is marshalled to the dispatcher; only the actual device write
/// goes straight out, and that goes through the applier lane like every other
/// write so an SDK client and an effect can never interleave on one transport.
///
/// Takeover and release are per device. A claim stops our effects on that one
/// device and nothing else; a release puts that one device back from what the
/// user wants for it NOW (MainViewModel.RestoreDevice), leaving every other
/// claim and every other device alone. It used to snapshot the whole desk at
/// the first claim and restore all of it at the last release, which had two
/// ways of being wrong: a device released while another was still held kept
/// the client's last frame until the unrelated claim ended, and the restore
/// undid hand edits made meanwhile on devices no client had touched - a null
/// applied-profile name was read as "nothing changed" when it meant the exact
/// opposite.</summary>
public sealed class OpenRgbHost : IOpenRgbHost
{
    readonly MainViewModel _vm;
    readonly LightingController _lighting;
    readonly Dispatcher _ui;
    readonly object _gate = new();

    /// <summary>Claims standing right now. Only the rescan note reads it:
    /// "every client lost its claim" is worth a history line only when one
    /// actually held something, and every rescan calls ResetExternal.</summary>
    int _claims;

    /// <summary>The app is going down. Restoring is both pointless and unsafe
    /// from here: the callback is queued to a dispatcher that will not pump
    /// again, and if it did run it would write to handles that are already
    /// closed.</summary>
    volatile bool _shuttingDown;

    public void Shutdown()
    {
        _shuttingDown = true;
        lock (_gate) _claims = 0;
    }

    public OpenRgbHost(MainViewModel vm, LightingController lighting, Dispatcher ui)
    {
        _vm = vm;
        _lighting = lighting;
        _ui = ui;
    }

    /// <summary>Snapshot of the list, not the live collection: it is an
    /// ObservableCollection owned by the UI thread, and a client enumerating it
    /// during a rescan would throw.</summary>
    public IReadOnlyList<IRgbDevice> Devices { get; private set; } = Array.Empty<IRgbDevice>();

    /// <summary>Called on the UI thread after a detect.</summary>
    public void SetDevices(IReadOnlyList<IRgbDevice> devices) => Devices = devices.ToArray();

    public IReadOnlyList<Rgb> ColorsOf(IRgbDevice device)
    {
        // ComposedFrame reads engine state; ask the UI thread for it.
        if (_ui.CheckAccess()) return _lighting.ComposedFrame(device);
        try { return _ui.Invoke(() => _lighting.ComposedFrame(device), DispatcherPriority.Send); }
        catch (Exception ex)
        {
            Log.Warn("orgb-server", $"colors for {device.Name}: {ex.Message}");
            return Array.Empty<Rgb>();
        }
    }

    public void BeginExternal(IRgbDevice device)
    {
        Log.Info("lighting", $"{device.Name}: handed to an SDK client, your lighting saved");
        // An SDK client is the one source of lighting change with no visible
        // trace at all: nothing in the UI moves, the status line does not
        // mention it, and the user is left thinking their effects broke.
        ActivityLog.Note(ActivityKind.SdkClient,
            $"An OpenRGB client took control of {device.Name}. Your lighting is saved and comes back when it lets go.");
        lock (_gate) _claims++;
        // Our effects on this device would fight the client for the lane. The
        // view model keeps what was running as the device's pending intent,
        // which is what the release restores.
        _ui.Invoke(() => _vm.StopEffectsOn(device));
    }

    public void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors)
    {
        // No dispatcher hop: this is the hot path, and the applier is already
        // the thing that serializes device writes.
        _lighting.PushExternalFrame(device, offset, colors);
    }

    /// <summary>A rescan dropped every claim at once. Nothing to put back from
    /// here: the device instances are being replaced, and Rescan itself carries
    /// the stored frames and every effect assignment - the ones a client had
    /// stopped included, CaptureEffects records those - onto the new instances
    /// by name. What is left is to forget the takeover.</summary>
    public void ResetExternal()
    {
        if (_shuttingDown) return;
        // Only worth a history line when a client actually held something:
        // every rescan calls this, and a rescan with no SDK client attached is
        // not a lighting event.
        bool held;
        lock (_gate) { held = _claims > 0; _claims = 0; }
        if (held)
            ActivityLog.Note(ActivityKind.SdkClient,
                "Devices were rescanned, so every OpenRGB client lost its claim and your lighting is back.");
        // Whatever the clients had painted dies with the claims: the device
        // instances themselves are being replaced.
        _lighting.ForgetExternalAll();
        _ui.InvokeAsync(() =>
        {
            // One refresh for the whole set rather than one per device.
            foreach (var device in _vm.Devices) _vm.ReleaseHold(device, refresh: false);
            _vm.RefreshDeviceHealth();
        });
    }

    public void EndExternal(IRgbDevice device)
    {
        if (_shuttingDown) return;
        Log.Info("lighting", $"{device.Name}: SDK client done, your lighting coming back");
        ActivityLog.Note(ActivityKind.SdkClient,
            $"The OpenRGB client released {device.Name}, so your lighting is coming back.");
        lock (_gate) { if (_claims > 0) _claims--; }
        // The next client to claim this device starts from the user's lighting,
        // not from where the departing one left the pixels.
        _lighting.ForgetExternal(device);
        _ui.InvokeAsync(() =>
        {
            // This device, now, whatever else is still held: the hold ends and
            // the user's own lighting for it comes back. Another claim on some
            // other device is that device's business.
            _vm.ReleaseHold(device);
            _vm.RestoreDevice(device);
        });
    }
}
