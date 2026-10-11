using OpenCvSharp;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Overlays;

/// <summary>Vision assigns dialogues; local pixels only trim placement and estimate the original glyph size.</summary>
public sealed class VisionPlacementRefiner(CapturedBitmap source,CapturedBitmap? segmentation=null)
{
    private readonly Dictionary<string, (string Key, TranslatedBlock Block)> _geometry = [];

    public IReadOnlyList<TranslatedBlock> Refine(IReadOnlyList<TranslatedBlock> blocks, IReadOnlyList<OcrBlock> ink)
    {
        foreach (var missing in _geometry.Keys.Except(blocks.Select(block => block.Id)).ToArray()) _geometry.Remove(missing);
        return blocks.Select(block =>
        {
            if (!block.HasVisionPlacement || !block.Bounds.IsUsable) return block;
            var original = block.Bounds;
            var margin = Math.Clamp(Math.Min(original.Width, original.Height) / 10, 4, 20);
            var vicinity = new PixelRect(original.X - margin, original.Y - margin, original.Width + margin * 2, original.Height + margin * 2);
            var peers = blocks.Where(other => other.Id != block.Id && other.Bounds.IsUsable).ToArray();
            var lines = ink.Where(line => Contains(vicinity, line.BoundsInCapturePixels.Center)
                && !peers.Any(other => Contains(other.Bounds, line.BoundsInCapturePixels.Center)))
                .Select(line => Tighten(line.BoundsInCapturePixels)).Where(rectangle => rectangle.IsUsable).Distinct().ToArray();
            var key = $"{block.Bounds}:{block.LayoutBounds}:{block.IsVertical}:{string.Join(',', block.SourceIds)}:" + string.Join(';', lines)
                + string.Join(';', peers.Select(other => other.Bounds));
            if (!_geometry.TryGetValue(block.Id, out var cached) || cached.Key != key)
            {
                TranslatedBlock geometry;
                if (!block.PreciseWords && peers.Any(other => Overlap(block.Bounds, other.Bounds) > .2))
                    geometry = block with { Bounds = default, TextBounds = [], LayoutBounds = default };
                else
                {
                    if(segmentation is not null && lines.Length>0) original=lines.Aggregate(original,(a,b)=>a.Union(b));
                    var glyphs=segmentation is null?[]:ComicInkExtractor.Glyphs(source,segmentation,original,block.PreciseWords?block.TextBounds:lines);
                    var text=glyphs.Count>0?glyphs:block.TextBounds;
                    if(glyphs.Count>0) original=glyphs.Aggregate((a,b)=>a.Union(b));
                    var layout = block.PreciseWords&&segmentation is null?original:block.SourceIds.Count > 0 ? FindRoomyLayout(original, text)
                        : block.LayoutBounds.IsUsable ? block.LayoutBounds : original;
                    if (peers.Any(other => Overlap(layout, other.Bounds) > .08)) layout = original;
                    if ((long)layout.Width*layout.Height < (long)original.Width*original.Height*.65) layout=original;
                    if (block.SourceIds.Count == 0) layout = FitInnerRectangle(layout);
                    var vertical = block.SourceIds.Count > 0 ? layout.Height > layout.Width * .55 && block.IsVertical : block.IsVertical;
                    geometry = block with {Bounds=original, TextBounds = block.SourceIds.Count > 0 ? text : lines.Length > 0 ? lines : [original],
                        LayoutBounds = layout, IsVertical = vertical };
                }
                cached = (key, geometry); _geometry[block.Id] = cached;
            }
            return block with { Bounds = cached.Block.Bounds, TextBounds = cached.Block.TextBounds, LayoutBounds = cached.Block.LayoutBounds,
                IsVertical = cached.Block.IsVertical };
        }).ToArray();
    }

    private PixelRect FindRoomyLayout(PixelRect original, IReadOnlyList<PixelRect> text)
    {
        var sizes = text.Select(rect => Math.Min(rect.Width, rect.Height)).Order().ToArray();
        var glyph = sizes.Length == 0 ? 24 : sizes[sizes.Length / 2];
        var margin = Math.Clamp(glyph * 3, 32, 180);
        return FitInnerRectangle(new(original.X - margin, original.Y - margin, original.Width + margin * 2, original.Height + margin * 2), original);
    }

