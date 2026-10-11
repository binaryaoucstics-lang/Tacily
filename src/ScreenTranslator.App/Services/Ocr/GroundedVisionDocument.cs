using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Ocr;

public sealed record GroundedVisionDocument(IReadOnlyList<ImageAtlasRegion> Regions, byte[]? DetailPng)
{
    public IReadOnlyList<VisionReadingHint> ReadingHints { get; init; } = [];
    public CapturedBitmap? InkMask { get; init; }
    public IReadOnlyList<byte[]> ReadingSheets {get;init;}=[];
}

