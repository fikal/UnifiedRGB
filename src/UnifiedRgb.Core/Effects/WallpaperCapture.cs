using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using WGC = Windows.Graphics.Capture;
using WDX = Windows.Graphics.DirectX;
using WDXD3 = Windows.Graphics.DirectX.Direct3D11;

namespace UnifiedRgb.Core.Effects;

/// <summary>Captures the ACTUAL wallpaper - not the composed screen - via the
/// Windows Graphics Capture API pointed at Wallpaper Engine's DirectX render
/// window (WPEDesktopDX11Window). WGC reads that window's own swapchain even
/// when other windows/games cover it (PrintWindow can't - it returns the
/// on-screen composite for an occluded DX window). Each captured GPU frame is
/// copied to a tiny CPU-readable staging texture and downsampled to a grid;
/// sampleable by LED position. No Chroma / SDK involved.</summary>
public static class WallpaperCapture
{
    const int W = ColorGrid.W, H = ColorGrid.H;

    static readonly object _lock = new();
    static readonly ColorGrid _grid = new(blend: 0.4);   // shared grid core
    static long _lastTouch, _lastFrame;
    static Thread? _thread;

    public static bool WindowFound => Environment.TickCount64 - Interlocked.Read(ref _lastFrame) < 3000;

    public static void Touch()
    {
        Interlocked.Exchange(ref _lastTouch, Environment.TickCount64);
        if (_thread != null) return;
        lock (_lock)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "wallpaper-capture" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public static Rgb Sample(float x, float y) => _grid.Sample(x, y);

    /*----- WGC session lifetime -----*/
    static ID3D11Device? _d3d;
    static ID3D11DeviceContext? _ctx;
    static WGC.GraphicsCaptureItem? _item;
    static WGC.Direct3D11CaptureFramePool? _pool;
    static WGC.GraphicsCaptureSession? _session;
    static ID3D11Texture2D? _staging;
    static WDXD3.IDirect3DDevice? _winrtDevice;   // reused wrapper over _d3d (see StartSession)
    static IntPtr _capturedWnd;
    static long _sessionStart;
    static double _cropX, _cropY, _cropW = 1, _cropH = 1;   // primary-monitor sub-rect (fractions)

    static void Loop()
    {
        var bgra = new byte[W * H * 4];
        int fails = 0;
        try
        {
            while (Environment.TickCount64 - Interlocked.Read(ref _lastTouch) < 5000)
            {
                try
                {
                    // Re-derive the render window only when the session is NOT
                    // healthy: no session, the captured window died, or frames
                    // stopped. The old unconditional FindWallpaperWindow() cost
                    // process snapshots + a full EnumWindows with a string per
                    // window, 10x/s, forever - ~0.5-1 MB/s of garbage to
                    // re-learn an HWND that changes maybe once an hour.
                    long now = Environment.TickCount64;
                    bool healthy = _session != null && _capturedWnd != IntPtr.Zero && IsWindow(_capturedWnd)
                                   && (now - Interlocked.Read(ref _lastFrame) < 3000 || now - _sessionStart < 3000);
                    if (!healthy)
                    {
                        IntPtr wnd = FindWallpaperWindow();
                        if (wnd == IntPtr.Zero) { Log.Occasional("wallpaper", "nowin", "no WE render window"); TearDown(); Thread.Sleep(500); continue; }
                        if (wnd != _capturedWnd || _session == null)
                        { Log.Info("wallpaper", $"found window {wnd:X}, starting WGC"); TearDown(); StartSession(wnd); }
                    }
                    PumpFrame(bgra);
                    fails = 0;
                }
                catch (Exception ex)
                {
                    Log.Occasional("wallpaper", "cap", $"capture failed: {ex.GetType().Name}: {ex.Message}");
                    TearDown();
                    if (++fails >= 20) { Log.Warn("wallpaper", "too many capture failures - giving up"); break; }
                    Thread.Sleep(500);
                }
                Thread.Sleep(100);
            }
        }
        finally { TearDown(); DisposeDevice(); lock (_lock) _thread = null; }
    }

    static void EnsureDevice()
    {
        if (_d3d != null) return;
        D3D11.D3D11CreateDevice(null!, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport, null!,
            out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
        if (dev == null || ctx == null) throw new InvalidOperationException("D3D11 device create failed");
        _d3d = dev; _ctx = ctx;
    }

    static void StartSession(IntPtr wnd)
    {
        Log.Info("wallpaper", "step: EnsureDevice");
        EnsureDevice();
        // WGC item from HWND (interop).
        Log.Info("wallpaper", "step: activation factory");
        var interop = WGC.GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        // IGraphicsCaptureItem ABI interface IID (NOT the projected runtime
        // class GUID - that's E_INVALIDARG "value not in expected range").
        Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        // WGC needs a TOP-LEVEL window; WE's render surface is a child of the
        // desktop's WorkerW, which throws E_INVALIDARG. Capture the top-level
        // ancestor - it still holds the wallpaper and isn't affected by
        // overlapping app windows.
        IntPtr top = GetAncestor(wnd, 2 /*GA_ROOT*/);
        if (top == IntPtr.Zero) top = wnd;
        Log.Info("wallpaper", $"step: CreateForWindow (child {wnd:X} -> top {top:X} class '{ClassName(top)}')");
        interop.CreateForWindow(top, ref iid, out IntPtr itemPtr);
        _item = WGC.GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        Log.Info("wallpaper", "step: item created");

        // Wrap our D3D device as a WinRT IDirect3DDevice — created ONCE and
        // reused across sessions (the D3D device itself is reused too). The
        // old per-session wrapper was never disposed, leaking a COM ref on
        // every WE restart / capture-error recovery.
        if (_winrtDevice == null)
        {
            using var dxgi = _d3d!.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
            _winrtDevice = MarshalInspectable<WDXD3.IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        var winrtDevice = _winrtDevice;

        var size = _item.Size;
        _pool = WGC.Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, WDX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsBorderRequired = false; } catch { }   // hide the yellow capture outline
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        _session.StartCapture();
        _capturedWnd = wnd;
        _sessionStart = Environment.TickCount64;   // grace period before "no frames" re-find

        // We capture Progman = the whole virtual desktop. Crop to the PRIMARY
        // monitor (where the wallpaper is) as fractions of the virtual desktop,
        // so it's DPI-independent: primary sits at virtual (0,0).
        double vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);   // XVIRTUALSCREEN, YVIRTUALSCREEN
        double vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);   // CXVIRTUALSCREEN, CYVIRTUALSCREEN
        double pw = GetSystemMetrics(0), ph = GetSystemMetrics(1);     // CXSCREEN, CYSCREEN (primary)
        if (vw > 0 && vh > 0)
        {
            _cropX = (0 - vx) / vw; _cropY = (0 - vy) / vh;
            _cropW = pw / vw; _cropH = ph / vh;
        }
        else { _cropX = _cropY = 0; _cropW = _cropH = 1; }
        Log.Info("wallpaper", $"WGC session started {size.Width}x{size.Height}, crop primary [{_cropX:0.00},{_cropY:0.00} {_cropW:0.00}x{_cropH:0.00}]");
    }

