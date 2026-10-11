using System.IO;
using SkiaSharp;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Translation;

public sealed class InPlaceRendererTests
{
    [Fact]
    public void RemovesLettersPreservesCaptionBorderAndNeverMutatesOriginal()
    {
        using var bitmap = new SKBitmap(400, 180, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false })
        using (var font = new SKFont(SKTypeface.Default, 30))
        {
            canvas.Clear(SKColors.White);
            paint.IsStroke = true; paint.StrokeWidth = 2;
            canvas.DrawRect(new SKRect(18, 25, 380, 145), paint);
            paint.IsStroke = false; canvas.DrawText("HELLO WORLD", 40, 95, font, paint);
        }
        var source = Capture(bitmap); var untouched = source.Pixels.ToArray();
        var block = new TranslatedBlock("p1", "HELLO WORLD", "你好，世界", new(25, 55, 320, 60));
        var renderer = new InPlaceImageRenderer(source);
        var patch = renderer.Prepare(block);
        Assert.NotNull(patch);
        Assert.True(patch.ErasedPixels > 100);
        Assert.True(DarkPixels(patch.Background) < DarkPixels(source, patch.Bounds) * .15);
        var rendered = renderer.Render(patch, block);
        Assert.True(DarkPixels(rendered) > 10);
        Assert.Equal(patch.Background.Size, rendered.Size);
        Assert.Equal(untouched, source.Pixels.ToArray());
        Assert.Equal(0, source.Pixels.Span[25 * source.Stride + 30 * 4]);
    }

    [Fact]
    public void BlankAndUnlocatedBlocksNeverProduceAnErasePatch()
    {
        var pixels = Enumerable.Repeat((byte)255, 100 * 100 * 4).ToArray();
        var renderer = new InPlaceImageRenderer(new(100, 100, 400, pixels));
        Assert.Null(renderer.Prepare(new("p1", "Hello", "你好", default)));
        Assert.Null(renderer.Prepare(new("p1", "Hello", "你好", new(10, 10, 70, 40))));
    }

    [Fact]
    public void InPlaceStreamKeepsPopupCollapsedAndRetainsVerticalPartialState()
    {
        var view = new TranslationResultViewModel { IsBusy = true, IsInPlaceEnabled = true };
        view.ApplyStreamUpdate(new("你") { ImageSegments = [new("p1", "こんにちは", "你", new(20, 30, 40, 180))
            { IsVertical = true, IsComplete = false, TextBounds = [new(20, 30, 40, 180)] }], StreamingTail = "" });
        Assert.False(view.IsExpanded);
        Assert.True(view.Segments[0].IsVertical);
        Assert.False(view.Segments[0].IsComplete);
        view.ToggleExpandedCommand.Execute(null);
        Assert.True(view.IsExpanded);
        view.ToggleInPlaceCommand.Execute(null);
        Assert.False(view.IsInPlaceEnabled);
    }

    [Fact]
    public void SameVerticalSourceUsesUnrotatedGlyphStrip()
    {
        using var source = new SKBitmap(48, 180, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.Black })
        { canvas.Clear(SKColors.White); for (var index = 0; index < 5; index++) canvas.DrawRect(new SKRect(16, 12 + index * 30, 32, 30 + index * 30), paint); }
        using var strip = VerticalTextStrip.Build(source);
        Assert.True(strip.Width > strip.Height);
        Assert.True(strip.Height < source.Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BubbleCompletionExcludesOtherParagraphsEvenWhenTheyAreOnlyPartlyLocated(bool neighborIncomplete)
    {
        using var bitmap = new SKBitmap(600, 400, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White })
        { canvas.Clear(SKColors.Black); canvas.DrawRect(new SKRect(60, 60, 340, 240), paint); }
        var neighbor = new PixelRect(230, 90, 70, 20);
        var missing = new PixelRect(90, 150, 80, 20);
        var lines = new OcrBlock[] {
            new("a", "FIRST", 1, new(90, 90, 80, 20), 0), new("b", "SECOND", 1, new(90, 120, 80, 20), 1),
            new("c", "JUNK", .6, missing, 2), new("d", "NEIGHBOR", 1, neighbor, 3) };
        var regions = new BubbleTextGrounder(Capture(bitmap)).Match([
            new("p1", "FIRSTSECONDMISSING", 1, default, 0),
            new("p2", neighborIncomplete ? "NEIGHBOROTHER" : "NEIGHBOR", 1, default, 1)], lines);
        Assert.Equal(3, regions[0].TextBounds.Count);
        Assert.Contains(missing, regions[0].TextBounds);
        Assert.DoesNotContain(neighbor, regions[0].TextBounds);
    }

    [Fact]
    public void LateGeometryPreservesCompletedTypesettingStateAndText()
    {
        var view = new TranslationResultViewModel();
        view.SetSegments([new("p1", "シャワー", "洗澡", default) { IsComplete = true }]);
        view.ApplyStreamUpdate(new("洗") { IsLayoutUpdate = true, ImageSegments = [new("p1", "シャワー", "洗", new(10, 20, 40, 160))
            { IsComplete = false, IsVertical = true, TextBounds = [new(10, 20, 40, 160)] }] });
        Assert.True(view.Segments[0].IsComplete);
        Assert.True(view.Segments[0].IsVertical);
        Assert.Equal("洗澡", view.Segments[0].Translation);
        Assert.False(view.IsExpanded);
    }

    [Fact]
    public void SourceFreePlacementUsesRoomyAreaAndHorizontalChineseEvenWhenOriginalWasVertical()
    {
        using var bitmap = new SKBitmap(420, 280, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
        { canvas.Clear(SKColors.White); for (var index = 0; index < 4; index++) canvas.DrawRect(new SKRect(194, 70 + index * 36, 218, 94 + index * 36), paint); }
        var block = new TranslatedBlock("p1", "", "谁知道呢？我也不太了解这种事情。", new(190, 65, 32, 144))
            { HasVisionPlacement = true, LayoutBounds = new(90, 50, 240, 180), TextBounds = [new(190, 65, 32, 144)] };
        var renderer = new InPlaceImageRenderer(Capture(bitmap));
        var patch = renderer.Prepare(block);
        Assert.NotNull(patch);
        Assert.False(patch.IsVertical);
        Assert.Equal(block.LayoutBounds, patch.LayoutBounds);
        Assert.True(patch.Bounds.Width >= 240);
        var rendered = renderer.Render(patch, block);
        var changed = 0;
        for (var y = 0; y < rendered.Height; y++)
            for (var x = 0; x < rendered.Width; x++)
            {
                var offset = y * rendered.Stride + x * 4;
                if (rendered.Pixels.Span[offset] == patch.Background.Pixels.Span[offset]) continue;
                changed++;
                Assert.InRange(x + patch.Bounds.X, block.LayoutBounds.X, block.LayoutBounds.Right - 1);
                Assert.InRange(y + patch.Bounds.Y, block.LayoutBounds.Y, block.LayoutBounds.Bottom - 1);
            }
        Assert.True(changed > 100);
        Assert.True(DarkPixels(patch.Background) < 10);
    }

    [Fact]
    public void LocalBubbleShapeKeepsTheLayoutCornersInsideItsCurvedInterior()
    {
        using var bitmap = new SKBitmap(380, 300, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White })
        { canvas.Clear(SKColors.Black); canvas.DrawOval(new SKRect(40, 30, 340, 270), paint); }
        var hint = new PixelRect(40, 30, 300, 240);
        var fit = new VisionPlacementRefiner(Capture(bitmap)).FitInnerRectangle(hint);
        Assert.True((long)fit.Width * fit.Height < (long)hint.Width * hint.Height);
        foreach (var (x, y) in new[] { (fit.X, fit.Y), (fit.Right - 1, fit.Y), (fit.X, fit.Bottom - 1), (fit.Right - 1, fit.Bottom - 1) })
            Assert.True(bitmap.GetPixel(x, y).Red > 200);
    }

    [Fact]
    public void DuplicateVisionBoxesRemainReadableInPopupAndDoNotEraseSharedArtwork()
    {
        var source = new CapturedBitmap(300, 300, 1200, Enumerable.Repeat((byte)255, 300 * 300 * 4).ToArray());
        var bounds = new PixelRect(40, 40, 100, 180);
        var blocks = new TranslatedBlock[] {
            new("p1", "", "第一句", bounds) { HasVisionPlacement = true, LayoutBounds = bounds },
            new("p2", "", "第二句", bounds) { HasVisionPlacement = true, LayoutBounds = bounds } };
        var refined = new VisionPlacementRefiner(source).Refine(blocks, []);
        Assert.All(refined, block => { Assert.False(block.Bounds.IsUsable); Assert.NotEmpty(block.Translation); });
    }

    [Fact]
    public void LateSourceFreeLayoutKeepsFullTranslationAndPlacementFlags()
    {
        var view = new TranslationResultViewModel();
        view.SetSegments([new("p1", "", "完整译文", new(10, 20, 80, 100)) { HasVisionPlacement = true, IsComplete = true }]);
        view.ApplyStreamUpdate(new("完") { IsLayoutUpdate = true, ImageSegments = [new("p1", "", "完", new(12, 22, 76, 96))
            { HasVisionPlacement = true, LayoutBounds = new(8, 18, 100, 120), IsComplete = false }] });
        Assert.Equal("完整译文", view.Segments[0].Translation);
        Assert.True(view.Segments[0].IsComplete);
        Assert.True(view.Segments[0].HasVisionPlacement);
        Assert.Equal(new PixelRect(8, 18, 100, 120), view.Segments[0].LayoutBounds);
    }

    [Fact]
    public void GreyLettersTouchingBlackBorderCanBeRemovedWithoutErasingItsCore()
    {
        using var bitmap = new SKBitmap(400, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false })
        using (var font = new SKFont(SKTypeface.Default, 32))
        {
            canvas.Clear(SKColors.White); paint.IsStroke = true; paint.StrokeWidth = 2;
            canvas.DrawRect(new SKRect(40, 40, 360, 210), paint);
            paint.IsStroke = false; paint.Color = new SKColor(110, 110, 110);
            canvas.DrawText("HELLO WORLD", 80, 57, font, paint);
        }
        var source = Capture(bitmap);
        var renderer = new InPlaceImageRenderer(source);
        var block = new TranslatedBlock("p1", "", "你好世界", new(72, 24, 256, 48))
            { HasVisionPlacement = true, LayoutBounds = new(60, 78, 280, 100) };
        var patch = renderer.Prepare(block);
        Assert.NotNull(patch);
        var grey = 0;
        using var repaired=LocalTextDetector.ToSkia(source);
        using(var canvas=new SKCanvas(repaired))using(var tile=LocalTextDetector.ToSkia(patch.Background,patch.Transparent))canvas.DrawBitmap(tile,patch.Bounds.X,patch.Bounds.Y);
        var composite=Capture(repaired);
        for (var y=patch.Bounds.Y;y<patch.Bounds.Bottom;y++)for(var x=patch.Bounds.X;x<patch.Bounds.Right;x++)
            if (composite.Pixels.Span[y*composite.Stride+x*4] is > 85 and < 135) grey++;
        Assert.True(grey < 20);
        Assert.True(DarkPixels(composite,patch.Bounds) >= DarkPixels(source, patch.Bounds) * .95);
    }

    private static CapturedBitmap Capture(SKBitmap bitmap)
    {
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.Width * 4, pixels);
    }
    private static int DarkPixels(CapturedBitmap bitmap, PixelRect? area = null)
    {
        var region = area ?? new PixelRect(0, 0, bitmap.Width, bitmap.Height);
        var count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
            for (var x = region.X; x < region.Right; x++) if (bitmap.Pixels.Span[y * bitmap.Stride + x * 4+3]>128 && bitmap.Pixels.Span[y * bitmap.Stride + x * 4] < 100) count++;
        return count;
    }
}
