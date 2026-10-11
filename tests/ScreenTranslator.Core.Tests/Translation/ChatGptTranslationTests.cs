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
        public Task<string> GetAccessTokenAsync(HttpClient http, string id, CancellationToken token) => Task.FromResult("oauth-test");
        public CancellationToken GetSessionCancellation(string id) => CancellationToken.None;
        public void ReportInferenceFailure(string id, ChatGptException error) => Failure = error;
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Uri = request.RequestUri; Bearer = request.Headers.Authorization?.ToString(); Accept = request.Headers.Accept.SingleOrDefault()?.MediaType;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(token);
            var result = new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
            result.Headers.Add("x-request-id", "test-request"); return result;
        }
    }
}
