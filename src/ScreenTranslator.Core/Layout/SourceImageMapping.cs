using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

/// <summary>Natural image pixels to the displayed image content, clipped to its visible viewport.</summary>
public sealed record SourceImageMapping(PixelSize ImageSize, DipRect DisplayBounds, PixelRect VisibleBounds)
{
    /// <summary>Unclipped projection. A bitmap must be clipped by the viewport, never squeezed to fit it.</summary>
    public DipRect Project(PixelRect region, PixelPoint offset = default) => ImageSize.IsEmpty ? default : new(
        DisplayBounds.X + offset.X + region.X * DisplayBounds.Width / ImageSize.Width,
        DisplayBounds.Y + offset.Y + region.Y * DisplayBounds.Height / ImageSize.Height,
        region.Width * DisplayBounds.Width / ImageSize.Width, region.Height * DisplayBounds.Height / ImageSize.Height);

    public PixelRect? ToScreen(PixelRect text, PixelPoint offset = default)
    {
        if (ImageSize.IsEmpty || DisplayBounds.Width <= 0 || DisplayBounds.Height <= 0) return null;
        var clipped = text.Intersect(new PixelRect(0, 0, ImageSize.Width, ImageSize.Height));
        var sx = DisplayBounds.Width / ImageSize.Width;
        var sy = DisplayBounds.Height / ImageSize.Height;
        var left = (int)Math.Floor(DisplayBounds.X + clipped.X * sx);
        var top = (int)Math.Floor(DisplayBounds.Y + clipped.Y * sy);
        var right = (int)Math.Ceiling(DisplayBounds.X + clipped.Right * sx);
        var bottom = (int)Math.Ceiling(DisplayBounds.Y + clipped.Bottom * sy);
        var screen = new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)).Intersect(VisibleBounds);
        return screen.IsUsable ? screen.Translate(offset) : null;
    }

    public (double X, double Y)? ToImage(PixelPoint screen, PixelPoint offset = default)
    {
        var x = screen.X - offset.X;
        var y = screen.Y - offset.Y;
        if (ImageSize.IsEmpty || DisplayBounds.Width <= 0 || DisplayBounds.Height <= 0
            || x < VisibleBounds.X || x >= VisibleBounds.Right || y < VisibleBounds.Y || y >= VisibleBounds.Bottom) return null;
        var ix = (x - DisplayBounds.X) * ImageSize.Width / DisplayBounds.Width;
        var iy = (y - DisplayBounds.Y) * ImageSize.Height / DisplayBounds.Height;
        return ix >= 0 && ix < ImageSize.Width && iy >= 0 && iy < ImageSize.Height ? (ix, iy) : null;
    }
}
