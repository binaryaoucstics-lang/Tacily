using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
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

    [Theory]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.XHigh, "xhigh")]
    [InlineData(ThinkingLevel.Max, "max")]
    [InlineData(ThinkingLevel.Minimal, "low")]
    public async Task OpenAiWireEffortDistinguishesNewLevelsAndNeverSendsMinimalToSol(ThinkingLevel level, string expected)
    {
        using var http = new HttpClient(new WireHandler(TranslationProviderKind.OpenAI, expected));
        var provider = TranslationProviderFactory.Create(http, new Secrets(), new AppSettings
            { TranslationProvider = TranslationProviderKind.OpenAI, OpenAiModel = "gpt-6.1-sol", ThinkingLevel = level });
        await provider.TranslateRawAsync(Request(), default);
    }

    [Theory]
    [InlineData(TranslationProviderKind.DeepSeek, ThinkingLevel.Medium, "low")]
    [InlineData(TranslationProviderKind.DeepSeek, ThinkingLevel.XHigh, "high")]
    [InlineData(TranslationProviderKind.DeepSeek, ThinkingLevel.Minimal, "none")]
    [InlineData(TranslationProviderKind.Claude, ThinkingLevel.Medium, "low")]
    [InlineData(TranslationProviderKind.Claude, ThinkingLevel.XHigh, "high")]
    [InlineData(TranslationProviderKind.Claude, ThinkingLevel.Minimal, "low")]
    public async Task FactoryPreventsCrossProviderNewLevelsFromAppearingOnWire(TranslationProviderKind kind, ThinkingLevel level, string expected)
    {
        using var http = new HttpClient(new WireHandler(kind, expected));
        var provider = TranslationProviderFactory.Create(http, new Secrets(), new AppSettings
            { TranslationProvider = kind, ClaudeModel = "claude-sonnet-5-5", ThinkingLevel = level });
        await provider.TranslateRawAsync(Request(), default);
    }

    [Theory]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.5")]
    public void ApiKeyNoneIsNotReplacedByOAuthRulesOrMetadata(string model)
    {
        var api = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, model, ThinkingLevel.None,
            supportedLevels: [ThinkingLevel.Max]);
        Assert.Equal("none", api.Effort);
        Assert.Equal(ThinkingLevel.None, api.EffectiveLevel);
        var oauth = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, model, ThinkingLevel.None, true);
        Assert.Equal("low", oauth.Effort);
        Assert.Equal(ThinkingLevel.Low, oauth.EffectiveLevel);
    }

    [Fact]
    public void OAuthMetadataOverridesFallbackAndExplicitEmptyMetadataOmitsReasoning()
    {
        var selected = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, "gpt-6.1-sol", ThinkingLevel.High,
            true, [ThinkingLevel.Medium, ThinkingLevel.XHigh]);
        Assert.Equal("medium", selected.Effort);
        Assert.Equal(ThinkingLevel.Medium, selected.EffectiveLevel);
        var future = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, "future-model", ThinkingLevel.XHigh,
            true, [ThinkingLevel.Low, ThinkingLevel.XHigh]);
        Assert.Equal("xhigh", future.Effort);
        Assert.Equal(ThinkingLevel.XHigh, future.EffectiveLevel);
        foreach (var levels in new IReadOnlyList<ThinkingLevel>[] { [], [(ThinkingLevel)99] })
        {
            var empty = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, "gpt-6.1-sol", ThinkingLevel.Max, true, levels);
            Assert.Equal("default", empty.Mode);
            Assert.Null(empty.Effort);
        }
        var metadataNone = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, "future-model", ThinkingLevel.None,
            true, [ThinkingLevel.None, ThinkingLevel.Minimal]);
        Assert.Equal("none", metadataNone.Effort);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    [InlineData(TranslationProviderKind.ChatCompletions)]
    public void UnknownModelsPreserveTheRequestedLevelWithoutAddingParameters(TranslationProviderKind kind)
    {
        foreach (var requested in Enum.GetValues<ThinkingLevel>())
        {
            var config = TranslationProviderConfiguration.Thinking(kind, "future-model", requested);
            Assert.Equal("default", config.Mode);
            Assert.Null(config.Effort);
            Assert.Equal(requested, config.EffectiveLevel);
        }
    }

    [Theory]
    [InlineData(ThinkingLevel.None, 90)]
    [InlineData(ThinkingLevel.Low, 90)]
    [InlineData(ThinkingLevel.Minimal, 90)]
    [InlineData(ThinkingLevel.Medium, 120)]
    [InlineData(ThinkingLevel.High, 180)]
    [InlineData(ThinkingLevel.XHigh, 240)]
    [InlineData(ThinkingLevel.Max, 300)]
    public void FactoryTimeoutFollowsEffectiveSolLevel(ThinkingLevel level, int seconds)
    {
        using var http = new HttpClient();
        var provider = TranslationProviderFactory.Create(http, new Secrets(), new AppSettings
            { TranslationProvider = TranslationProviderKind.OpenAI, OpenAiModel = "gpt-6.1-sol", ThinkingLevel = level });
        var options = (TranslationApiOptions)typeof(ApiTranslationProvider).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.Timeout);
        Assert.Equal(level, options.ThinkingLevel);
        var custom = TranslationProviderFactory.Create(http, new Secrets(), new AppSettings { TranslationProvider = TranslationProviderKind.OpenAI },
            level, TimeSpan.FromSeconds(17));
        var customOptions = (TranslationApiOptions)typeof(ApiTranslationProvider).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(custom)!;
        Assert.Equal(TimeSpan.FromSeconds(17), customOptions.Timeout);
    }

    private static TranslationRequest Request() => new("en", "zh-CN", TranslationStyle.Natural, "", [new OcrBlock("p1", "Hello", 1, new(0, 0, 10, 10), 0)]);

    private sealed class Secrets : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken token = default) => Task.FromResult<string?>("synthetic-key");
        public Task SetAsync(string name, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class WireHandler(TranslationProviderKind provider, string effort) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.Equal(effort, provider switch
            {
                TranslationProviderKind.OpenAI => body.GetProperty("reasoning").GetProperty("effort").GetString(),
                TranslationProviderKind.Claude => body.GetProperty("output_config").GetProperty("effort").GetString(),
                _ => body.GetProperty("reasoning_effort").GetString(),
            });
            Assert.False(body.TryGetProperty("multi_agent", out _));
            const string answer = "{\"blocks\":[{\"id\":\"p1\",\"translation\":\"你好\"}]}";
            var response = provider switch
            {
                TranslationProviderKind.OpenAI => JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } } }),
                TranslationProviderKind.Claude => JsonSerializer.Serialize(new { type = "message", role = "assistant", stop_reason = "end_turn", content = new[] { new { type = "text", text = answer } } }),
                _ => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