    public PixelRect FitInnerRectangle(PixelRect hint, PixelRect? anchor = null)
    {
        hint = hint.Intersect(new(0, 0, source.Width, source.Height));
        if (!hint.IsUsable || (long)hint.Width * hint.Height > 3_000_000) return hint;
        using var bitmap = new SkiaSharp.SKBitmap(hint.Width, hint.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque);
        var bytes = new byte[hint.Width * 4];
        for (var y = 0; y < hint.Height; y++)
        {
            source.Pixels.Span.Slice((hint.Y + y) * source.Stride + hint.X * 4, bytes.Length).CopyTo(bytes);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels() + y * bitmap.RowBytes, bytes.Length);
        }
        using var bgra = Mat.FromPixelData(hint.Height, hint.Width, MatType.CV_8UC4, bitmap.GetPixels(), bitmap.RowBytes);
        using var gray = new Mat(); using var mask = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        if(segmentation is not null && anchor is {} ink)
        {
            var a=ink.Intersect(hint); var samples=new List<byte>();
            for(var y=a.Y;y<a.Bottom;y+=2) for(var x=a.X;x<a.Right;x+=2) samples.Add(gray.At<byte>(y-hint.Y,x-hint.X));
            var background=samples.Count==0?(byte)255:samples.Order().ElementAt(samples.Count/2);
            for(var y=a.Y;y<a.Bottom;y++) for(var x=a.X;x<a.Right;x++)
                if(segmentation.Pixels.Span[y*segmentation.Stride+x*4]>=45) gray.Set(y-hint.Y,x-hint.X,background);
        }
        Cv2.Threshold(gray, mask, 210, 255, ThresholdTypes.Binary);
        if (anchor is null && Cv2.CountNonZero(mask) < hint.Width * hint.Height * .55)
        {
            Cv2.Threshold(gray, mask, 55, 255, ThresholdTypes.BinaryInv);
            if (Cv2.CountNonZero(mask) < hint.Width * hint.Height * .55) return hint;
        }
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var center = anchor is { } point ? new Point2f(point.Center.X - hint.X, point.Center.Y - hint.Y) : new Point2f(hint.Width / 2f, hint.Height / 2f);
        var minimumArea = anchor is { } original ? Math.Max(100, (long)original.Width * original.Height * .2) : hint.Width * hint.Height * .25;
        var shape = contours.Where(contour => Cv2.ContourArea(contour) >= minimumArea
            && Cv2.PointPolygonTest(contour, center, false) >= 0).OrderByDescending(contour => Cv2.ContourArea(contour)).FirstOrDefault();
        if (shape is null && anchor is not null)
        {
            Cv2.Threshold(gray, mask, 55, 255, ThresholdTypes.BinaryInv);
            Cv2.FindContours(mask, out contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            shape = contours.Where(contour => Cv2.ContourArea(contour) >= minimumArea
                && Cv2.PointPolygonTest(contour, center, false) >= 0).OrderByDescending(contour => Cv2.ContourArea(contour)).FirstOrDefault();
        }
        if (shape is null) return anchor ?? hint;
        var shapeBounds = Cv2.BoundingRect(shape);
        if (anchor is { } plain && shapeBounds.X == 0 && shapeBounds.Y == 0 && shapeBounds.Right >= hint.Width && shapeBounds.Bottom >= hint.Height)
            return plain;
        using var filled = Mat.Zeros(hint.Height, hint.Width, MatType.CV_8UC1).ToMat();
        Cv2.DrawContours(filled, [shape], 0, Scalar.White, -1);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
        Cv2.Erode(filled, filled, kernel);
        var best = LargestRectangle(filled);
        var fitted=best.IsUsable ? best.Translate(new(hint.X, hint.Y)) : anchor ?? hint;
        if(anchor is {} guard && (Math.Abs(fitted.Center.X-guard.Center.X)>Math.Max(10,guard.Width*.3)
            || Math.Abs(fitted.Center.Y-guard.Center.Y)>Math.Max(10,guard.Height*.3))) return guard;
        return fitted;
    }

    private static PixelRect LargestRectangle(Mat mask)
    {
        var pixels = new byte[mask.Width * mask.Height];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
        var heights = new int[mask.Width + 1]; var stack = new int[mask.Width + 1]; var best = default(PixelRect);
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++) heights[x] = pixels[y * mask.Width + x] == 0 ? 0 : heights[x] + 1;
            var top = -1;
            for (var x = 0; x <= mask.Width; x++)
            {
                while (top >= 0 && heights[stack[top]] > heights[x])
                {
                    var height = heights[stack[top--]];
                    var left = top < 0 ? 0 : stack[top] + 1;
                    if ((long)(x - left) * height > (long)best.Width * best.Height) best = new(left, y + 1 - height, x - left, height);
                }
                stack[++top] = x;
            }
        }
        return best;
    }

    private static PixelRect Tighten(PixelRect rect) => rect.Height >= rect.Width * 1.7
        ? new(rect.X + rect.Width / 5, rect.Y, rect.Width - 2 * (rect.Width / 5), rect.Height)
        : new(rect.X, rect.Y + rect.Height / 5, rect.Width, rect.Height - 2 * (rect.Height / 5));
    private static bool Contains(PixelRect rect, PixelPoint point) => point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom;
    private static double Overlap(PixelRect left, PixelRect right)
    {
        var overlap = left.Intersect(right);
        return (long)overlap.Width * overlap.Height / (double)Math.Max(1, Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height));
    }
}
