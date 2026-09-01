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

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    // (GetDevice / notification methods follow; unused)
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    // (OpenPropertyStore / GetId / GetState follow; unused)
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    int Initialize(int shareMode, int streamFlags, long bufferDuration,
                   long periodicity, IntPtr format, IntPtr audioSessionGuid);
    int GetBufferSize(out uint bufferFrames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint padding);
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    int GetMixFormat(out IntPtr format);
    int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr handle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                  out ulong devicePosition, out ulong qpcPosition);
    int ReleaseBuffer(uint frames);
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
            Check(enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device), "endpoint");

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
                    Marshal.ReleaseComObject(_client);
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
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = "wasapi-loopback" };
        _thread.Start();
        Log.Info("audio", $"loopback capture started ({_sampleRate} Hz, {_channels} ch, " +
                          $"{(_isFloat ? "f32" : "i16")}, {(_wakeEvent != null ? "event-driven" : "polling")})");
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
        while (_running)
        {
            try
            {
                while (_running && _capture!.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    Check(_capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _), "get buffer");
                    if (frames > 0)
                    {
                        if (_mono.Length < frames) _mono = new float[frames];
                        bool silent = (flags & 0x2) != 0;   // AUDCLNT_BUFFERFLAGS_SILENT
                        if (silent) Array.Clear(_mono, 0, (int)frames);
                        else MixToMono(data, (int)frames);
                        _onSamples(_mono, (int)frames, _sampleRate);
                    }
                    _capture.ReleaseBuffer(frames);
                }
            }
            catch (Exception ex)
            {
                if (_running) Log.Warn("audio", $"capture loop error: {ex.Message}");
                break;                                  // device gone: owner restarts us
            }
            // Event mode: sleep until the endpoint signals a ready buffer. The
            // 30 ms timeout keeps silence/idle behavior identical and doubles
            // as a graceful floor on endpoints whose loopback events misfire.
            if (_wakeEvent != null) _wakeEvent.WaitOne(30);
            else Thread.Sleep(10);
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

    public void Dispose()
    {
        _running = false;
        _wakeEvent?.Set();                              // pop the loop out of its wait
        _thread?.Join(500);
        try { _client?.Stop(); } catch { }
        if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        if (_client != null) { Marshal.ReleaseComObject(_client); _client = null; }
        _wakeEvent?.Dispose(); _wakeEvent = null;
    }
}
