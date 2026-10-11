using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Settings;

/// <summary>Connection metadata only. Keys are stored separately in the OS secret store.</summary>
public sealed record TranslationProviderPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "自定义服务商";
    public TranslationProviderKind Provider { get; init; } = TranslationProviderKind.ChatCompletions;
    public string Model { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    public TranslationAuthenticationMode AuthenticationMode { get; init; }
    public string ChatGptAccountId { get; init; } = "";
    public bool ChatGptFastModeEnabled { get; init; }
    public ThinkingLevel ThinkingLevel { get; init; }
    public bool BoundedLowThinkingEnabled { get; init; } = true;
    public ApiNetworkOptions Network { get; init; } = new();
    public override string ToString() => Name;
}
