using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public sealed class TranslationOrchestrator
{
    private const string RepairInstruction =
        """

        FORMAT_REPAIR: The previous response did not match the required JSON schema.
        Return one valid JSON object only, with exactly one translation for every supplied block ID.
        Previous invalid response:
        """;

    private readonly ITranslationProvider _provider;
    private readonly TranslationResponseValidator _validator;

    public TranslationOrchestrator(
        ITranslationProvider provider,
        TranslationResponseValidator validator)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken,
        IProgress<TranslationStreamUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Blocks.Count == 0)
        {
            return new TranslationResult(Array.Empty<TranslatedBlock>());
        }

        var expectedIds = ValidateRequest(request);
        var rawResponse = await SendAsync(request).ConfigureAwait(false);

        try
        {
            return ParseResult(rawResponse);
        }
        catch (TranslationFormatException firstException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repairRequest = request with
            {
                Context = string.Concat(
                    request.Context,
                    request.UsesNativeMapping?"\nFORMAT_REPAIR: Return translations object keyed by every supplied r ID. Values are strings; empty only for nontext. No p IDs, coordinates or original text.":request.IsDirectVision ? request.VisionRegions.Count>0
                        ? "\nFORMAT_REPAIR: "+firstException.Message+" Valid JSON p1,p2,...; select one r ID and ink box relative to that crop (0..1000) per dialogue. If reusing an r ID, ink boxes MUST enclose different speakers and NOT overlap. No original text. Previous invalid response:\n"+rawResponse[..Math.Min(rawResponse.Length,6000)]
                        : "\nFORMAT_REPAIR: Valid JSON blocks, sequential p1,p2,...; box, area, flow before translation. Coordinates 0..1000 on full image. No original text."
                        : request.IsOcrDocument ? "\nFORMAT_REPAIR: Valid grouped JSON only. Cover every input ID once, in order; group only consecutive IDs." : RepairInstruction,
                    request.IsOcrDocument || request.IsDirectVision ? string.Empty : rawResponse),
            };

            progress?.Report(new TranslationStreamUpdate(string.Empty));
            var repairedResponse = await SendAsync(repairRequest).ConfigureAwait(false);

            try
            {
                return ParseResult(repairedResponse);
            }
            catch (TranslationFormatException secondException)
            {
                throw new TranslationFormatException(
                    "翻译模型两次返回的文本块格式都无法验证。",
                    new AggregateException(firstException, secondException));
            }
        }

        TranslationResult ParseResult(string json)
        {
            if(request.UsesNativeMapping)return new(VisionSemantics.Filter(NativeVisionTranslations.Parse(json,request.VisionRegions).Blocks.Select(b=>VisionSemantics.Apply(b,request)).ToArray(), request));
            if (request.IsDirectVision) return new(VisionSemantics.Filter(DirectVisionTranslation.Parse(json, request.ImageSize ?? default, request.VisionRegions,request.IsFocusedVision)
                .Blocks.Select(block => VisionSemantics.Apply(block, request)).ToArray(), request));
            if (request.IsOcrDocument) return OcrTranslationGroups.Parse(json, request);
            if (request.AtlasRegions.Count > 0 && !request.IsOcrDocument) return _validator.ParseAtlas(json, request.AtlasRegions);
            if (request.ImagePng is not null && request.LocateImageText)
                return _validator.ParseImageBlocks(json, new PixelSize(request.Blocks[0].BoundsInCapturePixels.Width, request.Blocks[0].BoundsInCapturePixels.Height));
            var translations = _validator.Parse(json, expectedIds);
            var sources = request.ImagePng is null ? null : _validator.ParseSources(json, expectedIds);
            var blocks = request.Blocks.Select(block => new TranslatedBlock(
                block.Id,
                sources is null ? block.Text : sources[block.Id],
                translations[block.Id],
                block.BoundsInCapturePixels)
            { TextBounds = request.AtlasRegions.FirstOrDefault(region => region.Id == block.Id)?.TextBounds ?? [] })
            .ToArray();

            return new TranslationResult(blocks);
        }

        Task<string> SendAsync(TranslationRequest item) =>
            progress is not null && _provider is IStreamingTranslationProvider streaming
                ? streaming.TranslateRawStreamingAsync(item, progress, cancellationToken)
                : _provider.TranslateRawAsync(item, cancellationToken);
    }

    private static string[] ValidateRequest(TranslationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            throw new ArgumentException("Target language is required.", nameof(request));
        }

        var ids = request.Blocks.Select(block => block.Id).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ArgumentException("Translation block IDs must be non-empty and unique.", nameof(request));
        }

        return ids;
    }
}
