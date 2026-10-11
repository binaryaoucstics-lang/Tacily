using System.Runtime.InteropServices;
using OpenCvSharp;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public static class ComicPanelDetector
{
    public static IReadOnlyList<PixelRect> Detect(CapturedBitmap bitmap)
    {
        using var source = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC4);
        var pixels = bitmap.Pixels.ToArray();
        for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(pixels, y * bitmap.Stride, source.Ptr(y), bitmap.Width * 4);
        using var gray = new Mat(); using var binary = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Threshold(gray, binary, 140, 255, ThresholdTypes.BinaryInv);
        using var closing=Cv2.GetStructuringElement(MorphShapes.Rect,new OpenCvSharp.Size(3,3));
        Cv2.MorphologyEx(binary,binary,MorphTypes.Close,closing);
        Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var area = (long)bitmap.Width * bitmap.Height;
        var panels = contours.Select(contour => (Bounds: Cv2.BoundingRect(contour), Area: Math.Abs(Cv2.ContourArea(contour))))
            .Where(item => item.Area >= area * .025 && item.Area < area * .85
                && item.Bounds.Width >= bitmap.Width * .12 && item.Bounds.Height >= bitmap.Height * .10
                && item.Area >= (long)item.Bounds.Width * item.Bounds.Height * .65)
            .Select(item => new PixelRect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height)).ToArray();
        return panels.Length is >= 2 and <= 30 ? panels : [];
    }

    public static IReadOnlyList<TextRegion> RemovePanelBorders(IReadOnlyList<TextRegion> regions, IReadOnlyList<PixelRect> panels) =>
        regions.Where(region => !panels.Any(panel => region.Bounds.Height > region.Bounds.Width * 4
            && Math.Min(Math.Abs(region.Bounds.Center.X - panel.X), Math.Abs(region.Bounds.Center.X - panel.Right)) < Math.Max(8, region.Bounds.Width * .35)
            && region.Bounds.Y >= panel.Y && region.Bounds.Bottom <= panel.Bottom + 8)).ToArray();

    public static IReadOnlyList<TextRegionGroup> IncludeVerticalDashes(CapturedBitmap bitmap, IReadOnlyList<TextRegionGroup> groups)
    {
        if (!groups.Any(group => group.IsVertical)) return groups;
        using var source = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC4);
        var pixels = bitmap.Pixels.ToArray();
        for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(pixels, y * bitmap.Stride, source.Ptr(y), bitmap.Width * 4);
        using var gray = new Mat(); using var binary = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Threshold(gray, binary, 140, 255, ThresholdTypes.BinaryInv);
        Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var dashes = contours.Select(Cv2.BoundingRect).Where(rect => rect.Width <= 4 && rect.Height is >= 30 and <= 120)
            .Select(rect => new PixelRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
        var limits = new PixelRect(0, 0, bitmap.Width, bitmap.Height);
        return groups.Select(group =>
        {
            if (!group.IsVertical) return group;
            var matches = dashes.Where(dash => group.TextBounds.Any(text => dash.Height >= text.Width * .7
                && dash.X >= text.X + text.Width * .15 && dash.Right <= text.Right + text.Width * .1
                && dash.Y >= text.Y && dash.Bottom <= text.Bottom + text.Width * 1.5)).ToArray();
            if (matches.Length == 0) return group;
            var crop = matches.Select(dash => new PixelRect(dash.X - 6, dash.Y - 6, dash.Width + 12, dash.Height + 12))
                .Aggregate(group.CropBounds, (left, right) => left.Union(right)).Intersect(limits);
            return group with { CropBounds = crop, TextBounds = group.TextBounds.Concat(matches).ToArray(), PunctuationBounds = matches };
        }).ToArray();
    }
}
