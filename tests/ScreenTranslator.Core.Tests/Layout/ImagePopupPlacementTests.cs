using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class ImagePopupPlacementTests
{
    [Theory]
    [InlineData(100, 400, 448, 512)]
    [InlineData(1400, 400, 448, 940)]
    [InlineData(700, 900, 448, 240)]
    [InlineData(400, 1400, 448, 0)]
    public void Prefers_Right_Then_Left_Without_Covering_Image(int imageX, int imageWidth, int panelWidth, int expectedX)
    {
        var image = new PixelRect(imageX, 100, imageWidth, 800);
        var result = ImagePopupPlacement.Place(image, new PixelRect(0, 0, 1920, 1080), new PixelSize(panelWidth, 520), 260, 200, 12);
        Assert.Equal(expectedX, result.X);
        Assert.True(image.Intersect(result).IsEmpty);
    }

    [Fact]
    public void Expanded_Size_Rechecks_Side_And_Supports_Negative_Monitor_Origin()
    {
        var image = new PixelRect(-1200, 100, 500, 700);
        var work = new PixelRect(-1920, 0, 1100, 1000);
        // This monitor leaves only space to the left, including after expanding.
        var collapsed = ImagePopupPlacement.Place(image, work, new PixelSize(320, 88), 260, 64, 12);
        var expanded = ImagePopupPlacement.Place(image, work, new PixelSize(448, 520), 260, 200, 12);
        Assert.True(image.Intersect(collapsed).IsEmpty);
        Assert.True(image.Intersect(expanded).IsEmpty);
        Assert.Equal(-1660, expanded.X);
    }

    [Fact]
    public void Uses_Vertical_Free_Space_If_Both_Sides_Are_Too_Narrow()
    {
        var image = new PixelRect(100, 100, 1720, 600);
        var result = ImagePopupPlacement.Place(image, new PixelRect(0, 0, 1920, 1080), new PixelSize(448, 520), 260, 200, 12);
        Assert.Equal(712, result.Y);
        Assert.Equal(368, result.Height);
        Assert.True(image.Intersect(result).IsEmpty);
    }
}
