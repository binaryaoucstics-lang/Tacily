using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ChatGptModelCapabilitiesTests
{
    [Theory]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6-sol", true)]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("gpt-5.6-terra", true)]
    [InlineData("gpt-6-luna", false)]
    [InlineData("gpt-5.6-luna", false)]
    public async Task RealObjectMetadataRetainsMediumXhighMaxAndIgnoresCodexUltra(string model, bool hasUltra)
    {
        var efforts = new List<string> { "low", "medium", "high", "xhigh", "max" };
        if (hasUltra) efforts.Add("ultra");
        var json = JsonSerializer.Serialize(new { models = new[] { new
        {
            slug = model, visibility = "list", display_name = "Model display",
            supported_reasoning_levels = efforts.Select(effort => new { effort, description = "Account model effort" }),
            default_reasoning_level = "medium",
        } } });
        var tokens = new Tokens();
        using var http = Client(json);
        var result = await new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "account-a");
        var choice = Assert.Single(result.Choices);
        Assert.Equal(model, Assert.Single(result.Models));
        Assert.Equal("Model display", choice.DisplayName);
        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max], choice.SupportedReasoningLevels);
        Assert.Equal(ThinkingLevel.Medium, choice.DefaultReasoningLevel);
        Assert.Equal(choice.SupportedReasoningLevels, tokens.GetSupportedThinkingLevels("account-a", model));
        Assert.Equal(1, tokens.Updates);
        foreach (var requested in Enum.GetValues<ThinkingLevel>())
        {
            var config = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, model, requested,
                true, tokens.GetSupportedThinkingLevels("account-a", model));
            Assert.Contains(config.EffectiveLevel, choice.SupportedReasoningLevels!);
            Assert.Contains(config.Effort, efforts.Take(5));
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(",\"supported_reasoning_levels\":null", false)]
    [InlineData(",\"supported_reasoning_levels\":{}", false)]
    [InlineData(",\"supported_reasoning_levels\":\"low\"", false)]
    [InlineData(",\"supported_reasoning_levels\":42", false)]
    [InlineData(",\"supported_reasoning_levels\":[]", false)]
    [InlineData(",\"supported_reasoning_levels\":[{\"effort\":\"ultra\"},{\"effort\":\"bogus\"}]", false)]
    public async Task AbsentAndPresentButUnusableCapabilitiesStayDistinct(string field, bool absent)
    {
        var tokens = new Tokens();
        using var http = Client("{\"models\":[{\"slug\":\"gpt-6.1-sol\",\"visibility\":\"list\",\"default_reasoning_level\":\"low\"" + field + "}]}");
        var choice = Assert.Single((await new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "a")).Choices);
        Assert.Null(choice.DefaultReasoningLevel);
        var levels = tokens.GetSupportedThinkingLevels("a", choice.Id);
        if (absent) { Assert.Null(choice.SupportedReasoningLevels); Assert.Null(levels); }
        else { Assert.Empty(choice.SupportedReasoningLevels!); Assert.Empty(levels!); }
        var config = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, choice.Id, ThinkingLevel.None, true, levels);
        Assert.Equal(absent ? "low" : null, config.Effort);
    }

    [Theory]
    [InlineData("\"medium\"", ThinkingLevel.Medium)]
    [InlineData("\"low\"", ThinkingLevel.Low)]
    [InlineData("\"high\"", null)]
    [InlineData("\"ultra\"", null)]
    [InlineData("\"Medium\"", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("42", null)]
    public async Task MalformedEntriesAreIgnoredAndDefaultMustBeLegalAndSupported(string defaultJson, ThinkingLevel? expectedDefault)
    {
        using var http = Client($$$"""
            {"models":[{"slug":"future-model","visibility":"list",
            "supported_reasoning_levels":[null,42,"high",[],{},
              {"effort":42},{"effort":null},{"effort":{}},{"effort":"ultra"},{"effort":"LOW"},
              {"effort":"medium"},{"effort":"low"},{"effort":"medium"},{"effort":"xhigh"}],
            "default_reasoning_level":{{{defaultJson}}}}]}
            """);
        var tokens = new Tokens();
        var choice = Assert.Single((await new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "a")).Choices);
        Assert.Equal([ThinkingLevel.Medium, ThinkingLevel.Low, ThinkingLevel.XHigh], choice.SupportedReasoningLevels);
        Assert.Equal(expectedDefault, choice.DefaultReasoningLevel);
        var config = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, choice.Id, ThinkingLevel.None, true,
            tokens.GetSupportedThinkingLevels("a", choice.Id));
        Assert.Equal("low", config.Effort);
        Assert.Equal(ThinkingLevel.Low, config.EffectiveLevel);
    }

    [Fact]
    public async Task CodecRecognizedNoneAndMinimalAreNotDiscardedFromMetadata()
    {
        using var http = Client("""{"models":[{"slug":"future-model","visibility":"list","supported_reasoning_levels":[{"effort":"none"},{"effort":"minimal"}],"default_reasoning_level":"minimal"}]}""");
        var choice = Assert.Single((await new TranslationModelCatalog(http).ReadChatGptAsync(new Tokens(), "a")).Choices);
        Assert.Equal([ThinkingLevel.None, ThinkingLevel.Minimal], choice.SupportedReasoningLevels);
        Assert.Equal(ThinkingLevel.Minimal, choice.DefaultReasoningLevel);
    }

    [Fact]
    public async Task OnlyVisibleUniqueValidModelsContributeToSnapshot()
    {
        using var http = Client("""
            {"models":[null,42,{},"gpt-6-sol",{"slug":"hidden","visibility":"hidden"},
            {"slug":"bad\u0001id","visibility":"list"},
            {"slug":"future-model","visibility":"list","supported_reasoning_levels":[{"effort":"low"}]},
            {"slug":"future-model","visibility":"list","supported_reasoning_levels":[{"effort":"max"}]}]}
            """);
        var tokens = new Tokens();
        var choice = Assert.Single((await new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "a")).Choices);
        Assert.Equal("future-model", choice.Id);
        Assert.Equal(choice.Id, choice.DisplayName);
        Assert.Equal([ThinkingLevel.Low], tokens.GetSupportedThinkingLevels("a", choice.Id));
        Assert.Null(tokens.GetSupportedThinkingLevels("a", "hidden"));
    }

    [Fact]
    public async Task SuccessfulRefreshReplacesSnapshotIncludingEmptyListWithoutCrossingAccounts()
    {
        var tokens = new Tokens();
        using var http = new HttpClient(new Handler(new Queue<string>([
            """{"models":[{"slug":"shared-model","visibility":"list","supported_reasoning_levels":[{"effort":"low"}]},{"slug":"removed-model","visibility":"list","supported_reasoning_levels":[{"effort":"high"}]}]}""",
            """{"models":[{"slug":"shared-model","visibility":"list","supported_reasoning_levels":[{"effort":"max"}]}]}""",
            """{"models":[{"slug":"shared-model","visibility":"list"}]}""",
            """{"models":[]}""",
        ])));
        var catalog = new TranslationModelCatalog(http);
        await catalog.ReadChatGptAsync(tokens, "a");
        await catalog.ReadChatGptAsync(tokens, "b");
        Assert.Equal([ThinkingLevel.Low], tokens.GetSupportedThinkingLevels("a", "shared-model"));
        Assert.Equal([ThinkingLevel.Max], tokens.GetSupportedThinkingLevels("b", "shared-model"));
        await catalog.ReadChatGptAsync(tokens, "a");
        Assert.Null(tokens.GetSupportedThinkingLevels("a", "shared-model"));
        Assert.Null(tokens.GetSupportedThinkingLevels("a", "removed-model"));
        Assert.Empty((await catalog.ReadChatGptAsync(tokens, "b")).Models);
        Assert.Null(tokens.GetSupportedThinkingLevels("b", "shared-model"));
        Assert.Equal(4, tokens.Updates);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"models\":{}}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("invalid JSON")]
    public async Task FailedCatalogDoesNotReplaceThePreviousSnapshot(string body)
    {
        var tokens = new Tokens();
        tokens.UpdateModelCapabilities("a", [new("model", "model") { SupportedReasoningLevels = [ThinkingLevel.Medium] }]);
        using var http = Client(body);
        await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "a"));
        Assert.Equal(1, tokens.Updates);
        Assert.Equal([ThinkingLevel.Medium], tokens.GetSupportedThinkingLevels("a", "model"));
    }

    [Fact]
    public async Task CancellationDoesNotInstallStaleMetadata()
    {
        var tokens = new Tokens();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = Client("""{"models":[]}""");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranslationModelCatalog(http).ReadChatGptAsync(tokens, "a", cancellation.Token));
        Assert.Equal(0, tokens.Updates);
    }

    [Fact]
    public void AccountCacheCopiesLevelsAndNeverPersistsCapabilitiesOrReadsSecrets()
    {
        var service = new ChatGptAccountService(new NoSecrets(), _ => throw new InvalidOperationException());
        var levels = new List<ThinkingLevel> { ThinkingLevel.Medium, ThinkingLevel.XHigh };
        service.UpdateModelCapabilities("a", [new("model", "Model") { SupportedReasoningLevels = levels }]);
        levels.Clear();
        Assert.Equal([ThinkingLevel.Medium, ThinkingLevel.XHigh], service.GetSupportedThinkingLevels("a", "model"));
        Assert.Null(service.GetSupportedThinkingLevels("b", "model"));
        var restarted = new ChatGptAccountService(new NoSecrets(), _ => throw new InvalidOperationException());
        Assert.Null(restarted.GetSupportedThinkingLevels("a", "model"));
    }

    private static HttpClient Client(string json) => new(new Handler(new Queue<string>([json])));

    private sealed class Handler(Queue<string> bodies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer synthetic-oauth", request.Headers.Authorization!.ToString());
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(bodies.Dequeue(), Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Tokens : IChatGptTokenProvider
    {
        private readonly ChatGptAccountService _cache = new(new NoSecrets(), _ => throw new InvalidOperationException());
        public int Updates { get; private set; }
        public Task<string> GetAccessTokenAsync(HttpClient http, string accountId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-oauth"); }
        public CancellationToken GetSessionCancellation(string accountId) => CancellationToken.None;
        public void ReportInferenceFailure(string accountId, ChatGptException error) { }
        public IReadOnlyList<ThinkingLevel>? GetSupportedThinkingLevels(string accountId, string model) => _cache.GetSupportedThinkingLevels(accountId, model);
        public void UpdateModelCapabilities(string accountId, IReadOnlyList<TranslationModelChoice> models)
        { Updates++; _cache.UpdateModelCapabilities(accountId, models); }
    }

    private sealed class NoSecrets : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken token = default) => throw new InvalidOperationException("No secret reads expected");
        public Task SetAsync(string name, string value, CancellationToken token = default) => throw new InvalidOperationException("No secret writes expected");
        public Task DeleteAsync(string name, CancellationToken token = default) => throw new InvalidOperationException("No secret deletes expected");
    }
}
