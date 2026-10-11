using System.Text.Json;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>Bounded native wire adapter, shared by the controller and protocol tests.</summary>
public static class BrowserPageAutoProtocol
{
    public static bool TryReadCandidate(Guid connection, JsonElement root, out AutoPageCandidate? candidate)
    {
        candidate = null;
        try
        {
            if (root.GetProperty("frameId").GetInt32() != 0) return false;
            var manualMode = OptionalString(root, "manualMode");
            if (manualMode is not (null or "selection" or "page")) return false;
            var blockElements = root.GetProperty("blocks");
            if (blockElements.GetArrayLength() > 48) return false;
            var blocks = new List<BrowserPageTextBlock>(blockElements.GetArrayLength());
            foreach (var blockElement in blockElements.EnumerateArray())
            {
                if (!TryReadBlock(blockElement, out var block)) return false;
                blocks.Add(block!);
            }
            PageTextSample[]? samples = null;
            if (root.TryGetProperty("samples", out var sampleElements))
            {
                if (sampleElements.GetArrayLength() > 64) return false;
                samples = sampleElements.EnumerateArray().Select(sample => new PageTextSample(
                    sample.GetProperty("text").GetString()!, OptionalString(sample, "kind") ?? "body",
                    OptionalString(sample, "languageHint"))).ToArray();
            }
            PageTextStatistics? statistics = null;
            if (root.TryGetProperty("pageSummary", out var counts)) statistics = new(
                Count(counts, "totalCharacters"), Count(counts, "hanCharacters"), Count(counts, "kanaCharacters"),
                Count(counts, "latinCharacters"), Count(counts, "proseBlocks"), Count(counts, "hanProseCharacters"),
                Count(counts, "latinProseCharacters"), Count(counts, "kanaProseCharacters"));
            var value = new AutoPageCandidate(connection, root.GetProperty("requestId").GetString()!,
                root.GetProperty("documentToken").GetString()!, root.GetProperty("tabId").GetInt32(),
                root.GetProperty("browserWindowId").GetInt32(), root.GetProperty("generation").GetInt64(),
                root.GetProperty("revision").GetInt64(), OptionalString(root, "documentLanguage"), blocks, samples, statistics,
                manualMode, OptionalString(root, "siteHost"));
            if (!BrowserPageAutoCoordinator.IsBounded(value)) return false;
            candidate = value; return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or OverflowException) { return false; }
    }

    public static string Serialize(AutoPageCandidate candidate, AutoPagePacket packet) => JsonSerializer.Serialize(new
    {
        type = packet.Type, requestId = candidate.RequestId, documentToken = candidate.DocumentToken,
        tabId = candidate.TabId, browserWindowId = candidate.WindowId,
        generation = candidate.Generation, revision = candidate.Revision, manualMode = candidate.ManualMode,
        needsTranslation = packet.Assessment?.NeedsTranslation,
        allowImages = candidate.ManualMode is null && packet.Assessment is { IsMixedTargetLanguage: false },
        reason = packet.Assessment?.ReasonCode,
        updates = packet.Updates?.Select(update => new
            { id = update.Id, text = update.Translation, complete = update.IsComplete, failed = update.Failed, fromCache = update.FromCache }).ToArray()
            ?? [],
        finished = packet.Finished, error = packet.Error,
    });

    public static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static bool TryReadBlock(JsonElement element, out BrowserPageTextBlock? block)
    {
        block = null;
        var id = element.GetProperty("id").GetString();
        var text = element.GetProperty("text").GetString();
        if (text is null) return false;
        var groupId = OptionalString(element, "groupId");
        var context = OptionalString(element, "context");
        var foreignHanContext = false;
        if (element.TryGetProperty("foreignHanContext", out var foreignHanElement))
        {
            if (foreignHanElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            foreignHanContext = foreignHanElement.GetBoolean();
        }
        string? contextKind = null;
        if (element.TryGetProperty("contextKind", out var contextKindElement))
        {
            if (contextKindElement.ValueKind != JsonValueKind.String) return false;
            contextKind = contextKindElement.GetString();
            if (contextKind is not ("source" or "selection")) return false;
        }
        var hasHash = element.TryGetProperty("sourceGroupHash", out var hashElement);
        var hasGroupLength = element.TryGetProperty("sourceGroupLength", out var groupLengthElement);
        var hasStart = element.TryGetProperty("sourceStart", out var startElement);
        var hasLength = element.TryGetProperty("sourceLength", out var lengthElement);
        if (!hasHash && !hasGroupLength && !hasStart && !hasLength)
        {
            block = new(id!, text, groupId, context, ForeignHanContext: foreignHanContext, ContextKind: contextKind);
            return true;
        }
        if (!hasHash || !hasGroupLength || !hasStart || !hasLength || hashElement.ValueKind != JsonValueKind.String ||
            groupLengthElement.ValueKind != JsonValueKind.Number || startElement.ValueKind != JsonValueKind.Number ||
            lengthElement.ValueKind != JsonValueKind.Number) return false;
        var hash = hashElement.GetString();
        var groupLength = groupLengthElement.GetInt32();
        var start = startElement.GetInt32();
        var length = lengthElement.GetInt32();
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || groupLength is < 0 or > 1_000_000 ||
            start < 0 || length < 0 || length != text.Length || (long)start + length > groupLength) return false;
        block = new(id!, text, groupId, context, hash, groupLength, start, length, foreignHanContext, contextKind);
        return true;
    }

    private static int Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
}
