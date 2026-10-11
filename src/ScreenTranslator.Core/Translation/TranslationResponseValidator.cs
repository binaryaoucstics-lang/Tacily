using System.Text.Json;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public sealed class TranslationResponseValidator
{
    public TranslationResult ParseAtlas(string json, IReadOnlyList<ImageAtlasRegion> regions)
    {
        var translations = Parse(json, regions.Select(region => region.Id).ToArray());
        using var document = JsonDocument.Parse(json);
        var sources = new Dictionary<string, string>();
        foreach (var block in document.RootElement.GetProperty("blocks").EnumerateArray())
        {
            if (!block.TryGetProperty("sourceText", out var source) || source.ValueKind != JsonValueKind.String)
                throw new TranslationFormatException("拼图翻译缺少原文。");
            sources.Add(block.GetProperty("id").GetString()!, source.GetString()!);
        }
        var result = new List<TranslatedBlock>();
        foreach (var region in regions)
        {
            var source = sources[region.Id]; var translation = translations[region.Id];
            if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(translation)) continue;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation)) throw new TranslationFormatException("拼图分段字段不完整。");
            result.Add(new TranslatedBlock(region.Id, source, translation, region.SourceBounds) { TextBounds = region.TextBounds });
        }
        if (result.Count == 0) throw new TranslationNoTextException("检测到的区域没有可识读文字，请重新选择。");
        return new TranslationResult(result);
    }

    public TranslationResult ParseImageBlocks(string json, PixelSize imageSize)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new TranslationFormatException("图片翻译缺少分段结果。");
            var result = new List<TranslatedBlock>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!)
                    || !block.TryGetProperty("sourceText", out var source) || source.ValueKind != JsonValueKind.String
                    || !block.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.String
                    || !block.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Array || bbox.GetArrayLength() != 4)
                    throw new TranslationFormatException("图片分段缺少原文、译文或位置。");
                var coordinates = bbox.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : double.NaN).ToArray();
                if (coordinates.Any(value => !double.IsFinite(value) || value < 0 || value > 1000)
                    || coordinates[2] <= coordinates[0] || coordinates[3] <= coordinates[1])
                    throw new TranslationFormatException("图片文字位置无效。");
                if (string.IsNullOrWhiteSpace(source.GetString()) || string.IsNullOrWhiteSpace(translation.GetString()))
                    throw new TranslationFormatException("图片分段文字为空。");
                var left = (int)Math.Floor(coordinates[0] * imageSize.Width / 1000);
                var top = (int)Math.Floor(coordinates[1] * imageSize.Height / 1000);
                var right = (int)Math.Ceiling(coordinates[2] * imageSize.Width / 1000);
                var bottom = (int)Math.Ceiling(coordinates[3] * imageSize.Height / 1000);
                result.Add(new TranslatedBlock(id.GetString()!, source.GetString()!, translation.GetString()!,
                    new PixelRect(left, top, right - left, bottom - top)));
            }
            if (result.Count == 0) throw new TranslationNoTextException("图片内没有可读取的文字，请放大图片或重新选择。");
            return new TranslationResult(result);
        }
        catch (JsonException exception) { throw new TranslationFormatException("图片翻译不是有效 JSON。", exception); }
        catch (InvalidOperationException exception) { throw new TranslationFormatException("图片翻译结构无效。", exception); }
    }

    public IReadOnlyDictionary<string, string> ParseSources(string json, IReadOnlyCollection<string> expectedIds)
    {
        _ = Parse(json, expectedIds);
        using var document = JsonDocument.Parse(json);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in document.RootElement.GetProperty("blocks").EnumerateArray())
        {
            if (!block.TryGetProperty("sourceText", out var source) || source.ValueKind != JsonValueKind.String)
                throw new TranslationFormatException("视觉翻译缺少识读出的原文，请重试。");
            var text = source.GetString()!;
            var translation = block.GetProperty("translation").GetString();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
                throw new TranslationNoTextException("选区内没有可读取的文字，请拖框重新选择或放大原内容。");
            sources.Add(block.GetProperty("id").GetString()!, text);
        }
        return sources;
    }

    public IReadOnlyDictionary<string, string> Parse(
        string json,
        IReadOnlyCollection<string> expectedIds)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationFormatException("DeepSeek 返回了空 JSON。");
        }

        ArgumentNullException.ThrowIfNull(expectedIds);

        HashSet<string> expected;
        try
        {
            expected = expectedIds.ToHashSet(StringComparer.Ordinal);
        }
        catch (ArgumentNullException exception)
        {
            throw new ArgumentException("Expected block IDs cannot contain null.", nameof(expectedIds), exception);
        }

        if (expected.Count != expectedIds.Count || expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Expected block IDs must be non-empty and unique.", nameof(expectedIds));
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("blocks", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array)
            {
                throw new TranslationFormatException("DeepSeek 返回的 JSON 缺少 blocks 数组。");
            }

            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !block.TryGetProperty("translation", out var translationElement)
                    || translationElement.ValueKind != JsonValueKind.String)
                {
                    throw new TranslationFormatException("DeepSeek 返回了无效的文本块结构。");
                }

                var id = idElement.GetString();
                var translation = translationElement.GetString();
                if (string.IsNullOrWhiteSpace(id) || translation is null)
                {
                    throw new TranslationFormatException("DeepSeek 返回的文本块字段为空。");
                }

                if (!translations.TryAdd(id, translation))
                {
                    throw new TranslationFormatException($"DeepSeek 返回了重复 ID：{id}");
                }
            }

            if (!translations.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            {
                throw new TranslationFormatException("DeepSeek 返回的文本块与 OCR 文本块不一致。");
            }

            return translations;
        }
        catch (TranslationFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new TranslationFormatException("DeepSeek 返回的内容不是有效 JSON。", exception);
        }
    }
}
