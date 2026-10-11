using System.Text.Json;
using System.Text.Encodings.Web;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

internal sealed record TranslationInputPart(string? Text, string? ImageBase64, string? Detail);
internal sealed record TranslationRequestContent(string SystemPrompt, IReadOnlyList<TranslationInputPart> Parts);

/// <summary>Shared translation instructions and image order; transport schemas stay in each provider.</summary>
internal static class TranslationPromptBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions UserContentSerializerOptions = new(SerializerOptions)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static TranslationRequestContent Create(TranslationRequest request, bool briefGuidance)
    {
        var userRequest = new
        {
            sourceLanguage = request.SourceLanguage,
            targetLanguage = request.TargetLanguage,
            style = request.Style.ToString().ToLowerInvariant(),
            context = request.Context,
            imageSize = request.AtlasRegions.Count > 0 ? null : request.ImageSize is { } size ? new { width = size.Width, height = size.Height }
                : request.LocateImageText && request.Blocks.Count > 0
                    ? new { width = request.Blocks[0].BoundsInCapturePixels.Width, height = request.Blocks[0].BoundsInCapturePixels.Height } : null,
            imageTextCropBounds = request.AtlasRegions.Count > 0 ? null : request.ImageTextCropBounds,
            blocks = request.Blocks.Select(block => new
            {
                id = block.Id,
                text = request.ImagePng is null ? block.Text : null,
                direction = request.AtlasRegions.FirstOrDefault(region => region.Id == block.Id)?.IsVertical == true ? "vertical-rtl" : null,
                order = request.IsOcrDocument ? (int?)block.ReadingOrder : null,
                region = request.IsOcrDocument && block.BoundsInCapturePixels.IsUsable ? new[] { block.BoundsInCapturePixels.X, block.BoundsInCapturePixels.Y,
                    block.BoundsInCapturePixels.Width, block.BoundsInCapturePixels.Height } : null,
            }),
        };

        var instructions = request.IsPageText ? JsonSerializer.Serialize(userRequest, UserContentSerializerOptions)
            : request.VisionTask == VisionRequestTask.Translation
            ? JsonSerializer.Serialize(new { sourceLanguage = request.SourceLanguage, targetLanguage = request.TargetLanguage,
                style = request.Style.ToString().ToLowerInvariant(), context = request.Context,
                regions = request.VisionRegions.Count == 0 ? null : request.VisionRegions.Select(region => new { id = region.Id,
                    direction = region.IsVertical ? "vertical-rtl" : "horizontal" }).ToArray() }, SerializerOptions)
            : request.VisionTask == VisionRequestTask.Placement
            ? JsonSerializer.Serialize(new { targetLanguage = request.TargetLanguage, coordinateSystem = "normalized-1000",
                regions = request.VisionRegions.Count == 0 ? null : request.VisionRegions.Select(region => new { id = region.Id,
                    box = NormalizedPlacementBox(region.SourceBounds, request.ImageSize),
                    direction = region.IsVertical ? "vertical-rtl" : "horizontal" }).ToArray() }, SerializerOptions)
            : request.IsDirectVision
            ? JsonSerializer.Serialize(new { sourceLanguage = request.SourceLanguage, targetLanguage = request.TargetLanguage,
                style = request.Style.ToString().ToLowerInvariant(), context = request.Context,
                imageSize = request.ImageSize is { } imageSize ? new { width = imageSize.Width, height = imageSize.Height } : null,
                regions = request.VisionRegions.Count == 0 ? null : request.VisionRegions.Select(region => new {
                    id = region.Id, box = new[] { region.SourceBounds.X, region.SourceBounds.Y, region.SourceBounds.Right, region.SourceBounds.Bottom },
                    direction = region.IsVertical ? "vertical-rtl" : "horizontal", paragraph=region.IsParagraph,precise=region.PreciseWords }).ToArray(),
                readingHints = request.VisionReadingHints.Count == 0 ? null : request.VisionReadingHints }, SerializerOptions)
            : JsonSerializer.Serialize(userRequest, SerializerOptions);
        var content = new List<TranslationInputPart> { new(instructions, null, null) };
        if (request.ImagePng is { Length: > 0 } imagePng)
        {
            content.Add(new(null, Convert.ToBase64String(imagePng), null));
            if(request.IsDirectVision&&request.VisionReadingSheets.Count>0)
                foreach(var sheet in request.VisionReadingSheets)content.Add(new(null, Convert.ToBase64String(sheet), "original"));
            else if (request.IsDirectVision && request.VisionReadingDetailPng is { Length: > 0 } readingDetail)
                content.Add(new(null, Convert.ToBase64String(readingDetail), null));
            if (!request.IsDirectVision && request.AtlasRegions.Count == 0 && request.ImageTextCropPng is { Length: > 0 } detail)
            {
                content.Add(new("Detailed text crop for reading only. All output bbox coordinates must reference the FIRST full source image.", null, null));
                content.Add(new(null, Convert.ToBase64String(detail), null));
            }
        }
        var systemPrompt = request.IsPageText ? DeepSeekTranslationProvider.PageTextPrompt : request.VisionTask == VisionRequestTask.Translation ? ParallelVisionTranslation.TranslationPrompt : request.VisionTask == VisionRequestTask.Placement ? ParallelVisionTranslation.PlacementPrompt
            : request.IsVisionVerification?DirectVisionTranslation.VerificationPrompt:request.IsRegionalVision?NativeVisionTranslations.RegionalPrompt:request.UsesNativeMapping?NativeVisionTranslations.Prompt:request.IsDirectVision ? request.VisionRegions.Count > 0 ? DirectVisionTranslation.GroundedPrompt : DirectVisionTranslation.Prompt : request.IsOcrDocument ? DeepSeekTranslationProvider.OcrGroupingPrompt : request.ImagePng is null ? DeepSeekTranslationProvider.SystemPrompt : request.AtlasRegions.Count > 0 ? DeepSeekTranslationProvider.AtlasPrompt : request.LocateImageText ? DeepSeekTranslationProvider.ImageLayoutPrompt : DeepSeekTranslationProvider.VisionPrompt;
        if (briefGuidance) systemPrompt += "\n" + DeepSeekTranslationProvider.BriefTranslationGuidance;
        if (request.ImagePng is { Length: > 0 })
            systemPrompt += "\nOmit already-target-language text, photo/copyright credits, URLs, media watermarks and pure numerals/formulas. Keep meaningful dialogue, captions and names; do not typeset excluded content.";
        return new(systemPrompt, content);
    }

    internal static void Validate(TranslationRequest request, ThinkingLevel thinking)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsPageText && (request.ImagePng is not null || request.ImageTextCropPng is not null
            || request.ImageSize is not null || request.ImageTextCropBounds is not null || request.AtlasRegions.Count > 0
            || request.IsDirectVision || request.IsOcrDocument || request.LocateImageText || request.UsesNativeMapping
            || request.VisionTask != VisionRequestTask.Default || request.VisionRegions.Count > 0
            || request.VisionReadingDetailPng is not null || request.VisionReadingSheets.Count > 0))
            throw new ArgumentException("Page text requests must contain text blocks only.", nameof(request));
        if (!Enum.IsDefined(thinking)) throw new ArgumentOutOfRangeException(nameof(request), "Invalid thinking override.");
    }

    private static int[]? NormalizedPlacementBox(PixelRect bounds, PixelSize? sourceSize)
    {
        if (sourceSize is not { Width: > 0, Height: > 0 } size) return null;
        int X(int value) => (int)Math.Clamp(Math.Round(value * 1000d / size.Width), 0, 1000);
        int Y(int value) => (int)Math.Clamp(Math.Round(value * 1000d / size.Height), 0, 1000);
        return [X(bounds.X), Y(bounds.Y), X(bounds.Right), Y(bounds.Bottom)];
    }

    internal static int OutputTokenBudget(TranslationRequest request, ThinkingLevel thinking)
    {
        var usual = thinking switch { ThinkingLevel.None => 8192, ThinkingLevel.Low => 16384, _ => 32768 };
        if (request.MaxOutputTokensOverride is not { } requested) return usual;
        // max_tokens includes reasoning. A tiny "speed" cap cannot eat the entire
        // page translation. Small bounded metadata-only requests may opt in.
        var minimum = request.VisionTask == VisionRequestTask.Placement && thinking == ThinkingLevel.None ? 256 : usual;
        return Math.Clamp(requested, minimum, 65536);
    }

}
