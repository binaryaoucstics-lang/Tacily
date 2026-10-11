using System.Runtime.InteropServices;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Capture;

public static class TextAtlasBuilder
{
    public static TextAtlas Build(CapturedBitmap source, IReadOnlyList<TextRegion> regions,
        TextReadingDirection direction = TextReadingDirection.Auto, string idPrefix = "p")
    {
        var panels = ComicPanelDetector.Detect(source);
        var groups = ComicReadingOrder.Sort(ComicPanelDetector.IncludeVerticalDashes(source,
            TextRegionGrouper.GroupDetailed(ComicPanelDetector.RemovePanelBorders(regions, panels), source.Size, direction)), panels);
        return BuildGroups(source,groups,idPrefix);
    }
    public static TextAtlas BuildGroups(CapturedBitmap source,IReadOnlyList<TextRegionGroup> groups,string idPrefix="p",bool paragraphs=false)
    {
        if (groups.Count == 0) throw new InvalidOperationException("没有检测到文字区域，请拖框选择文字或调整检测方式。");
        const int padding = 10, labelHeight = 24;
        var area = groups.Sum(group => (long)(group.CropBounds.Width + padding) * (group.CropBounds.Height + labelHeight + padding));
        var width = Math.Max(groups.Max(group => group.CropBounds.Width) + padding * 2, (int)Math.Ceiling(Math.Sqrt(area)));
        var x = padding; var y = padding; var rowHeight = 0;
        var mapping = new List<ImageAtlasRegion>();
        foreach (var group in groups)
        {
            var bounds = group.CropBounds;
            if (x > padding && x + bounds.Width + padding > width) { x = padding; y += rowHeight + padding; rowHeight = 0; }
            mapping.Add(new ImageAtlasRegion($"{idPrefix}{mapping.Count + 1}", bounds, new PixelRect(x, y + labelHeight, bounds.Width, bounds.Height))
            { TextBounds = group.TextBounds, IsVertical = group.IsVertical, IsParagraph=paragraphs });
            rowHeight = Math.Max(rowHeight, labelHeight + bounds.Height);
            x += bounds.Width + padding;
        }
        var height = y + rowHeight + padding;
        if (width > 8192 || height > 8192) throw new InvalidOperationException("文字区域过多，请分两次框选翻译。");
        using var input = LocalTextDetector.ToSkia(source);
        using var atlas = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(atlas))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var font = new SKFont(SKTypeface.Default, 15))
        {
            canvas.Clear(SKColors.White);
            foreach (var region in mapping)
            {
                canvas.DrawText($"[{region.Id}]", region.AtlasBounds.X, region.AtlasBounds.Y - 6, font, paint);
                canvas.DrawBitmap(input, new SKRect(region.SourceBounds.X, region.SourceBounds.Y, region.SourceBounds.Right, region.SourceBounds.Bottom),
                    new SKRect(region.AtlasBounds.X, region.AtlasBounds.Y, region.AtlasBounds.Right, region.AtlasBounds.Bottom));
            }
        }
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++) Marshal.Copy(atlas.GetPixels() + row * atlas.RowBytes, pixels, row * width * 4, width * 4);
        return new TextAtlas(new CapturedBitmap(width, height, width * 4, pixels), mapping);
    }
}
