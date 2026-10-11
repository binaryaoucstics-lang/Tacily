using System.Runtime.InteropServices;
using SkiaSharp;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>Suppresses faint coloured ruling that otherwise joins handwritten glyphs into huge detections.</summary>
public static class VisionInkPreprocessor
{
    public static CapturedBitmap CleanRuling(CapturedBitmap source)
    {
        var bytes=source.Pixels.ToArray(); var changed=0;
        for(var y=0;y<source.Height;y++) for(var x=0;x<source.Width;x++)
        {
            var i=y*source.Stride+x*4; int b=bytes[i],g=bytes[i+1],r=bytes[i+2];
            var light=(r*299+g*587+b*114)/1000;
            // Faint cyan/green graph paper only. Strong coloured text and artwork retain their pixels.
            if(light>=150 && Math.Min(g,b)-r>=10 && Math.Max(g,b)-r>=22)
            { bytes[i]=bytes[i+1]=bytes[i+2]=255; changed++; }
        }
        return changed>=source.Width*(long)source.Height*.003 ? new(source.Width,source.Height,source.Stride,bytes) : source;
    }
    internal static CapturedBitmap ResizeForLayout(CapturedBitmap image)
    {
        const int longestEdge = 2048;
        if (Math.Max(image.Width, image.Height) <= longestEdge) return image;
        var scale = longestEdge / (double)Math.Max(image.Width, image.Height);
        using var source = LocalTextDetector.ToSkia(image);
        using var resized = source.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(image.Width * scale)),
            Math.Max(1, (int)Math.Round(image.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Opaque), SKSamplingOptions.Default);
        var stride = resized.Width * 4;
        var pixels = new byte[stride * resized.Height];
        for (var y = 0; y < resized.Height; y++) Marshal.Copy(resized.GetPixels() + y * resized.RowBytes, pixels, y * stride, stride);
        return new CapturedBitmap(resized.Width, resized.Height, stride, pixels);
    }
}
