namespace ScreenTranslator.Core.Models;

public enum TranslationStyle
{
    Natural,
    Literal,
    Learning,
}

public sealed record TranslationRequest(
    string SourceLanguage,
    string TargetLanguage,
    TranslationStyle Style,
    string Context,
    IReadOnlyList<OcrBlock> Blocks)
{
    public byte[]? ImagePng { get; init; }
    public bool LocateImageText { get; init; }
    public byte[]? ImageTextCropPng { get; init; }
    public PixelRect? ImageTextCropBounds { get; init; }
    public IReadOnlyList<ImageAtlasRegion> AtlasRegions { get; init; } = [];
    public PixelSize? ImageSize { get; init; }
    public bool IsOcrDocument { get; init; }
    public bool IsPageText { get; init; }
    public bool IsDirectVision { get; init; }
    public bool UsesNativeMapping{get;init;}
    public bool IsRegionalVision {get;init;}
    public bool IsVisionVerification {get;init;}
    public bool IsFocusedVision {get;init;}
    public ThinkingLevel? ThinkingOverride {get;init;}
    public VisionRequestTask VisionTask { get; init; }
    public int? MaxOutputTokensOverride { get; init; }
    /// <summary>Ask compatible Chat Completions servers for streaming usage during an explicit benchmark.</summary>
    public bool RequestUsageMetrics { get; init; }
    public IReadOnlyList<ImageAtlasRegion> VisionRegions { get; init; } = [];
    public byte[]? VisionReadingDetailPng { get; init; }
    public IReadOnlyList<byte[]> VisionReadingSheets {get;init;}=[];
    public IReadOnlyList<VisionReadingHint> VisionReadingHints { get; init; } = [];
}

public sealed record VisionReadingHint(string RegionId, string Text, double Confidence)
{
    public string? Kind { get; init; }
    public string? Name { get; init; }
}

// Existing numeric values are persisted in settings; append new levels without renumbering.
public enum ThinkingLevel { None = 0, Low = 1, High = 2, Max = 3, Medium = 4, XHigh = 5, Minimal = 6 }

public enum VisionRequestTask { Default, Translation, Placement }

public enum TranslationInputMode { Automatic, TextOnly, VisionOnly }

public enum TranslationTriggerMode { ClickOrDrag, MousePosition }

public sealed record TranslationStreamUpdate(string Translation)
{
    public IReadOnlyList<TranslatedBlock>? ImageSegments { get; init; }
    public string? StreamingTail { get; init; }
    public bool IsLayoutUpdate { get; init; }
}

public sealed record TranslatedBlock(
    string Id,
    string SourceText,
    string Translation,
    PixelRect Bounds)
{
    public IReadOnlyList<PixelRect> TextBounds { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public bool IsVertical { get; init; }
    public bool IsComplete { get; init; } = true;
    public bool HasVisionPlacement { get; init; }
    public PixelRect LayoutBounds { get; init; }
    public bool PreciseWords {get;init;}
    public float GlyphSizeHint{get;init;}
    public PixelRect VisionInkBounds {get;init;}
    /// <summary>Optional last source glyphs, used with SourceText's short prefix to join independent vision streams.</summary>
    public string SourceAnchorTail { get; init; } = string.Empty;
    public VisionTypographyHint? TypographyHint { get; init; }
}

public sealed record TranslationResult(IReadOnlyList<TranslatedBlock> Blocks);
