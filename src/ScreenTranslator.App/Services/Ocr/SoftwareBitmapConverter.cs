using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Core.Models;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace ScreenTranslator.App.Services.Ocr;

public static class SoftwareBitmapConverter
{
    public static SoftwareBitmap ToSoftwareBitmap(CapturedBitmap bitmap) =>
        ToSoftwareBitmapAsync(bitmap).GetAwaiter().GetResult();

    public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(
        CapturedBitmap bitmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return await ToSoftwareBitmapAsync(
                ToBitmapSource(bitmap),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static SoftwareBitmap ToSoftwareBitmap(BitmapSource source) =>
        ToSoftwareBitmapAsync(source).GetAwaiter().GetResult();

    public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        BitmapSource bgraSource = source;
        if (source.Format != PixelFormats.Bgra32 &&
            source.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(
                source,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
            converted.Freeze();
            bgraSource = converted;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bgraSource));

        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using var output = randomAccessStream.AsStreamForWrite();
        await png.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        randomAccessStream.Seek(0);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder
            .CreateAsync(randomAccessStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        return await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    public static BitmapSource ToBitmapSource(
        CapturedBitmap bitmap,
        double dpiX = 96,
        double dpiY = 96)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            palette: null,
            bitmap.Pixels.ToArray(),
            bitmap.Stride);
        source.Freeze();
        return source;
    }

}
