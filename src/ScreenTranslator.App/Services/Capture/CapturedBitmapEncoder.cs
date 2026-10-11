using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public static class CapturedBitmapEncoder
{
    public static CapturedBitmap DecodeOriginal(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth > 20000 || frame.PixelHeight > 20000 || (long)frame.PixelWidth * frame.PixelHeight > 24_000_000)
            throw new InvalidDataException("图片过大，请拖框选择需要翻译的部分。");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(frame.PixelWidth * 4);
        var pixels = new byte[checked(stride * frame.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        // Composite transparent pixels on white before passing them to OCR.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            for (var channel = 0; channel < 3; channel++) pixels[index + channel] = (byte)((pixels[index + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[index + 3] = 255;
        }
        return new CapturedBitmap(frame.PixelWidth, frame.PixelHeight, stride, pixels);
    }

    public static BitmapSource Preview(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    public static byte[] ToPng(CapturedBitmap bitmap,bool transparent=false)
    {
        // Screen captures are opaque; ignore the unused alpha byte even for alternate capture backends.
        var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, transparent?PixelFormats.Pbgra32:PixelFormats.Bgr32,
            null, bitmap.Pixels.ToArray(), bitmap.Stride);
        image.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
