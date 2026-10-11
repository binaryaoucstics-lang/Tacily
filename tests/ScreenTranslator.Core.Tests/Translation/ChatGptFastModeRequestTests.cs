using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ChatGptFastModeRequestTests
{
    private const string Answer = """{"blocks":[{"id":"b1","translation":"你好"}]}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_Default_Omits_Tier_Even_When_Thinking_Is_None(bool progress)
    {
        var handler = new Handler(Completed());
        var provider = Provider(handler);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await Translate(provider, Request(), progress));
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.False(json.RootElement.TryGetProperty("service_tier", out _));
        Assert.Null(metrics!.RequestedServiceTier);
        Assert.Null(metrics.EffectiveServiceTier);
        Assert.Equal("none", json.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("image", false)]
    [InlineData("image", true)]
    [InlineData("page", false)]
    [InlineData("page", true)]
    public async Task OAuth_Fast_Uses_Real_Tier_For_All_Inputs_And_Preserves_Protocol(string input, bool progress)
    {
        var answer = input == "image"
            ? """{"blocks":[{"id":"p1","box":[100,100,300,400],"translation":"你好"}]}""" : Answer;
        var request = input switch
        {
            "image" => Request() with { IsDirectVision = true, ImagePng = [1, 2, 3], ImageSize = new(1000, 1000) },
            "page" => Request() with { IsPageText = true },
            _ => Request(),
        };
        var handler = new Handler(Completed("\"fast\"", answer));
        var secrets = new Secrets();
        var provider = Provider(handler, fast: true, secrets: secrets);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(answer, await Translate(provider, request, progress));
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal("fast", body.GetProperty("service_tier").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.False(body.TryGetProperty("max_output_tokens", out _));
        Assert.Equal("gpt-6-luna", body.GetProperty("model").GetString());
        Assert.Equal("none", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_object", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        var parts = body.GetProperty("input")[0].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(input == "image", parts.Any(part => part.GetProperty("type").GetString() == "input_image"));
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer oauth-test", handler.Authorization);
        Assert.Equal("text/event-stream", handler.Accept);
        Assert.False(secrets.Read);
        Assert.Equal("fast", metrics!.RequestedServiceTier);
        Assert.Equal("fast", metrics.EffectiveServiceTier);
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics.Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, false)]
    [InlineData(TranslationProviderKind.OpenAI, true)]
    [InlineData(TranslationProviderKind.Claude, false)]
    [InlineData(TranslationProviderKind.Claude, true)]
    public async Task Api_Key_And_Claude_Omit_Fast_Even_With_Stale_Enabled_Option(TranslationProviderKind kind, bool progress)
    {
        var response = progress ? ApiTranslationProviderTests.Transcript(kind, Answer) : ApiTranslationProviderTests.Envelope(kind, Answer);
        var handler = new Handler(response);
        var provider = Provider(handler, fast: true, oauth: false, kind: kind);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await Translate(provider, Request(), progress));
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.False(json.RootElement.TryGetProperty("service_tier", out _));
        Assert.Equal(progress, json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Null(metrics!.RequestedServiceTier);
        Assert.Null(metrics.EffectiveServiceTier);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("\"fast\"", "fast")]
    [InlineData("\"priority\"", "priority")]
    [InlineData("\"default\"", "default")]
    [InlineData(null, null)]
    [InlineData("null", null)]
    [InlineData("\"auto\"", null)]
    [InlineData("\"arbitrary-server-value\"", null)]
    [InlineData("\"PRIORITY\"", null)]
    [InlineData("\" fast\"", null)]
    [InlineData("\"fast\\n\"", null)]
    [InlineData("42", null)]
    [InlineData("{}", null)]
    public async Task Only_Completed_Response_Known_Tier_Is_Reported(string? tierJson, string? expected)
    {
        var created = Event(new { type = "response.created", response = new { status = "in_progress", service_tier = "fast" } });
        var handler = new Handler(created + Completed(tierJson));
        var provider = Provider(handler, fast: true);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await provider.TranslateRawAsync(Request(), default));
        Assert.Equal("fast", metrics!.RequestedServiceTier);
        Assert.Equal(expected, metrics.EffectiveServiceTier);
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics.Outcome);
    }

    [Fact]
    public async Task Overlong_Tier_Is_Not_Recorded_And_Tier_Does_Not_Leak_Between_Requests()
    {
        var handler = new Handler(Completed("\"priority\""));
        var provider = Provider(handler, fast: true);
        var metrics = new List<ApiTranslationRequestMetrics>();
        provider.RequestCompleted += metrics.Add;
        await provider.TranslateRawAsync(Request(), default);
        handler.Response = Completed(JsonSerializer.Serialize(new string('x', 4096)));
        await provider.TranslateRawAsync(Request(), default);
        Assert.Equal("priority", metrics[0].EffectiveServiceTier);
        Assert.Null(metrics[1].EffectiveServiceTier);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_Summary_Completion_Reports_Tier_When_Output_Is_Empty_Or_Omitted(bool omitOutput)
    {
        var completed = new Dictionary<string, object?> { ["status"] = "completed", ["service_tier"] = "priority" };
        if (!omitOutput) completed["output"] = Array.Empty<object>();
        var handler = new Handler(Event(new { type = "response.output_text.delta", delta = Answer })
            + Event(new { type = "response.completed", response = completed }));
        var provider = Provider(handler, fast: true);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await provider.TranslateRawAsync(Request(), default));
        Assert.Equal("priority", metrics!.EffectiveServiceTier);
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_Key_Responses_Still_Report_Actual_Server_Tier_Without_Requesting_It(bool progress)
    {
        var response = progress ? Completed("\"default\"") : Envelope("\"default\"");
        var handler = new Handler(response);
        var provider = Provider(handler, fast: true, oauth: false);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await Translate(provider, Request(), progress));
        Assert.Null(metrics!.RequestedServiceTier);
        Assert.Equal("default", metrics.EffectiveServiceTier);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("error")]
    [InlineData("failed")]
    public async Task Tier_Rejection_Retains_Diagnostics_Without_Retry_Or_Returning_Partial_Translation(string failure)
    {
        var error = new { code = "unsupported_value", type = "invalid_request_error", param = "service_tier", message = "service_tier fast is unavailable for this model" };
        var partial = Event(new { type = "response.output_text.delta", delta = Answer });
        var response = failure switch
        {
            "http" => JsonSerializer.Serialize(new { error }),
            "error" => partial + Event(new { type = "error", error }),
            _ => partial + Event(new { type = "response.failed", response = new { status = "failed", error } }),
        };
        var handler = new Handler(response, failure == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        var tokens = new Tokens();
        var provider = Provider(handler, fast: true, tokens: tokens);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        var progress = new CollectedProgress();
        var exception = await Assert.ThrowsAsync<ChatGptException>(() => provider.TranslateRawStreamingAsync(Request(), progress, default));
        Assert.Contains("关闭快速模式", exception.Message);
        Assert.Contains("更换可用模型", exception.Message);
        Assert.Contains(error.message, exception.Message);
        Assert.Equal(error.message, exception.ServerMessage);
        Assert.Equal(error.code, exception.Code);
        Assert.Equal(error.type, exception.ErrorType);
        Assert.Equal("service_tier", exception.Parameter);
        Assert.Equal("fast-test-request", exception.RequestId);
        Assert.Equal(failure == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, exception.StatusCode);
        Assert.Same(exception, tokens.Failure);
        Assert.Equal("fast", metrics!.RequestedServiceTier);
        Assert.Null(metrics.EffectiveServiceTier);
        Assert.Equal(failure == "http" ? DeepSeekRequestOutcome.HttpError : DeepSeekRequestOutcome.Interrupted, metrics.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(failure != "http", progress.Updates.Count > 0);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("incomplete")]
    [InlineData("mismatch")]
    public async Task Fast_Does_Not_Relax_Terminal_Success_Requirements(string failure)
    {
        var response = Event(new { type = "response.created", response = new { status = "in_progress", service_tier = "priority" } })
            + Event(new { type = "response.output_text.delta", delta = Answer });
        response += failure switch
        {
            "eof" => "data: [DONE]\n\n",
            "incomplete" => Event(new { type = "response.incomplete", response = new { status = "incomplete", service_tier = "fast" } }),
            _ => Event(new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Envelope("\"fast\"", Answer + " ")) }),
        };
        var handler = new Handler(response);
        var provider = Provider(handler, fast: true);
        ApiTranslationRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        var exception = await Record.ExceptionAsync(() => provider.TranslateRawAsync(Request(), default));
        Assert.True(exception is TranslationUnavailableException or TranslationFormatException);
        Assert.NotEqual(DeepSeekRequestOutcome.Success, metrics!.Outcome);
        if (failure != "mismatch") Assert.Null(metrics.EffectiveServiceTier);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true, "\"priority\"", "priority")]
    [InlineData(true, "\"default\"", "default")]
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    public async Task Benchmark_Passes_Requested_And_Actual_Tiers_Independently(bool fast, string? tierJson, string? expected)
    {
        const string answer = """{"blocks":[{"id":"benchmark","translation":"打开新页面时会识别并翻译文字。"}]}""";
        var handler = new Handler(Completed(tierJson, answer));
        var result = await TranslationBenchmark.RunAsync(Provider(handler, fast: fast), ThinkingLevel.None, default);
        Assert.Equal(fast ? "fast" : null, result.RequestedServiceTier);
        Assert.Equal(expected, result.EffectiveServiceTier);
        Assert.Equal("打开新页面时会识别并翻译文字。", result.Translation);
        Assert.Equal(1, handler.Calls);
    }

    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "",
        [new("b1", "Hello", 1, new(0, 0, 100, 20), 0)]);
    private static Task<string> Translate(ApiTranslationProvider provider, TranslationRequest request, bool progress) => progress
        ? provider.TranslateRawStreamingAsync(request, new CollectedProgress(), default) : provider.TranslateRawAsync(request, default);
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Envelope(string? tierJson, string answer = Answer)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["status"] = "completed",
            ["output"] = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } },
            ["usage"] = new { input_tokens = 20, output_tokens = 12 },
        };
        if (tierJson is not null) envelope["service_tier"] = JsonSerializer.Deserialize<JsonElement>(tierJson);
        return JsonSerializer.Serialize(envelope);
    }
    private static string Completed(string? tierJson = null, string answer = Answer) =>
        Event(new { type = "response.output_text.delta", delta = answer })
        + Event(new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Envelope(tierJson, answer)) });
    private static ApiTranslationProvider Provider(Handler handler, bool fast = false, bool oauth = true,
        TranslationProviderKind kind = TranslationProviderKind.OpenAI, Tokens? tokens = null, Secrets? secrets = null)
    {
        var options = new TranslationApiOptions
        {
            Provider = kind, Model = kind == TranslationProviderKind.OpenAI ? "gpt-6-luna" : "claude-sonnet-5-5",
            BaseUrl = kind == TranslationProviderKind.OpenAI ? "https://api.openai.com/v1" : "https://api.anthropic.com/v1",
            UseChatGptPlan = oauth, ChatGptFastModeEnabled = fast, ChatGptTokens = tokens ?? new Tokens(),
            ChatGptAccountId = "test-account", ThinkingLevel = ThinkingLevel.None,
        };
        return kind == TranslationProviderKind.OpenAI ? new OpenAiResponsesTranslationProvider(new(handler), secrets ?? new(), options)
            : new ClaudeMessagesTranslationProvider(new(handler), secrets ?? new(), options);
    }
    private sealed class Tokens : IChatGptTokenProvider
    {
        internal ChatGptException? Failure;
        public Task<string> GetAccessTokenAsync(HttpClient http, string accountId, CancellationToken token) => Task.FromResult("oauth-test");
        public CancellationToken GetSessionCancellation(string accountId) => CancellationToken.None;
        public IReadOnlyList<ThinkingLevel>? GetSupportedThinkingLevels(string accountId, string model) =>
            [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High];
        public void ReportInferenceFailure(string accountId, ChatGptException error) => Failure = error;
    }
    private sealed class Secrets : ISecretStore
    {
        internal bool Read;
        public Task<string?> GetAsync(string name, CancellationToken token = default) { Read = true; return Task.FromResult<string?>("api-test"); }
        public Task SetAsync(string name, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal string Response = response;
        internal string? Body, Authorization, Accept;
        internal Uri? Uri;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.SingleOrDefault()?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(token);
            var result = new HttpResponseMessage(status) { Content = new StringContent(Response, Encoding.UTF8, "text/event-stream") };
            result.Headers.Add("x-request-id", "fast-test-request");
            return result;
        }
    }
    private sealed class CollectedProgress : IProgress<TranslationStreamUpdate>
    {
        internal List<TranslationStreamUpdate> Updates { get; } = [];
        public void Report(TranslationStreamUpdate update) => Updates.Add(update);
    }
}
