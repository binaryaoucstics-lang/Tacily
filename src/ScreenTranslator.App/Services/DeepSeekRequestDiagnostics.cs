using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Abstractions;

namespace ScreenTranslator.App.Services;

internal static class DeepSeekRequestDiagnostics
{
    internal static void Observe(ITranslationProvider provider, string role)
    {
        if (provider is DeepSeekTranslationProvider deepSeek) Observe(deepSeek, role);
        else if (provider is ApiTranslationProvider api) api.RequestCompleted += metrics =>
            AppDiagnostics.Write($"translation_api_request provider={metrics.Provider} role={role} requested_thinking={metrics.RequestedThinking} effective_thinking={metrics.EffectiveThinkingMode} effort={metrics.Effort} budget={metrics.MaxTokens} first_content_ms={metrics.FirstContentMilliseconds} input_tokens={metrics.InputTokens} output_tokens={metrics.OutputTokens} elapsed_ms={metrics.ElapsedMilliseconds} outcome={metrics.Outcome}");
    }

    internal static void Observe(DeepSeekTranslationProvider provider, string role) => provider.RequestCompleted += metrics =>
        AppDiagnostics.Write($"deepseek_request role={role} thinking={metrics.RequestedThinking} effort={metrics.ReasoningEffort} brief={metrics.BriefGuidance} budget={metrics.MaxTokens} first_reasoning_ms={metrics.FirstReasoningMilliseconds} first_content_ms={metrics.FirstContentMilliseconds} reasoning_tokens={metrics.ReasoningTokens} content_tokens={metrics.ContentTokens} elapsed_ms={metrics.ElapsedMilliseconds} outcome={metrics.Outcome}");
}
