using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Core.Effects;

/// <summary>Receives Chroma effect frames from the UnifiedRGB Chroma shim
/// (RzChromaSDK64.dll) over a named pipe, so Wallpaper Engine and Chroma games
/// drive UnifiedRGB devices. The shim sends a keyboard grid (6x22) and/or a
/// ChromaLink array (5) representing screen regions; we expose that as a small
/// sampleable grid so the ChromaSync effect can read it by LED position.
///
/// message = [uint8 type][uint16 rows][uint16 cols][rows*cols COLORREF(BGR)]
///   type 1 = keyboard grid · type 2 = chromalink</summary>
public static class ChromaFeed
{
    const string PipeName = "UnifiedRgbChroma";

    static readonly object _lock = new();
    // ONE reference publishes grid+dims together. Grid/rows/cols used to be
    // three separate fields; the REST server pushes 1x1 frames (static) and
    // r x c frames (custom) from concurrent pool threads, so a reader could
    // index a 1-element grid with 6x22 dims for a whole frame interval.
    // Keyboard (type 1) and ChromaLink (type 2) frames are kept apart: a host
    // that sends both per frame (Wallpaper Engine does) used to overwrite one
    // grid with the other, so Sample() alternated between a 132-cell and a
    // 5-cell picture - visible flicker. The keyboard grid wins while fresh.
    sealed record Frame(Rgb[] Grid, int Rows, int Cols, long Stamp);
    static volatile Frame? _kb, _cl;
    static long _lastFrame;
    const int PreferKeyboardMs = 1000;
    // Lives for the app's life once started, so it is never disposed.
    static volatile ChromaPipeServer? _pipe;
    // Every accepted instance holds a slot; a host that leaks connections (or
    // any hostile local process) must not pile those up in the elevated 24/7
    // process.
    const int MaxClients = 16;
    const int FirstFrameDeadlineMs = 30_000;
    // With every slot taken, a client this long without a frame gives its slot
    // to a new connection (ChromaPipeServer's summary has why 30 s is safe).
    internal const int IdleEvictMs = 30_000;
    // ...and a connection that has not sent its FIRST frame gives it up after
    // this long: a real shim writes its frame in the same call that connects,
    // so only something holding slots open is still silent by then. Without
    // it, sixteen silent connections re-opened as each was dropped kept every
    // slot under 30 s idle, and Wallpaper Engine was refused for good.
    internal const int UnprovenEvictMs = 2_000;

    /// <summary>A frame arrived within the last few seconds.</summary>
    public static bool Active => Environment.TickCount64 - Interlocked.Read(ref _lastFrame) < 4000;

    /// <summary>Start the pipe server (idempotent). Runs for the app's life.</summary>
    public static void Start()
    {
        if (_pipe != null) return;
        lock (_lock)
        {
            if (_pipe != null) return;
            var pipe = new ChromaPipeServer(PipeName, FirstFrameDeadlineMs, MaxClients, Publish, IdleEvictMs, UnprovenEvictMs);
            pipe.Start();
            _pipe = pipe;
        }
    }

    /// <summary>Push a frame from another source (the Chroma REST server, which
    /// modern games use instead of the C++ DLL). Same slots the pipe feeds:
    /// type 1 = keyboard grid, type 2 = ChromaLink.</summary>
    public static void PushGrid(Rgb[] grid, int rows, int cols, int type = 1)
    {
        if (grid.Length == 0 || rows <= 0 || cols <= 0 || grid.Length < rows * cols) return;
        Publish(type, grid, rows, cols);
    }

    /// <summary>A host is alive but sent nothing for our slots (mouse/headset/
    /// mousepad frames): keeps <see cref="Active"/> true without touching a grid.</summary>
    public static void Touch() => Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);

    static void Publish(int type, Rgb[] grid, int rows, int cols)
    {
        long now = Environment.TickCount64;
        var f = new Frame(grid, rows, cols, now);
        if (type == 2) _cl = f; else _kb = f;
        Interlocked.Exchange(ref _lastFrame, now);
    }

    /// <summary>Color at normalized (x, y). Averages the ChromaLink 5-strip or
    /// samples the keyboard grid cell; falls back to black when idle.</summary>
    public static Rgb Sample(float x, float y)
    {
        if (!Active) return default;
        var f = _kb;
        if (f == null || Environment.TickCount64 - f.Stamp > PreferKeyboardMs) f = _cl ?? f;
        if (f == null) return default;
        int gx = Math.Clamp((int)(x * f.Cols), 0, f.Cols - 1);
        int gy = Math.Clamp((int)(y * f.Rows), 0, f.Rows - 1);
        return f.Grid[gy * f.Cols + gx];
    }
}

