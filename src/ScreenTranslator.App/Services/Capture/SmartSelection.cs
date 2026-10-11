using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Capture;

public sealed record SmartSelection(PixelRect Bounds, string? AccessibleText, nint SourceWindowHandle)
{
    public PixelRect? ImageBounds { get; init; }
    public IReadOnlyList<OcrBlock> ImageOcrLines { get; init; } = [];
    public PixelRect? TextCropBounds { get; init; }
}
