using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ChatGptTranslationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_Stream_Summary_Uses_Deltas_When_Aggregate_Output_Is_Empty_Or_Omitted(bool omitOutput)
    {
        var terminal = omitOutput
            ? Event(new { type = "response.completed", response = new { status = "completed", usage = new { input_tokens = 20, output_tokens = 12 } } })
            : Event(new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 20, output_tokens = 12 } } });
        var handler = new Handler(Event(new { type = "response.output_text.delta", delta = Answer })
            + Event(new { type = "response.output_text.done", text = Answer }) + terminal);
        var provider = Provider(handler, new Tokens());
        ApiTranslationRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        Assert.Equal(Answer, await provider.TranslateRawAsync(Request(), default));
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics!.Outcome);
        Assert.Equal(12, metrics.OutputTokens); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Summary_Completion_Without_Any_Text_Is_Still_A_Failure()
    {
        var handler = new Handler(Event(new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>() } }));
        await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(handler, new Tokens()).TranslateRawAsync(Request(), default));
    }

    [Fact]
    public async Task A_Provided_Final_Text_Envelope_Must_Still_Match_The_Deltas()
    {
        var inconsistent = Event(new { type = "response.output_text.delta", delta = Answer })
            + Event(new { type = "response.completed", response = new { status = "completed", output = new[]
            { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = Answer + " " } } } } } });
        await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(new Handler(inconsistent), new Tokens()).TranslateRawAsync(Request(), default));
    }
    private const string Answer = """{"blocks":[{"id":"b1","translation":"你好"}]}""";
    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "", [new("b1", "Hello", 1, new(0, 0, 100, 20), 0)]);
    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Completed() => Event(new { type = "response.output_text.delta", delta = Answer })
        + Event(new { type = "response.completed", response = new { status = "completed", output = new[]
        { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = Answer } } } } } });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_Requests_Always_Stream_Without_Api_Key_Or_Unsupported_Budget(bool progress)
    {
        var tokens = new Tokens(); var secrets = new Secrets(); var handler = new Handler(Completed());
        var settings = new AppSettings { TranslationProvider = TranslationProviderKind.OpenAI,
            OpenAiAuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth, ChatGptAccountId = "account",
            OpenAiBaseUrl = "https://custom-proxy.example/v1" };
        var provider = TranslationProviderFactory.Create(new(handler), secrets, settings, chatGptTokens: tokens);
        var answer = progress ? await ((IStreamingTranslationProvider)provider).TranslateRawStreamingAsync(Request(), new Progress<TranslationStreamUpdate>(), default)
            : await provider.TranslateRawAsync(Request(), default);
        Assert.Equal(Answer, answer);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer oauth-test", handler.Bearer);
        Assert.Equal("text/event-stream", handler.Accept);
        Assert.False(secrets.Read);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean()); Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("input").ValueKind);
        Assert.False(body.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.TryGetProperty("previous_response_id", out _));
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false, "text", false)]
    [InlineData(true, "text", false)]
    [InlineData(false, "page", true)]
    [InlineData(true, "page", true)]
    [InlineData(false, "image", true)]
    [InlineData(true, "image", true)]
    [InlineData(false, "sheets", true)]
    [InlineData(true, "sheets", true)]
    [InlineData(false, "placement", true)]
    [InlineData(true, "placement", true)]
    [InlineData(false, "translation", true)]
    [InlineData(true, "translation", true)]
    public async Task Json_Mode_Passes_Input_Admission_Once_With_Original_Data_And_Images(bool oauth, string kind, bool streaming)
    {
        var request = kind switch
        {
            "page" => Request() with { IsPageText = true },
            "image" => Request() with { ImagePng = [1, 2, 3], ImageSize = new(100, 200),
                ImageTextCropPng = [4, 5], ImageTextCropBounds = new(2, 3, 10, 20) },
            "sheets" => Request() with { ImagePng = [1, 2, 3], ImageSize = new(100, 200), IsDirectVision = true,
                VisionReadingSheets = [new byte[] { 4, 5 }, new byte[] { 6, 7 }], VisionReadingDetailPng = [99] },
            "placement" => Request() with { ImagePng = [1, 2, 3], ImageSize = new(100, 200), VisionTask = VisionRequestTask.Placement },
            "translation" => Request() with { ImagePng = [1, 2, 3], ImageSize = new(100, 200), VisionTask = VisionRequestTask.Translation },
            _ => Request(),
        };
        var handler = new Handler(oauth || streaming ? Completed() : ApiTranslationProviderTests.Envelope(TranslationProviderKind.OpenAI, Answer));
        var secrets = new Secrets();
        var provider = new OpenAiResponsesTranslationProvider(new(handler), secrets, new()
        { Provider = TranslationProviderKind.OpenAI, Model = "gpt-6-luna", BaseUrl = "https://api.openai.com/v1",
            UseChatGptPlan = oauth, ChatGptTokens = new Tokens(), ChatGptAccountId = "account" });
        var answer = streaming
            ? await provider.TranslateRawStreamingAsync(request, new Progress<TranslationStreamUpdate>(), default)
            : await provider.TranslateRawAsync(request, default);
        Assert.Equal(Answer, answer);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(!oauth, secrets.Read);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal(oauth || streaming, body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("store").GetBoolean());
        var input = body.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("developer", input[1].GetProperty("role").GetString());
        var guidance = input[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("JSON object", guidance);
        Assert.Contains("schema in the instructions", guidance);
        var parts = input[0].GetProperty("content");
        var sourceText = parts[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("json", sourceText, StringComparison.OrdinalIgnoreCase);
        using var source = JsonDocument.Parse(sourceText);
        Assert.Equal(request.TargetLanguage, source.RootElement.GetProperty("targetLanguage").GetString());
        if (kind != "placement")
        {
            Assert.Equal(request.SourceLanguage, source.RootElement.GetProperty("sourceLanguage").GetString());
            Assert.Equal(request.Context, source.RootElement.GetProperty("context").GetString());
        }
        if (kind is "text" or "page")
            Assert.Equal(request.Blocks[0].Text, source.RootElement.GetProperty("blocks")[0].GetProperty("text").GetString());
        var images = parts.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "input_image")
            .Select(part => Convert.FromBase64String(part.GetProperty("image_url").GetString()!.Split(',')[1])).ToArray();
        var expectedImages = kind switch
        {
            "image" => new[] { request.ImagePng!, request.ImageTextCropPng! },
            "sheets" => new[] { request.ImagePng!, request.VisionReadingSheets[0], request.VisionReadingSheets[1] },
            "translation" or "placement" => new[] { request.ImagePng! },
            _ => Array.Empty<byte[]>(),
        };
        Assert.Equal(expectedImages.Length, images.Length);
        for (var i = 0; i < images.Length; i++) Assert.Equal(expectedImages[i], images[i]);
        if (kind == "image")
        {
            Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
            Assert.Contains("FIRST full source image", parts[2].GetProperty("text").GetString());
            Assert.Equal("input_image", parts[3].GetProperty("type").GetString());
        }
        if (oauth)
            foreach (var prohibited in new[] { "max_output_tokens", "previous_response_id", "temperature", "top_p", "api_key" })
                Assert.False(body.TryGetProperty(prohibited, out _));
    }

    [Fact]
    public async Task Each_Request_Reads_Current_Account_Model_Capabilities()
    {
        var tokens = new Tokens { Levels = [ThinkingLevel.Low] };
        var handler = new Handler(Completed());
        var provider = Provider(handler, tokens);
        await provider.TranslateRawAsync(Request(), default);
        tokens.Levels = [ThinkingLevel.High];
        await provider.TranslateRawAsync(Request() with { ThinkingOverride = ThinkingLevel.High }, default);
        Assert.Equal(new[] { ("account", "gpt-6-luna"), ("account", "gpt-6-luna") }, tokens.CapabilityQueries);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_Streamed_Usage_Limit_Is_A_Failure_Even_After_Text_Arrives()
    {
        var tokens = new Tokens();
        var failed = Event(new { type = "response.output_text.delta", delta = Answer }) + Event(new { type = "response.failed",
            response = new { status = "failed", error = new { code = "subscription_sharing_usage_limit_exceeded", param = "model" } } });
        var handler = new Handler(failed);
        var provider = Provider(handler, tokens);
        var error = await Assert.ThrowsAsync<ChatGptException>(() => provider.TranslateRawAsync(Request(), default));
        Assert.True(error.IsUsageLimit); Assert.Equal("model", error.Parameter);
        Assert.Same(error, tokens.Failure); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Interrupted_Stream_Cannot_Return_Partial_Translation()
    {
        var provider = Provider(new Handler(Event(new { type = "response.output_text.delta", delta = Answer })), new Tokens());
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => provider.TranslateRawAsync(Request(), default));
    }

    [Fact]
    public async Task Direct_Admission_Failure_Preserves_Status_Shape_And_Request_Id()
    {
        var handler = new Handler("""{"detail":"Admission rejected"}""", HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<ChatGptException>(() => Provider(handler, new Tokens()).TranslateRawAsync(Request(), default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("detail", error.ResponseShape); Assert.Equal("test-request", error.RequestId);
        Assert.DoesNotContain("API Key", error.Message);
        Assert.Contains("Admission rejected", error.Message);
        Assert.Contains("HTTP 403", error.Message);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("error")]
    public async Task Stream_Failures_Preserve_Actual_Reason_Type_And_Official_Request_Id(string type)
    {
        var serviceError = new { code = (string?)null, type = "invalid_request_error", param = "input", message = "Input must contain JSON" };
        var failure = type == "error" ? Event(new { type, error = serviceError })
            : Event(new { type, response = new { status = "failed", error = serviceError } });
        var handler = new Handler(Event(new { type = "response.output_text.delta", delta = Answer }) + failure)
            { RequestIdHeader = "x-oai-request-id" };
        var tokens = new Tokens();
        var error = await Assert.ThrowsAsync<ChatGptException>(() => Provider(handler, tokens).TranslateRawAsync(Request(), default));
        Assert.Null(error.Code);
        Assert.Equal("invalid_request_error", error.ErrorType);
        Assert.Equal("input", error.Parameter);
        Assert.Equal("error", error.ResponseShape);
        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Equal("test-request", error.RequestId);
        Assert.Contains("Input must contain JSON", error.Message);
        Assert.Contains("HTTP 200", error.Message);
        Assert.Same(error, tokens.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Invalid_Http_Input_Is_Reported_Once_Without_Retry()
    {
        var handler = new Handler("""{"error":{"type":"invalid_request_error","code":null,"param":"input","message":"Response input messages must contain the word 'json' in some form to use 'text.format' of type 'json_object'."}}""", HttpStatusCode.BadRequest)
            { RequestIdHeader = "x-oai-request-id" };
        var error = await Assert.ThrowsAsync<ChatGptException>(() => Provider(handler, new Tokens()).TranslateRawAsync(Request(), default));
        Assert.Contains("must contain the word 'json'", error.Message);
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("参数 input", error.Message);
        Assert.Null(error.Code);
        Assert.Equal("invalid_request_error", error.ErrorType);
        Assert.Equal("test-request", error.RequestId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Account_Model_Catalog_Uses_Slugs_Display_Names_Visibility_And_Server_Order()
    {
        var handler = new Handler("""{"models":[{"slug":"z-model","display_name":"Z model","visibility":"list"},{"slug":"hidden","visibility":"hide"},{"slug":"a-model","display_name":"A model","visibility":"list"},{"slug":"z-model","visibility":"list"}]}""");
        var list = await new TranslationModelCatalog(new(handler)).ReadChatGptAsync(new Tokens(), "account");
        Assert.Equal(["z-model", "a-model"], list.Models);
        Assert.Equal(["Z model", "A model"], list.Choices.Select(c => c.DisplayName));
        Assert.Equal("https://api.openai.com/v1/models", handler.Uri!.AbsoluteUri);
    }

    [Fact]
    public void Presets_Select_Their_Own_Account_And_Include_Authentication_In_Cache_Identity()
    {
        var id = Guid.NewGuid().ToString("N");
        var settings = new AppSettings { TranslationProvider = TranslationProviderKind.OpenAI, ChatGptAccountId = "default",
            SelectedTranslationPresetId = id, TranslationProviderPresets = [new() { Id = id, Provider = TranslationProviderKind.OpenAI,
                Model = "model", BaseUrl = "https://proxy.example/v1", AuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth, ChatGptAccountId = "preset" }] };
        Assert.True(TranslationProviderConfiguration.UsesChatGptPlan(settings));
        Assert.Equal("preset", TranslationProviderConfiguration.ChatGptAccountId(settings));
        Assert.Equal("https://api.openai.com/v1", TranslationProviderConfiguration.BaseUrl(settings));
        var api = settings with { TranslationProviderPresets = [settings.TranslationProviderPresets[0] with { AuthenticationMode = TranslationAuthenticationMode.ApiKey }] };
        Assert.NotEqual(TranslationProviderConfiguration.TransportIdentity(settings), TranslationProviderConfiguration.TransportIdentity(api));
    }

    private static OpenAiResponsesTranslationProvider Provider(Handler handler, Tokens tokens) => new(new(handler), new Secrets(), new()
    { Provider = TranslationProviderKind.OpenAI, Model = "gpt-6-luna", BaseUrl = "https://api.openai.com/v1", UseChatGptPlan = true, ChatGptTokens = tokens, ChatGptAccountId = "account" });
    private sealed class Tokens : IChatGptTokenProvider
    {
        internal ChatGptException? Failure;
        internal IReadOnlyList<ThinkingLevel>? Levels;
        internal List<(string Account, string Model)> CapabilityQueries = [];
        public Task<string> GetAccessTokenAsync(HttpClient http, string id, CancellationToken token) => Task.FromResult("oauth-test");
        public CancellationToken GetSessionCancellation(string id) => CancellationToken.None;
        public void ReportInferenceFailure(string id, ChatGptException error) => Failure = error;
        public IReadOnlyList<ThinkingLevel>? GetSupportedThinkingLevels(string accountId, string model)
        { CapabilityQueries.Add((accountId, model)); return Levels; }
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
        internal string? Body, Bearer, Accept; internal Uri? Uri; internal int Calls;
        internal string RequestIdHeader = "x-request-id";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri; Bearer = request.Headers.Authorization?.ToString(); Accept = request.Headers.Accept.SingleOrDefault()?.MediaType;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(token);
            if (Body is not null)
            {
                using var json = JsonDocument.Parse(Body);
                var root = json.RootElement;
                // Reproduce the real Responses admission check: top-level instructions do not count.
                if (root.TryGetProperty("text", out var text) && text.GetProperty("format").GetProperty("type").GetString() == "json_object"
                    && !root.GetProperty("input").EnumerateArray().SelectMany(message => message.GetProperty("content").EnumerateArray())
                        .Any(part => part.GetProperty("type").GetString() == "input_text"
                            && part.GetProperty("text").GetString()!.Contains("json", StringComparison.OrdinalIgnoreCase)))
                    return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"type":"invalid_request_error","param":"input","message":"Input must contain JSON"}}""") };
            }
            var result = new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
            result.Headers.Add(RequestIdHeader, "test-request"); return result;
        }
    }
}
