using System.Text.Json;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

/// <summary>LLM groups reference source IDs; source text and all positions come from local input.</summary>
public static class OcrTranslationGroups
{
    public static TranslationResult Parse(string json, TranslationRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new TranslationFormatException("OCR 翻译缺少分组 blocks 数组。");
            var result = new List<TranslatedBlock>();
            var cursor = 0;
            foreach (var group in blocks.EnumerateArray()) result.Add(ReadGroup(group, request, ref cursor));
            if (cursor != request.Blocks.Count) throw new TranslationFormatException("OCR 翻译分组遗漏了原文片段。");
            return new TranslationResult(result);
        }
        catch (JsonException exception) { throw new TranslationFormatException("OCR 分组译文不是有效 JSON。", exception); }
    }

    public static IReadOnlyList<TranslatedBlock> ReadCompleted(IEnumerable<string> objects, TranslationRequest request)
    {
        var result = new List<TranslatedBlock>();
        var cursor = 0;
        foreach (var json in objects)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                result.Add(ReadGroup(document.RootElement, request, ref cursor));
            }
            catch (Exception exception) when (exception is JsonException or TranslationFormatException) { break; }
        }
        return result;
    }

    private static TranslatedBlock ReadGroup(JsonElement group, TranslationRequest request, ref int cursor)
    {
        if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("translation", out var translation)
            || translation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(translation.GetString()))
            throw new TranslationFormatException("OCR 翻译分组缺少译文。");
        string[] ids;
        if (group.TryGetProperty("ids", out var members))
        {
            if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() == 0
                || members.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                throw new TranslationFormatException("OCR 翻译分组的 ids 无效。");
            ids = members.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        else if (group.TryGetProperty("id", out var legacyId) && legacyId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(legacyId.GetString()))
            ids = [legacyId.GetString()!]; // Safe compatibility for an unmerged singleton response.
        else throw new TranslationFormatException("OCR 翻译分组缺少原文片段 ID。");
        var start = cursor;
        if (start + ids.Length > request.Blocks.Count || ids.Where((id, index) => id != request.Blocks[start + index].Id).Any())
            throw new TranslationFormatException("OCR 分组只能按原顺序覆盖连续片段，不能重复、跳过或新增 ID。");
        var sources = request.Blocks.Skip(cursor).Take(ids.Length).ToArray();
        var bounds = new List<PixelRect>();
        foreach (var source in sources)
        {
            var region = request.AtlasRegions.FirstOrDefault(item => item.Id == source.Id);
            if (region is not null)
            {
                if (region.TextBounds.Count > 0) bounds.AddRange(region.TextBounds.Where(rect => rect.IsUsable));
                else if (region.SourceBounds.IsUsable) bounds.Add(region.SourceBounds);
            }
            else if (source.BoundsInCapturePixels.IsUsable) bounds.Add(source.BoundsInCapturePixels);
        }
        var boxes = bounds.Distinct().ToArray();
        var envelope = boxes.Length == 0 ? default : boxes.Aggregate((left, right) => left.Union(right));
        var result = new TranslatedBlock(ids[0], string.Join("\n", sources.Select(source => source.Text)), translation.GetString()!, envelope)
            { TextBounds = boxes, SourceIds = ids };
        cursor += ids.Length;
        return result;
    }
}