/// <summary>The shim's named pipe: one instance per connected host, each read
/// asynchronously, every frame handed to <c>publish(type, grid, rows, cols)</c>.
/// An instance rather than statics so the harness can run one under its own
/// name; the app runs exactly one, under ChromaFeed's, for its whole life.
///
/// The client cap bounds the connections, but a connection that simply never
/// sends anything would keep its slot forever: sixteen silent local processes
/// could lock every real shim host out. So the FIRST frame has a deadline. A
/// real host sends one as soon as it connects, whereas an established host may
/// legitimately go quiet for hours between games and must not be dropped.
///
/// The pipe is opened overlapped for that. The old reaper disposed a silent
/// client's pipe from another thread, but a synchronous ReadFile cannot be
/// interrupted that way: the close waits for the read, the read waited for a
/// byte that never came, and the slot was never given back.
///
/// The deadline alone still left a way in: ONE valid frame made a client
/// "real" for good, so sixteen connections that each sent one frame and then
/// nothing locked Wallpaper Engine out just the same. So when every slot is
/// taken, a new connection is given the slot of the client that has gone
/// longest without a frame, provided that is at least idleEvictMs (30 s in
/// the app). A client that keeps sending is never evicted; when every slot is
/// busy like that the newcomer is refused, as before.
///
/// Why that never costs a real host anything, from what the shim
/// (native/chroma-shim/RzChromaSDK.cpp, SendFrame) does:
///   - it opens the pipe only to send a frame, and writes that frame in the
///     same call, so a real connection is never idle at the start;
///   - it writes whenever the host creates or sets an effect: Wallpaper Engine
///     and Chroma animations do that at their frame rate, many times a second,
///     nowhere near 30 s while they run;
///   - a host that has gone quiet (Wallpaper Engine paused behind a fullscreen
///     game, a game that set one static colour) is not lighting anything by
///     then: ChromaFeed.Active lets a frame go after 4 s;
///   - after a failed write it closes its handle and reopens the pipe on its
///     next frame (the 1 s reconnect gate counts from its last attempt, long
///     past for an established connection). An evicted host loses the one
///     frame whose write found the pipe closed, and is back on the next.
/// A host refused because every slot is busy retries the same way, about once
/// a second while it has frames to send.</summary>
internal sealed class ChromaPipeServer : IDisposable
{
    readonly string _name;
    readonly int _firstFrameDeadlineMs;
    readonly int _maxClients;
    readonly int _idleEvictMs;
    readonly int _unprovenEvictMs;
    readonly Action<int, Rgb[], int, int> _publish;
    readonly CancellationTokenSource _stop = new();
    readonly CancellationToken _stopping;
    readonly LogBudget _connLog = new(10);
    readonly List<PipeClient> _live = new();   // the slots; guarded by locking it
    bool _firstInstance = true;   // touched only by the accept thread
    Thread? _accept;

    public ChromaPipeServer(string name, int firstFrameDeadlineMs, int maxClients, Action<int, Rgb[], int, int> publish,
        int idleEvictMs = ChromaFeed.IdleEvictMs, int unprovenEvictMs = ChromaFeed.UnprovenEvictMs)
    {
        _name = name;
        _firstFrameDeadlineMs = firstFrameDeadlineMs;
        _maxClients = maxClients;
        _idleEvictMs = idleEvictMs;
        _unprovenEvictMs = unprovenEvictMs;
        _publish = publish;
        _stopping = _stop.Token;
    }

    /// <summary>Connections holding a slot right now.</summary>
    public int ClientCount { get { lock (_live) return _live.Count; } }

