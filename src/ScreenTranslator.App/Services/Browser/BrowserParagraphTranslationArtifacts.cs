using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>
/// Privacy-preserving verified source-range translations. Source hashes prove a
/// safe old-fine -> new-coarse DOM rebind; target text is never guessed into finer
/// source ranges. Neither original source nor paragraph context is serialized.
/// </summary>
internal static class BrowserParagraphTranslationArtifacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal const int MaximumRanges = 512;

    internal sealed record Range(int Start, int Length, string SourceHash, string Translation);
    internal sealed record Artifact(int Version, string GroupHash, int GroupLength, IReadOnlyList<Range> Ranges);

    internal static bool HasIdentity(BrowserPageTextBlock block) => block.SourceGroupHash is { Length: 64 }
        && block.SourceGroupLength is > 0 and <= 1_000_000 && block.SourceStart is >= 0
        && block.SourceLength == block.Text.Length && (long)block.SourceStart + block.SourceLength <= block.SourceGroupLength;

    internal static string? Key(BrowserPageTextBlock block, BrowserPageTranslationOptions options, string context, bool selectionOverridesPolicy)
    {
        if (!HasIdentity(block)) return null;
        return Hash(JsonSerializer.Serialize(new
        {
            contract = "verified-paragraph-source-ranges-v1", source = block.SourceGroupHash!.ToUpperInvariant(),
            length = block.SourceGroupLength, options.SourceLanguage, options.TargetLanguage,
            options.Style, userContext = options.Context, options.CacheScope, selectionOverridesPolicy,
            sourceContextIdentity = context,
        }, Json));
    }

    internal static Artifact? Read(string text, BrowserPageTextBlock block)
    {
        if (text.Length > 131_072 || !HasIdentity(block)) return null;
        try
        {
            var artifact = JsonSerializer.Deserialize<Artifact>(text, Json);
            if (artifact is null || artifact.Version != 1 || artifact.GroupLength != block.SourceGroupLength
                || !string.Equals(artifact.GroupHash, block.SourceGroupHash, StringComparison.OrdinalIgnoreCase)
                || artifact.Ranges is null || artifact.Ranges.Count > MaximumRanges
                || artifact.Ranges.Any(range => range is null || range.Start < 0 || range.Length <= 0
                    || (long)range.Start + range.Length > artifact.GroupLength
                    || range.SourceHash is not { Length: 64 } || !range.SourceHash.All(Uri.IsHexDigit)
                    || string.IsNullOrWhiteSpace(range.Translation) || range.Translation.Length > 6_000)
                || artifact.Ranges.Select(range => (range.Start, range.Length)).Distinct().Count() != artifact.Ranges.Count) return null;
            return artifact;
        }
        catch (JsonException) { return null; }
    }

    internal static string? Replay(Artifact artifact, BrowserPageTextBlock block, string targetLanguage, bool foreignHanContext, bool explicitSelection)
    {
        var start = block.SourceStart!.Value; var end = start + block.Text.Length;
        var exact = artifact.Ranges.FirstOrDefault(range => range.Start == start && range.Length == block.Text.Length
            && range.SourceHash == Hash(block.Text));
        if (exact is not null) return exact.Translation;
        var candidates = artifact.Ranges.Where(range => range.Start >= start && range.Start + range.Length <= end)
            .OrderBy(range => range.Start).ThenByDescending(range => range.Length).ToArray();
        var cursor = start; var output = new StringBuilder(); var used = 0;
        foreach (var range in candidates)
        {
            if (range.Start < cursor) continue;
            var gap = block.Text.Substring(cursor - start, range.Start - cursor);
            // Native snippets/punctuation/whitespace are copied from this request.
            // Any unknown foreign gap makes replay incomplete and unsafe.
            if (PageTextEligibility.NeedsTranslation(gap, targetLanguage, foreignHanContext, explicitSelection: explicitSelection)) return null;
            if (range.SourceHash != Hash(block.Text.Substring(range.Start - start, range.Length))) return null;
            output.Append(gap); output.Append(range.Translation); cursor = range.Start + range.Length; used++;
        }
        if (used == 0) return null;
        var tail = block.Text[(cursor - start)..];
        if (PageTextEligibility.NeedsTranslation(tail, targetLanguage, foreignHanContext, explicitSelection: explicitSelection)) return null;
        output.Append(tail);
        var text = output.ToString();
        // Facts must also stay on their newly merged DOM node.
        return PageTextSemanticGuard.FindUnsafeIds([block], new Dictionary<string, string> { [block.Id] = text }).Count == 0
            ? text : null;
    }

    internal static string Merge(Artifact? existing, BrowserPageTextBlock identity, IEnumerable<(BrowserPageTextBlock Block, string Translation)> complete)
    {
        var ranges = (existing?.Ranges ?? []).ToDictionary(range => (range.Start, range.Length), range => range);
        foreach (var (block, translation) in complete)
        {
            if (!HasIdentity(block) || translation.Length > 6_000) continue;
            ranges[(block.SourceStart!.Value, block.Text.Length)] = new(block.SourceStart.Value,
                block.Text.Length, Hash(block.Text), translation);
        }
        var bounded = ranges.Values.OrderBy(range => range.Start).ThenByDescending(range => range.Length)
            .Take(MaximumRanges).ToArray();
        return JsonSerializer.Serialize(new Artifact(1, identity.SourceGroupHash!.ToUpperInvariant(),
            identity.SourceGroupLength!.Value, bounded), Json);
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
