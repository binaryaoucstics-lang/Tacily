using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class SourceImageMappingTests
{
    [Fact]
    public void NaturalPixelsScaleToVisiblePageAndRoundTripAfterWindowMovement()
    {
        var mapping = new SourceImageMapping(new PixelSize(1000, 2000), new DipRect(-1200, 50, 500, 1000),
            new PixelRect(-1200, 100, 500, 600));
        var offset = new PixelPoint(80, 20);
        Assert.Equal(new PixelRect(-1070, 120, 100, 100), mapping.ToScreen(new PixelRect(100, 100, 200, 200), offset));
        Assert.Equal((100d, 100d), mapping.ToImage(new PixelPoint(-1070, 120), offset));
        Assert.Null(mapping.ToImage(new PixelPoint(-1070, 119), offset));
        Assert.Null(mapping.ToScreen(new PixelRect(100, 1500, 200, 100), offset));
    }

    [Fact]
    public void CoverClipsBothHighlightAndPointerHitToTheDisplayedBox()
    {
        var mapping = new SourceImageMapping(new PixelSize(1000, 500), new DipRect(0, 100, 800, 400), new PixelRect(200, 100, 400, 400));
        Assert.Equal(new PixelRect(200, 180, 80, 80), mapping.ToScreen(new PixelRect(100, 100, 250, 100)));
        Assert.Null(mapping.ToImage(new PixelPoint(199, 180)));
        Assert.Equal((250d, 100d), mapping.ToImage(new PixelPoint(200, 180)));
    }
}
