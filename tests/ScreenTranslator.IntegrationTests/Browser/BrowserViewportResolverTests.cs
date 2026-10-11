using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserViewportResolverTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PhysicalRenderWidgetProducesCorrectQueryOriginAndImageMapping(double dpr)
    {
        var viewport = new PixelRect(-2200, 180, (int)(800*dpr), (int)(600*dpr));
        var window = new PixelRect(-2210, 60, viewport.Width+20, viewport.Height+140);
        var point = new PixelPoint(viewport.X+(int)(100*dpr), viewport.Y+(int)(140*dpr));
        Assert.True(BrowserViewportResolver.IsViewport(viewport,window,point,new(800,600),dpr));
        var reply = new BrowserImageReply("r",true,"image","document",new(1000,1400),new(40,80,500,700),new(40,80,500,520),dpr,new(800,600),4);
        var mapping=reply.CreateMapping(new(1000,1400),viewport);
        Assert.NotNull(mapping);
        var position=mapping.ToImage(point);
        Assert.NotNull(position);
        Assert.Equal(120,position.Value.X,5);
        Assert.Equal(120,position.Value.Y,5);
    }

    [Fact]
    public void ChildFrameOrWholeWindowCannotPretendToBePageViewport()
    {
        var window=new PixelRect(0,0,1220,1040);var point=new PixelPoint(200,200);
        Assert.False(BrowserViewportResolver.IsViewport(new(10,100,500,500),window,point,new(800,600),1.5));
        Assert.False(BrowserViewportResolver.IsViewport(window,window,point,new(800,600),1.5));
        Assert.False(BrowserViewportResolver.IsViewport(new(10,100,1200,900),window,new(8,100),new(800,600),1.5));
        Assert.False(BrowserViewportResolver.IsViewport(new(10,100,1200,900),window,point,new(800,600),double.NaN));
    }

    [Fact]
    public void WindowMatchingAcceptsPhysicalAndDipCoordinatesOnScaledNegativeMonitor()
    {
        var window=new BrowserWindowSnapshot(1,new(-2400,150,1800,1350),144);
        Assert.Equal(0,BrowserViewportResolver.WindowMatchError(window,new(-2400,150,1800,1350)));
        Assert.Equal(0,BrowserViewportResolver.WindowMatchError(window,new(-1600,100,1200,900)));
        Assert.True(BrowserViewportResolver.WindowMatchError(window,new(0,0,1200,900))>260);
        Assert.Equal(double.PositiveInfinity,BrowserViewportResolver.WindowMatchError(window,new(double.NaN,0,1200,900)));
    }
}
