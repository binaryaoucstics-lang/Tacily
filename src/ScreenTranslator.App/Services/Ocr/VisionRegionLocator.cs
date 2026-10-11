using System.Diagnostics;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>Detection alone supplies glyph scale and ink bounds; no text is recognized.</summary>
public sealed class VisionRegionLocator(LocalTextDetector detector)
{
    public async Task<IReadOnlyList<OcrBlock>> LocateAsync(CapturedBitmap source, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        var image = VisionInkPreprocessor.CleanRuling(VisionInkPreprocessor.ResizeForLayout(source));
        var regions = await detector.DetectAsync(image, token).ConfigureAwait(false);
        var sx = source.Width / (double)image.Width; var sy = source.Height / (double)image.Height;
        var lines = regions.Select((region, index) => new OcrBlock($"ink-{index}", string.Empty, region.Confidence,
            new PixelRect((int)Math.Floor(region.Bounds.X * sx), (int)Math.Floor(region.Bounds.Y * sy),
                (int)Math.Ceiling(region.Bounds.Width * sx), (int)Math.Ceiling(region.Bounds.Height * sy))
                .Intersect(new(0, 0, source.Width, source.Height)), index)).ToArray();
        AppDiagnostics.Write($"vision_regions_complete elapsed_ms={elapsed.ElapsedMilliseconds} regions={lines.Length} recognition=false");
        return lines;
    }
}
