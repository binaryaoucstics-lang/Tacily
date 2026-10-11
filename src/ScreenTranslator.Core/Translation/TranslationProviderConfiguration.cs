using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using System.Globalization;
using ScreenTranslator.Core.Authentication;

namespace ScreenTranslator.Core.Translation;

public enum TranslationProviderKind { DeepSeek, OpenAI, Claude, ChatCompletions }

/// <summary>Capabilities verified against official documentation on 2026-10-11.</summary>
public static class TranslationProviderConfiguration
{
    public const string OpenAiApiKeyName = "openai-api-key";
    public const string ClaudeApiKeyName = "claude-api-key";
    public const string DefaultOpenAiModel = "gpt-6-luna";
    public const string DefaultClaudeModel = "claude-sonnet-5-5";
    public const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    public const string DefaultClaudeBaseUrl = "https://api.anthropic.com/v1";
    // Fast is the mode name; OAuth requests use Codex's wire value for compatibility.
    // https://learn.chatgpt.com/docs/config-file/config-reference (service_tier)
    public const string ChatGptFastServiceTier = "priority";

    public static IReadOnlyList<string> Models(TranslationProviderKind provider) => provider switch
    {
        TranslationProviderKind.OpenAI => ["gpt-6-luna", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.5", "gpt-5.4", "gpt-4.1", "gpt-4.1-mini"],
        TranslationProviderKind.Claude => ["claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-4-6", "claude-sonnet-4-6"],
        TranslationProviderKind.ChatCompletions => [],
        _ => ["deepseek-flash"],
    };

    public static string Label(TranslationProviderKind provider) => provider switch
    { TranslationProviderKind.OpenAI => "OpenAI", TranslationProviderKind.Claude => "Claude", TranslationProviderKind.ChatCompletions => "OpenAI 兼容", _ => "DeepSeek" };

    public static TranslationProviderPreset? ActivePreset(AppSettings settings) =>
        settings.TranslationProviderPresets?.FirstOrDefault(p => p.Id == settings.SelectedTranslationPresetId);

    public static TranslationProviderKind Provider(AppSettings settings) => ActivePreset(settings)?.Provider ?? settings.TranslationProvider;
    public static bool UsesChatGptPlan(AppSettings settings) => Provider(settings) == TranslationProviderKind.OpenAI
        && (ActivePreset(settings)?.AuthenticationMode ?? settings.OpenAiAuthenticationMode) == TranslationAuthenticationMode.ChatGptOAuth;
    public static string ChatGptAccountId(AppSettings settings) => ActivePreset(settings)?.ChatGptAccountId ?? settings.ChatGptAccountId;
    public static bool ChatGptFastModeEnabled(AppSettings settings) => UsesChatGptPlan(settings)
        && (ActivePreset(settings)?.ChatGptFastModeEnabled ?? settings.ChatGptFastModeEnabled);
    public static ApiNetworkOptions Network(AppSettings settings) => ActivePreset(settings)?.Network ?? settings.ApiNetwork ?? new();
    public static string SecretName(AppSettings settings) => ActivePreset(settings) is { } preset
        ? PresetSecretName(preset.Id) : SecretName(settings.TranslationProvider);
    public static string PresetSecretName(string id) => Guid.TryParseExact(id, "N", out _)
        ? "translation-preset-" + id : throw new TranslationConfigurationException("预设标识无效。");

    public static string SecretName(TranslationProviderKind provider) => provider switch
    {
        TranslationProviderKind.DeepSeek => DeepSeekTranslationProvider.ApiKeyName,
        TranslationProviderKind.OpenAI => OpenAiApiKeyName,
        TranslationProviderKind.Claude => ClaudeApiKeyName,
        TranslationProviderKind.ChatCompletions => "chat-completions-api-key",
        _ => throw new TranslationConfigurationException("不支持的翻译服务商。"),
    };

    public static string Model(AppSettings settings) => ActivePreset(settings)?.Model ?? (settings.TranslationProvider switch
    { TranslationProviderKind.OpenAI => settings.OpenAiModel, TranslationProviderKind.Claude => settings.ClaudeModel,
        TranslationProviderKind.ChatCompletions => settings.ChatCompletionsModel, _ => settings.DeepSeekModel });

    public static string BaseUrl(AppSettings settings) => UsesChatGptPlan(settings) ? DefaultOpenAiBaseUrl : ActivePreset(settings)?.BaseUrl ?? (settings.TranslationProvider switch
    { TranslationProviderKind.OpenAI => settings.OpenAiBaseUrl, TranslationProviderKind.Claude => settings.ClaudeBaseUrl,
        TranslationProviderKind.ChatCompletions => settings.ChatCompletionsBaseUrl, _ => settings.DeepSeekBaseUrl });

    public static Uri Endpoint(TranslationProviderKind provider, string baseUrl)
    {
        if (!Enum.IsDefined(provider) || !Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new TranslationConfigurationException("接口地址须为 HTTPS；本机服务可使用 HTTP，地址不能包含密钥、查询参数或片段。");
        if (provider == TranslationProviderKind.DeepSeek)
        {
            if (uri.Host != "api.deepseek.com" || uri.Scheme != "https")
                throw new TranslationConfigurationException("DeepSeek 使用官方接口 https://api.deepseek.com。");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        }
        var suffix = provider switch { TranslationProviderKind.OpenAI => "/responses", TranslationProviderKind.ChatCompletions => "/chat/completions", _ => "/messages" };
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(suffix, StringComparison.Ordinal)) return new Uri(uri.AbsoluteUri.TrimEnd('/'));
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/responses", StringComparison.Ordinal)
            || path.EndsWith("/messages", StringComparison.Ordinal))
            throw new TranslationConfigurationException("接口地址与所选服务商格式不一致，请填写对应的 Base URL。");
        // Preserve proxy path prefixes. Official bare origins acquire /v1; explicit prefixes are retained.
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + (path.Length == 0 ? "/v1" : "") + suffix);
    }

    public static ApiThinkingConfiguration Thinking(TranslationProviderKind provider, string model, ThinkingLevel requested,
        bool useChatGptPlan = false, IReadOnlyList<ThinkingLevel>? supportedLevels = null)
    {
        if (!Enum.IsDefined(requested)) throw new TranslationConfigurationException("思考等级无效。");
        if (!Enum.IsDefined(provider)) throw new TranslationConfigurationException("不支持的翻译服务商。");
        if (provider == TranslationProviderKind.ChatCompletions) return new("default", null, requested);
        if (provider == TranslationProviderKind.OpenAI)
        {
            // Only OAuth account metadata overrides the documented public API capabilities.
            // An explicit empty snapshot must not reintroduce fallback parameters.
            var metadata = useChatGptPlan && supportedLevels is not null;
            var levels = metadata ? supportedLevels!.Where(level => Enum.IsDefined(level)).Distinct().ToArray()
                : TranslationThinkingCatalog.SupportedLevels(provider, model, useChatGptPlan);
            if (levels.Count == 0) return new("default", null, requested);
            if (!metadata && levels.Count == 1 && levels[0] == ThinkingLevel.None)
                return new("unsupported", null, ThinkingLevel.None);
            var effective = TranslationThinkingCatalog.Normalize(requested, levels);
            return new("reasoning", ThinkingLevelNames.Effort(effective), effective);
        }
        var originalRequested = requested;
        requested = TranslationThinkingCatalog.Normalize(requested, TranslationThinkingCatalog.SupportedLevels(provider, model));
        var effort = ThinkingLevelNames.Effort(requested);
        if (provider == TranslationProviderKind.DeepSeek)
            return new(requested == ThinkingLevel.None ? "disabled" : "enabled", effort, requested);
        if (Matches(model, "claude-opus-5-5") || Matches(model, "claude-fable-5-1") || Matches(model, "claude-fable-5")
            || Matches(model, "claude-mythos-5-1") || Matches(model, "claude-mythos-5") || model == "claude-mythos-preview")
            return new("adaptive", requested == ThinkingLevel.None ? "low" : effort,
                requested == ThinkingLevel.None ? ThinkingLevel.Low : requested);
        if (Matches(model, "claude-sonnet-5-5"))
            return new(requested == ThinkingLevel.None ? "between_tools" : "adaptive",
                requested == ThinkingLevel.None ? "low" : effort, requested);
        if (Matches(model, "claude-haiku-5-5") || Matches(model, "claude-opus-5") || Matches(model, "claude-sonnet-5")
            || Matches(model, "claude-opus-4-8") || Matches(model, "claude-opus-4-7")
            || Matches(model, "claude-opus-4-6") || Matches(model, "claude-sonnet-4-6"))
            return new(requested == ThinkingLevel.None ? "disabled" : "adaptive", requested == ThinkingLevel.None ? "low" : effort, requested);
        return new("default", null, originalRequested);
    }

    private static bool Matches(string model, string known) => model == known
        || model.StartsWith(known + "-", StringComparison.Ordinal)
            && DateOnly.TryParseExact(model.AsSpan(known.Length + 1), ["yyyyMMdd", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static string ThinkingDescription(TranslationProviderKind provider, string model, ThinkingLevel requested)
    {
        var config = Thinking(provider, model, requested);
        if (config.Mode == "default") return "自定义模型使用服务端默认思考配置；未核实的模型不发送额外思考参数。";
        if (config.Mode == "unsupported") return "该模型不支持独立思考参数。";
        if (requested == ThinkingLevel.None && config.EffectiveLevel != ThinkingLevel.None)
            return "该模型无法关闭思考：实际以低等级运行。网页翻译和并行定位也使用最低可用等级。";
        if (config.Mode == "between_tools") return "当前不启用前置思考（between_tools）；翻译请求不使用工具。";
        return provider == TranslationProviderKind.Claude
            ? "使用 adaptive thinking 与 output_config.effort；等级控制强度，并非思考 token 的硬上限。"
            : provider == TranslationProviderKind.OpenAI ? $"使用 Responses reasoning.effort={config.Effort}；等级控制强度，并非思考 token 的硬上限。"
            : "低等级控制推理强度，并非思考 token 的硬上限。";
    }

    public static string TransportIdentity(AppSettings settings)
    {
        var identity = string.Join("\n", Provider(settings), Model(settings), BaseUrl(settings));
        if (UsesChatGptPlan(settings)) identity += "\nchatgpt:" + ChatGptAccountId(settings);
        return ActivePreset(settings) is { } preset ? identity + "\npreset:" + preset.Id : identity;
    }

    // Network changes reconfigure live jobs, but do not invalidate translated content.
    public static string ConnectionIdentity(AppSettings settings) => TransportIdentity(settings) + "\nnetwork:" + Network(settings).Identity
        + (ChatGptFastModeEnabled(settings) ? "\nservice_tier:" + ChatGptFastServiceTier : "");
}

public sealed record ApiThinkingConfiguration(string Mode, string? Effort, ThinkingLevel EffectiveLevel);

public sealed record TranslationApiOptions
{
    public required TranslationProviderKind Provider { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public string? SecretName { get; init; }
    public ThinkingLevel ThinkingLevel { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public bool UseBriefTranslationGuidance { get; init; } = true;
    public bool UseChatGptPlan { get; init; }
    public bool ChatGptFastModeEnabled { get; init; }
    public string ChatGptAccountId { get; init; } = "";
    public IChatGptTokenProvider? ChatGptTokens { get; init; }
}

public static class TranslationProviderFactory
{
    public static ITranslationProvider Create(HttpClient http, ISecretStore secrets, AppSettings settings,
        ThinkingLevel? thinkingOverride = null, TimeSpan? timeout = null, bool allowLowWaitRetry = false,
        IChatGptTokenProvider? chatGptTokens = null)
    {
        var kind = TranslationProviderConfiguration.Provider(settings);
        var thinking = thinkingOverride ?? TranslationProviderConfiguration.ActivePreset(settings)?.ThinkingLevel ?? settings.ThinkingLevel;
        var model = TranslationProviderConfiguration.Model(settings);
        var useChatGptPlan = TranslationProviderConfiguration.UsesChatGptPlan(settings);
        var effective = TranslationProviderConfiguration.Thinking(kind, model, thinking, useChatGptPlan,
            useChatGptPlan ? chatGptTokens?.GetSupportedThinkingLevels(TranslationProviderConfiguration.ChatGptAccountId(settings), model) : null).EffectiveLevel;
        if (kind == TranslationProviderKind.DeepSeek) thinking = effective;
        var duration = timeout ?? TimeSpan.FromSeconds(effective switch
        { ThinkingLevel.None => kind == TranslationProviderKind.DeepSeek ? 45 : 60,
            ThinkingLevel.Minimal => 60, ThinkingLevel.Low => 90, ThinkingLevel.Medium => 120,
            ThinkingLevel.High => 180, ThinkingLevel.XHigh => 240, _ => 300 });
        var endpoint = TranslationProviderConfiguration.Endpoint(kind, TranslationProviderConfiguration.BaseUrl(settings));
        if (kind is TranslationProviderKind.DeepSeek or TranslationProviderKind.ChatCompletions)
            return new DeepSeekTranslationProvider(http, secrets, new DeepSeekOptions
            {
                BaseUri = new Uri(TranslationProviderConfiguration.BaseUrl(settings)), Endpoint = endpoint,
                Model = TranslationProviderConfiguration.Model(settings), ThinkingLevel = thinking,
                SecretName = TranslationProviderConfiguration.SecretName(settings),
                GenericChatCompletions = kind == TranslationProviderKind.ChatCompletions,
                UseBriefTranslationGuidance = settings.BriefThinkingGuidanceEnabled, Timeout = duration,
                FirstContentTimeout = kind == TranslationProviderKind.DeepSeek && allowLowWaitRetry && settings.BoundedLowThinkingEnabled && thinking == ThinkingLevel.Low
                    ? TimeSpan.FromSeconds(12) : null,
            });
        var options = new TranslationApiOptions
        {
            Provider = kind, Model = TranslationProviderConfiguration.Model(settings), SecretName = TranslationProviderConfiguration.SecretName(settings),
            BaseUrl = TranslationProviderConfiguration.BaseUrl(settings), ThinkingLevel = thinking, Timeout = duration,
            UseBriefTranslationGuidance = settings.BriefThinkingGuidanceEnabled,
            UseChatGptPlan = TranslationProviderConfiguration.UsesChatGptPlan(settings),
            ChatGptFastModeEnabled = TranslationProviderConfiguration.ChatGptFastModeEnabled(settings),
            ChatGptAccountId = TranslationProviderConfiguration.ChatGptAccountId(settings), ChatGptTokens = chatGptTokens,
        };
        return kind == TranslationProviderKind.OpenAI
            ? new OpenAiResponsesTranslationProvider(http, secrets, options)
            : new ClaudeMessagesTranslationProvider(http, secrets, options);
    }
}
