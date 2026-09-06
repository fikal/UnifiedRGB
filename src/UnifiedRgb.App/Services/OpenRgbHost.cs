using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.App.Services;

/// <summary>Bridges the SDK server to our lighting.
///
/// The socket threads never touch WPF. Everything that reads or writes view
/// model state is marshalled to the dispatcher; only the actual device write
/// goes straight out, and that goes through the applier lane like every other
/// write so an SDK client and an effect can never interleave on one transport.
///
/// Takeover is all-or-nothing by design. The first device a client claims
/// snapshots the whole lighting state, and the last one released puts it back.
/// Per-device restore would need per-device effect surgery the view model does
/// not do, and getting that subtly wrong means the user's rig comes back
/// almost right, which is worse than the pause.</summary>
public sealed class OpenRgbHost : IOpenRgbHost
{
    readonly MainViewModel _vm;
    readonly LightingController _lighting;
    readonly Dispatcher _ui;
    readonly object _gate = new();

    MainViewModel.LightState? _snapshot;
    int _externalCount;

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
        _ui.Invoke(() =>
        {
            lock (_gate)
            {
                // First device taken: remember everything, once.
                _snapshot ??= _vm.CaptureState();
                _externalCount++;
            }
            // Our effects on this device would fight the client for the lane.
            _vm.StopEffectsOn(device);
        });
    }

    public void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors)
    {
        // No dispatcher hop: this is the hot path, and the applier is already
        // the thing that serializes device writes.
        _lighting.PushExternalFrame(device, offset, colors);
    }

    public void EndExternal(IRgbDevice device)
    {
        _ui.InvokeAsync(() =>
        {
            MainViewModel.LightState? restore = null;
            lock (_gate)
            {
                if (_externalCount > 0) _externalCount--;
                if (_externalCount == 0 && _snapshot != null)
                {
                    restore = _snapshot;
                    _snapshot = null;
                }
            }
            if (restore != null) _vm.RestoreState(restore);
        });
    }
}
