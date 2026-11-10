namespace BatteryManager.App;

// Physical screen pixels; negative origins are valid on secondary monitors.
internal readonly record struct DisplayRect(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
}

internal static class PanelPlacement
{
    /// <summary>Up to this many devices the panel fits its rows exactly; more devices scroll inside it.</summary>
    public const int MaxVisibleRows = 3;

    /// <summary>Height of one device row. Rows hold one line (name, number, battery glyph) since the bars were removed.</summary>
    public const int RowHeight = 48;

    /// <param name="frameHeight">Physical pixels of window frame that are not client area (window height minus
    /// client height). Added so the client area, not the outer window, gets the content height.</param>
    public static DisplayRect Calculate(DisplayRect monitorWorkArea, DisplayRect trayAnchor,
        double scale, int rowCount, bool demo, int frameHeight = 0)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (rowCount < 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (frameHeight < 0) throw new ArgumentOutOfRangeException(nameof(frameHeight));
        var work = monitorWorkArea;
        if (work.Width <= 0 || work.Height <= 0 || work.Right > int.MaxValue || work.Bottom > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(monitorWorkArea));
        if (trayAnchor.Width < 0 || trayAnchor.Height < 0) throw new ArgumentOutOfRangeException(nameof(trayAnchor));

        // Reduce margins on tiny work areas before sizing, so even a one-pixel
        // work area produces a contained rectangle rather than a negative size.
        var horizontalMargin = (int)Math.Min(Math.Round(12 * scale), (work.Width - 1) / 2);
        var verticalMargin = (int)Math.Min(Math.Round(12 * scale), (work.Height - 1) / 2);
        var availableWidth = work.Width - horizontalMargin * 2;
        var availableHeight = work.Height - verticalMargin * 2;
        var width = (int)Math.Clamp(Math.Round(360 * scale), 1, availableWidth);
        // Rows are RowHeight; 50 covers the list padding (4 + 4) and the footer (4 + 32 + 4 + 1 border) with 1 spare.
        var visibleRows = Math.Clamp(rowCount, 1, MaxVisibleRows);
        var desiredHeight = Math.Round((visibleRows * RowHeight + 50 + (demo ? 28 : 0)) * scale) + frameHeight;
        var height = (int)Math.Clamp(desiredHeight, 1, availableHeight);

        var left = work.X + horizontalMargin;
        var top = work.Y + verticalMargin;
        var rightAligned = checked((int)(work.Right - horizontalMargin - width));
        var bottomAligned = checked((int)(work.Bottom - verticalMargin - height));
        var x = rightAligned;
        var y = bottomAligned;
        if (trayAnchor.Bottom <= work.Y)
            y = top;
        else if (trayAnchor.Right <= work.X)
        {
            x = left;
            y = Math.Clamp(trayAnchor.Y, top, bottomAligned);
        }
        else if (trayAnchor.X >= work.Right)
            y = Math.Clamp(trayAnchor.Y, top, bottomAligned);
        return new(x, y, width, height);
    }
}
