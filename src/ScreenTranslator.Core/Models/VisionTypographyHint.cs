namespace ScreenTranslator.Core.Models;

public enum VisionFontFamily { Sans, Serif, Handwriting, Brush, Display }

/// <summary>Optional style advice; local glyph pixels remain authoritative for ink colours.</summary>
public sealed record VisionTypographyHint(
    VisionFontFamily Family = VisionFontFamily.Sans,
    bool Bold = false,
    float Angle = 0,
    float Skew = 0,
    float Outline = 0,
    bool Hollow = false)
{
    public string? ColorHex { get; init; }
    public string? OutlineColorHex { get; init; }
    public bool? Vertical { get; init; }
}
