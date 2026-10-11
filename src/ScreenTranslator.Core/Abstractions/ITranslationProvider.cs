using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Abstractions;

public interface ITranslationProvider
{
    Task<string> TranslateRawAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
