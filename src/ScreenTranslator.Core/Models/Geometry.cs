namespace ScreenTranslator.Core.Models;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IsUsable => Width >= 8 && Height >= 8;

    public PixelPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= left || bottom <= top
            ? new PixelRect(left, top, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    public PixelRect Translate(PixelPoint offset) =>
        new(X + offset.X, Y + offset.Y, Width, Height);

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}

public readonly record struct DipRect(double X, double Y, double Width, double Height);

public sealed record MonitorTransform
{
    public MonitorTransform(PixelRect physicalBounds, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Monitor scale must be finite and positive.");
        }

        PhysicalBounds = physicalBounds;
        Scale = scale;
    }

    public PixelRect PhysicalBounds { get; }

    public double Scale { get; }

    public DipRect ToDip(PixelRect physical) =>
        new(
            physical.X / Scale,
            physical.Y / Scale,
            physical.Width / Scale,
            physical.Height / Scale);

    public PixelRect ToPhysical(DipRect dip) =>
        new(
            checked((int)Math.Round(dip.X * Scale, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(dip.Y * Scale, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(dip.Width * Scale, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(dip.Height * Scale, MidpointRounding.AwayFromZero)));
}
