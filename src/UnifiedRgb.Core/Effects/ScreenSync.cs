using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Effects;

/// <summary>Ambient screen sampler: the primary display, downscaled by GDI
/// (HALFTONE = averaged, not point-sampled) into a small color grid at 10 Hz
/// on its own background thread, with temporal smoothing so LEDs glide
/// between colors instead of flickering. Starts on first Touch, stops itself
/// when nothing has sampled for a few seconds. Zero dependencies.</summary>
public static class AmbientScreen
{
    const int W = ColorGrid.W, H = ColorGrid.H;
    const double Vibrance = 1.45;       // saturation boost - a screen's averaged
                                        // regions skew gray; the rig looks dead
                                        // without a push toward the dominant hue

    static readonly object _lock = new();
    static readonly ColorGrid _grid = new(blend: 0.35);   // shared grid core
    static Thread? _thread;
    static long _lastTouch;

    /// <summary>Keep the sampler alive; call from every effect frame.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastTouch, Environment.TickCount64);
        if (_thread != null) return;
        lock (_lock)
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ambient-screen" };
            _thread.Start();
        }
    }

    /// <summary>Color of the screen region at normalized position (x, y).</summary>
    public static Rgb Sample(float x, float y) => _grid.Sample(x, y);

    static void Loop()
    {
        var bgra = new byte[W * H * 4];
        while (true)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastTouch) > 5000)
            {
                lock (_lock) _thread = null;
                return;
            }
            try
            {
                if (Capture(bgra)) _grid.BlendBgra(bgra, Vibrance);
            }
            catch (Exception ex) { Log.Occasional("ambient", "cap", $"screen capture failed: {ex.Message}"); }
            Thread.Sleep(100);
        }
    }

    static bool Capture(byte[] outBgra)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);   // primary screen
        if (sw <= 0 || sh <= 0) return false;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return false;
        IntPtr hdcMem = IntPtr.Zero, dib = IntPtr.Zero;
        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            var bmi = new BITMAPINFO
            {
                biSize = 40, biWidth = W, biHeight = -H,   // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            dib = CreateDIBSection(hdcMem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            // Restore the DC's original bitmap before deletion (correct GDI
            // select/restore pairing for a path that runs 10x/s forever).
            IntPtr prevBmp = SelectObject(hdcMem, dib);
            try
            {
                SetStretchBltMode(hdcMem, 4 /* HALFTONE = averaging */);
                SetBrushOrgEx(hdcMem, 0, 0, IntPtr.Zero);
                if (!StretchBlt(hdcMem, 0, 0, W, H, hdcScreen, 0, 0, sw, sh, 0x00CC0020 /* SRCCOPY */))
                    return false;
                GdiFlush();
                Marshal.Copy(bits, outBgra, 0, outBgra.Length);
                return true;
            }
            finally { SelectObject(hdcMem, prevBmp); }
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // color table unused for 32bpp BI_RGB
        public uint c0, c1, c2;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
        IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}

/// <summary>Every LED mirrors the screen region at its physical position -
/// the whole rig follows the wallpaper (animated ones included: Wallpaper
/// Engine, videos, games). The sampler's temporal smoothing keeps it calm.</summary>
public sealed class ScreenSync : IEffect
{
    public string Name => "Screen Ambient";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;      // live capture - can't be pre-baked to the fans
    public bool HasSpeed => false;      // mirrors the screen; speed is meaningless

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        AmbientScreen.Touch();
        for (int i = 0; i < buf.Length; i++)
            buf[i] = AmbientScreen.Sample(pos[i].X, pos[i].Y);
    }
}
