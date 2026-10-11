using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationProviderConfigurationTests
{
    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, "https://api.openai.com", "https://api.openai.com/v1/responses")]
    [InlineData(TranslationProviderKind.OpenAI, "https://api.openai.com/v1/", "https://api.openai.com/v1/responses")]
    [InlineData(TranslationProviderKind.OpenAI, "https://proxy.example/openai/v1", "https://proxy.example/openai/v1/responses")]
    [InlineData(TranslationProviderKind.OpenAI, "https://proxy.example/api/responses", "https://proxy.example/api/responses")]
    [InlineData(TranslationProviderKind.Claude, "https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData(TranslationProviderKind.Claude, "http://127.0.0.1:8795/v1/messages", "http://127.0.0.1:8795/v1/messages")]
    public void Endpoint_Preserves_Prefixes_And_Avoids_Duplicate_Version_Or_Method(TranslationProviderKind provider, string source, string expected)
        => Assert.Equal(expected, TranslationProviderConfiguration.Endpoint(provider, source).AbsoluteUri);

    [Theory]
    [InlineData("https://key@proxy.example/v1")]
    [InlineData("https://proxy.example/v1?key=secret")]
    [InlineData("https://proxy.example/v1#fragment")]
    [InlineData("http://proxy.example/v1")]
    [InlineData("file:///c:/some-path")]
    [InlineData("https://proxy.example/v1/chat/completions")]
    [InlineData("https://proxy.example/v1/messages")]
    public void Unsupported_Or_Credential_Bearing_BaseUrls_Are_Rejected(string url) =>
        Assert.Throws<TranslationConfigurationException>(() => TranslationProviderConfiguration.Endpoint(TranslationProviderKind.OpenAI, url));

    [Theory]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-4.1-mini")]
    public void NonReasoning_Model_Uses_No_Optional_Reasoning_Parameters(string model)
    {
        var result = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, model, ThinkingLevel.Max);
        Assert.Equal("unsupported", result.Mode);
        Assert.Null(result.Effort);
        Assert.Equal(ThinkingLevel.None, result.EffectiveLevel);
    }

    [Theory]
    [InlineData("gpt-6-luna-pro")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("gpt-5.5-pro")]
    [InlineData("gpt-4.1-custom")]
    [InlineData("claude-fable-6")]
    public void Similar_Unknown_Names_Do_Not_Inherit_Unverified_Capabilities(string model)
    {
        var provider = model.StartsWith("claude-", StringComparison.Ordinal) ? TranslationProviderKind.Claude : TranslationProviderKind.OpenAI;
        Assert.Equal("default", TranslationProviderConfiguration.Thinking(provider, model, ThinkingLevel.High).Mode);
    }

    [Fact]
    public void Old_Settings_Preserve_DeepSeek_And_New_Profile_Values_Survive_Migration()
    {
        var legacy = new AppSettings { Version = 8 };
        Assert.Equal(TranslationProviderKind.DeepSeek, legacy.Migrate(out _).TranslationProvider);
        var current = new AppSettings { TranslationProvider = TranslationProviderKind.Claude, OpenAiModel = "custom-openai",
            OpenAiBaseUrl = "https://proxy.example/openai/v1", ClaudeModel = "claude-haiku-5-5", ClaudeBaseUrl = "https://proxy.example/claude/v1" };
        Assert.Equal(current with { Version = AppSettings.CurrentVersion }, current.Migrate(out _));
        Assert.Equal(TranslationProviderKind.DeepSeek, (current with { TranslationProvider = (TranslationProviderKind)99 }).Migrate(out _).TranslationProvider);
    }
}
