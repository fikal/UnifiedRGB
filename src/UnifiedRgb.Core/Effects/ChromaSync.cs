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
    static Thread? _server;
    // Every accepted instance costs a reader thread blocked in Read with no
    // timeout; a host that leaks connections (or any hostile local process)
    // must not pile those up in the elevated 24/7 process.
    const int MaxClients = 16;
    static int _clients;
    static readonly LogBudget _connLog = new(10);

    /// <summary>A frame arrived within the last few seconds.</summary>
    public static bool Active => Environment.TickCount64 - Interlocked.Read(ref _lastFrame) < 4000;

    /// <summary>Start the pipe server (idempotent). Runs for the app's life.</summary>
    public static void Start()
    {
        if (_server != null) return;
        lock (_lock)
        {
            if (_server != null) return;
            _server = new Thread(ServerLoop) { IsBackground = true, Name = "chroma-feed" };
            _server.Start();
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
    const uint PIPE_TYPE_BYTE = 0, PIPE_WAIT = 0;
    const uint PIPE_UNLIMITED_INSTANCES = 255;
    const int ERROR_ACCESS_DENIED = 5;
    static bool _firstInstance = true;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint rev, out IntPtr psd, out int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuf, uint inBuf, uint timeout, ref SECURITY_ATTRIBUTES sa);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    static NamedPipeServerStream CreateServer()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(PipeSddl, 1, out var psd, out _))
            throw new InvalidOperationException("bad pipe SDDL");
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = psd, bInheritHandle = 0 };
            // PIPE_UNLIMITED_INSTANCES: with maxInstances=1 a second host
            // (Wallpaper Engine + a game) got ERROR_PIPE_BUSY for as long as the
            // first stayed connected. Every accepted connection gets its own
            // reader thread; the accept loop immediately creates the next instance.
            // FIRST_PIPE_INSTANCE on our very first create: if the name already
            // exists another process got there first and would share the
            // hosts' connections with us - refuse instead of silently joining
            // it. Later instances are ours (the flag would fail them).
            uint openMode = PIPE_ACCESS_INBOUND | (_firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
            var h = CreateNamedPipeW(@"\\.\pipe\" + PipeName, openMode,
                PIPE_TYPE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES, 0, 1 << 20, 0, ref sa);
            if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _firstInstance = false;
            // isConnected:false - the handle is a fresh, unconnected pipe; with
            // `true` a client racing CreateNamedPipe->ConnectNamedPipe made
            // WaitForConnection throw and tore down that client's connection.
            return new NamedPipeServerStream(PipeDirection.In, false, false, h);
        }
        finally { LocalFree(psd); }
    }

    static void ServerLoop()
    {
        // Accept loop: one pipe instance per connected host, each served on its
        // own thread, so a second host never waits for the first to leave.
        while (true)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreateServer(); }
            catch (Exception ex) { AcceptError(ex); continue; }
            // Split from the create: an instance whose accept fails (a client
            // connected and dropped before we got here: "pipe is broken") must
            // be released here, not left to the finalizer.
            try { pipe.WaitForConnection(); }
            catch (Exception ex) { pipe.Dispose(); AcceptError(ex); continue; }
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                pipe.Dispose();
                Log.Occasional("chroma-clients", "chroma", $"more than {MaxClients} pipe clients - refusing extra connections");
                continue;
            }
            if (_connLog.Allow()) Log.Info("chroma", "host connected to the pipe");
            new Thread(() => ServeClient(pipe)) { IsBackground = true, Name = "chroma-feed-client" }.Start();
        }
    }

    static void AcceptError(Exception ex)
    {
        if (_firstInstance && ex is System.ComponentModel.Win32Exception { NativeErrorCode: ERROR_ACCESS_DENIED })
        {
            Log.Occasional("chroma-squat", "chroma", $"pipe {PipeName} already exists (another process owns it) - Chroma feed off until it goes away");
            Thread.Sleep(5000);
            return;
        }
        Log.Occasional("chroma", "feed", $"pipe accept error: {ex.Message}");
        Thread.Sleep(200);
    }

    static void ServeClient(NamedPipeServerStream pipe)
    {
        // Reused across messages on THIS connection: the old per-message
        // `new byte[n*4]` allocated at the host's frame rate (up to 28 KB/frame
        // at the size ceiling). The published grid itself must stay a fresh
        // array — it's handed out by reference.
        var head = new byte[5];
        byte[] body = Array.Empty<byte>();
        try
        {
            using (pipe)
            {
                bool firstFrame = true;
                while (ReadExact(pipe, head, 5))
                {
                    int rows = head[1] | (head[2] << 8);
                    int cols = head[3] | (head[4] << 8);
                    int n = rows * cols;
                    if (n <= 0 || n > 4096) break;               // sanity
                    if (body.Length < n * 4) body = new byte[n * 4];
                    if (!ReadExact(pipe, body, n * 4)) break;

                    var grid = new Rgb[n];
                    for (int i = 0; i < n; i++)
                    {
                        // COLORREF = 0x00BBGGRR
                        byte r = body[i * 4], gg = body[i * 4 + 1], b = body[i * 4 + 2];
                        grid[i] = new Rgb(r, gg, b);
                    }
                    Publish(head[0], grid, rows, cols);
                    if (firstFrame) { if (_connLog.Allow()) Log.Info("chroma", $"first frame: type={head[0]} {rows}x{cols}"); firstFrame = false; }
                }
            }
        }
        catch (Exception ex) { Log.Occasional("chroma", "feed", $"pipe error: {ex.Message}"); }
        finally { Interlocked.Decrement(ref _clients); }
        if (_connLog.Allow()) Log.Info("chroma", "host disconnected from the pipe");
    }

    static bool ReadExact(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
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
