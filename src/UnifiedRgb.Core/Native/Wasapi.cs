using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/*-----------------------------------------------------------*\
| Minimal WASAPI loopback capture (raw COM interop, no NAudio)|
|                                                             |
| Opens the default render endpoint in shared-mode loopback   |
| and polls captured frames on a background thread, delivering|
| mono float samples to a callback. Vtable order on the COM   |
| interfaces is load-bearing — do not reorder methods.        |
\*-----------------------------------------------------------*/

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

// Every method carries [PreserveSig]: without it the CLR turns a failing
// HRESULT into a COMException and hands back an unrelated trailing "retval"
// as the int, so the `>= 0` checks below could never see a refusal and the
// event->polling fallback in Start() was unreachable.
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    // (GetDevice / notification methods follow; unused)
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    // Vtable order matters: OpenPropertyStore sits between Activate and GetId.
    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IntPtr properties);
    // The endpoint id string is CoTaskMem-allocated; LPWStr out marshalling frees it.
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    // (GetState follows; unused)
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration,
                   long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);
    [PreserveSig]
    int GetStreamLatency(out long latency);
    [PreserveSig]
    int GetCurrentPadding(out uint padding);
    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig]
    int GetMixFormat(out IntPtr format);
    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig]
    int Start();
    [PreserveSig]
    int Stop();
    [PreserveSig]
    int Reset();
    [PreserveSig]
    int SetEventHandle(IntPtr handle);
    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                  out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig]
    int ReleaseBuffer(uint frames);
    [PreserveSig]
    int GetNextPacketSize(out uint frames);
}

/// <summary>Shared-mode loopback capture of the default output device.
/// Runs its own polling thread; Samples(mono float, sample rate) fires on it.</summary>
public sealed class WasapiLoopback : IDisposable
{
    const int ERender = 0, EMultimedia = 1;
    const int ClsCtxAll = 0x17;
    const int LoopbackFlag = 0x00020000;               // AUDCLNT_STREAMFLAGS_LOOPBACK
    const int EventFlag = 0x00040000;                  // AUDCLNT_STREAMFLAGS_EVENTCALLBACK

    static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IidCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    readonly Action<float[], int, int> _onSamples;     // (buffer, count, sampleRate)
    IAudioClient? _client;
    IAudioCaptureClient? _capture;
    Thread? _thread;
    AutoResetEvent? _wakeEvent;                        // event-driven capture (null = polling fallback)
    volatile bool _running;

    /// <summary>False once the polling thread has exited (device lost etc.).</summary>
    public bool IsAlive => _running && _thread is { IsAlive: true };

    /// <summary>The endpoint this capture opened on, so the owner can notice
    /// when Windows' default output moves elsewhere: a shared-mode loopback
    /// stream does NOT follow a default-device switch, it just delivers nothing
    /// from then on (speakers to headset = the rig goes dark and stays dark).</summary>
    public string? EndpointId { get; private set; }

