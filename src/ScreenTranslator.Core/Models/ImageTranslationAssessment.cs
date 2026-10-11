namespace ScreenTranslator.Core.Models;

/// <summary>A local, fallible decision used only to start optional automatic image translation.</summary>
public sealed record ImageTranslationAssessment(
    bool NeedsTranslation,
    string Reason,
    double Confidence,
    string DetectedLanguage,
    IReadOnlyList<TextRegion> TextRegions,
    long ElapsedMilliseconds)
{
    public string ReasonCode { get; init; } = "unknown";
    public IReadOnlyList<OcrBlock> RecognizedText { get; init; } = [];
    public int DetectorRegionCount { get; init; }
    public bool WasDownscaled { get; init; }
    public string? ErrorType { get; init; }
    public IReadOnlyList<OcrBlock> AllRecognizedText { get; init; } = [];
    public IReadOnlyList<TextRegion> DetectedTextRegions { get; init; } = [];
}