    /// <summary>One accepted connection: when it last sent a frame, and the
    /// switch that drops it.</summary>
    sealed class PipeClient : IDisposable
    {
        readonly CancellationTokenSource _drop;
        long _lastFrame;           // TickCount64 of its last whole frame; of the connect until the first
        volatile bool _evicted;
        volatile bool _proven;     // has sent a whole frame

        public PipeClient(CancellationToken stopping)
        {
            _drop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _lastFrame = Environment.TickCount64;
        }

        /// <summary>Cancelled when the server stops or the client is evicted.</summary>
        public CancellationToken Dropped => _drop.Token;
        public bool Evicted => _evicted;
        public long IdleMs(long now) => now - Interlocked.Read(ref _lastFrame);
        public bool Proven => _proven;
        public void Framed() { Interlocked.Exchange(ref _lastFrame, Environment.TickCount64); _proven = true; }

        /// <summary>Only ever called under the slot lock, while the client is
        /// still in the slot list; ServeClientAsync takes the client out under
        /// that same lock before disposing it, so this never meets a disposed
        /// token source.</summary>
        public void Evict() { _evicted = true; _drop.Cancel(); }

        public void Dispose() => _drop.Dispose();
    }

    /// <summary>A slot for a new connection: a free one, else a client's that
    /// can give it up - one still without its first frame after
    /// unprovenEvictMs (never a real host, so these go first), or one that has
    /// gone idleEvictMs without a frame, the longest idle first. That client is
    /// cancelled, and gone from the count at once. Null when every slot is busy.</summary>
    PipeClient? Admit(out long evictedIdleMs)
    {
        evictedIdleMs = -1;
        lock (_live)
        {
            if (_live.Count >= _maxClients)
            {
                long now = Environment.TickCount64;
                PipeClient? victim = null;
                long victimMs = -1;
                bool victimSilent = false;
                foreach (var c in _live)
                {
                    long idle = c.IdleMs(now);
                    bool silent = !c.Proven;
                    if (idle < (silent ? _unprovenEvictMs : _idleEvictMs)) continue;
                    if (victim == null || (silent && !victimSilent) || (silent == victimSilent && idle > victimMs))
                    { victim = c; victimMs = idle; victimSilent = silent; }
                }
                if (victim == null) return null;
                _live.Remove(victim);
                victim.Evict();
                evictedIdleMs = victimMs;
            }
            var client = new PipeClient(_stopping);
            _live.Add(client);
            return client;
        }
    }

    void Release(PipeClient client)
    {
        lock (_live) _live.Remove(client);   // already gone when it was evicted
        client.Dispose();
    }

