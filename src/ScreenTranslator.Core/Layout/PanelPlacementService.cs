using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public sealed class PanelPlacementService
{
    public PixelRect Place(
        PixelRect selection,
        PixelSize panelSize,
        PixelRect workArea,
        int gap = 12)
    {
        if (panelSize.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(panelSize), "Panel size must be positive.");
        }

        if (workArea.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), "Work area must be positive.");
        }

        if (gap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gap));
        }

        var right = new PixelRect(
            selection.Right + gap,
            Clamp(selection.Y, workArea.Y, workArea.Bottom - panelSize.Height),
            panelSize.Width,
            panelSize.Height);
        if (Fits(right, workArea))
        {
            return right;
        }

        var left = new PixelRect(
            selection.X - gap - panelSize.Width,
            Clamp(selection.Y, workArea.Y, workArea.Bottom - panelSize.Height),
            panelSize.Width,
            panelSize.Height);
        if (Fits(left, workArea))
        {
            return left;
        }

        var below = new PixelRect(
            Clamp(selection.X, workArea.X, workArea.Right - panelSize.Width),
            selection.Bottom + gap,
            panelSize.Width,
            panelSize.Height);
        if (Fits(below, workArea))
        {
            return below;
        }

        var above = new PixelRect(
            Clamp(selection.X, workArea.X, workArea.Right - panelSize.Width),
            selection.Y - gap - panelSize.Height,
            panelSize.Width,
            panelSize.Height);
        if (Fits(above, workArea))
        {
            return above;
        }

        var constrainedWidth = Math.Min(panelSize.Width, workArea.Width);
        var constrainedHeight = Math.Min(panelSize.Height, workArea.Height);
        return new PixelRect(
            Clamp(selection.Right + gap, workArea.X, workArea.Right - constrainedWidth),
            Clamp(selection.Y, workArea.Y, workArea.Bottom - constrainedHeight),
            constrainedWidth,
            constrainedHeight);
    }

    private static bool Fits(PixelRect rectangle, PixelRect workArea) =>
        rectangle.X >= workArea.X
        && rectangle.Y >= workArea.Y
        && rectangle.Right <= workArea.Right
        && rectangle.Bottom <= workArea.Bottom;

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
