using System.Globalization;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

/// <summary>Documented API capabilities; OAuth account metadata can override this fallback.</summary>
public static class TranslationThinkingCatalog
{
    public static IReadOnlyList<ThinkingLevel> SupportedLevels(TranslationProviderKind provider, string model, bool useChatGptPlan = false)
    {
        if (provider != TranslationProviderKind.OpenAI) return [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max];
        if (Matches(model, "gpt-6-astra") || Matches(model, "gpt-6.1-sol"))
            return [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max];
        if (Matches(model, "gpt-6-sol") || Matches(model, "gpt-6-luna") || Matches(model, "gpt-5.6-luna")
            || Matches(model, "gpt-5.6-terra") || Matches(model, "gpt-5.6-sol") || model == "gpt-5.6")
            return useChatGptPlan
                ? [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max]
                : [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max];
        if (Matches(model, "gpt-5.5") || Matches(model, "gpt-5.4"))
            return [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh];
        if (Matches(model, "gpt-4.1") || Matches(model, "gpt-4.1-mini") || Matches(model, "gpt-4.1-nano")
            || Matches(model, "gpt-4o") || Matches(model, "gpt-4o-mini")) return [ThinkingLevel.None];
        return [];
    }

    public static ThinkingLevel Normalize(ThinkingLevel requested, IReadOnlyList<ThinkingLevel> supported)
    {
        if (!Enum.IsDefined(requested)) throw new TranslationConfigurationException("思考等级无效。");
        if (supported.Count == 0) return ThinkingLevel.None;
        if (supported.Contains(requested)) return requested;
        ThinkingLevel[] order = [ThinkingLevel.None, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium,
            ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max];
        var position = Array.IndexOf(order, requested);
        return order.Take(position + 1).Reverse().FirstOrDefault(supported.Contains, supported[0]);
    }

    private static bool Matches(string model, string known) => model == known
        || model.StartsWith(known + "-", StringComparison.Ordinal)
            && DateOnly.TryParseExact(model.AsSpan(known.Length + 1), ["yyyyMMdd", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
