using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using ScreenTranslator.IntegrationTests.TestInfrastructure;

namespace ScreenTranslator.IntegrationTests.Ocr;

public sealed class LocalTextDetectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Local_Model_Finds_Text_Without_Reading_Characters_And_Atlas_Removes_Empty_Space(bool dark)
    {
        CapturedBitmap bitmap = null!;
        await StaTest.RunAsync(() =>
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, new System.Windows.Rect(0, 0, 1000, 1800));
                foreach (var (text, y) in new[] { ("IMAGE TITLE", 50), ("中文文字检测 English text", 420), ("Far below: translate this paragraph.", 1600) })
                    drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), System.Windows.FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), 30, dark ? Brushes.White : Brushes.Black, 1), new System.Windows.Point(44, y));
            }
            var rendered = new RenderTargetBitmap(1000, 1800, 96, 96, PixelFormats.Pbgra32); rendered.Render(visual);
            var pixels = new byte[1000 * 1800 * 4]; rendered.CopyPixels(pixels, 4000, 0);
            bitmap = new CapturedBitmap(1000, 1800, 4000, pixels);
        });
        var modelPath = FindModel();
        using var detector = new LocalTextDetector(modelPath);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var regions = await detector.DetectAsync(bitmap, limit.Token);
        Assert.True(regions.Count >= 3);
        foreach (var y in new[] { 50, 420, 1600 })
            Assert.Contains(regions, region => region.Bounds.Y <= y + 30 && region.Bounds.Bottom >= y + 20);
        var atlas = TextAtlasBuilder.Build(bitmap, regions);
        Assert.True((long)atlas.Bitmap.Width * atlas.Bitmap.Height < (long)bitmap.Width * bitmap.Height * 0.4);
        foreach (var region in atlas.Regions)
        {
            var src = bitmap.Pixels.Slice(region.SourceBounds.Y * bitmap.Stride + region.SourceBounds.X * 4, region.SourceBounds.Width * 4).ToArray();
            var crop = atlas.Bitmap.Pixels.Slice(region.AtlasBounds.Y * atlas.Bitmap.Stride + region.AtlasBounds.X * 4, region.AtlasBounds.Width * 4).ToArray();
            Assert.Equal(src, crop);
        }
    }

    private static string FindModel()
    {
        var path = LocalTextDetector.DefaultModelPath;
        if (System.IO.File.Exists(path)) return path;
        throw new System.IO.FileNotFoundException("Test model unavailable.");
    }
}
