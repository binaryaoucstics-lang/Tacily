using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Abstractions;

public interface IStreamingTranslationProvider
{
    Task<string> TranslateRawStreamingAsync(TranslationRequest request,
        IProgress<TranslationStreamUpdate> progress, CancellationToken cancellationToken);
}
