using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserManualImageRequestTests
{
    [Fact]
    public void ExplicitImageIgnoresAutomaticGateAndContinuesOffscreenWithTheSameSource()
    {
        using var json = JsonDocument.Parse("""{"requestId":"req","imageId":"img","documentToken":"doc","tabId":1,"browserWindowId":2,"frameId":0,"generation":1}""");
        Assert.True(BrowserManualImageRequest.TryRead(json.RootElement, out var request));
        var image = new BrowserImageReply("req",true,"img","doc",new(850,1200),default,default,1,new(1280,720),0) { AutoImageAllowed = false };
        Assert.True(request!.Matches(image)); Assert.False(request.Matches(image, requireVisible:true));
        Assert.False(request.Matches(image with { DocumentToken="new" }));
        Assert.False(request.Matches(image with { ImageId="other" }));
        Assert.False(request.Matches(image with { Natural=new(double.NaN,100) }));
    }
    [Theory]
    [InlineData("requestId", "")]
    [InlineData("imageId", "")]
    [InlineData("documentToken", "")]
    [InlineData("frameId", 1)]
    [InlineData("generation", 0)]
    [InlineData("tabId", -1)]
    public void MalformedIdentityIsRejected(string key, object value)
    {
        var fields=new Dictionary<string,object>{{"requestId","r"},{"imageId","i"},{"documentToken","d"},{"tabId",1},{"browserWindowId",2},{"frameId",0},{"generation",1}};
        fields[key]=value; using var json=JsonDocument.Parse(JsonSerializer.Serialize(fields));
        Assert.False(BrowserManualImageRequest.TryRead(json.RootElement,out _));
    }
}
