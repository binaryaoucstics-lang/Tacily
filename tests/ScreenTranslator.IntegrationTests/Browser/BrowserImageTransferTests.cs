using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserImageTransferTests
{
    [Fact]
    public void ImageLargerThanNativeMessageRoundTripsInOrderedChunks()
    {
        var bytes = new byte[1_600_000]; new Random(2).NextBytes(bytes);
        var base64 = Convert.ToBase64String(bytes);
        var transfer = new BrowserImageTransfer();
        for (int offset = 0, index = 0; offset < base64.Length; offset += 262144, index++)
            transfer.Append(index, base64.Substring(offset, Math.Min(262144, base64.Length - offset)));
        Assert.Equal(bytes, transfer.Complete(base64.Length));
    }

    [Fact]
    public void MissingReorderedAndOversizedChunksAreRejected()
    {
        var transfer = new BrowserImageTransfer();
        Assert.Throws<InvalidDataException>(() => transfer.Append(1, "YQ=="));
        Assert.Throws<InvalidDataException>(() => transfer.Append(0, new string('a', 262145)));
        transfer.Append(0, "YQ==");
        Assert.Throws<InvalidDataException>(() => transfer.Complete(8));
        Assert.Equal(new byte[] { 97 }, transfer.Complete(4));
    }

    [Fact]
    public void MappingUsesActualDecodedResolutionDprAndMeasuredViewportOrigin()
    {
        var reply = new BrowserImageReply("r", true, "i", "d", new CssSize(500, 1000), new CssRect(20, -40, 400, 800),
            new CssRect(20, 0, 400, 500), 1.5, new CssSize(800, 600), 4);
        var mapping = reply.CreateMapping(new PixelSize(1000, 2000), new PixelRect(-1600, 160, 1200, 900));
        Assert.NotNull(mapping);
        Assert.Equal(new PixelRect(-1570, 160, 600, 750), mapping.VisibleBounds);
        Assert.Equal(new PixelRect(-1510, 160, 120, 60), mapping.ToScreen(new PixelRect(100, 100, 200, 100)));
        Assert.Null(reply.CreateMapping(new PixelSize(1000, 2000), new PixelRect(0, 0, 800, 600)));
        Assert.Null((reply with { DevicePixelRatio = double.NaN }).CreateMapping(new PixelSize(1000, 2000), new PixelRect(0, 0, 1200, 900)));
    }
}
