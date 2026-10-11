using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Browser;

public enum BrowserSessionDecision
{
    Ignore,
    Move,
    Invalidate,
}

public sealed record BrowserSessionUpdate(
    BrowserSessionDecision Decision,
    double DeltaXDip,
    double DeltaYDip,
    DipRect? ScrollContainerDip,
    string? TargetId,
    string? Reason)
{
    public static BrowserSessionUpdate Ignore { get; } =
        new(BrowserSessionDecision.Ignore, 0, 0, null, null, null);

    public static BrowserSessionUpdate Move(
        double deltaXDip,
        double deltaYDip,
        DipRect? scrollContainerDip,
        string targetId) =>
        new(
            BrowserSessionDecision.Move,
            deltaXDip,
            deltaYDip,
            scrollContainerDip,
            targetId,
            null);

    public static BrowserSessionUpdate Invalidate(string reason) =>
        new(BrowserSessionDecision.Invalidate, 0, 0, null, null, reason);
}

public sealed class BrowserTrackingSession
{
    private const double EqualityTolerance = 0.000_001;

    private readonly double _initialDevicePixelRatio;
    private readonly CssSize _initialViewportSize;
    private readonly double _monitorScale;
    private readonly DipRect _viewportBoundsDip;
    private long _navigationGeneration;