    public void Start()
    {
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "chroma-feed" };
        _accept.Start();
    }

    /// <summary>Stop accepting and drop every connection. The app never calls
    /// this (the pipe serves for its whole life); the harness does. The accept
    /// thread is let go before the token source is, so it never reads a
    /// disposed one.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _accept?.Join(2000);
        _stop.Dispose();
    }

    // We run elevated (high integrity); Wallpaper Engine runs as a normal user
    // (medium). A default pipe blocks the lower process from connecting, so we
    // create it with an SDDL that grants Everyone read/write AND labels the
    // pipe MEDIUM integrity (NW = no write-up): WE and games write, sandboxed
    // low-integrity processes are kept off it (the old LW label let them in,
    // and the Anonymous ACE served nothing). Administrators/SYSTEM get full
    // access explicitly - the server's own 2nd+ instances are access-checked
    // against this DACL too. Everyone keeps GENERIC_WRITE, which carries
    // FILE_CREATE_PIPE_INSTANCE (0x4), because the installed shims open with
    // GENERIC_WRITE; narrowing it to FILE_WRITE_DATA needs the shim to open
    // with the narrower mask first, or every installed shim stops connecting.
    const string PipeSddl = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGW;;;WD)S:(ML;;NW;;;ME)";
    const uint PIPE_ACCESS_INBOUND = 0x00000001;
    const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint PIPE_TYPE_BYTE = 0, PIPE_WAIT = 0;
    // The ACL lets Everyone write (the shim runs inside Wallpaper Engine and
    // games, as whoever is signed in), and Everyone includes a network logon:
    // without this, another machine could open the pipe over SMB and paint the
    // desk. Only processes on this PC may connect.
    internal const uint PIPE_REJECT_REMOTE_CLIENTS = 0x8;
    const uint PIPE_UNLIMITED_INSTANCES = 255;
    const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint rev, out IntPtr psd, out int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuf, uint inBuf, uint timeout, ref SECURITY_ATTRIBUTES sa);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    NamedPipeServerStream CreateServer()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(PipeSddl, 1, out var psd, out _))
            throw new InvalidOperationException("bad pipe SDDL");
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = psd, bInheritHandle = 0 };
            // PIPE_UNLIMITED_INSTANCES: with maxInstances=1 a second host
            // (Wallpaper Engine + a game) got ERROR_PIPE_BUSY for as long as the
            // first stayed connected. Every accepted connection is served on its
            // own; the accept loop immediately creates the next instance.
            // FIRST_PIPE_INSTANCE on our very first create: if the name already
            // exists another process got there first and would share the
            // hosts' connections with us - refuse instead of silently joining
            // it. Later instances are ours (the flag would fail them).
            // OVERLAPPED: so a read can be cancelled (see the class summary).
            uint openMode = PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED
                          | (_firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
            var h = CreateNamedPipeW(@"\\.\pipe\" + _name, openMode,
                PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, PIPE_UNLIMITED_INSTANCES, 0, 1 << 20, 0, ref sa);
            if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _firstInstance = false;
            // isConnected:false - the handle is a fresh, unconnected pipe; with
            // `true` a client racing CreateNamedPipe->ConnectNamedPipe made
            // WaitForConnection throw and tore down that client's connection.
            // isAsync:true - it was created overlapped, and must be driven so.
            return new NamedPipeServerStream(PipeDirection.In, true, false, h);
        }
        finally { LocalFree(psd); }
    }

    void AcceptLoop()
    {
        // Accept loop: one pipe instance per connected host, each served
        // asynchronously, so a second host never waits for the first to leave.
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreateServer(); }
            catch (Exception ex) { AcceptError(ex); continue; }   // AcceptError logs it and paces the retry
            // Split from the create: an instance whose accept fails (a client
            // connected and dropped before we got here: "pipe is broken") must
            // be released here, not left to the finalizer.
            try { pipe.WaitForConnectionAsync(_stopping).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }   // Dispose: the server is stopping
            catch (Exception ex) { pipe.Dispose(); AcceptError(ex); continue; }   // AcceptError logs it
            var client = Admit(out long evictedIdleMs);
            if (client == null)
            {
                pipe.Dispose();
                Log.Occasional("chroma-clients", "chroma",
                    $"all {_maxClients} pipe slots are held by clients that sent a frame in the last {_idleEvictMs / 1000.0:0.#} s - refusing a new connection");
                continue;
            }
            if (evictedIdleMs >= 0)
                Log.Occasional("chroma-pipe-evict", "chroma",
                    $"all {_maxClients} pipe slots taken - dropped the client {evictedIdleMs / 1000.0:0.#} s without a frame to admit a new one");
            if (_connLog.Allow()) Log.Info("chroma", "host connected to the pipe");
            _ = ServeClientAsync(pipe, client);
        }
    }

    void AcceptError(Exception ex)
    {
        if (_firstInstance && ex is System.ComponentModel.Win32Exception { NativeErrorCode: ERROR_ACCESS_DENIED })
        {
            Log.Occasional("chroma-squat", "chroma", $"pipe {_name} already exists (another process owns it) - Chroma feed off until it goes away");
            Pause(5000);
            return;
        }
        Log.Occasional("chroma-accept", "chroma", $"pipe accept error: {ex.Message}");
        Pause(200);
    }

    /// <summary>Sleep, cut short by Dispose.</summary>
    void Pause(int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (!_stopping.IsCancellationRequested && Environment.TickCount64 < until) Thread.Sleep(50);
    }

    async Task ServeClientAsync(NamedPipeServerStream pipe, PipeClient client)
    {
        // Reused across messages on THIS connection: the old per-message
        // `new byte[n*4]` allocated at the host's frame rate (up to 28 KB/frame
        // at the size ceiling). The published grid itself must stay a fresh
        // array — it's handed out by reference.
        var head = new byte[5];
        byte[] body = Array.Empty<byte>();
        bool proven = false;
        try
        {
            using (pipe)
            using (var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(client.Dropped))
            {
                firstFrame.CancelAfter(_firstFrameDeadlineMs);
                while (true)
                {
                    // Proven: a real host. No deadline from here on, however
                    // quiet it goes; only a newcomer needing its slot after
                    // idleEvictMs of quiet takes it (Admit).
                    var ct = proven ? client.Dropped : firstFrame.Token;
                    if (!await ReadExact(pipe, head, 5, ct)) break;
                    int rows = head[1] | (head[2] << 8);
                    int cols = head[3] | (head[4] << 8);
                    int n = rows * cols;
                    if (n <= 0 || n > 4096) break;               // sanity
                    if (body.Length < n * 4) body = new byte[n * 4];
                    if (!await ReadExact(pipe, body, n * 4, ct)) break;

                    var grid = new Rgb[n];
                    for (int i = 0; i < n; i++)
                    {
                        // COLORREF = 0x00BBGGRR
                        byte r = body[i * 4], gg = body[i * 4 + 1], b = body[i * 4 + 2];
                        grid[i] = new Rgb(r, gg, b);
                    }
                    _publish(head[0], grid, rows, cols);
                    client.Framed();
                    if (!proven)
                    {
                        proven = true;
                        if (_connLog.Allow()) Log.Info("chroma", $"first frame: type={head[0]} {rows}x{cols}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (client.Evicted) { /* its slot went to a newcomer; Admit's caller logged it */ }
        catch (OperationCanceledException) when (!proven && !_stopping.IsCancellationRequested)
        {
            Log.Occasional("chroma-silent", "chroma",
                $"dropping a pipe client that connected but sent no frame within {_firstFrameDeadlineMs / 1000.0:0.#}s");
        }
        catch (OperationCanceledException) { /* the server is being disposed (the harness): the pipe went with it */ }
        catch (Exception ex) { Log.Occasional("chroma-pipe", "chroma", $"pipe error: {ex.Message}"); }
        finally { Release(client); }
        if (_connLog.Allow()) Log.Info("chroma", "host disconnected from the pipe");
    }

    static async Task<bool> ReadExact(Stream s, byte[] buf, int len, CancellationToken ct)
    {
        int got = 0;
        while (got < len)
        {
            int n = await s.ReadAsync(buf.AsMemory(got, len - got), ct);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}

/// <summary>Per-minute cap for peer-triggered log lines (a pipe connect, a REST
/// init): any local process - or a web page posting to localhost - could
/// otherwise grow the log without bound at request rate.</summary>
internal sealed class LogBudget
{
    readonly object _gate = new();
    readonly int _perMinute;
    long _window;
    int _count;

    public LogBudget(int perMinute) => _perMinute = perMinute;

    public bool Allow()
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (now - _window >= 60_000) { _window = now; _count = 0; }
            return ++_count <= _perMinute;
        }
    }
}

/// <summary>Every LED mirrors the Chroma feed at its physical position -
/// the rig follows Wallpaper Engine (and Chroma-enabled games) directly. Shows
/// a slow "waiting" breath until the first frame arrives.</summary>
public sealed class ChromaSync : IEffect
{
    public string Name => "Chroma Sync";
    public bool UsesBaseColor => false;
    // Live feed - must STREAM, never bake into a fixed hardware loop.
    public bool Bakeable => false;
    public bool HasSpeed => false;      // driven by the game/host; speed is meaningless
    // The host pushes a new frame at any moment: keep the engine at full rate
    // (its idle throttle would turn a game's next frame into 100 ms of lag).
    public bool LiveInput => true;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        ChromaFeed.Start();
        if (!ChromaFeed.Active)
        {
            // No host connected yet: a dim teal breath so it's obviously armed
            // but not receiving.
            double v = 0.05 + 0.10 * (0.5 + 0.5 * Math.Sin(t * 1.5));
            var c = ColorUtil.HsvToRgb(180, 0.7, v);
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
            return;
        }
        for (int i = 0; i < buf.Length; i++)
            buf[i] = ChromaFeed.Sample(pos[i].X, pos[i].Y);
    }
}
