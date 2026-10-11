using OpenCvSharp;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>Only a confirmed local bubble can complete a partially recognized multi-column dialogue.</summary>
public sealed class BubbleTextGrounder(CapturedBitmap source)
{
    private readonly Lazy<IReadOnlyList<PixelRect>> _bubbles = new(() => FindBubbles(source));

    public IReadOnlyList<ImageAtlasRegion> Match(IReadOnlyList<OcrBlock> paragraphs, IReadOnlyList<OcrBlock> lines)
    {
        var strong = WholeImageTextMatcher.Match(paragraphs, lines, true).ToArray();
        var partial = WholeImageTextMatcher.Match(paragraphs, lines, true, .55);
        for (var index = 0; index < strong.Length; index++)
        {
            if (strong[index].TextBounds.Count > 0 || partial[index].TextBounds.Count < 2) continue;
            var anchors = partial[index].TextBounds;
            var bubble = _bubbles.Value.Where(bubble => anchors.All(anchor => Contains(bubble, anchor.Center)))
                .OrderBy(bubble => (long)bubble.Width * bubble.Height).FirstOrDefault();
            if (!bubble.IsUsable) continue;
            var claimed = strong.Select((region, other) => region.TextBounds.Count > 0 ? region : partial[other])
                .Where((_, other) => other != index).SelectMany(region => region.TextBounds).ToArray();
            var rectangles = lines.Where(line => Contains(bubble, line.BoundsInCapturePixels.Center))
                .Select(line => line.BoundsInCapturePixels).Where(rect => rect.IsUsable && !claimed.Any(other => Contains(other, rect.Center))).Distinct().ToArray();
            if (rectangles.Length < anchors.Count || rectangles.Length > anchors.Count + 3) continue;
            strong[index] = strong[index] with { SourceBounds = rectangles.Aggregate((a, b) => a.Union(b)), TextBounds = rectangles,
                IsVertical = rectangles.Count(rect => rect.Height >= rect.Width * 1.7) * 2 >= rectangles.Length };
        }
        return strong;
    }

    private static IReadOnlyList<PixelRect> FindBubbles(CapturedBitmap source)
    {
        var scale = Math.Min(1, 1280.0 / Math.Max(source.Width, source.Height));
        using var bitmap = LocalTextDetector.ToSkia(source);
        using var resized = bitmap.Resize(new SkiaSharp.SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)), SkiaSharp.SKSamplingOptions.Default);
        using var input = Mat.FromPixelData(resized.Height, resized.Width, MatType.CV_8UC4, resized.GetPixels(), resized.RowBytes);
        using var gray = new Mat(); Cv2.CvtColor(input, gray, ColorConversionCodes.BGRA2GRAY);
        var result = new List<PixelRect>();
        foreach (var dark in new[] { false, true })
        {
            using var mask = new Mat(); using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
            Cv2.Threshold(gray, mask, dark ? 45 : 220, 255, dark ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
            for (var index = 1; index < count; index++)
            {
                var x = stats.At<int>(index, 0); var y = stats.At<int>(index, 1);
                var w = stats.At<int>(index, 2); var h = stats.At<int>(index, 3); var area = stats.At<int>(index, 4);
                if (w < 16 || h < 24 || area < 500 || area > gray.Width * gray.Height * .25 || area < (long)w * h * .35) continue;
                if (x < 2 || y < 2 || x + w >= gray.Width - 2 || y + h >= gray.Height - 2) continue;
                result.Add(new((int)(x / scale), (int)(y / scale), (int)(w / scale), (int)(h / scale)));
            }
        }
        return result;
    }
    private static bool Contains(PixelRect rectangle, PixelPoint point) => point.X >= rectangle.X && point.X < rectangle.Right && point.Y >= rectangle.Y && point.Y < rectangle.Bottom;
}
