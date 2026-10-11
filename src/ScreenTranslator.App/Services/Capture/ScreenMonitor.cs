using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public sealed record ScreenMonitor(
    nint Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public double ScaleX => DpiX / 96d;

    public double ScaleY => DpiY / 96d;
}
