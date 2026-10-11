using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class InPlaceMappingTests
{
    [Theory]
    [InlineData("シャワー", "しゃわー")]
    [InlineData("ﾊﾟﾝﾀﾞ", "ぱんだ")]
    [InlineData("ガンバッテ", "がんばって")]
    public void KanaDifferencesLocateWithoutChangingSource(string local, string vision)
    {
        var source = new OcrBlock("p1", vision, 1, default, 0);
        var region = Assert.Single(WholeImageTextMatcher.Match([source], [new("l1", local, .98, new(10, 20, 40, 180), 0)], true));
        Assert.Single(region.TextBounds);
        Assert.Equal(vision, source.Text);
    }

    [Fact]
    public void SmallRecognitionErrorUsesUniqueContextButAmbiguousRepeatedTextDoesNot()
    {
        var rectangle = new PixelRect(20, 40, 40, 180);
        var source = new OcrBlock("p1", "がんばって", 1, default, 0);
        Assert.Single(WholeImageTextMatcher.Match([source], [new("l1", "がんばうて", .99, rectangle, 0)], true)[0].TextBounds);
        var repeated = WholeImageTextMatcher.Match([new("p1", "ありがとう", 1, default, 0), new("p2", "ありがとう", 1, default, 1)],
            [new("l1", "ありがとう", .99, rectangle, 0), new("l2", "ありがとう", .99, rectangle.Translate(new(200, 0)), 1)], true);
        Assert.All(repeated, region => Assert.Empty(region.TextBounds));
    }

    [Fact]
    public void PartialCoincidenceCannotEraseTheWholeParagraph()
    {
        var regions = WholeImageTextMatcher.Match([new("p1", "Today we must finish the whole document", 1, default, 0)],
            [new("l1", "Today", .99, new(20, 30, 100, 40), 0)], true);
        Assert.Empty(regions[0].TextBounds);
    }

    [Fact]
    public void ShortKanaTypoNeedsHighConfidenceAndUniquePosition()
    {
        var source = new OcrBlock("p1", "やたっ♪", 1, default, 0);
        var local = new OcrBlock("l1", "やたう", .99, new(20, 30, 50, 160), 0);
        Assert.Single(WholeImageTextMatcher.Match([source], [local], true)[0].TextBounds);
        Assert.Empty(WholeImageTextMatcher.Match([source], [local with { Confidence = .6 }], true)[0].TextBounds);
        Assert.Empty(WholeImageTextMatcher.Match([source], [local, local with { Id = "l2", BoundsInCapturePixels = new(200, 30, 50, 160) }], true)[0].TextBounds);
    }

    [Fact]
    public void PartiallyVisiblePatchKeepsFullProjectionInsteadOfSqueezingItsBitmap()
    {
        var mapping = new SourceImageMapping(new(1000, 2000), new(100, 100, 500, 1000), new(100, 100, 500, 500));
        var region = new PixelRect(50, 900, 200, 400);
        Assert.Equal(new DipRect(125, 550, 100, 200), mapping.Project(region));
        Assert.Equal(new PixelRect(125, 550, 100, 50), mapping.ToScreen(region));
        Assert.Equal(new DipRect(325, 650, 100, 200), mapping.Project(region, new(200, 100)));
    }
}
