using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class TextLayoutServiceTests
{
    private readonly TextLayoutService _service = new();

    [Fact]
    public void NormalizeAndOrder_Removes_Blanks_Normalizes_Whitespace_And_Orders_Rows()
    {
        OcrBlock[] blocks =
        [
            Block("right", "  right \r\n text ", 120, 10, 80, 20, 8),
            Block("second", "second", 10, 50, 80, 20, 2),
            Block("blank", " \t ", 0, 0, 20, 20, 0),
            Block("left", "left", 10, 12, 80, 20, 9),
        ];

        var result = _service.NormalizeAndOrder(blocks);

        Assert.Equal(["left", "right", "second"], result.Select(block => block.Id));
        Assert.Equal("right text", result[1].Text);
        Assert.Equal([0, 1, 2], result.Select(block => block.ReadingOrder));
    }

    [Fact]
    public void Layout_Offsets_Capture_Coordinates_To_Virtual_Desktop()
    {
        var block = Block("b1", "text", 10, 20, 30, 40, 0);

        var result = _service.Layout([block], new PixelRect(-1920, 100, 500, 300));

        Assert.Equal(new PixelRect(-1910, 120, 30, 40), result[0].BoundsInCapturePixels);
    }

    private static OcrBlock Block(
        string id,
        string text,
        int x,
        int y,
        int width,
        int height,
        int order) =>
        new(id, text, 1, new PixelRect(x, y, width, height), order);
}
