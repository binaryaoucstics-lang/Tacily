using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Abstractions;

public interface ITextDetector
{
    Task<IReadOnlyList<TextRegion>> DetectAsync(CapturedBitmap bitmap, CancellationToken cancellationToken);
}
