namespace UnifiedRgb.Core.Effects;

/// <summary>Turning a device's own LED coordinates into desk coordinates.
///
/// This is the whole trick behind desk-wide effects. Every effect renders
/// against normalized positions and knows nothing about devices, so feeding it
/// desk positions instead of device-local ones makes all 58 of them run across
/// the desk with no change to any of them. The mapping is computed once when a
/// channel starts, never per frame.</summary>
public static class CanvasMapper
{
    /// <summary>One local position (0..1 inside the device) as a desk position
    /// (0..1 across the canvas). Flip first, then rotate, then place: flipping
    /// after a rotation would mirror a different axis than the button said.
    ///
    /// Rotation turns the device's layout inside its rectangle and does not
    /// change the rectangle, so what you drag on the desk is what it covers.</summary>
    public static LedPos Map(LedPos local, CanvasItem item, int canvasWidth, int canvasHeight)
    {
        float x = Math.Clamp(local.X, 0f, 1f);
        float y = Math.Clamp(local.Y, 0f, 1f);

        if (item.FlipX) x = 1f - x;
        if (item.FlipY) y = 1f - y;

        switch (((item.Rotation % 360) + 360) % 360)
        {
            case 90: (x, y) = (1f - y, x); break;
            case 180: (x, y) = (1f - x, 1f - y); break;
            case 270: (x, y) = (y, 1f - x); break;
        }

        double w = Math.Max(1, canvasWidth), h = Math.Max(1, canvasHeight);
        return new LedPos((float)((item.X + x * item.W) / w), (float)((item.Y + y * item.H) / h));
    }

    /// <summary>Desk positions for a device's LED range, or null when the
    /// canvas is off or this device has no place on it. Null means the caller
    /// falls back to the device-local coordinates it has always used.</summary>
    public static LedPos[]? Positions(IRgbDevice device, int offset, int count, CanvasLayout? layout)
    {
        if (layout is not { Enabled: true }) return null;
        var item = layout.ItemFor(device.Name);
        if (item == null) return null;

        // The range's own local coordinates first, so a zone still renders as
        // its own shape; then every point moved onto the desk.
        var local = EffectEngine.ZonePositions(device, offset, count);
        var desk = new LedPos[local.Length];
        for (int i = 0; i < local.Length; i++)
            desk[i] = Map(local[i], item, layout.Width, layout.Height);
        return desk;
    }

    /// <summary>Positions from a hand-described layout, for devices whose
    /// driver cannot know their shape: a strip stuck to a GPU, a ring of fan
    /// LEDs, a serpentine matrix. Null when the description does not fit the
    /// LED count, because a half-right layout is worse than the fallback.</summary>
    public static LedPos[]? FromOverride(LedLayoutOverride? layout, int ledCount)
    {
        if (layout == null || ledCount <= 0) return null;

        switch (layout.Shape?.ToLowerInvariant())
        {
            case "strip":
            {
                var pos = new LedPos[ledCount];
                for (int i = 0; i < ledCount; i++)
                    pos[i] = new LedPos(ledCount == 1 ? 0.5f : i / (float)(ledCount - 1), 0.5f);
                return pos;
            }

            case "ring":
            {
                var pos = new LedPos[ledCount];
                for (int i = 0; i < ledCount; i++)
                {
                    // Start at the top and run clockwise, which is how a fan
                    // ring is almost always wired and how the preview draws it.
                    double angle = -Math.PI / 2 + 2 * Math.PI * i / ledCount;
                    pos[i] = new LedPos((float)(0.5 + 0.5 * Math.Cos(angle)),
                                        (float)(0.5 + 0.5 * Math.Sin(angle)));
                }
                return pos;
            }

            case "grid":
            {
                int cols = Math.Max(1, layout.Cols);
                int rows = Math.Max(1, layout.Rows);
                if (cols * rows < ledCount) return null;      // the description is too small to be right

                var pos = new LedPos[ledCount];
                for (int i = 0; i < ledCount; i++)
                {
                    int row = i / cols;
                    int col = i % cols;
                    // Serpentine: every other row is wired back the other way,
                    // so LED 11 of a 10-wide grid is under LED 10, not above 1.
                    if (layout.Serpentine && (row & 1) == 1) col = cols - 1 - col;
                    pos[i] = new LedPos(cols == 1 ? 0.5f : col / (float)(cols - 1),
                                        rows == 1 ? 0.5f : row / (float)(rows - 1));
                }
                return pos;
            }
        }
        return null;
    }
}
