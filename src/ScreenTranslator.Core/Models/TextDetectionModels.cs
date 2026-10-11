namespace ScreenTranslator.Core.Models;

public enum TextReadingDirection { Auto, Horizontal, JapaneseVertical }
public sealed record TextRegion(PixelRect Bounds, double Confidence);
public sealed record TextRegionGroup(PixelRect CropBounds, IReadOnlyList<PixelRect> TextBounds, bool IsVertical)
{
    public IReadOnlyList<PixelRect> PunctuationBounds { get; init; } = [];
}
public sealed record ImageAtlasRegion(string Id, PixelRect SourceBounds, PixelRect AtlasBounds)
{
    // Crop padding belongs to the atlas, not the local highlight.
    public IReadOnlyList<PixelRect> TextBounds { get; init; } = [];
    public bool IsVertical { get; init; }
    public bool IsParagraph { get; init; }
    public bool PreciseWords {get;init;}
}
public sealed record TextAtlas(CapturedBitmap Bitmap, IReadOnlyList<ImageAtlasRegion> Regions);
