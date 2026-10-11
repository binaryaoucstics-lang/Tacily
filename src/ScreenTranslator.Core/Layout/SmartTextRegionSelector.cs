using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public sealed record SmartTextRegion(PixelRect Bounds, string Text);

/// <summary>Groups OCR lines around a point without crossing paragraph breaks or columns.</summary>
public static class SmartTextRegionSelector
{
    public static SmartTextRegion? Select(IReadOnlyList<OcrBlock> blocks, PixelPoint point, PixelSize imageSize)
    {
        var usable = blocks.Where(b => !b.BoundsInCapturePixels.IsEmpty && !string.IsNullOrWhiteSpace(b.Text)).ToArray();
        if (usable.Length == 0) return null;
        var seed = usable.OrderBy(b => DistanceSquared(b.BoundsInCapturePixels, point)).First();
        var seedBounds = seed.BoundsInCapturePixels;
        var tolerance = Math.Max(14, seedBounds.Height * 0.8);
        if (DistanceSquared(seedBounds, point) > tolerance * tolerance) return null;

        var chosen = new List<OcrBlock> { seed };
        AddLines(-1);
        AddLines(1);
        var bounds = chosen.Select(b => b.BoundsInCapturePixels).Aggregate((a, b) => a.Union(b));
        const int padding = 8;
        bounds = new PixelRect(bounds.X - padding, bounds.Y - padding, bounds.Width + padding * 2, bounds.Height + padding * 2)
            .Intersect(new PixelRect(0, 0, imageSize.Width, imageSize.Height));
        return new SmartTextRegion(bounds, string.Join("\n", chosen.OrderBy(b => b.BoundsInCapturePixels.Y).Select(b => b.Text)));

        void AddLines(int direction)
        {
            var current = seed;
            for (var i = 0; i < 24; i++)
            {
                var rect = current.BoundsInCapturePixels;
                var next = usable.Where(b => !chosen.Contains(b))
                    .Where(b => direction < 0 ? b.BoundsInCapturePixels.Y < rect.Y - rect.Height * 0.5 : b.BoundsInCapturePixels.Y > rect.Y + rect.Height * 0.5)
                    .Where(b => SameColumn(seedBounds, b.BoundsInCapturePixels))
                    .OrderBy(b => Math.Abs(b.BoundsInCapturePixels.Y - rect.Y)).FirstOrDefault();
                if (next is null) break;
                var candidate = next.BoundsInCapturePixels;
                var gap = direction < 0 ? rect.Y - candidate.Bottom : candidate.Y - rect.Bottom;
                var heightRatio = (double)candidate.Height / Math.Max(1, seedBounds.Height);
                if (gap < -3 || gap > Math.Max(8, seedBounds.Height * 0.75) || heightRatio < 0.65 || heightRatio > 1.45) break;
                chosen.Add(next);
                current = next;
            }
        }
    }

    private static bool SameColumn(PixelRect seed, PixelRect other)
    {
        var overlap = Math.Max(0, Math.Min(seed.Right, other.Right) - Math.Max(seed.X, other.X));
        return Math.Abs(seed.X - other.X) <= Math.Max(20, seed.Height * 1.5)
            && overlap >= Math.Min(seed.Width, other.Width) * 0.45;
    }

    private static double DistanceSquared(PixelRect rect, PixelPoint point)
    {
        var dx = Math.Max(0, Math.Max(rect.X - point.X, point.X - rect.Right));
        var dy = Math.Max(0, Math.Max(rect.Y - point.Y, point.Y - rect.Bottom));
        return (double)dx * dx + (double)dy * dy;
    }
}
