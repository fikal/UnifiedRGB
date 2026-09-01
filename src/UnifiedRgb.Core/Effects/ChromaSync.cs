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
    static volatile Rgb[]? _grid;      // row-major
    static volatile int _rows = 1, _cols = 1;
    static long _lastFrame;
    static Thread? _server;

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
    /// modern games use instead of the C++ DLL). Same grid the pipe feeds.</summary>
    public static void PushGrid(Rgb[] grid, int rows, int cols)
    {
        if (grid.Length == 0 || rows <= 0 || cols <= 0) return;
        _grid = grid; _rows = rows; _cols = cols;
        Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);
    }

    /// <summary>Color at normalized (x, y). Averages the ChromaLink 5-strip or
    /// samples the keyboard grid cell; falls back to black when idle.</summary>
    public static Rgb Sample(float x, float y)
    {
        var g = _grid;
        if (g == null || !Active) return default;
        int gx = Math.Clamp((int)(x * _cols), 0, _cols - 1);
        int gy = Math.Clamp((int)(y * _rows), 0, _rows - 1);
        return g[gy * _cols + gx];
    }

    // We run elevated (high integrity); Wallpaper Engine runs as a normal user
    // (medium). A default pipe blocks the lower process from connecting, so we
    // create it with an SDDL that grants Everyone read/write AND labels the
    // pipe low-integrity, so writes "down" from WE are allowed.
    const string PipeSddl = "D:(A;;GRGW;;;WD)(A;;GRGW;;;AN)S:(ML;;NW;;;LW)";
    const uint PIPE_ACCESS_INBOUND = 0x00000001;
    const uint PIPE_TYPE_BYTE = 0, PIPE_WAIT = 0;

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
            var h = CreateNamedPipeW(@"\\.\pipe\" + PipeName, PIPE_ACCESS_INBOUND,
                PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0, 1 << 20, 0, ref sa);
            if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.In, false, true, h);
        }
        finally { LocalFree(psd); }
    }

    static void ServerLoop()
    {
        // Reused across connections and messages (single-threaded loop): the
        // old per-message `new byte[n*4]` allocated at the host's frame rate
        // (up to 28 KB/frame at the size ceiling). The published grid itself
        // must stay a fresh array — it's handed out by reference.
        var head = new byte[5];
        byte[] body = Array.Empty<byte>();
        while (true)
        {
            try
            {
                using var pipe = CreateServer();
                pipe.WaitForConnection();
                Log.Info("chroma", "host connected to the pipe");
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
                    _grid = grid; _rows = rows; _cols = cols;
                    Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);
                    if (firstFrame) { Log.Info("chroma", $"first frame: type={head[0]} {rows}x{cols}"); firstFrame = false; }
                }
            }
            catch (Exception ex) { Log.Occasional("chroma", "feed", $"pipe error: {ex.Message}"); }
            Thread.Sleep(200);   // client gone; wait for the next connection
        }
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
