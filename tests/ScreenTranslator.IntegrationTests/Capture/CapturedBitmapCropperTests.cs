using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class CapturedBitmapCropperTests
{
    [Fact]
    public void Crop_CopiesRequestedPixelsAndRemovesSourcePadding()
    {
        var source = new CapturedBitmap(
            2,
            2,
            12,
            new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99,
                9, 10, 11, 12, 13, 14, 15, 16, 99, 99, 99, 99,
            });

        var result = CapturedBitmapCropper.Crop(
            source,
            new PixelRect(1, 0, 1, 2));

        Assert.Equal(1, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(4, result.Stride);
        Assert.Equal(
            new byte[] { 5, 6, 7, 8, 13, 14, 15, 16 },
            result.Pixels.ToArray());
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(1, 1, 2, 1)]
    public void Crop_RejectsOutOfBoundsSelection(
        int x,
        int y,
        int width,
        int height)
    {
        var source = new CapturedBitmap(2, 2, 8, new byte[16]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CapturedBitmapCropper.Crop(
                source,
                new PixelRect(x, y, width, height)));
    }
}
