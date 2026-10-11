using System.Runtime.InteropServices;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace ScreenTranslator.App.Services.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrBlock>> RecognizeAsync(
        CapturedBitmap bitmap,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            throw new PlatformNotSupportedException(
                "Windows OCR 仅支持 Windows 10 及更高版本。");
        }

        var engine = CreateEngine(languageTag);
        if (bitmap.Width > OcrEngine.MaxImageDimension ||
            bitmap.Height > OcrEngine.MaxImageDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitmap),
                $"OCR 图片的宽和高不能超过 {OcrEngine.MaxImageDimension} 像素。");
        }

        using var softwareBitmap = await SoftwareBitmapConverter
            .ToSoftwareBitmapAsync(bitmap, cancellationToken)
            .ConfigureAwait(false);
        var result = await engine
            .RecognizeAsync(softwareBitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Lines.Count == 0)
        {
            return Array.Empty<OcrBlock>();
        }

        var blocks = new List<OcrBlock>(result.Lines.Count);
        for (var index = 0; index < result.Lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = result.Lines[index];
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text) || line.Words.Count == 0)
            {
                continue;
            }

            var bounds = GetBounds(line.Words, bitmap.Width, bitmap.Height);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            blocks.Add(
                new OcrBlock(
                    $"block-{blocks.Count + 1}",
                    text,
                    1.0,
                    bounds,
                    blocks.Count));
        }

        return blocks;
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        OcrEngine? engine;
        if (string.IsNullOrWhiteSpace(languageTag) ||
            string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        else
        {
            try
            {
                var language = new Language(languageTag);
                engine = OcrEngine.TryCreateFromLanguage(language);
            }
            catch (Exception exception) when (
                exception is ArgumentException or TypeLoadException or COMException)
            {
                throw new OcrLanguageUnavailableException(languageTag);
            }
        }

        return engine ?? throw new OcrLanguageUnavailableException(languageTag);
    }

    private static PixelRect GetBounds(
        IReadOnlyList<OcrWord> words,
        int bitmapWidth,
        int bitmapHeight)
    {
        var left = words.Min(word => word.BoundingRect.Left);
        var top = words.Min(word => word.BoundingRect.Top);
        var right = words.Max(word => word.BoundingRect.Right);
        var bottom = words.Max(word => word.BoundingRect.Bottom);

        var x = Math.Clamp((int)Math.Floor(left), 0, bitmapWidth);
        var y = Math.Clamp((int)Math.Floor(top), 0, bitmapHeight);
        var clampedRight = Math.Clamp((int)Math.Ceiling(right), x, bitmapWidth);
        var clampedBottom = Math.Clamp((int)Math.Ceiling(bottom), y, bitmapHeight);
        return new PixelRect(x, y, clampedRight - x, clampedBottom - y);
    }
}
