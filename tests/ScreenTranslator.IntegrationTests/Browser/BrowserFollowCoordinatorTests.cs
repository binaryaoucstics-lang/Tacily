using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserFollowCoordinatorTests
{
    [Fact]
    public void Root_Scroll_Moves_Existing_Overlay_Against_Page_Direction()
    {
        var overlay = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var coordinator = CreateCoordinator(overlay);

        coordinator.Handle(CreateScroll(deltaYCss: 50));

        Assert.Equal(150, overlay.TrackingBounds.Y);
        Assert.True(overlay.IsTrackingVisible);
        Assert.Equal(1, overlay.MoveCount);
    }

    [Fact]
    public void Scroll_Hides_Overlay_After_It_Leaves_Selection()
    {
        var overlay = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var coordinator = CreateCoordinator(overlay);

        coordinator.Handle(CreateScroll(deltaYCss: 500));

        Assert.False(overlay.IsTrackingVisible);
    }

    [Fact]
    public void Changed_Document_Invalidates_And_Hides_Overlays()
    {
        var overlay = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var coordinator = CreateCoordinator(overlay);
        string? reason = null;
        coordinator.Invalidated += (_, args) => reason = args.Reason;

        coordinator.Handle(CreateScroll(
            deltaYCss: 10,
            documentToken: "other-document"));

        Assert.True(coordinator.IsInvalidated);
        Assert.False(overlay.IsTrackingVisible);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Other_Browser_Window_Is_Ignored()
    {
        var overlay = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var coordinator = CreateCoordinator(overlay);

        coordinator.Handle(CreateScroll(
            deltaYCss: 50,
            browserWindowId: 99));

        Assert.Equal(200, overlay.TrackingBounds.Y);
        Assert.Equal(0, overlay.MoveCount);
    }

    [Fact]
    public void Browser_Window_Move_Offsets_Selection_And_Overlay()
    {
        var overlay = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var coordinator = CreateCoordinator(overlay);

        coordinator.OffsetWithBrowserWindow(25, -10);
        coordinator.Handle(CreateScroll(deltaYCss: 5));

        Assert.Equal(125, overlay.TrackingBounds.X);
        Assert.Equal(185, overlay.TrackingBounds.Y);
    }

    [Fact]
    public void Removed_Overlay_No_Longer_Receives_Scroll_Updates()
    {
        var removed = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var remaining = new FakeOverlay(new DipRect(100, 260, 220, 40));
        var coordinator = CreateCoordinator(removed, remaining);

        Assert.True(coordinator.RemoveOverlay(removed));
        coordinator.Handle(CreateScroll(deltaYCss: 50));

        Assert.Equal(200, removed.TrackingBounds.Y);
        Assert.Equal(210, remaining.TrackingBounds.Y);
        Assert.Equal(1, coordinator.OverlayCount);
    }

    [Fact]
    public void Multiple_Groups_Keep_Independent_Selection_Bounds()
    {
        var first = new FakeOverlay(new DipRect(100, 200, 220, 40));
        var second = new FakeOverlay(new DipRect(500, 500, 180, 36));
        var coordinator = CreateCoordinator(first);
        coordinator.AddGroup(
            [second],
            new DipRect(480, 460, 260, 180));

        coordinator.Handle(CreateScroll(deltaYCss: 50));

        Assert.Equal(150, first.TrackingBounds.Y);
        Assert.Equal(450, second.TrackingBounds.Y);
        Assert.True(first.IsTrackingVisible);
        Assert.True(second.IsTrackingVisible);
        Assert.Equal(2, coordinator.OverlayCount);
    }

    private static BrowserFollowCoordinator CreateCoordinator(
        params ITrackedOverlay[] overlays)
    {
        var hello = new BrowserHello(
            BrowserKind.Chrome,
            BrowserWindowId: 4,
            TabId: 8,
            DocumentToken: "document-1",
            NavigationGeneration: 1,
            DevicePixelRatio: 1,
            ViewportSize: new CssSize(1200, 800),
            BrowserWindowBounds: new CssRect(0, 0, 1280, 900));
        var session = new BrowserTrackingSession(
            hello,
            monitorScale: 1,
            viewportBoundsDip: new DipRect(0, 80, 1200, 800));
        return new BrowserFollowCoordinator(
            session,
            overlays,
            selectionBounds: new DipRect(80, 120, 300, 300));
    }

    private static BrowserScroll CreateScroll(
        double deltaYCss,
        int browserWindowId = 4,
        string documentToken = "document-1") =>
        new(
            browserWindowId,
            TabId: 8,
            documentToken,
            NavigationGeneration: 1,
            DeltaXCss: 0,
            DeltaYCss: deltaYCss,
            DevicePixelRatio: 1,
            ScrollContainer: null);

    private sealed class FakeOverlay(DipRect bounds) : ITrackedOverlay
    {
        public DipRect TrackingBounds { get; private set; } = bounds;

        public bool IsTrackingVisible { get; private set; } = true;

        public int MoveCount { get; private set; }

        public void MoveTo(DipRect next)
        {
            TrackingBounds = next;
            MoveCount++;
        }

        public void SetTrackingVisibility(bool visible) =>
            IsTrackingVisible = visible;
    }
}
