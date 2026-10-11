using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.App.Services.Ocr;

namespace ScreenTranslator.App.Services.Capture;

public sealed class SmartSelectionResolver(IOcrEngine ocr)
{
    public Task<SmartSelection> FromWholeImageAsync(MonitorCapture capture, PixelRect imageBounds,
        string sourceLanguage, nint sourceHandle, CancellationToken token)
    {
        var image = imageBounds.Intersect(capture.Monitor.Bounds);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new SmartSelection(image, null, sourceHandle) { ImageBounds = image });
    }

    public async Task<SmartSelection?> FromImageAsync(MonitorCapture capture, PixelPoint absolutePoint,
        string sourceLanguage, nint sourceHandle, CancellationToken token)
    {
        var monitor = capture.Monitor;
        var localPoint = new PixelPoint(absolutePoint.X - monitor.Bounds.X, absolutePoint.Y - monitor.Bounds.Y);
        var contextBounds = new PixelRect(localPoint.X - 900, localPoint.Y - 650, 1800, 1300)
            .Intersect(new PixelRect(0, 0, capture.Bitmap.Width, capture.Bitmap.Height));
        if (!contextBounds.IsUsable) return null;
        var bitmap = CapturedBitmapCropper.Crop(capture.Bitmap, contextBounds);
        var blocks = await ocr.RecognizeAsync(bitmap, sourceLanguage, token).ConfigureAwait(false);
        var region = SmartTextRegionSelector.Select(blocks,
            new PixelPoint(localPoint.X - contextBounds.X, localPoint.Y - contextBounds.Y),
            new PixelSize(bitmap.Width, bitmap.Height));
        if (region is null) return null;
        return new SmartSelection(region.Bounds.Translate(new PixelPoint(
            monitor.Bounds.X + contextBounds.X, monitor.Bounds.Y + contextBounds.Y)), null, sourceHandle);
    }
}
