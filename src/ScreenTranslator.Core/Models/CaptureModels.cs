namespace ScreenTranslator.Core.Models;

/// <summary>
/// A tightly packed or stride-padded BGRA8 bitmap owned by the caller.
/// </summary>
public sealed record CapturedBitmap
{
    public CapturedBitmap(
        int width,
        int height,
        int stride,
        ReadOnlyMemory<byte> pixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (stride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride must fit one BGRA8 row.");
        }

        if (pixels.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer is smaller than the declared bitmap.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public ReadOnlyMemory<byte> Pixels { get; }

    public PixelSize Size => new(Width, Height);
}

public sealed record MonitorSnapshot(
    string DeviceId,
    PixelRect PhysicalBounds,
    PixelRect WorkingArea,
    double Scale,
    CapturedBitmap Bitmap);

public sealed record ScreenSelection(
    string DeviceId,
    PixelRect BoundsInVirtualDesktopPixels,
    CapturedBitmap Bitmap);
