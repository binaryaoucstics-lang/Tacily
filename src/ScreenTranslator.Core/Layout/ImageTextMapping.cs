using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class ImageTextMapping
{
    public static DipRect ToPreview(PixelRect text, PixelSize image, DipSize preview)
    {
        if (image.IsEmpty || preview.Width <= 0 || preview.Height <= 0) return default;
        var scale = Math.Min(preview.Width / image.Width, preview.Height / image.Height);
        return new DipRect((preview.Width - image.Width * scale) / 2 + text.X * scale,
            (preview.Height - image.Height * scale) / 2 + text.Y * scale, text.Width * scale, text.Height * scale);
    }

    public static PixelRect? ToScreen(PixelRect text, PixelRect crop, PixelRect originalWindow, PixelRect currentWindow)
    {
        if (originalWindow.Width != currentWindow.Width || originalWindow.Height != currentWindow.Height) return null;
        return text.Intersect(new PixelRect(0, 0, crop.Width, crop.Height))
            .Translate(new PixelPoint(crop.X + currentWindow.X - originalWindow.X, crop.Y + currentWindow.Y - originalWindow.Y));
    }
}
