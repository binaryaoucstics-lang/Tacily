using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Models;

public sealed class GeometryTests
{
    [Fact]
    public void Intersect_Clips_Rectangle_To_Monitor()
    {
        var selection = new PixelRect(-20, 10, 100, 80);
        var monitor = new PixelRect(0, 0, 1920, 1080);

        Assert.Equal(new PixelRect(0, 10, 80, 80), selection.Intersect(monitor));
    }

    [Fact]
    public void Physical_To_Dip_Uses_Target_Monitor_Scale()
    {
        var transform = new MonitorTransform(new PixelRect(1920, 0, 2560, 1440), 1.5);

        Assert.Equal(
            new DipRect(1280, 0, 640, 480),
            transform.ToDip(new PixelRect(1920, 0, 960, 720)));
    }

    [Fact]
    public void CapturedBitmap_Rejects_Undersized_Buffer()
    {
        Assert.Throws<ArgumentException>(
            () => new CapturedBitmap(10, 10, 40, new byte[399]));
    }
}
