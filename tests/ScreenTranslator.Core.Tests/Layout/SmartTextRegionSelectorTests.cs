using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class SmartTextRegionSelectorTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Select_Whole_Paragraph_Without_Next_Paragraph_Or_Adjacent_Column(double scale)
    {
        var lines = new[]
        {
            Line("a", "First line", 50, 80, 300, 20),
            Line("b", "Second line", 50, 105, 320, 20),
            Line("c", "Last line", 50, 130, 120, 20),
            Line("other-column", "Unrelated column", 500, 105, 300, 20),
            Line("next-paragraph", "Another paragraph", 50, 185, 300, 20),
        }.Select(b => b with { BoundsInCapturePixels = Scale(b.BoundsInCapturePixels, scale) }).ToArray();
        var result = SmartTextRegionSelector.Select(lines, new PixelPoint((int)(80 * scale), (int)(115 * scale)), new PixelSize(2000, 1200));
        Assert.NotNull(result);
        Assert.Equal("First line\nSecond line\nLast line", result.Text);
        Assert.True(result.Bounds.Y <= 80 * scale);
        Assert.True(result.Bounds.Bottom >= 150 * scale);
        Assert.True(result.Bounds.Right < 500 * scale);
    }

    [Fact]
    public void Blank_Area_Does_Not_Select_Distant_Text()
    {
        var result = SmartTextRegionSelector.Select([Line("a", "Hello", 100, 100, 180, 20)], new PixelPoint(700, 500), new PixelSize(1000, 800));
        Assert.Null(result);
    }

    [Fact]
    public void Padding_Is_Clipped_To_Image_And_Larger_Heading_Is_Excluded()
    {
        var result = SmartTextRegionSelector.Select([
            Line("title", "Heading", 0, 0, 200, 40),
            Line("a", "Body", 0, 45, 200, 20),
            Line("b", "Next line", 0, 70, 180, 20)
        ], new PixelPoint(5, 55), new PixelSize(205, 95));
        Assert.NotNull(result);
        Assert.Equal("Body\nNext line", result.Text);
        Assert.Equal(0, result.Bounds.X);
        Assert.Equal(95, result.Bounds.Bottom);
    }

    private static OcrBlock Line(string id, string text, int x, int y, int w, int h) => new(id, text, 1, new PixelRect(x, y, w, h), 0);
    private static PixelRect Scale(PixelRect r, double s) => new((int)(r.X * s), (int)(r.Y * s), (int)(r.Width * s), (int)(r.Height * s));
}
