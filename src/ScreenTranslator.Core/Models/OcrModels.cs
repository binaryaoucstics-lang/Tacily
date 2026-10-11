namespace ScreenTranslator.Core.Models;

public sealed record OcrBlock(
    string Id,
    string Text,
    double Confidence,
    PixelRect BoundsInCapturePixels,
    int ReadingOrder);
