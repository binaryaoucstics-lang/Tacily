namespace ScreenTranslator.Core.Models;

/// <summary>
/// Plain, read-only DOM text. IDs are opaque to the translation model.
/// Source group metadata describes the author's canonical group UTF-8 SHA256 and UTF-16 spans without sending its text.
/// Canonical text collapses ASCII HTML whitespace to one space and trims its ends; raw DOM text stays local.
/// </summary>
public sealed record BrowserPageTextBlock(
    string Id,
    string Text,
    string? GroupId = null,
    string? Context = null,
    string? SourceGroupHash = null,
    int? SourceGroupLength = null,
    int? SourceStart = null,
    int? SourceLength = null,
    bool ForeignHanContext = false,
    string? ContextKind = null);

/// <summary>A replacement for one original DOM node, never a model-generated node ID.</summary>
public sealed record BrowserPageTranslationUpdate(
    string Id,
    string Translation,
    bool IsComplete,
    bool FromCache = false,
    bool Failed = false);

public sealed record BrowserPageTranslationResult(
    IReadOnlyList<BrowserPageTranslationUpdate> Blocks,
    int RequestCount,
    int CacheHits)
{
    public int CompletedCount => Blocks.Count(block => block.IsComplete && !block.Failed);
    public int FailedCount => Blocks.Count(block => block.Failed);
}
