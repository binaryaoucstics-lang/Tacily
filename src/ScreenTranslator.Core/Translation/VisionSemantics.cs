using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public static class VisionSemantics
{
    /// <summary>
    /// Exclude proven non-translation source text before the erase/render queue.
    /// sourceIsComplete must be false for the main VLM's first/last-glyph anchors.
    /// Translation text is deliberately never used as evidence of source language.
    /// </summary>
    public static IReadOnlyList<TranslatedBlock> Filter(IReadOnlyList<TranslatedBlock> blocks,
        string targetLanguage, IReadOnlyList<OcrBlock>? localText = null, bool sourceIsComplete = true)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var japaneseContext = (sourceIsComplete && blocks.Any(block => ImageTextEligibility.HasJapaneseScript(block.SourceText)))
            || localText is not null && localText.Any(line => double.IsFinite(line.Confidence) && line.Confidence >= .88
                && ImageTextEligibility.HasJapaneseScript(line.Text));
        return blocks.Where(block => ShouldTranslate(block, targetLanguage, localText, sourceIsComplete, japaneseContext)).ToArray();
    }

    public static IReadOnlyList<TranslatedBlock> Filter(IReadOnlyList<TranslatedBlock> blocks, TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Ordinary reading hints can cover just one readable line in a much
        // larger crop. They have no glyph envelope/completeness contract and
        // therefore cannot prove that the whole requested paragraph is native.
        var local = request.VisionReadingHints.Where(hint => hint.Kind == "complete-source"
            && double.IsFinite(hint.Confidence) && hint.Confidence >= .88)
            .Select(hint => (Hint: hint, Region: request.VisionRegions.FirstOrDefault(region => region.Id == hint.RegionId)))
            .Where(item => item.Region is not null).Select((item, index) => new OcrBlock(item.Hint.RegionId,
                item.Hint.Text, item.Hint.Confidence, item.Region!.SourceBounds, index)).ToArray();
        return Filter(blocks, request.TargetLanguage, local,
            sourceIsComplete: request.VisionTask == VisionRequestTask.Placement || request.IsOcrDocument);
    }

    private static bool ShouldTranslate(TranslatedBlock block, string target, IReadOnlyList<OcrBlock>? local,
        bool completeSource, bool japaneseContext)
    {
        if (completeSource && !string.IsNullOrWhiteSpace(block.SourceText)
            && IsExcludedSource(block.SourceText, target, japaneseContext)) return false;
        if (local is null || !block.Bounds.IsUsable) return true;
        var matched = local.Where(line => line.BoundsInCapturePixels.IsUsable
            && Coverage(line.BoundsInCapturePixels, block.Bounds) >= .8).ToArray();
        var readable = matched.Where(line => double.IsFinite(line.Confidence) && line.Confidence >= .88
            && !string.IsNullOrWhiteSpace(line.Text)).DistinctBy(line => (line.Text, line.BoundsInCapturePixels)).ToArray();
        if (readable.Length == 0) return true;
        // A recognized target-language line inside a larger foreign paragraph is
        // insufficient. Require most of the claimed ink envelope and coverage of
        // every overlapping native detector line, including unreadable lines.
        var envelope = readable.Select(line => line.BoundsInCapturePixels).Aggregate((a, b) => a.Union(b));
        if (Coverage(block.Bounds, envelope) < .7) return true;
        var native = local.Where(line => line.BoundsInCapturePixels.IsUsable
            && Coverage(line.BoundsInCapturePixels, block.Bounds) >= .35).ToArray();
        if (native.Any(line => !readable.Any(known => Coverage(line.BoundsInCapturePixels, known.BoundsInCapturePixels) >= .8))) return true;
        return !readable.All(line => IsExcludedSource(line.Text, target, japaneseContext));
    }

    private static bool IsExcludedSource(string source, string target, bool japaneseContext) =>
        ImageTextEligibility.ReadableContent(source).Length == 0
        || ImageTextEligibility.IsAlreadyTargetLanguage(source, target, japaneseContext);

    private static double Coverage(PixelRect original, PixelRect envelope)
    {
        var overlap = original.Intersect(envelope);
        return overlap.Width * (long)overlap.Height / (double)Math.Max(1, original.Width * (long)original.Height);
    }

    public static TranslatedBlock Apply(TranslatedBlock block, TranslationRequest request)
    {
        if (!request.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || block.SourceIds.Count != 1) return block;
        var signature = request.VisionReadingHints.FirstOrDefault(hint => hint.RegionId == block.SourceIds[0]
            && hint.Kind == "signature" && hint.Confidence >= .9 && !string.IsNullOrWhiteSpace(hint.Name));
        return signature is null ? block : block with { Translation = signature.Name + " 留", IsComplete = true };
    }
}
