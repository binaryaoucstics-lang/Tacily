using System.Diagnostics;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

public enum BenchmarkRateKind { Unavailable, ContentGeneration, OverallOutput }

public sealed record TranslationBenchmarkResult(string Source, string Translation, TimeSpan Elapsed,
    TimeSpan? FirstCharacterLatency, double? TokensPerSecond, int? OutputTokens, int? ReasoningTokens, BenchmarkRateKind RateKind)
{
    public string? RequestedServiceTier { get; init; }
    public string? EffectiveServiceTier { get; init; }
}

/// <summary>One real streaming translation, no format retries, cached answers or character-to-token estimates.</summary>
public static class TranslationBenchmark
{
    public const string SourceSentence = "When you open a new page, this translator quickly identifies the text, keeps the original layout, and smoothly displays the Chinese translation as each part arrives.";
    public const string BlockId = "benchmark";

    public static async Task<TranslationBenchmarkResult> RunAsync(ITranslationProvider provider, ThinkingLevel thinking,
        CancellationToken token, IProgress<TranslationStreamUpdate>? progress = null)
    {
        if (provider is not IStreamingTranslationProvider streaming)
            throw new TranslationConfigurationException("当前接口不支持流式翻译，无法测量首字延迟。");
        DeepSeekRequestMetrics? deepMetrics = null; ApiTranslationRequestMetrics? apiMetrics = null;
        void ObserveDeep(DeepSeekRequestMetrics metrics) => deepMetrics = metrics;
        void ObserveApi(ApiTranslationRequestMetrics metrics) => apiMetrics = metrics;
        if (provider is DeepSeekTranslationProvider deep) deep.RequestCompleted += ObserveDeep;
        if (provider is ApiTranslationProvider api) api.RequestCompleted += ObserveApi;
        var clock = Stopwatch.StartNew(); TimeSpan? first = null;
        var request = new TranslationRequest("en", "zh-CN", TranslationStyle.Natural, "",
            [new OcrBlock(BlockId, SourceSentence, 1, new PixelRect(0, 0, 1, 1), 0)])
            { ThinkingOverride = thinking, RequestUsageMetrics = true };
        try
        {
            var raw = await streaming.TranslateRawStreamingAsync(request, new InlineProgress(update =>
            {
                if (!string.IsNullOrWhiteSpace(update.Translation)) first ??= clock.Elapsed;
                progress?.Report(update);
            }), token).ConfigureAwait(false);
            var translation = new TranslationResponseValidator().Parse(raw, [BlockId])[BlockId];
            if (string.IsNullOrWhiteSpace(translation) || translation.Trim() == SourceSentence)
                throw new TranslationFormatException("测试句未返回有效译文。");
            clock.Stop();
            var tokens = deepMetrics?.CompletionTokens ?? apiMetrics?.OutputTokens;
            var reasoning = deepMetrics?.ReasoningTokens ?? apiMetrics?.ReasoningTokens;
            var start = deepMetrics?.FirstContentMilliseconds ?? apiMetrics?.FirstContentMilliseconds;
            var last = deepMetrics?.LastContentMilliseconds ?? apiMetrics?.LastContentMilliseconds;
            // Unknown server reasoning accounting is not assumed to be zero.
            var noReasoning = deepMetrics is { ReasoningCharacters: 0, RequestedThinking: ThinkingLevel.None }
                && provider is DeepSeekTranslationProvider { IsGenericChatCompletions: false }
                || apiMetrics?.EffectiveThinkingMode is "disabled" or "unsupported" or "between_tools"
                || apiMetrics is { EffectiveThinkingMode: "reasoning", Effort: "none" };
            var rate = CalculateRate(tokens, reasoning, noReasoning, start, last, clock.Elapsed);
            return new(SourceSentence, translation, clock.Elapsed, first, rate.Tps, tokens, reasoning, rate.Kind)
            { RequestedServiceTier = apiMetrics?.RequestedServiceTier, EffectiveServiceTier = apiMetrics?.EffectiveServiceTier };
        }
        finally
        {
            if (provider is DeepSeekTranslationProvider completedDeep) completedDeep.RequestCompleted -= ObserveDeep;
            if (provider is ApiTranslationProvider completedApi) completedApi.RequestCompleted -= ObserveApi;
        }
    }

    public static (double? Tps, BenchmarkRateKind Kind) CalculateRate(int? output, int? reasoning, bool noReasoning,
        long? firstContent, long? lastContent, TimeSpan total)
    {
        if (output is not > 0) return (null, BenchmarkRateKind.Unavailable);
        if (reasoning > output) return (null, BenchmarkRateKind.Unavailable);
        if (reasoning is >= 0 && reasoning <= output || noReasoning)
        {
            if (firstContent is not { } first || lastContent is not { } last || last <= first) return (null, BenchmarkRateKind.Unavailable);
            var content = output.Value - (reasoning ?? 0);
            return content > 0 ? (content * 1000d / (last - first), BenchmarkRateKind.ContentGeneration) : (null, BenchmarkRateKind.Unavailable);
        }
        return total.TotalSeconds > 0 ? (output.Value / total.TotalSeconds, BenchmarkRateKind.OverallOutput) : (null, BenchmarkRateKind.Unavailable);
    }
    private sealed class InlineProgress(Action<TranslationStreamUpdate> report) : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) => report(value); }
}
