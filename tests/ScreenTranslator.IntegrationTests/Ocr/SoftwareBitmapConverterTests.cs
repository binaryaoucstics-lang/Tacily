using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using Windows.Graphics.Imaging;

namespace ScreenTranslator.IntegrationTests.Ocr;

public sealed class SoftwareBitmapConverterTests
{
    [Fact]
    public void ToBitmapSource_PreservesDimensionsAndFreezesImage()
    {
        var bitmap = CreateBitmap();

        var result = SoftwareBitmapConverter.ToBitmapSource(bitmap, 120, 144);

        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
        Assert.Equal(120, result.DpiX, precision: 3);
        Assert.Equal(144, result.DpiY, precision: 3);
        Assert.True(result.IsFrozen);
    }

    [Fact]
    public void ToSoftwareBitmap_CreatesBgra8Bitmap()
    {
        var bitmap = CreateBitmap();

        using var result = SoftwareBitmapConverter.ToSoftwareBitmap(bitmap);

        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
        Assert.Equal(BitmapPixelFormat.Bgra8, result.BitmapPixelFormat);
        Assert.Equal(BitmapAlphaMode.Premultiplied, result.BitmapAlphaMode);
    }

    [Fact]
    public void BitmapSourceRoundTrip_PreservesDimensions()
    {
        var source = SoftwareBitmapConverter.ToBitmapSource(CreateBitmap());

        using var result = SoftwareBitmapConverter.ToSoftwareBitmap(source);

        Assert.Equal(source.PixelWidth, result.PixelWidth);
        Assert.Equal(source.PixelHeight, result.PixelHeight);
    }

    private static CapturedBitmap CreateBitmap() =>
        new(
            2,
            2,
            8,
            new byte[]
            {
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 255,
            });
}
