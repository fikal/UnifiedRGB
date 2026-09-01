namespace UnifiedRgb.Core.Effects;

/// <summary>Shared core for the live capture sources (screen, wallpaper): a
/// fixed 32x18 color grid, sampled by normalized LED position, updated by
/// blending each new BGRA capture over the previous state so LEDs glide
/// between colors instead of flickering.
///
/// Publishing is PING-PONG: two buffers swap on every blend, so the steady
/// state allocates nothing (the old implementations built a fresh Rgb[576]
/// per 100 ms tick, ~17.5 KB/s each). A reader holding the just-swapped
/// buffer can see one tick's colors tear for a frame — cosmetically invisible
/// on LEDs and identical in spirit to the old race-free-by-luck pattern.</summary>
sealed class ColorGrid
{
    public const int W = 32, H = 18;

    readonly double _blend;
    Rgb[] _front = new Rgb[W * H];
    Rgb[] _back = new Rgb[W * H];

    /// <param name="blend">new-capture weight per tick (0..1)</param>
    public ColorGrid(double blend) => _blend = blend;

    /// <summary>Color of the region at normalized position (x, y).</summary>
    public Rgb Sample(float x, float y)
    {
        var g = Volatile.Read(ref _front);
        int gx = Math.Clamp((int)(x * W), 0, W - 1);
        int gy = Math.Clamp((int)(y * H), 0, H - 1);
        return g[gy * W + gx];
    }

    /// <summary>Blend a W*H BGRA capture into the grid and publish it.
    /// vibrance &gt; 1 pushes each channel away from the region's gray point
    /// (screens average toward gray; LEDs look dead without the push).</summary>
    public void BlendBgra(byte[] bgra, double vibrance = 1.0)
    {
        var cur = _front;
        var next = _back;
        for (int i = 0; i < W * H; i++)
        {
            double b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            if (vibrance > 1.0)
            {
                double gray = (r + g + b) / 3.0;
                r = Math.Clamp(gray + (r - gray) * vibrance, 0, 255);
                g = Math.Clamp(gray + (g - gray) * vibrance, 0, 255);
                b = Math.Clamp(gray + (b - gray) * vibrance, 0, 255);
            }
            var old = cur[i];
            next[i] = new Rgb(
                (byte)(old.R + (r - old.R) * _blend),
                (byte)(old.G + (g - old.G) * _blend),
                (byte)(old.B + (b - old.B) * _blend));
        }
        _back = cur;
        Volatile.Write(ref _front, next);
    }
}
