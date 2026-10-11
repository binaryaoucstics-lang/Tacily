using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Abstractions;

public interface IOcrEngine
{
    Task<IReadOnlyList<OcrBlock>> RecognizeAsync(
        CapturedBitmap bitmap,
        string? languageTag,
        CancellationToken cancellationToken);
}
