using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>Keep readable anchors and locally detected glyph areas, including text that cannot be confidently transcribed.</summary>
public static class DualVisionLocalEvidence
{
    public static IReadOnlyList<OcrBlock> FromAssessment(ImageTranslationAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return assessment.AllRecognizedText.Concat(assessment.DetectedTextRegions.Select((region, index) =>
            new OcrBlock($"detected-{index}", "", region.Confidence, region.Bounds, index)))
            .Where(block => block.BoundsInCapturePixels.IsUsable).ToArray();
    }
}
