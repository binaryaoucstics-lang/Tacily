using System.Text;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class ImageTextPositionCalibrator
{
    public static IReadOnlyList<TranslatedBlock> Calibrate(IReadOnlyList<TranslatedBlock> segments, IReadOnlyList<OcrBlock> lines, PixelSize size)
    {
        if (lines.Count == 0) return segments;
        return segments.Select(segment =>
        {
            var source = Normalize(segment.SourceText);
            var vicinity = new PixelRect(segment.Bounds.X - 48, segment.Bounds.Y - 48, segment.Bounds.Width + 96, segment.Bounds.Height + 96);
            var matches = lines.Where(line =>
            {
                var text = Normalize(line.Text);
                return text.Length >= 3 && source.Contains(text, StringComparison.Ordinal) && !line.BoundsInCapturePixels.Intersect(vicinity).IsEmpty;
            }).ToArray();
            if (matches.Length == 0 || matches.Sum(line => Normalize(line.Text).Length) < source.Length * 0.8) return segment;
            var union = matches.Select(line => line.BoundsInCapturePixels).Aggregate((left, right) => left.Union(right));
            var calibrated = new PixelRect(union.X - 4, union.Y - 4, union.Width + 8, union.Height + 8)
                .Intersect(new PixelRect(0, 0, size.Width, size.Height));
            return segment with { Bounds = calibrated };
        }).ToArray();
    }

    private static string Normalize(string value)
    {
        var normalized = new StringBuilder();
        foreach (var character in value)
            if (char.IsLetterOrDigit(character)) normalized.Append(char.ToLowerInvariant(character));
        return normalized.ToString();
    }
}
