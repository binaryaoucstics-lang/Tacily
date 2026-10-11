using ScreenTranslator.Core.Models;
using System.Text.Json;

namespace ScreenTranslator.Core.Translation;

public static class ImageTranslationStreamParser
{
    public static IReadOnlyList<TranslatedBlock> ReadCompletedOcrBlocks(string json, TranslationRequest request)
    {
        return OcrTranslationGroups.ReadCompleted(ReadObjects(json), request);
    }

    public static string ReadTranslationTail(string json)
    {
        var complete = ReadObjects(json);
        var start = complete.Count == 0 ? 0 : json.LastIndexOf(complete[^1], StringComparison.Ordinal) + complete[^1].Length;
        return StreamingTranslationParser.ReadTranslation(json[start..]);
    }

    public static IReadOnlyList<TranslatedBlock> ReadCompletedAtlasBlocks(string json, IReadOnlyList<ImageAtlasRegion> regions)
    {
        var result = new List<TranslatedBlock>();
        var mapping = regions.ToDictionary(region => region.Id);
        foreach (var block in ReadObjects(json))
        {
            try
            {
                using var document = JsonDocument.Parse(block);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !mapping.TryGetValue(id.GetString()!, out var region)
                    || !root.TryGetProperty("sourceText", out var source) || source.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(source.GetString()) || string.IsNullOrWhiteSpace(translation.GetString())) continue;
                if (result.All(item => item.Id != region.Id))
                    result.Add(new TranslatedBlock(region.Id, source.GetString()!, translation.GetString()!, region.SourceBounds) { TextBounds = region.TextBounds });
            }
            catch (JsonException) { }
        }
        return regions.Select(region => result.FirstOrDefault(block => block.Id == region.Id)).OfType<TranslatedBlock>().ToArray();
    }

    public static IReadOnlyList<TranslatedBlock> ReadCompletedBlocks(string json, PixelSize size)
    {
        var result = new List<TranslatedBlock>();
        foreach (var block in ReadObjects(json))
        {
            try { result.AddRange(new TranslationResponseValidator().ParseImageBlocks("{\"blocks\":[" + block + "]}", size).Blocks); }
            catch (TranslationFormatException) { }
        }
        return result;
    }

    internal static IReadOnlyList<string> ReadObjects(string json)
    {
        var key = json.IndexOf("\"blocks\"", StringComparison.Ordinal);
        if (key < 0) return [];
        var start = json.IndexOf('[', key + 8);
        if (start < 0) return [];
        var result = new List<string>();
        var depth = 0;
        var objectStart = -1;
        var inString = false;
        var escaped = false;
        for (var index = start + 1; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') { inString = true; continue; }
            if (character == '{') { if (depth++ == 0) objectStart = index; }
            else if (character == '}' && depth > 0 && --depth == 0)
            {
                result.Add(json[objectStart..(index + 1)]);
            }
            else if (character == ']' && depth == 0) break;
        }
        return result;
    }
}
