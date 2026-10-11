using ScreenTranslator.Core.Models;
using System.Text.Json;

namespace ScreenTranslator.Core.Translation;

/// <summary>
/// Reads only the id/translation fields of the top-level blocks array. Incomplete
/// translation strings are visible; unknown and ambiguous IDs never reach a DOM node.
/// </summary>
public static class PageTextStreaming
{
    public static IReadOnlyList<TranslatedBlock> ReadStreaming(
        string json, IReadOnlyList<OcrBlock> input)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(input);
        var known = input.ToDictionary(block => block.Id, StringComparer.Ordinal);
        if (!TryFindBlocks(json, out var index)) return [];
        var result = new Dictionary<string, TranslatedBlock>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        while (index < json.Length)
        {
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == ',') { index++; continue; }
            if (index >= json.Length || json[index] == ']') break;
            if (json[index] != '{') break;
            var item = ReadBlock(json, ref index);
            if (item.Id is { } id && item.Translation is { } translation
                && !item.Invalid && known.TryGetValue(id, out var source))
            {
                if (result.ContainsKey(id) || duplicates.Contains(id))
                {
                    result.Remove(id);
                    duplicates.Add(id);
                }
                else
                    result.Add(id, new(id, source.Text, translation, default)
                    { IsComplete = item.Closed });
            }
            if (!item.Closed) break;
        }
        return input.Where(block => result.ContainsKey(block.Id)).Select(block => result[block.Id]).ToArray();
    }

    public static IReadOnlyList<TranslatedBlock> ParseComplete(
        string json, IReadOnlyList<OcrBlock> input)
    {
        var translations = new TranslationResponseValidator().Parse(json, input.Select(block => block.Id).ToArray());
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.EnumerateObject().Count(property => property.NameEquals("blocks")) != 1
            || document.RootElement.GetProperty("blocks").EnumerateArray().Any(block =>
                block.EnumerateObject().Count(property => property.NameEquals("id")) != 1
                || block.EnumerateObject().Count(property => property.NameEquals("translation")) != 1))
            throw new TranslationFormatException("网页翻译字段重复，已保留原文。");
        if (translations.Values.Any(string.IsNullOrWhiteSpace))
            throw new TranslationFormatException("网页翻译存在空白段落，已保留原文。");
        return input.Select(block => new TranslatedBlock(block.Id, block.Text, translations[block.Id], default)).ToArray();
    }

    private static bool TryFindBlocks(string json, out int index)
    {
        index = 0;
        SkipSpace(json, ref index);
        if (index >= json.Length || json[index++] != '{') return false;
        while (index < json.Length)
        {
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == ',') { index++; continue; }
            if (index >= json.Length || json[index] != '"') return false;
            var key = StreamingTranslationParser.ReadString(json, ref index, out var complete);
            if (!complete) return false;
            SkipSpace(json, ref index);
            if (index >= json.Length || json[index++] != ':') return false;
            SkipSpace(json, ref index);
            if (key == "blocks")
            {
                if (index >= json.Length || json[index++] != '[') return false;
                return true;
            }
            if (!SkipValue(json, ref index)) return false;
        }
        return false;
    }

    private static PartialBlock ReadBlock(string json, ref int index)
    {
        index++;
        string? id = null, translation = null;
        var seenId = false;
        var seenTranslation = false;
        var invalid = false;
        while (index < json.Length)
        {
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == ',') { index++; continue; }
            if (index < json.Length && json[index] == '}')
            { index++; return new(id, translation, true, invalid); }
            if (index >= json.Length || json[index] != '"') break;
            var key = StreamingTranslationParser.ReadString(json, ref index, out var complete);
            if (!complete) break;
            SkipSpace(json, ref index);
            if (index >= json.Length || json[index++] != ':') break;
            SkipSpace(json, ref index);
            if (key is "id" or "translation")
            {
                if (key == "id") { invalid |= seenId; seenId = true; }
                else { invalid |= seenTranslation; seenTranslation = true; }
                if (index >= json.Length || json[index] != '"') { invalid = true; break; }
                var value = StreamingTranslationParser.ReadString(json, ref index, out complete);
                if (key == "id") { if (complete) id = value; }
                else translation = value;
                if (!complete) break;
            }
            else if (!SkipValue(json, ref index)) break;
        }
        return new(id, translation, false, invalid);
    }

    private static bool SkipValue(string json, ref int index)
    {
        if (index >= json.Length) return false;
        if (json[index] == '"')
        {
            StreamingTranslationParser.ReadString(json, ref index, out var complete);
            return complete;
        }
        if (json[index] is '{' or '[')
        {
            var depth = 0;
            while (index < json.Length)
            {
                var value = json[index];
                if (value == '"')
                {
                    StreamingTranslationParser.ReadString(json, ref index, out var complete);
                    if (!complete) return false;
                    continue;
                }
                index++;
                if (value is '{' or '[') depth++;
                else if (value is '}' or ']') { if (--depth == 0) return true; }
            }
            return false;
        }
        var start = index;
        while (index < json.Length && json[index] is not ',' and not '}' and not ']'
            && !char.IsWhiteSpace(json[index])) index++;
        return index > start && index < json.Length;
    }

    private static void SkipSpace(string value, ref int index)
    { while (index < value.Length && char.IsWhiteSpace(value[index])) index++; }

    private sealed record PartialBlock(string? Id, string? Translation, bool Closed, bool Invalid);
}
