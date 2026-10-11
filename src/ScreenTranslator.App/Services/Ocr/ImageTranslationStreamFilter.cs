using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Ocr;

public static class ImageTranslationStreamFilter
{
    public static TranslationStreamUpdate Apply(TranslationStreamUpdate update, TranslationRequest request,
        IReadOnlyList<OcrBlock> evidence, bool evidenceReady)
    {
        if (update.ImageSegments is not { } blocks) return update;
        // Null keeps plain text streaming in the real view model. Empty means
        // an authoritative empty result and would clear its translated text.
        if (!evidenceReady) return update with { ImageSegments = null };
        var eligible = VisionSemantics.Filter(VisionSemantics.Filter(blocks, request),
            request.TargetLanguage, evidence, sourceIsComplete: false);
        return update with { ImageSegments = eligible, Translation = string.Join("\n",
            new[] { string.Join("\n", eligible.Select(block => block.Translation)), update.StreamingTail ?? "" }
                .Where(text => text.Length > 0)) };
    }
}
