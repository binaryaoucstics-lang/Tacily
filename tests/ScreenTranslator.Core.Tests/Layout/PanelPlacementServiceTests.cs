using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class PanelPlacementServiceTests
{
    private readonly PanelPlacementService _service = new();

    [Fact]
    public void Place_Uses_Right_When_It_Fits()
    {
        var result = _service.Place(
            new PixelRect(100, 100, 200, 100),
            new PixelSize(420, 360),
            new PixelRect(0, 0, 1920, 1080));

        Assert.Equal(new PixelRect(312, 100, 420, 360), result);
    }

    [Fact]
    public void Place_Uses_Left_When_Right_Does_Not_Fit()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);
        var selection = new PixelRect(1700, 100, 200, 100);

        Assert.Equal(
            new PixelRect(1268, 100, 420, 360),
            _service.Place(selection, new PixelSize(420, 360), workArea, gap: 12));
    }

    [Fact]
    public void Place_Constrains_Oversized_Panel_To_Negative_WorkArea()
    {
        var result = _service.Place(
            new PixelRect(-100, 100, 80, 80),
            new PixelSize(1200, 900),
            new PixelRect(-1920, 0, 1000, 800));

        Assert.Equal(new PixelRect(-1920, 0, 1000, 800), result);
    }
}
