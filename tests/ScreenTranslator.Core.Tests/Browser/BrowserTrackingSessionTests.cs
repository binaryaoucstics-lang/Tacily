using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Browser;

public sealed class BrowserTrackingSessionTests
{
    [Fact]
    public void Matching_Scroll_With_Increasing_Generation_Returns_Mapped_Move()
    {
        var session = CreateSession();
        var scroll = CreateScroll(
            navigationGeneration: 11,
            deltaXCss: 10,
            deltaYCss: 50);

        var update = session.Apply(scroll);

        Assert.Equal(BrowserSessionDecision.Move, update.Decision);
        Assert.Equal(12, update.DeltaXDip, 3);
        Assert.Equal(60, update.DeltaYDip, 3);
        Assert.Equal(11, session.NavigationGeneration);
        Assert.Null(update.ScrollContainerDip);
        Assert.Equal(BrowserProtocol.RootTargetId, update.TargetId);
    }

    [Fact]
    public void Nested_Scroll_Maps_Container_From_Viewport_Css_To_Screen_Dip()
    {
        var session = CreateSession();
        var scroll = CreateScroll(
            scrollContainer: new CssRect(20, 30, 200, 100),
            targetId: "element-7");

        var update = session.Apply(scroll);

        Assert.Equal(BrowserSessionDecision.Move, update.Decision);
        Assert.Equal(new DipRect(124, 236, 240, 120), update.ScrollContainerDip);
        Assert.Equal("element-7", update.TargetId);
    }

    [Fact]
    public void Different_Window_Is_Ignored_Without_Disturbing_Session()
    {
        var session = CreateSession();

        var update = session.Apply(CreateScroll(browserWindowId: 99));

        Assert.Equal(BrowserSessionDecision.Ignore, update.Decision);
        Assert.Equal(10, session.NavigationGeneration);
    }

    [Fact]
    public void Zero_Delta_Is_Ignored()
    {
        var session = CreateSession();

        var update = session.Apply(CreateScroll(deltaXCss: 0, deltaYCss: 0));

        Assert.Equal(BrowserSessionDecision.Ignore, update.Decision);
    }

    [Fact]
    public void Same_Window_With_Different_Tab_Invalidates()
    {
        var session = CreateSession();

        var update = session.Apply(CreateScroll(tabId: 8));

        Assert.Equal(BrowserSessionDecision.Invalidate, update.Decision);
    }

    [Theory]
    [InlineData("new-document", 10, 0, 1.5)]
    [InlineData("document-a", 9, 0, 1.5)]
    [InlineData("document-a", 10, 2, 1.5)]
    [InlineData("document-a", 10, 0, 2.0)]
    public void Changed_Document_Stale_Generation_Frame_Or_Zoom_Invalidates(
        string documentToken,
        long generation,
        int frameId,
        double dpr)
    {
        var session = CreateSession();
        var update = session.Apply(
            CreateScroll(
                documentToken: documentToken,
                navigationGeneration: generation,
                frameId: frameId,
                devicePixelRatio: dpr));

        Assert.Equal(BrowserSessionDecision.Invalidate, update.Decision);
    }

    [Fact]
    public void NonFinite_Delta_Invalidates()
    {
        var session = CreateSession();

        var update = session.Apply(CreateScroll(deltaYCss: double.NaN));

        Assert.Equal(BrowserSessionDecision.Invalidate, update.Decision);
    }

    [Fact]
    public void Explicit_Invalidation_For_Current_Document_Invalidates()
    {
        var session = CreateSession();
        var update = session.Apply(
            new BrowserInvalidated(
                BrowserWindowId: 4,
                TabId: 7,
                DocumentToken: "document-a",
                NavigationGeneration: 10,
                Reason: "navigation"));

        Assert.Equal(BrowserSessionDecision.Invalidate, update.Decision);
        Assert.Equal("navigation", update.Reason);
    }

    [Fact]
    public void Protocol_RoundTrips_CamelCase_Discriminator_And_Enums()
    {
        BrowserMessage hello = CreateHello();

        var json = BrowserProtocol.Serialize(hello);
        var restored = BrowserProtocol.Deserialize(json);

        Assert.Contains("\"type\":\"hello\"", json, StringComparison.Ordinal);
        Assert.Contains("\"browser\":\"chrome\"", json, StringComparison.Ordinal);
        Assert.Equal(hello, restored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"type":"scroll","browserWindowId":4}""")]
    [InlineData("""{"type":"unknown"}""")]
    public void Protocol_Rejects_Incomplete_Or_Unknown_Json(string json) =>
        Assert.Throws<BrowserProtocolException>(() => BrowserProtocol.Deserialize(json));

    [Fact]
    public void Protocol_Rejects_OutOfRange_Delta_And_Invalid_Container()
    {
        Assert.Throws<BrowserProtocolException>(
            () => BrowserProtocol.Validate(
                CreateScroll(deltaYCss: 100_001)));

        Assert.Throws<BrowserProtocolException>(
            () => BrowserProtocol.Validate(
                CreateScroll(
                    scrollContainer: new CssRect(0, 0, double.NaN, 100),
                    targetId: "nested")));
    }

    private static BrowserTrackingSession CreateSession() =>
        new(
            CreateHello(),
            monitorScale: 1.25,
            viewportBoundsDip: new DipRect(100, 200, 960, 720));

    private static BrowserHello CreateHello() =>
        new(
            Browser: BrowserKind.Chrome,
            BrowserWindowId: 4,
            TabId: 7,
            DocumentToken: "document-a",
            NavigationGeneration: 10,
            DevicePixelRatio: 1.5,
            ViewportSize: new CssSize(800, 600),
            BrowserWindowBounds: new CssRect(50, 50, 1200, 900));

    private static BrowserScroll CreateScroll(
        int browserWindowId = 4,
        int tabId = 7,
        string documentToken = "document-a",
        long navigationGeneration = 10,
        double deltaXCss = 0,
        double deltaYCss = 20,
        double devicePixelRatio = 1.5,
        CssRect? scrollContainer = null,
        string targetId = BrowserProtocol.RootTargetId,
        int frameId = 0) =>
        new(
            browserWindowId,
            tabId,
            documentToken,
            navigationGeneration,
            deltaXCss,
            deltaYCss,
            devicePixelRatio,
            scrollContainer,
            targetId,
            frameId);
}
