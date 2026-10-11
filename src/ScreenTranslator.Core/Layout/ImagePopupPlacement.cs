using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class ImagePopupPlacement
{
    public static PixelRect Place(PixelRect image, PixelRect work, PixelSize size, int minimumWidth, int minimumHeight, int gap)
    {
        var width = Math.Min(size.Width, work.Width);
        var height = Math.Min(size.Height, work.Height);
        var rightSpace = work.Right - image.Right - gap;
        var leftSpace = image.X - work.X - gap;
        var top = Math.Clamp(image.Y, work.Y, Math.Max(work.Y, work.Bottom - height));
        if (rightSpace >= width) return new PixelRect(image.Right + gap, top, width, height);
        if (leftSpace >= width) return new PixelRect(image.X - gap - width, top, width, height);
        if (leftSpace >= minimumWidth) return new PixelRect(work.X, top, leftSpace, height);
        if (rightSpace >= minimumWidth) return new PixelRect(image.Right + gap, top, rightSpace, height);
        var below = work.Bottom - image.Bottom - gap;
        var above = image.Y - work.Y - gap;
        var left = Math.Clamp(image.X, work.X, Math.Max(work.X, work.Right - width));
        if (below >= minimumHeight) return new PixelRect(left, image.Bottom + gap, width, Math.Min(height, below));
        if (above >= minimumHeight) return new PixelRect(left, image.Y - gap - Math.Min(height, above), width, Math.Min(height, above));
        // A full-screen image leaves no non-overlapping in-screen rectangle; use the nearest screen edge.
        return new PixelRect(work.X, top, width, height);
    }
}