    public BrowserTrackingSession(
        BrowserHello hello,
        double monitorScale,
        DipRect viewportBoundsDip)
    {
        ArgumentNullException.ThrowIfNull(hello);
        BrowserProtocol.Validate(hello);

        if (hello.FrameId != 0)
        {
            throw new ArgumentException(
                "Browser tracking must start from the top frame.",
                nameof(hello));
        }

        if (!double.IsFinite(monitorScale)
            || monitorScale < BrowserProtocol.MinimumDevicePixelRatio
            || monitorScale > BrowserProtocol.MaximumDevicePixelRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        if (!IsValidRect(viewportBoundsDip))
        {
            throw new ArgumentException("Viewport DIP bounds are invalid.", nameof(viewportBoundsDip));
        }

        Browser = hello.Browser;
        BrowserWindowId = hello.BrowserWindowId;
        TabId = hello.TabId;
        DocumentToken = hello.DocumentToken;
        _navigationGeneration = hello.NavigationGeneration;
        _initialDevicePixelRatio = hello.DevicePixelRatio;
        _initialViewportSize = hello.ViewportSize;
        _monitorScale = monitorScale;
        _viewportBoundsDip = viewportBoundsDip;
    }

    public BrowserKind Browser { get; }

    public int BrowserWindowId { get; }

    public int TabId { get; }

    public string DocumentToken { get; }

    public long NavigationGeneration => _navigationGeneration;

    public BrowserSessionUpdate Apply(BrowserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (GetWindowId(message) != BrowserWindowId)
        {
            return BrowserSessionUpdate.Ignore;
        }

        if (GetTabId(message) != TabId)
        {
            return BrowserSessionUpdate.Invalidate("浏览器已切换标签页。");
        }

        if (!BrowserProtocol.TryValidate(message, out var validationError))
        {
            return BrowserSessionUpdate.Invalidate(validationError ?? "浏览器消息无效。");
        }

        if (GetFrameId(message) != 0)
        {
            return BrowserSessionUpdate.Invalidate("无法可靠映射子框架滚动坐标。");
        }

        if (!string.Equals(
                GetDocumentToken(message),
                DocumentToken,
                StringComparison.Ordinal))
        {
            return BrowserSessionUpdate.Invalidate("页面文档已经变化。");
        }

        var generation = GetNavigationGeneration(message);
        if (generation < _navigationGeneration)
        {
            return BrowserSessionUpdate.Invalidate("收到乱序的页面导航代次。");
        }

        _navigationGeneration = generation;

        return message switch
        {
            BrowserInvalidated invalidated =>
                BrowserSessionUpdate.Invalidate(invalidated.Reason),
            BrowserHello hello => ApplyHello(hello),
            BrowserScroll scroll => ApplyScroll(scroll),
            _ => BrowserSessionUpdate.Invalidate("浏览器消息类型不受支持。"),
        };
    }

    private BrowserSessionUpdate ApplyHello(BrowserHello hello)
    {
        if (hello.Browser != Browser
            || !NearlyEqual(hello.DevicePixelRatio, _initialDevicePixelRatio)
            || !NearlyEqual(hello.ViewportSize.Width, _initialViewportSize.Width)
            || !NearlyEqual(hello.ViewportSize.Height, _initialViewportSize.Height))
        {
            return BrowserSessionUpdate.Invalidate("浏览器缩放或视口已经变化。");
        }

        return BrowserSessionUpdate.Ignore;
    }

    private BrowserSessionUpdate ApplyScroll(BrowserScroll scroll)
    {
        if (!NearlyEqual(scroll.DevicePixelRatio, _initialDevicePixelRatio))
        {
            return BrowserSessionUpdate.Invalidate("浏览器缩放已经变化。");
        }

        var deltaXDip = OverlayFollowCalculator.ToDip(
            scroll.DeltaXCss,
            scroll.DevicePixelRatio,
            _monitorScale);
        var deltaYDip = OverlayFollowCalculator.ToDip(
            scroll.DeltaYCss,
            scroll.DevicePixelRatio,
            _monitorScale);

        if (deltaXDip == 0 && deltaYDip == 0)
        {
            return BrowserSessionUpdate.Ignore;
        }

        DipRect? mappedContainer = null;
        if (scroll.ScrollContainer is { } container)
        {
            mappedContainer = new DipRect(
                _viewportBoundsDip.X + ToDipCoordinate(container.Left),
                _viewportBoundsDip.Y + ToDipCoordinate(container.Top),
                ToDipCoordinate(container.Width),
                ToDipCoordinate(container.Height));
        }

        return BrowserSessionUpdate.Move(
            deltaXDip,
            deltaYDip,
            mappedContainer,
            scroll.TargetId);
    }

    private double ToDipCoordinate(double cssValue) =>
        cssValue * _initialDevicePixelRatio / _monitorScale;

    private static int GetWindowId(BrowserMessage message) =>
        message switch
        {
            BrowserHello hello => hello.BrowserWindowId,
            BrowserScroll scroll => scroll.BrowserWindowId,
            BrowserInvalidated invalidated => invalidated.BrowserWindowId,
            _ => -1,
        };

    private static int GetTabId(BrowserMessage message) =>
        message switch
        {
            BrowserHello hello => hello.TabId,
            BrowserScroll scroll => scroll.TabId,
            BrowserInvalidated invalidated => invalidated.TabId,
            _ => -1,
        };

    private static int GetFrameId(BrowserMessage message) =>
        message switch
        {
            BrowserHello hello => hello.FrameId,
            BrowserScroll scroll => scroll.FrameId,
            BrowserInvalidated invalidated => invalidated.FrameId,
            _ => -1,
        };

    private static string GetDocumentToken(BrowserMessage message) =>
        message switch
        {
            BrowserHello hello => hello.DocumentToken,
            BrowserScroll scroll => scroll.DocumentToken,
            BrowserInvalidated invalidated => invalidated.DocumentToken,
            _ => string.Empty,
        };

    private static long GetNavigationGeneration(BrowserMessage message) =>
        message switch
        {
            BrowserHello hello => hello.NavigationGeneration,
            BrowserScroll scroll => scroll.NavigationGeneration,
            BrowserInvalidated invalidated => invalidated.NavigationGeneration,
            _ => -1,
        };

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= EqualityTolerance;

    private static bool IsValidRect(DipRect rectangle) =>
        double.IsFinite(rectangle.X)
        && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width)
        && double.IsFinite(rectangle.Height)
        && rectangle.Width > 0
        && rectangle.Height > 0;
}
