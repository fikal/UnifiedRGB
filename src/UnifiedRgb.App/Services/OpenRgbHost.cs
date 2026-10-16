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

    /// <summary>This host's server has been stopped for good (a rescan, or a
    /// restart of the server); the app gives the next server a host of its own.
    /// The stop waits only half a second for client threads, and one parked in
    /// the hop to the UI thread - which is busy doing the stop - outlives it. Its
    /// takeover then landed after the claims had been reset: a device held for
    /// a server that will never release it, every static write to it refused,
    /// and its next frame painted onto whatever instance it had. So from here a
    /// takeover or a frame is dropped, including a takeover already queued to
    /// the UI thread. Releases still go through: the stop itself issues them,
    /// and they are what gives the user's lighting back.</summary>
    public void Retire() => _retired = true;
    volatile bool _retired;

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
        // A stopped server's leftover thread describing an instance a rescan
        // replaced: composing it would store a frame for that instance
        // (FrameFor creates one) and pin it, and nobody is listening anyway.
        if (_retired) return new Rgb[device.LedCount];
        // ComposedFrame reads engine state; ask the UI thread for it. Asked
        // again when the hop runs: a request parked behind a rescan (clients
        // re-read the controllers on the device-list-changed packet) ran after
        // it and stored a frame for the replaced instance all the same.
        if (_ui.CheckAccess()) return FrameOrBlank(device);
        try { return _ui.Invoke(() => FrameOrBlank(device), DispatcherPriority.Send); }
        catch (Exception ex)
        {
            Log.Warn("orgb-server", $"colors for {device.Name}: {ex.Message}");
            return Array.Empty<Rgb>();
        }
    }

    /// <summary>UI thread. Blank for a retired host or a replaced instance.</summary>
    IReadOnlyList<Rgb> FrameOrBlank(IRgbDevice device) =>
        _retired || !_vm.Devices.Contains(device) ? new Rgb[device.LedCount] : _lighting.ComposedFrame(device);

    /// <summary>UI thread only: the claim number each device's hold belongs
    /// to. A takeover hops to the UI thread synchronously at Send priority and
    /// a release queues at Normal, so a new client's takeover could run first
    /// and the old client's release then took ITS hold away and put the user's
    /// lighting back under it. A release now only acts while its own claim is
    /// still the one recorded.</summary>
    readonly Dictionary<IRgbDevice, long> _holder = new();

    public void BeginExternal(IRgbDevice device) => BeginExternal(device, 0);

    public void BeginExternal(IRgbDevice device, long claim)
    {
        if (_retired) return;
        // Everything happens in the hop, and only if the host is still live
        // when it runs: a takeover queued while the UI thread was busy stopping
        // this host's server would otherwise land after it was retired (see
        // Retire), and its history line named a takeover that never happened.
        _ui.Invoke(() =>
        {
            if (_retired) return;
            Log.Info("lighting", $"{device.Name}: handed to an SDK client, your lighting saved");
            // An SDK client is the one source of lighting change with no visible
            // trace at all: nothing in the UI moves, the status line does not
            // mention it, and the user is left thinking their effects broke.
            ActivityLog.Note(ActivityKind.SdkClient,
                $"An OpenRGB client took control of {device.Name}. Your lighting is saved and comes back when it lets go.");
            lock (_gate) _claims++;
            if (claim != 0) _holder[device] = claim;
            // Our effects on this device would fight the client for the lane.
            // The view model keeps what was running as the device's pending
            // intent, which is what the release restores.
            _vm.StopEffectsOn(device);
        });
    }

    public void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors)
    {
        if (_retired) return;   // a frame from a stopped server's leftover thread: see Retire
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
        if (_shuttingDown || _retired) return;
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
        // The instances THIS host served, as they are now. The release runs
        // later, once the UI thread is free, and the rescan has swapped the
        // list by then: walking the view model's devices at that point released
        // the NEW instances - including one a client of the next server had
        // already, legitimately, taken.
        var served = Devices;
        _ui.InvokeAsync(() =>
        {
            // One refresh for the whole set rather than one per device.
            foreach (var device in served) { _holder.Remove(device); _vm.ReleaseHold(device, refresh: false); }
            _vm.RefreshDeviceHealth();
        });
    }

    public void EndExternal(IRgbDevice device) => EndExternal(device, 0);

    public void EndExternal(IRgbDevice device, long claim)
    {
        if (_shuttingDown) return;
        Log.Info("lighting", $"{device.Name}: SDK client done, your lighting coming back");
        ActivityLog.Note(ActivityKind.SdkClient,
            $"The OpenRGB client released {device.Name}, so your lighting is coming back.");
        lock (_gate) { if (_claims > 0) _claims--; }
        _ui.InvokeAsync(() =>
        {
            // A newer claim on this device reached the UI thread first: that
            // client owns it now, and its frames are what the device shows.
            if (claim != 0 && _holder.TryGetValue(device, out long current) && current != claim) return;
            _holder.Remove(device);
            // The next client to claim this device starts from the user's
            // lighting, not from where the departing one left the pixels. In
            // the hop, after the check: done straight away it also wiped the
            // frames of a newer client that had already started painting.
            _lighting.ForgetExternal(device);
            // This device, now, whatever else is still held: the hold ends and
            // the user's own lighting for it comes back. Another claim on some
            // other device is that device's business.
            _vm.ReleaseHold(device);
            _vm.RestoreDevice(device);
        });
    }
}
