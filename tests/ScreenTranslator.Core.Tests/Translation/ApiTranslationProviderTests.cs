using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ApiTranslationProviderTests
{
    private const string Answer = """{"blocks":[{"id":"b1","translation":"你好世界"}]}""";
    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "", [new("b1", "Hello world", 1, new(4, 8, 100, 20), 0)]);

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, "/v1/responses", "openai-api-key")]
    [InlineData(TranslationProviderKind.Claude, "/v1/messages", "claude-api-key")]
    public async Task Native_Payload_Uses_Provider_Key_And_Text_Schema(TranslationProviderKind kind, string path, string secretName)
    {
        var handler = new Handler(Envelope(kind, Answer));
        var store = new Secrets();
        var provider = Provider(kind, handler, store);
        Assert.Equal(Answer, await provider.TranslateRawAsync(Request() with { IsPageText = true }, default));
        Assert.Equal(path, handler.RequestUri!.AbsolutePath);
        Assert.Equal(secretName, store.LastKey);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("response_format", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.GetProperty("stream").GetBoolean());
        var content = kind == TranslationProviderKind.OpenAI ? body.GetProperty("input")[0].GetProperty("content") : body.GetProperty("messages")[0].GetProperty("content");
        Assert.Single(content.EnumerateArray());
        Assert.Equal(kind == TranslationProviderKind.OpenAI ? "input_text" : "text", content[0].GetProperty("type").GetString());
        using var source = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
        Assert.Equal("Hello world", source.RootElement.GetProperty("blocks")[0].GetProperty("text").GetString());
        Assert.Equal(DeepSeekTranslationProvider.PageTextPrompt, body.GetProperty(kind == TranslationProviderKind.OpenAI ? "instructions" : "system").GetString());
        if (kind == TranslationProviderKind.OpenAI)
        {
            Assert.Equal("Bearer test-only-key", handler.Authorization);
            Assert.Null(handler.ClaudeKey);
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Equal("json_object", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("none", body.GetProperty("reasoning").GetProperty("effort").GetString());
        }
        else
        {
            Assert.Null(handler.Authorization);
            Assert.Equal("test-only-key", handler.ClaudeKey);
            Assert.Equal("2023-06-01", handler.AnthropicVersion);
            Assert.Equal("between_tools", body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Full_Image_And_Reading_Sheets_Keep_Original_Bytes_And_Order(TranslationProviderKind kind)
    {
        var handler = new Handler(Envelope(kind, Answer));
        var request = Request() with { ImagePng = [1, 2, 3], IsDirectVision = true, ImageSize = new(100, 200),
            VisionReadingSheets = [new byte[] { 4, 5 }, new byte[] { 6, 7 }], VisionReadingDetailPng = [99] };
        await Provider(kind, handler).TranslateRawAsync(request, default);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        var parts = (kind == TranslationProviderKind.OpenAI ? root.GetProperty("input") : root.GetProperty("messages"))[0].GetProperty("content");
        var images = parts.EnumerateArray().Where(part => part.GetProperty("type").GetString() is "input_image" or "image").ToArray();
        Assert.Equal(3, images.Length);
        byte[] Decode(JsonElement image) => kind == TranslationProviderKind.OpenAI
            ? Convert.FromBase64String(image.GetProperty("image_url").GetString()!.Split(',')[1])
            : Convert.FromBase64String(image.GetProperty("source").GetProperty("data").GetString()!);
        Assert.Equal(request.ImagePng, Decode(images[0]));
        Assert.Equal(request.VisionReadingSheets[0], Decode(images[1]));
        Assert.Equal(request.VisionReadingSheets[1], Decode(images[2]));
        if (kind == TranslationProviderKind.Claude)
            Assert.All(images, image => Assert.Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString()));
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-6-luna", ThinkingLevel.Low, "reasoning", "low", 16384)]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-6.1-sol", ThinkingLevel.None, "reasoning", "low", 16384)]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-6-astra", ThinkingLevel.Max, "reasoning", "max", 32768)]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-5.5-2026-04-23", ThinkingLevel.Max, "reasoning", "xhigh", 32768)]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-5.4", ThinkingLevel.None, "reasoning", "none", 8192)]
    [InlineData(TranslationProviderKind.OpenAI, "gpt-5.6-terra", ThinkingLevel.High, "reasoning", "high", 32768)]
    [InlineData(TranslationProviderKind.Claude, "claude-opus-5-5", ThinkingLevel.None, "adaptive", "low", 16384)]
    [InlineData(TranslationProviderKind.Claude, "claude-sonnet-5-5", ThinkingLevel.None, "between_tools", "low", 8192)]
    [InlineData(TranslationProviderKind.Claude, "claude-haiku-5-5", ThinkingLevel.None, "disabled", "low", 8192)]
    [InlineData(TranslationProviderKind.Claude, "claude-fable-5-1", ThinkingLevel.Max, "adaptive", "max", 32768)]
    [InlineData(TranslationProviderKind.Claude, "claude-sonnet-4-6", ThinkingLevel.High, "adaptive", "high", 32768)]
    [InlineData(TranslationProviderKind.Claude, "claude-sonnet-4-6-20260217", ThinkingLevel.High, "adaptive", "high", 32768)]
    public async Task Actual_Thinking_Payload_Is_Model_Specific(TranslationProviderKind kind, string model, ThinkingLevel level,
        string mode, string effort, int budget)
    {
        var handler = new Handler(Envelope(kind, Answer));
        ApiTranslationRequestMetrics? metrics = null;
        var provider = Provider(kind, handler, model: model, level: level);
        provider.RequestCompleted += value => metrics = value;
        await provider.TranslateRawAsync(Request(), default);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        if (kind == TranslationProviderKind.OpenAI)
        {
            Assert.Equal(effort, body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(body.TryGetProperty("thinking", out _));
            Assert.Equal(budget, body.GetProperty("max_output_tokens").GetInt32());
        }
        else
        {
            Assert.Equal(mode, body.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal(effort, body.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal(budget, body.GetProperty("max_tokens").GetInt32());
            Assert.False(body.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        }
        Assert.NotNull(metrics);
        Assert.Equal(mode, metrics.EffectiveThinkingMode);
        Assert.Equal(level, metrics.RequestedThinking);
        Assert.Equal(effort, metrics.Effort);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Override_None_Actually_Replaces_High_And_Unknown_Models_Use_Defaults(TranslationProviderKind kind)
    {
        var handler = new Handler(Envelope(kind, Answer));
        await Provider(kind, handler, level: ThinkingLevel.High).TranslateRawAsync(Request() with { ThinkingOverride = ThinkingLevel.None }, default);
        using var first = JsonDocument.Parse(handler.Body!);
        Assert.Equal(kind == TranslationProviderKind.OpenAI ? "none" : "between_tools",
            first.RootElement.GetProperty(kind == TranslationProviderKind.OpenAI ? "reasoning" : "thinking").GetProperty(kind == TranslationProviderKind.OpenAI ? "effort" : "type").GetString());
        await Provider(kind, handler, model: "custom-future-model", level: ThinkingLevel.High).TranslateRawAsync(Request(), default);
        using var second = JsonDocument.Parse(handler.Body!);
        Assert.Equal("custom-future-model", second.RootElement.GetProperty("model").GetString());
        Assert.False(second.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(second.RootElement.TryGetProperty("thinking", out _));
        Assert.False(second.RootElement.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task SSE_Streams_Unicode_Prefixes_And_Never_Thinking_Or_Signatures(TranslationProviderKind kind)
    {
        var handler = new Handler(Transcript(kind, Answer), sse: true);
        var progress = new CollectedProgress();
        var provider = Provider(kind, handler);
        var answer = await provider.TranslateRawStreamingAsync(Request(), progress, default);
        Assert.Equal(Answer, answer);
        Assert.True(progress.Updates.Count > 1);
        Assert.Equal("你好世界", progress.Updates[^1].Translation);
        Assert.Contains(progress.Updates, value => value.Translation == "你");
        Assert.All(progress.Updates, update => Assert.DoesNotContain("SECRET_REASONING", JsonSerializer.Serialize(update)));
        Assert.DoesNotContain("SECRET_SIGNATURE", answer);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("text/event-stream", handler.Accept);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Direct_Vision_Streaming_Provides_Placement_And_Ocr_Document_Keeps_Groups(TranslationProviderKind kind)
    {
        const string vision = """{"blocks":[{"id":"p1","box":[100,100,300,400],"area":[80,80,320,420],"flow":"v","translation":"你好世界"}]}""";
        var progress = new CollectedProgress();
        var handler = new Handler(Transcript(kind, vision), sse: true);
        var raw = await Provider(kind, handler).TranslateRawStreamingAsync(Request() with { IsDirectVision = true, ImagePng = [1], ImageSize = new(1000, 1000) }, progress, default);
        Assert.Equal(vision, raw);
        var segment = Assert.Single(progress.Updates[^1].ImageSegments!);
        Assert.Equal(new PixelRect(100, 100, 200, 300), segment.Bounds);
        Assert.True(segment.IsVertical);
        var ocrHandler = new Handler(Envelope(kind, """{"blocks":[{"ids":["b1"],"translation":"你好世界"}]}"""));
        await Provider(kind, ocrHandler).TranslateRawAsync(Request() with { IsOcrDocument = true }, default);
        using var json = JsonDocument.Parse(ocrHandler.Body!);
        Assert.Equal(DeepSeekTranslationProvider.OcrGroupingPrompt, json.RootElement.GetProperty(kind == TranslationProviderKind.OpenAI ? "instructions" : "system").GetString());
        Assert.DoesNotContain("input_image", ocrHandler.Body);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task EOF_Without_Provider_Terminal_Event_Does_Not_Commit_Complete_JSON(TranslationProviderKind kind)
    {
        var stream = Transcript(kind, Answer, terminal: false);
        var handler = new Handler(stream, sse: true);
        var progress = new CollectedProgress();
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => Provider(kind, handler).TranslateRawStreamingAsync(Request(), progress, default));
        Assert.Equal("你好世界", progress.Updates[^1].Translation);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Truncation_And_Refusal_Are_Rejected_Even_With_Valid_Translation_JSON(TranslationProviderKind kind)
    {
        foreach (var reason in new[] { "truncated", "refused" })
        {
            var response = kind == TranslationProviderKind.OpenAI
                ? reason == "truncated" ? """{"status":"incomplete","output":[]} """ : """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"refused"}]}]}"""
                : JsonSerializer.Serialize(new { type = "message", role = "assistant", content = new[] { new { type = "text", text = Answer } }, stop_reason = reason == "truncated" ? "max_tokens" : "refusal" });
            await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(kind, new Handler(response)).TranslateRawAsync(Request(), default));
        }
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, 401, typeof(TranslationAuthenticationException))]
    [InlineData(TranslationProviderKind.Claude, 403, typeof(TranslationAuthenticationException))]
    [InlineData(TranslationProviderKind.OpenAI, 429, typeof(TranslationRateLimitException))]
    [InlineData(TranslationProviderKind.Claude, 529, typeof(TranslationUnavailableException))]
    [InlineData(TranslationProviderKind.OpenAI, 400, typeof(TranslationConfigurationException))]
    [InlineData(TranslationProviderKind.Claude, 404, typeof(TranslationConfigurationException))]
    [InlineData(TranslationProviderKind.Claude, 302, typeof(TranslationConfigurationException))]
    public async Task Http_Error_Is_Mapped_Without_Leaking_Server_Body_Or_Retrying(TranslationProviderKind kind, int code, Type errorType)
    {
        var handler = new Handler("SECRET_RESPONSE_BODY", (HttpStatusCode)code);
        var error = await Record.ExceptionAsync(() => Provider(kind, handler).TranslateRawAsync(Request(), default));
        Assert.IsType(errorType, error);
        Assert.DoesNotContain("SECRET_RESPONSE_BODY", error!.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Bad_JSON_Stream_And_Error_Event_Are_Never_Treated_As_Success(TranslationProviderKind kind)
    {
        foreach (var transcript in new[] { "data: {invalid}\n\n", "event: error\ndata: {\"type\":\"error\",\"error\":{\"message\":\"SECRET_BODY\"}}\n\n" })
        {
            var error = await Record.ExceptionAsync(() => Provider(kind, new Handler(transcript, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
            Assert.True(error is TranslationFormatException or TranslationUnavailableException);
            Assert.DoesNotContain("SECRET_BODY", error!.Message);
        }
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Missing_Key_And_Invalid_Page_Image_Stop_Before_HTTP(TranslationProviderKind kind)
    {
        var handler = new Handler(Envelope(kind, Answer));
        await Assert.ThrowsAsync<TranslationAuthenticationException>(() => Provider(kind, handler, new Secrets(null)).TranslateRawAsync(Request(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(kind, handler).TranslateRawAsync(Request() with { IsPageText = true, ImagePng = [1] }, default));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Timeout_Is_Mapped_And_User_Cancellation_Is_Preserved(TranslationProviderKind kind)
    {
        var handler = new Handler(Envelope(kind, Answer)) { Delay = TimeSpan.FromSeconds(3) };
        var provider = Provider(kind, handler, timeout: TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => provider.TranslateRawAsync(Request(), default));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Started = started;
        using var cancelled = new CancellationTokenSource();
        var task = Provider(kind, handler).TranslateRawAsync(Request(), cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Completed_Responses_Can_Contain_All_Text_Without_Deltas_And_Reject_Inconsistent_Final()
    {
        var final = Event("response.completed", new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Envelope(TranslationProviderKind.OpenAI, Answer)) });
        Assert.Equal(Answer, await Provider(TranslationProviderKind.OpenAI, new Handler(final, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
        var mismatch = Event("response.output_text.delta", new { type = "response.output_text.delta", delta = "{\"different\":1}" }) + final;
        await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(TranslationProviderKind.OpenAI, new Handler(mismatch, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
    }

    [Fact]
    public async Task SSE_Comments_CRLF_Multiline_Data_And_Unterminated_Final_Frame_Are_Handled()
    {
        var final = Envelope(TranslationProviderKind.OpenAI, Answer);
        var data = ": comment\r\nevent: future_event\r\ndata: {\"type\":\"future_event\"}\r\n\r\nevent: response.completed\r\ndata: {\"type\":\"response.completed\",\r\ndata: \"response\":" + final + "}";
        Assert.Equal(Answer, await Provider(TranslationProviderKind.OpenAI, new Handler(data, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
    }

    [Fact]
    public async Task Claude_Thinking_Block_Cannot_Smuggle_Text_Deltas_And_Open_Blocks_Cannot_Complete()
    {
        var start = Event("message_start", new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        var thinking = Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } });
        var invalid = start + thinking + Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = Answer } });
        await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(TranslationProviderKind.Claude, new Handler(invalid, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
        var open = Transcript(TranslationProviderKind.Claude, Answer).Replace(Event("content_block_stop", new { type = "content_block_stop", index = 1 }), "");
        await Assert.ThrowsAsync<TranslationFormatException>(() => Provider(TranslationProviderKind.Claude, new Handler(open, sse: true)).TranslateRawStreamingAsync(Request(), new CollectedProgress(), default));
    }

    public static string Envelope(TranslationProviderKind kind, string answer) => kind == TranslationProviderKind.OpenAI
        ? JsonSerializer.Serialize(new { status = "completed", output = new object[] {
            new { type = "reasoning", summary = new[] { new { type = "summary_text", text = "SECRET_REASONING" } } },
            new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } }, usage = new { input_tokens = 200, output_tokens = 55 } })
        : JsonSerializer.Serialize(new { type = "message", role = "assistant", content = new object[] {
            new { type = "thinking", thinking = "SECRET_REASONING", signature = "SECRET_SIGNATURE" }, new { type = "text", text = answer } }, stop_reason = "end_turn", usage = new { input_tokens = 200, output_tokens = 55 } });

    public static string Transcript(TranslationProviderKind kind, string answer, bool terminal = true)
    {
        var result = new StringBuilder();
        if (kind == TranslationProviderKind.OpenAI)
        {
            result.Append(Event("response.created", new { type = "response.created", response = new { status = "in_progress" } }));
            result.Append(Event("response.reasoning_summary_text.delta", new { type = "response.reasoning_summary_text.delta", delta = "SECRET_REASONING" }));
            foreach (var chunk in Chunks(answer)) result.Append(Event("response.output_text.delta", new { type = "response.output_text.delta", delta = chunk }));
            if (terminal) result.Append(Event("response.completed", new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(Envelope(kind, answer)) }));
        }
        else
        {
            result.Append(Event("message_start", new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>(), usage = new { input_tokens = 200, output_tokens = 1 } } }));
            result.Append(Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } }));
            result.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "SECRET_REASONING" } }));
            result.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "SECRET_SIGNATURE" } }));
            result.Append(Event("content_block_stop", new { type = "content_block_stop", index = 0 }));
            result.Append(Event("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } }));
            foreach (var chunk in Chunks(answer)) result.Append(Event("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = chunk } }));
            result.Append(Event("content_block_stop", new { type = "content_block_stop", index = 1 }));
            result.Append(Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 55 } }));
            if (terminal) result.Append(Event("message_stop", new { type = "message_stop" }));
        }
        return result.ToString();
    }

    private static IEnumerable<string> Chunks(string value) { foreach (var character in value) yield return character.ToString(); }
    private static string Event(string name, object data) => "event: " + name + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";
    private static ApiTranslationProvider Provider(TranslationProviderKind kind, Handler handler, Secrets? secrets = null,
        string? model = null, ThinkingLevel level = ThinkingLevel.None, TimeSpan? timeout = null)
    {
        var options = new TranslationApiOptions { Provider = kind, Model = model ?? (kind == TranslationProviderKind.OpenAI ? "gpt-6-luna" : "claude-sonnet-5-5"),
            BaseUrl = kind == TranslationProviderKind.OpenAI ? "https://api.openai.com/v1" : "https://api.anthropic.com/v1", ThinkingLevel = level, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return kind == TranslationProviderKind.OpenAI ? new OpenAiResponsesTranslationProvider(new HttpClient(handler), secrets ?? new(), options)
            : new ClaudeMessagesTranslationProvider(new HttpClient(handler), secrets ?? new(), options);
    }

    private sealed class Secrets(string? key = "test-only-key") : ISecretStore
    {
        internal string? LastKey;
        public Task<string?> GetAsync(string name, CancellationToken token = default) { LastKey = name; return Task.FromResult(key); }
        public Task SetAsync(string name, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK, bool sse = false) : HttpMessageHandler
    {
        internal string? Body, Authorization, ClaudeKey, AnthropicVersion, Accept;
        internal Uri? RequestUri;
        internal int CallCount;
        internal TimeSpan Delay;
        internal TaskCompletionSource? Started;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            CallCount++; RequestUri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            ClaudeKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            AnthropicVersion = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null;
            Accept = request.Headers.Accept.SingleOrDefault()?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(token); Started?.TrySetResult();
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token);
            return new(status) { Content = new StringContent(body, Encoding.UTF8, sse ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class CollectedProgress : IProgress<TranslationStreamUpdate>
    { internal List<TranslationStreamUpdate> Updates { get; } = []; public void Report(TranslationStreamUpdate update) => Updates.Add(update); }
}
