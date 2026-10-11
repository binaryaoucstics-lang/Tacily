using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public static class CapturedBitmapCropper
{
    public static CapturedBitmap Crop(CapturedBitmap source, PixelRect bounds)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.X < 0 || bounds.Y < 0 ||
            bounds.Right > source.Width ||
            bounds.Bottom > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "裁剪范围必须完全位于截图内且具有正宽高。");
        }

        var targetStride = checked(bounds.Width * 4);
        var targetPixels = GC.AllocateUninitializedArray<byte>(
            checked(targetStride * bounds.Height));
        var sourcePixels = source.Pixels.Span;

        for (var row = 0; row < bounds.Height; row++)
        {
            var sourceOffset = checked(
                ((bounds.Y + row) * source.Stride) + (bounds.X * 4));
            sourcePixels
                .Slice(sourceOffset, targetStride)
                .CopyTo(targetPixels.AsSpan(row * targetStride, targetStride));
        }

        return new CapturedBitmap(
            bounds.Width,
            bounds.Height,
            targetStride,
            targetPixels);
    }
}
