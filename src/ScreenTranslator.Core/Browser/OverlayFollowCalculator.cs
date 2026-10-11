using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Browser;

public sealed record OverlayFollowUpdate(
    DipRect Bounds,
    bool IsVisible,
    DipRect? VisibleBounds,
    bool WasMoved);

public static class OverlayFollowCalculator
{
    public static double ToDip(
        double cssValue,
        double devicePixelRatio,
        double monitorScale)
    {
        if (!double.IsFinite(cssValue)
            || Math.Abs(cssValue) > BrowserProtocol.MaximumAbsoluteDeltaCss)
        {
            throw new ArgumentOutOfRangeException(nameof(cssValue));
        }

        if (!double.IsFinite(devicePixelRatio)
            || devicePixelRatio < BrowserProtocol.MinimumDevicePixelRatio
            || devicePixelRatio > BrowserProtocol.MaximumDevicePixelRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(devicePixelRatio));
        }

        if (!double.IsFinite(monitorScale)
            || monitorScale < BrowserProtocol.MinimumDevicePixelRatio
            || monitorScale > BrowserProtocol.MaximumDevicePixelRatio)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        return cssValue * devicePixelRatio / monitorScale;
    }

    public static OverlayFollowUpdate ApplyRootScroll(
        DipRect block,
        DipRect selection,
        double deltaXDip,
        double deltaYDip)
    {
        ValidateRect(block, nameof(block), allowEmpty: false);
        ValidateRect(selection, nameof(selection), allowEmpty: false);
        ValidateDelta(deltaXDip, nameof(deltaXDip));
        ValidateDelta(deltaYDip, nameof(deltaYDip));

        var moved = MoveAgainstScroll(block, deltaXDip, deltaYDip);
        var visibleBounds = Intersect(moved, selection);
        return new OverlayFollowUpdate(
            moved,
            visibleBounds is not null,
            visibleBounds,
            WasMoved: deltaXDip != 0 || deltaYDip != 0);
    }

    public static OverlayFollowUpdate ApplyNestedScroll(
        DipRect block,
        DipRect selection,
        DipRect scrollContainer,
        double deltaXDip,
        double deltaYDip)
    {
        ValidateRect(block, nameof(block), allowEmpty: false);
        ValidateRect(selection, nameof(selection), allowEmpty: false);
        ValidateRect(scrollContainer, nameof(scrollContainer), allowEmpty: false);
        ValidateDelta(deltaXDip, nameof(deltaXDip));
        ValidateDelta(deltaYDip, nameof(deltaYDip));

        if (!Contains(scrollContainer, CenterX(block), CenterY(block)))
        {
            var selectionClip = Intersect(block, selection);
            return new OverlayFollowUpdate(
                block,
                selectionClip is not null,
                selectionClip,
                WasMoved: false);
        }

        var moved = MoveAgainstScroll(block, deltaXDip, deltaYDip);
        var clipRegion = Intersect(selection, scrollContainer);
        var visibleBounds = clipRegion is null ? null : Intersect(moved, clipRegion.Value);
        return new OverlayFollowUpdate(
            moved,
            visibleBounds is not null,
            visibleBounds,
            WasMoved: deltaXDip != 0 || deltaYDip != 0);
    }

    private static DipRect MoveAgainstScroll(
        DipRect rectangle,
        double deltaXDip,
        double deltaYDip) =>
        new(
            rectangle.X - deltaXDip,
            rectangle.Y - deltaYDip,
            rectangle.Width,
            rectangle.Height);

    private static DipRect? Intersect(DipRect left, DipRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(Right(left), Right(right));
        var bottomEdge = Math.Min(Bottom(left), Bottom(right));
        return rightEdge <= x || bottomEdge <= y
            ? null
            : new DipRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private static bool Contains(DipRect rectangle, double x, double y) =>
        x >= rectangle.X
        && x < Right(rectangle)
        && y >= rectangle.Y
        && y < Bottom(rectangle);

    private static double CenterX(DipRect rectangle) => rectangle.X + (rectangle.Width / 2);

    private static double CenterY(DipRect rectangle) => rectangle.Y + (rectangle.Height / 2);

    private static double Right(DipRect rectangle) => rectangle.X + rectangle.Width;

    private static double Bottom(DipRect rectangle) => rectangle.Y + rectangle.Height;

    private static void ValidateDelta(double value, string parameterName)
    {
        if (!double.IsFinite(value)
            || Math.Abs(value) > BrowserProtocol.MaximumAbsoluteDeltaCss
                * BrowserProtocol.MaximumDevicePixelRatio
                / BrowserProtocol.MinimumDevicePixelRatio)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateRect(
        DipRect rectangle,
        string parameterName,
        bool allowEmpty)
    {
        if (!double.IsFinite(rectangle.X)
            || !double.IsFinite(rectangle.Y)
            || !double.IsFinite(rectangle.Width)
            || !double.IsFinite(rectangle.Height)
            || rectangle.Width < 0
            || rectangle.Height < 0
            || (!allowEmpty && (rectangle.Width == 0 || rectangle.Height == 0)))
        {
            throw new ArgumentException("DIP rectangle is invalid.", parameterName);
        }
    }
}
