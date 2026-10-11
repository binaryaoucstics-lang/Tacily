using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Layout;

public readonly record struct DipSize(double Width, double Height);

public static class SidePanelBoundsService
{
    private const double PlacementGap = 12;
    private const double MinimumWidth = 320;
    private const double MaximumWidth = 520;
    private const double MinimumHeight = 280;
    private const double MaximumWorkAreaRatio = 0.8;

    public static DipRect Place(
        DipRect source,
        DipRect workArea,
        DipSize desired,
        WindowPlacement? previous)
    {
        EnsureValidWorkArea(workArea);

        var requestedWidth = previous is { IsValid: true }
            ? previous.Width
            : desired.Width;
        var requestedHeight = previous is { IsValid: true }
            ? previous.Height
            : desired.Height;

        var maximumWidth = Math.Max(1, Math.Min(MaximumWidth, workArea.Width));
        var minimumWidth = Math.Min(MinimumWidth, maximumWidth);
        var maximumHeight = Math.Max(1, workArea.Height * MaximumWorkAreaRatio);
        var minimumHeight = Math.Min(MinimumHeight, maximumHeight);
        var width = Math.Clamp(FiniteOr(requestedWidth, 392), minimumWidth, maximumWidth);
        var height = Math.Clamp(FiniteOr(requestedHeight, 520), minimumHeight, maximumHeight);

        var candidate = previous is { IsValid: true }
            ? new DipRect(previous.Left, previous.Top, width, height)
            : PlaceBeside(source, width, height, workArea);

        return Clamp(candidate, workArea);
    }

    public static DipRect Clamp(DipRect candidate, DipRect workArea)
    {
        EnsureValidWorkArea(workArea);

        var maximumWidth = Math.Max(1, Math.Min(MaximumWidth, workArea.Width));
        var minimumWidth = Math.Min(MinimumWidth, maximumWidth);
        var maximumHeight = Math.Max(1, workArea.Height * MaximumWorkAreaRatio);
        var minimumHeight = Math.Min(MinimumHeight, maximumHeight);
        var width = Math.Clamp(
            FiniteOr(candidate.Width, MinimumWidth),
            minimumWidth,
            maximumWidth);
        var height = Math.Clamp(
            FiniteOr(candidate.Height, MinimumHeight),
            minimumHeight,
            maximumHeight);
        var left = Math.Clamp(
            FiniteOr(candidate.X, workArea.X),
            workArea.X,
            Right(workArea) - width);
        var top = Math.Clamp(
            FiniteOr(candidate.Y, workArea.Y),
            workArea.Y,
            Bottom(workArea) - height);

        return new DipRect(left, top, width, height);
    }

    private static DipRect PlaceBeside(
        DipRect source,
        double width,
        double height,
        DipRect workArea)
    {
        if (Right(source) + PlacementGap + width <= Right(workArea))
        {
            return new DipRect(Right(source) + PlacementGap, source.Y, width, height);
        }

        if (source.X - PlacementGap - width >= workArea.X)
        {
            return new DipRect(source.X - PlacementGap - width, source.Y, width, height);
        }

        if (Bottom(source) + PlacementGap + height <= Bottom(workArea))
        {
            return new DipRect(source.X, Bottom(source) + PlacementGap, width, height);
        }

        return new DipRect(source.X, source.Y - PlacementGap - height, width, height);
    }

    private static void EnsureValidWorkArea(DipRect workArea)
    {
        if (!double.IsFinite(workArea.X) ||
            !double.IsFinite(workArea.Y) ||
            !double.IsFinite(workArea.Width) ||
            !double.IsFinite(workArea.Height) ||
            workArea.Width <= 0 ||
            workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workArea),
                "工作区必须是有限的正尺寸矩形。");
        }
    }

    private static double Right(DipRect rect) => rect.X + rect.Width;

    private static double Bottom(DipRect rect) => rect.Y + rect.Height;

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}
