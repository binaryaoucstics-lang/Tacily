using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

internal static class TranslationStreamingProgress
{
    internal static TranslationStreamUpdate Read(TranslationRequest request, string json)
    {
        var text = StreamingTranslationParser.ReadTranslation(json);
        var segments = request.IsPageText ? PageTextStreaming.ReadStreaming(json, request.Blocks)
            : request.VisionTask != VisionRequestTask.Default ? ParallelVisionTranslation.ReadStreaming(json, request.ImageSize ?? default, request.VisionRegions, request.VisionTask)
            : request.UsesNativeMapping?NativeVisionTranslations.ReadStreaming(json,request.VisionRegions):request.IsDirectVision ? DirectVisionTranslation.ReadStreaming(json, request.ImageSize ?? default, request.VisionRegions,request.IsFocusedVision)
            : request.IsOcrDocument ? ImageTranslationStreamParser.ReadCompletedOcrBlocks(json, request)
            : request.AtlasRegions.Count > 0 ? ImageTranslationStreamParser.ReadCompletedAtlasBlocks(json, request.AtlasRegions)
            : request.LocateImageText ? ImageTranslationStreamParser.ReadCompletedBlocks(json,
            new PixelSize(request.Blocks[0].BoundsInCapturePixels.Width, request.Blocks[0].BoundsInCapturePixels.Height)) : null;
        if (request.IsDirectVision && segments is not null)
        { segments = VisionSemantics.Filter(segments.Select(block => VisionSemantics.Apply(block, request)).ToArray(), request); text = string.Join("\n", segments.Select(block => block.Translation)); }
        if (request.IsPageText && segments is not null) text = string.Join("\n", segments.Select(block => block.Translation));
        return new TranslationStreamUpdate(text) { ImageSegments = segments,
                            IsLayoutUpdate = request.VisionTask == VisionRequestTask.Placement,
                            StreamingTail = request.IsPageText || request.IsDirectVision ? string.Empty : segments is null ? null : ImageTranslationStreamParser.ReadTranslationTail(json) };
    }
}