    /// <summary>The current default render endpoint's id, or null when it
    /// cannot be read (no output device at all).</summary>
    public static string? DefaultRenderEndpointId()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device) < 0 || device == null) return null;
            return device.GetId(out string id) >= 0 ? id : null;
        }
        catch { return null; }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    int _sampleRate, _channels;
    bool _isFloat; int _bytesPerSample;
    float[] _mono = new float[4800];

    public WasapiLoopback(Action<float[], int, int> onSamples) => _onSamples = onSamples;

    /// <summary>Open the endpoint and start polling. Throws on failure.</summary>
    public void Start()
    {
        // Enumerator + device are RELEASED after setup: the class is otherwise
        // deterministic about COM lifetime, and each Touch->idle->Touch cycle
        // used to strand another RCW pair for the finalizer.
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice? device = null;
        try
        {
            try { StartCore(enumerator, out device); }
            catch { Dispose(); throw; }   // a half-built client must not outlive the failure
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        StartPolling();
        Log.Info("audio", $"loopback capture started ({_sampleRate} Hz, {_channels} ch, " +
                          $"{(_isFloat ? "f32" : "i16")}, {(_wakeEvent != null ? "event-driven" : "polling")})");
    }

    void StartPolling()
    {
        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = "wasapi-loopback" };
        _thread.Start();
    }

    /// <summary>The real poll loop over a stand-in capture client (32-bit
    /// float frames), so the harness can drive the Dispose-while-delivering
    /// path without an audio endpoint. Nothing else calls this.</summary>
    internal void StartPollingForTest(IAudioCaptureClient capture, AutoResetEvent? wake, int sampleRate, int channels)
    {
        _capture = capture;
        _wakeEvent = wake;
        _sampleRate = sampleRate;
        _channels = channels;
        _isFloat = true;
        _bytesPerSample = 4;
        StartPolling();
    }

    void StartCore(IMMDeviceEnumerator enumerator, out IMMDevice device)
    {
        {
            Check(enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device), "endpoint");
            try { EndpointId = device.GetId(out string id) >= 0 ? id : null; } catch { EndpointId = null; }

            var iid = IidAudioClient;
            Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var clientObj), "activate");
            _client = (IAudioClient)clientObj;

            Check(_client.GetMixFormat(out var fmt), "mix format");
            try
            {
                ParseFormat(fmt);
                // EVENT-DRIVEN first: the engine wakes when a buffer is ready
                // (~10 ms cadence while audio plays) instead of spinning a
                // 100 Hz poll. Loopback event delivery is flaky on some
                // endpoints, so any refusal rebuilds the client for the
                // proven polling mode instead.
                bool eventOk = false;
                if (_client.Initialize(0, LoopbackFlag | EventFlag, 2_000_000, 0, fmt, IntPtr.Zero) >= 0)
                {
                    _wakeEvent = new AutoResetEvent(false);
                    eventOk = _client.SetEventHandle(_wakeEvent.SafeWaitHandle.DangerousGetHandle()) >= 0;
                }
                if (!eventOk)
                {
                    // An IAudioClient initializes exactly once: activate a
                    // fresh one for the plain polling path.
                    _wakeEvent?.Dispose(); _wakeEvent = null;
                    // Null it before the re-activate: if that fails, Dispose runs
                    // from Start's catch and must not touch the released RCW
                    // (InvalidComObjectException would replace the real HRESULT).
                    Marshal.ReleaseComObject(_client); _client = null;
                    Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var retryObj), "activate");
                    _client = (IAudioClient)retryObj;
                    Check(_client.Initialize(0, LoopbackFlag, 2_000_000, 0, fmt, IntPtr.Zero), "init");
                }
            }
            finally { Marshal.FreeCoTaskMem(fmt); }

            var cid = IidCaptureClient;
            Check(_client.GetService(ref cid, out var capObj), "capture service");
            _capture = (IAudioCaptureClient)capObj;
            Check(_client.Start(), "start");
        }
    }

    void ParseFormat(IntPtr fmt)
    {
        ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
        _channels = Marshal.ReadInt16(fmt, 2);
        _sampleRate = Marshal.ReadInt32(fmt, 4);
        ushort bits = (ushort)Marshal.ReadInt16(fmt, 14);
        _bytesPerSample = bits / 8;
        if (tag == 0xFFFE)                             // WAVE_FORMAT_EXTENSIBLE
        {
            int subType = Marshal.ReadInt32(fmt, 24);  // Data1 of the SubFormat guid
            _isFloat = subType == 3;                   // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        }
        else _isFloat = tag == 3;                      // WAVE_FORMAT_IEEE_FLOAT
        if (!_isFloat && _bytesPerSample != 2)
            throw new NotSupportedException($"unsupported mix format: tag={tag} bits={bits}");
    }

    void Poll()
    {
        // Both captured ONCE, before the loop. Dispose used to null and free
        // the fields even when its 500 ms Join gave up on this thread (a slow
        // consumer callback, a stalled endpoint), and the event wait below
        // read _wakeEvent OUTSIDE the try: a NullReference or ObjectDisposed
        // there is unhandled on a background thread, which ends the process.
        // Now nothing is freed until this thread has left (see Dispose), so
        // the locals stay valid for the whole loop, and the wait is inside
        // the try anyway.
        var capture = _capture;
        var wake = _wakeEvent;
        try
        {
            while (_running && capture != null)
            {
                try
                {
                    while (_running)
                    {
                        // A failing HRESULT (AUDCLNT_E_DEVICE_INVALIDATED when the
                        // endpoint is unplugged) must throw like every other call: a
                        // silent loop exit here left IsAlive true, so the watchdog
                        // kept polling a dead client for as long as the effect ran.
                        Check(capture.GetNextPacketSize(out uint packet), "next packet");
                        if (packet == 0) break;
                        Check(capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _), "get buffer");
                        try
                        {
                            if (frames > 0)
                            {
                                if (_mono.Length < frames) _mono = new float[frames];
                                bool silent = (flags & 0x2) != 0;   // AUDCLNT_BUFFERFLAGS_SILENT
                                if (silent) Array.Clear(_mono, 0, (int)frames);
                                else MixToMono(data, (int)frames);
                                _onSamples(_mono, (int)frames, _sampleRate);
                            }
                        }
                        finally { capture.ReleaseBuffer(frames); }   // a consumer throw must not wedge the stream (OUT_OF_ORDER)
                    }
                    // Event mode: sleep until the endpoint signals a ready buffer. The
                    // 30 ms timeout keeps silence/idle behavior identical and doubles
                    // as a graceful floor on endpoints whose loopback events misfire.
                    if (wake != null) wake.WaitOne(30);
                    else Thread.Sleep(10);
                }
                catch (Exception ex)
                {
                    if (_running) Log.Warn("audio", $"capture loop error: {ex.Message}");
                    break;                                  // device gone: owner restarts us
                }
            }
        }
        finally
        {
            // Second to arrive frees the native state: Dispose, if it has
            // already given up waiting for this thread, left it to us.
            if (Interlocked.Exchange(ref _handoff, 1) == 1)
            {
                ReleaseCom();
                _wakeEvent?.Dispose(); _wakeEvent = null;
            }
        }
    }
    unsafe void MixToMono(IntPtr data, int frames)
    {
        if (_isFloat)
        {
            float* p = (float*)data;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += p[i * _channels + c];
                _mono[i] = sum / _channels;
            }
        }
        else
        {
            short* p = (short*)data;
            for (int i = 0; i < frames; i++)
            {
                int sum = 0;
                for (int c = 0; c < _channels; c++) sum += p[i * _channels + c];
                _mono[i] = sum / (_channels * 32768f);
            }
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"WASAPI {what} failed", hr);
    }

    /// <summary>Who frees the client, the capture client and the wake event:
    /// Dispose when the poll thread has exited (or never ran), otherwise the
    /// poll thread on its way out. Whichever of the two arrives SECOND does
    /// it, decided by this flag, so it happens exactly once and never under a
    /// thread that is still using them.</summary>
    int _handoff;

    public void Dispose()
    {
        // Once. A second Dispose while the poll thread was still delivering read
        // the hand-off flag as "the thread has left" and freed the COM objects
        // under it.
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;
        _running = false;
        // Pop the loop out of its wait. A poll thread finishing a handed-off
        // release may have closed the event already (a second Dispose).
        try { _wakeEvent?.Set(); }
        catch (ObjectDisposedException) { }   // already released: the loop is gone
        var thread = _thread;
        // Never throw from here: Start's catch relies on Dispose to clean up
        // and then rethrow the ORIGINAL failure.
        //
        // Freed here only once the poll thread is gone (or never ran). If it
        // is still inside a delivery after 500 ms - a slow consumer, a stalled
        // endpoint - the event and the COM objects are left to it, and it frees
        // them on its way out; unless it left between the Join and the
        // exchange, in which case it left them to us.
        if (thread == null || thread.Join(500) || Interlocked.Exchange(ref _handoff, 1) == 1)
        {
            ReleaseCom();
            _wakeEvent?.Dispose(); _wakeEvent = null;
            return;
        }
        Log.Info("audio", "capture thread still delivering after 500 ms - it releases the stream itself when it returns");
    }

    /// <summary>Stop and release the COM objects, once: each field is taken
    /// with an exchange, so a second caller finds nothing to release. The wake
    /// event is disposed beside each call (the analyser wants to see it in
    /// Dispose).</summary>
    int _disposeOnce;

    void ReleaseCom()
    {
        var client = Interlocked.Exchange(ref _client, null);
        var capture = Interlocked.Exchange(ref _capture, null);
        // Each step is best effort: a client that refuses to stop or release
        // is already dead (device invalidated), and teardown must go on.
        try { client?.Stop(); } catch { }   // already stopped or invalidated
        try { if (capture != null) Marshal.ReleaseComObject(capture); } catch { }   // not an RCW, or already released
        try { if (client != null) Marshal.ReleaseComObject(client); } catch { }    // not an RCW, or already released
    }
}