    static void PumpFrame(byte[] bgra)
    {
        if (_pool == null) return;
        using var frame = _pool.TryGetNextFrame();
        if (frame == null) return;

        // The projected surface is IClosable: dispose it per frame instead of
        // leaving 20 finalizable RCWs/s to the GC.
        using var surface = frame.Surface;
        using var srcTex = GetTexture(surface);
        var desc = srcTex.Description;

        // Lazily (re)create a 1:1 staging copy of the source, CPU-readable.
        if (_staging == null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
        {
            _staging?.Dispose();
            _staging = _d3d!.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1,
                Format = desc.Format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            });
        }
        _ctx!.CopyResource(_staging, srcTex);

        var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Downsample(map.DataPointer, (int)map.RowPitch, (int)desc.Width, (int)desc.Height, bgra);
        }
        finally { _ctx.Unmap(_staging, 0); }

        _grid.BlendBgra(bgra);
        Interlocked.Exchange(ref _lastFrame, Environment.TickCount64);
    }

    // ID3D11Texture2D IID (hardcoded - a projected type's .GUID is NOT the
    // COM interface IID, which yields a garbage pointer and a native crash).
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    static ID3D11Texture2D GetTexture(WDXD3.IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = IID_ID3D11Texture2D;
        access.GetInterface(ref iid, out IntPtr p);
        if (p == IntPtr.Zero) throw new InvalidOperationException("no ID3D11Texture2D from surface");
        return new ID3D11Texture2D(p);
    }

    // Box-average the full frame down into the W x H grid.
    static unsafe void Downsample(IntPtr data, int rowPitch, int sw, int sh, byte[] outBgra)
    {
        byte* src = (byte*)data;
        // Crop to the primary-monitor sub-rect (in source pixels).
        int cx = Math.Clamp((int)(_cropX * sw), 0, sw - 1);
        int cy = Math.Clamp((int)(_cropY * sh), 0, sh - 1);
        int cw = Math.Clamp((int)(_cropW * sw), 1, sw - cx);
        int ch = Math.Clamp((int)(_cropH * sh), 1, sh - cy);
        for (int gy = 0; gy < H; gy++)
        {
            int y0 = cy + (int)((long)gy * ch / H), y1 = cy + (int)((long)(gy + 1) * ch / H);
            if (y1 <= y0) y1 = y0 + 1;
            for (int gx = 0; gx < W; gx++)
            {
                int x0 = cx + (int)((long)gx * cw / W), x1 = cx + (int)((long)(gx + 1) * cw / W);
                if (x1 <= x0) x1 = x0 + 1;
                long b = 0, g = 0, r = 0; int n = 0;
                int stepY = Math.Max(1, (y1 - y0) / 4), stepX = Math.Max(1, (x1 - x0) / 4);
                for (int y = y0; y < y1; y += stepY)
                {
                    byte* row = src + (long)y * rowPitch;
                    for (int x = x0; x < x1; x += stepX)
                    {
                        byte* px = row + x * 4;   // BGRA
                        b += px[0]; g += px[1]; r += px[2]; n++;
                    }
                }
                if (n == 0) n = 1;
                int i = (gy * W + gx) * 4;
                outBgra[i] = (byte)(b / n); outBgra[i + 1] = (byte)(g / n); outBgra[i + 2] = (byte)(r / n);
            }
        }
    }

    static void TearDown()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        _session = null; _pool = null; _item = null; _staging = null; _capturedWnd = IntPtr.Zero;
    }
    static void DisposeDevice()
    {
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { }
        try { _ctx?.Dispose(); } catch { }
        try { _d3d?.Dispose(); } catch { }
        _winrtDevice = null; _ctx = null; _d3d = null;
    }

    /*----- find Wallpaper Engine's render window (static delegates: no GC of thunks) -----*/
    static readonly EnumProc _topProc = TopCallback;
    static readonly EnumProc _childProc = ChildCallback;
    static readonly HashSet<uint> _wpPids = new();
    static long _pidsStamp;
    static IntPtr _best; static long _bestArea;

    static IntPtr FindWallpaperWindow()
    {
        if (Environment.TickCount64 - _pidsStamp > 2000)
        {
            _pidsStamp = Environment.TickCount64;
            _wpPids.Clear();
            foreach (var name in new[] { "wallpaper64", "wallpaper32", "wallpaperwindow" })
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    try { _wpPids.Add((uint)p.Id); } catch { } finally { p.Dispose(); }
        }
        if (_wpPids.Count == 0) return IntPtr.Zero;
        _best = IntPtr.Zero; _bestArea = 0;
        EnumWindows(_topProc, IntPtr.Zero);
        GC.KeepAlive(_topProc); GC.KeepAlive(_childProc);
        return _best;
    }

    static void Consider(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (!_wpPids.Contains(pid)) return;
        if (!GetWindowRect(h, out var r)) return;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        long area = (long)w * ht;
        if (area > _bestArea && w >= 640 && ht >= 360) { _bestArea = area; _best = h; }
    }
    static bool TopCallback(IntPtr h, IntPtr _)
    {
        Consider(h);
        var cn = ClassName(h);
        if (cn == "WorkerW" || cn == "Progman") EnumChildWindows(h, _childProc, IntPtr.Zero);
        return true;
    }
    static bool ChildCallback(IntPtr h, IntPtr _) { Consider(h); return true; }
    static string ClassName(IntPtr h) { var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString(); }

    /*----- interop -----*/
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);
        void CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface([In] ref Guid iid, out IntPtr p);
    }
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
}

/// <summary>Every LED mirrors the live wallpaper at its physical position -
/// follows Wallpaper Engine even when windows/games cover the screen. Amber
/// breath until the first frame is captured.</summary>
public sealed class WallpaperSync : IEffect
{
    public string Name => "Wallpaper";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;
    public bool HasSpeed => false;      // mirrors the wallpaper; speed is meaningless

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        WallpaperCapture.Touch();
        if (!WallpaperCapture.WindowFound)
        {
            double v = 0.05 + 0.10 * (0.5 + 0.5 * Math.Sin(t * 1.5));
            var c = ColorUtil.HsvToRgb(35, 0.7, v);
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
            return;
        }
        for (int i = 0; i < buf.Length; i++)
            buf[i] = WallpaperCapture.Sample(pos[i].X, pos[i].Y);
    }
}
