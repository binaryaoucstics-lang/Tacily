namespace ScreenTranslator.Core.Translation;

public sealed record DeepSeekOptions
{
    public Uri BaseUri { get; init; } = new("https://api.deepseek.com/");

    public string Model { get; init; } = "deepseek-flash";
    public string SecretName { get; init; } = DeepSeekTranslationProvider.ApiKeyName;
    public Uri? Endpoint { get; init; }
    public bool GenericChatCompletions { get; init; }

    public ScreenTranslator.Core.Models.ThinkingLevel ThinkingLevel { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Optional streaming wait limit before the first answer content; effort is not a latency limit.</summary>
    public TimeSpan? FirstContentTimeout { get; init; }

    /// <summary>A short one-pass instruction, independently switchable for honest A/B measurements.</summary>
    public bool UseBriefTranslationGuidance { get; init; } = true;
}

public enum DeepSeekRequestOutcome
{
    Success, Cancelled, Timeout, FirstContentTimeout, NetworkError, HttpError,
    Truncated, InvalidResponse, Interrupted
}

/// <summary>
/// Safe performance data. Contains no key, source, answer, image or reasoning text.
/// Timestamps are client receipt times, not a measurement of isolated server compute.
/// Missing usage stays null; character counts are not presented as token counts.
/// </summary>
public sealed record DeepSeekRequestMetrics(
    Guid RequestId,
    string RequestedModel,
    ScreenTranslator.Core.Models.ThinkingLevel RequestedThinking,
    string ReasoningEffort,
    int MaxTokens,
    string TaskKind,
    bool Streaming,
    bool BriefGuidance,
    long ElapsedMilliseconds,
    long? ResponseHeadersMilliseconds,
    long? FirstReasoningMilliseconds,
    long? FirstContentMilliseconds,
    long? ReasoningToContentMilliseconds,
    long ReasoningCharacters,
    long ContentCharacters,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    int? ContentTokens,
    string? ReturnedModel,
    string? FinishReason,
    DeepSeekRequestOutcome Outcome)
{
    public long? LastContentMilliseconds { get; init; }
}
