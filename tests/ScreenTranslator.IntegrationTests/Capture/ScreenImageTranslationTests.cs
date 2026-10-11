using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using ScreenTranslator.IntegrationTests.TestInfrastructure;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class ScreenImageTranslationTests
{
    [Fact]
    public async Task Png_Keeps_Screenshot_Colors_When_Gdi_Alpha_Is_Zero()
    {
        await StaTest.RunAsync(() =>
        {
            var bitmap = new CapturedBitmap(2, 1, 8, new byte[] { 255, 255, 255, 0, 3, 2, 1, 0 });
            var png = CapturedBitmapEncoder.ToPng(bitmap);
            var decoded = new FormatConvertedBitmap(CapturedBitmapEncoder.Preview(png), PixelFormats.Bgra32, null, 0);
            var pixels = new byte[8]; decoded.CopyPixels(pixels, 8, 0);
            Assert.Equal(new byte[] { 255, 255, 255, 255, 3, 2, 1, 255 }, pixels);
        });
    }

    public static IEnumerable<object[]> Samples() =>
        from dpi in new[] { 96, 120, 144, 192 }
        from font in new[] { 14, 18, 24, 32 }
        from dark in new[] { false, true }
        select new object[] { dpi, font, dark };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Real_Ocr_Selects_Paragraph_And_Cropped_Png_Excludes_Unrelated_Text(int dpi, int font, bool dark)
    {
        CapturedBitmap? bitmap = null;
        BitmapSource? preview = null;
        await StaTest.RunAsync(() =>
        {
            var scale = dpi / 96d;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var background = dark ? Brushes.Black : Brushes.White;
                var foreground = dark ? Brushes.White : Brushes.Black;
                drawing.DrawRectangle(background, null, new System.Windows.Rect(0, 0, 900, 460));
                Draw("SCREEN TEST", 24, 20, font * 1.65);
                Draw("Click this paragraph for translation.", 24, 100, font);
                Draw("It should include each wrapped line.", 24, 100 + font * 1.5, font);
                Draw("And stop before the next paragraph.", 24, 100 + font * 3, font);
                Draw("UNRELATED PARAGRAPH", 24, 100 + font * 5.5 + 36, font);
                void Draw(string text, int x, double y, double size) => drawing.DrawText(new FormattedText(text,
                    CultureInfo.GetCultureInfo("en-US"), System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), size, foreground, scale), new System.Windows.Point(x, y));
            }
            var rendered = new RenderTargetBitmap((int)(900 * scale), (int)(460 * scale), dpi, dpi, PixelFormats.Pbgra32);
            rendered.Render(visual); rendered.Freeze();
            var pixels = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
            rendered.CopyPixels(pixels, rendered.PixelWidth * 4, 0);
            bitmap = new CapturedBitmap(rendered.PixelWidth, rendered.PixelHeight, rendered.PixelWidth * 4, pixels);
            preview = rendered;
        });
        var scaling = dpi / 96d;
        var monitor = new ScreenMonitor(0, "test", new PixelRect(-1800, 0, bitmap!.Width, bitmap.Height),
            new PixelRect(-1800, 0, bitmap.Width, bitmap.Height), (uint)dpi, (uint)dpi, false);
        var point = new PixelPoint(-1800 + (int)(85 * scaling), (int)((100 + font * 1.5 + font * 0.6) * scaling));
        var selected = await new SmartSelectionResolver(new WindowsOcrEngine()).FromImageAsync(
            new MonitorCapture(monitor, bitmap, preview!), point, "en-US", 42, CancellationToken.None);
        Assert.NotNull(selected);
        var relative = selected.Bounds.Translate(new PixelPoint(1800, 0));
        var crop = CapturedBitmapCropper.Crop(bitmap, relative);
        Assert.True(crop.Height < bitmap.Height / 2);
        var lines = await new WindowsOcrEngine().RecognizeAsync(crop, "en-US", CancellationToken.None);
        var text = string.Join(" ", lines.Select(line => line.Text));
        Assert.Contains("translation", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wrapped line", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next paragraph", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UNRELATED", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SCREEN TEST", text, StringComparison.OrdinalIgnoreCase);
        var png = CapturedBitmapEncoder.ToPng(crop);
        Assert.True(png.Length > 100);
    }
}
