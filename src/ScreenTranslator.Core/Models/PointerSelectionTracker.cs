namespace ScreenTranslator.Core.Models;

public sealed record PointerSelection(PixelPoint Point, PixelRect Bounds, bool IsDrag);

public sealed class PointerSelectionTracker
{
    private PixelPoint _anchor;
    public bool IsSelecting { get; private set; }
    public PixelRect Bounds { get; private set; }

    public void Begin(PixelPoint point)
    {
        _anchor = point;
        IsSelecting = true;
        Bounds = new PixelRect(point.X, point.Y, 0, 0);
    }

    public void Move(PixelPoint point)
    {
        if (!IsSelecting) return;
        Bounds = new PixelRect(Math.Min(_anchor.X, point.X), Math.Min(_anchor.Y, point.Y),
            Math.Abs(point.X - _anchor.X), Math.Abs(point.Y - _anchor.Y));
    }

    public PointerSelection Complete(PixelPoint point)
    {
        if (!IsSelecting) throw new InvalidOperationException("No pointer selection is active.");
        Move(point);
        IsSelecting = false;
        return new PointerSelection(_anchor, Bounds, Bounds.Width >= 8 && Bounds.Height >= 8);
    }
}
