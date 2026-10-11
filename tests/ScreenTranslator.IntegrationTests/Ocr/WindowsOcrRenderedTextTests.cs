using System.Globalization;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Ocr;

public sealed class WindowsOcrRenderedTextTests
{
    [Fact]
    [Trait("Category", "OcrEnvironment")]
    public async Task RecognizesRenderedEnglishText()
    {
        const int width = 640;
        const int height = 160;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                System.Windows.Media.Brushes.White,
                null,
                new System.Windows.Rect(0, 0, width, height));
            drawing.DrawText(
                new System.Windows.Media.FormattedText(
                    "Hello screen",
                    CultureInfo.GetCultureInfo("en-US"),
                    System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI"),
                    72,
                    System.Windows.Media.Brushes.Black,
                    1),
                new System.Windows.Point(20, 20));
        }

        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        rendered.CopyPixels(pixels, stride, 0);

        var result = await new WindowsOcrEngine().RecognizeAsync(
            new CapturedBitmap(width, height, stride, pixels),
            "en-US",
            default);

        Assert.Contains(result, block => block.Text.Contains("Hello"));
    }
}
