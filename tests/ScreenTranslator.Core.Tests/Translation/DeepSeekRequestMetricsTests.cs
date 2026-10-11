using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class DeepSeekRequestMetricsTests
{
    [Fact]
    public async Task Streaming_Measures_Reasoning_Delay_And_Usage_Without_Exposing_Any_Text()
    {
        const string privateReasoning = "never-store-private-reasoning-9284";
        var handler = new Handler(new ScriptedStream([
            (Sse(new { model = "deepseek-v4.1-flash", choices = new[] { new { delta = new { reasoning_content = privateReasoning } } } }), 1),
            (Sse(new { choices = new[] { new { delta = new { content = "{\"blocks\":[{\"id\":\"b1\",\"translation\":\"你好\"}]}" } } } }), 40),
            (Sse(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 120, completion_tokens = 35, completion_tokens_details = new { reasoning_tokens = 20 } } }) + "data: [DONE]\n\n", 1)
        ]));
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low });
        DeepSeekRequestMetrics? metrics = null;
        provider.RequestCompleted += value => metrics = value;
        await provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default);
        Assert.NotNull(metrics);
        Assert.Equal(ThinkingLevel.Low, metrics.RequestedThinking);
        Assert.Equal("low", metrics.ReasoningEffort);
        Assert.Equal(16384, metrics.MaxTokens);
        Assert.Equal(privateReasoning.Length, metrics.ReasoningCharacters);
        Assert.True(metrics.FirstContentMilliseconds > metrics.FirstReasoningMilliseconds);
        Assert.True(metrics.ReasoningToContentMilliseconds >= 20);
        Assert.Equal(20, metrics.ReasoningTokens);
        Assert.Equal(35, metrics.CompletionTokens);
        Assert.Equal(15, metrics.ContentTokens);
        Assert.Equal("stop", metrics.FinishReason);
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics.Outcome);
        var safeJson = JsonSerializer.Serialize(metrics);
        Assert.DoesNotContain(privateReasoning, safeJson);
        Assert.DoesNotContain("source-private-913", safeJson);
        Assert.DoesNotContain("fake-secret-740", safeJson);
        Assert.DoesNotContain("你好", safeJson);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.True(payload.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task Usage_Only_Chunk_Is_Compatible_And_Missing_Usage_Stays_Null()
    {
        var content = Sse(new { choices = new[] { new { delta = new { content = "{\"blocks\":[]}" }, finish_reason = "stop" } } });
        var handler = new Handler(new ScriptedStream([(content + Sse(new { choices = Array.Empty<object>(), usage = new { completion_tokens = 7, prompt_tokens = 20 } }) + "data: [DONE]\n\n", 0)]));
        var provider = Provider(handler);
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default);
        Assert.Equal(7, metrics!.CompletionTokens);
        Assert.Null(metrics.ReasoningTokens);
        Assert.Null(metrics.ContentTokens);
        Assert.Null(metrics.FirstReasoningMilliseconds);
    }

    [Fact]
    public async Task Reasoning_Only_Stream_Has_A_Clear_First_Content_Timeout()
    {
        var handler = new Handler(new ScriptedStream([
            (Sse(new { choices = new[] { new { delta = new { reasoning_content = "count only" } } } }), 0),
            ("data: [DONE]\n\n", 1000)
        ]));
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low, Timeout = TimeSpan.FromSeconds(2), FirstContentTimeout = TimeSpan.FromMilliseconds(60) });
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        var error = await Assert.ThrowsAsync<TranslationFirstContentTimeoutException>(() => provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default));
        Assert.Contains("思考等待超时", error.Message);
        Assert.Equal(DeepSeekRequestOutcome.FirstContentTimeout, metrics!.Outcome);
        Assert.NotNull(metrics.FirstReasoningMilliseconds);
        Assert.Null(metrics.FirstContentMilliseconds);
        Assert.Equal("low", metrics.ReasoningEffort);
    }

    [Fact]
    public async Task First_Content_Deadline_Stops_After_Content_And_Does_Not_Cut_Off_The_Answer()
    {
        var handler = new Handler(new ScriptedStream([
            (Sse(new { choices = new[] { new { delta = new { content = "{\"blocks\":[]}" } } } }), 0),
            (Sse(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 90)
        ]));
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low, Timeout = TimeSpan.FromSeconds(2), FirstContentTimeout = TimeSpan.FromMilliseconds(40) });
        Assert.Equal("{\"blocks\":[]}", await provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default));
    }

    [Fact]
    public async Task Truncated_Stream_Still_Reports_Finish_And_Reasoning_Usage()
    {
        var handler = new Handler(new ScriptedStream([(Sse(new { choices = new[] { new { delta = new { content = "{" }, finish_reason = "length" } }, usage = new { completion_tokens = 16384, completion_tokens_details = new { reasoning_tokens = 16380 } } }), 0)]));
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low });
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await Assert.ThrowsAsync<TranslationFormatException>(() => provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default));
        Assert.Equal(DeepSeekRequestOutcome.Truncated, metrics!.Outcome);
        Assert.Equal("length", metrics.FinishReason);
        Assert.Equal(16380, metrics.ReasoningTokens);
    }

    [Fact]
    public async Task Metrics_Observers_Cannot_Break_Translation_Or_Other_Observers()
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"finish_reason":"stop","message":{"content":"{}","reasoning_content":"sensitive-thought"}}],"usage":{"prompt_tokens":3,"completion_tokens":10,"completion_tokens_details":{"reasoning_tokens":8}}}""");
        var provider = Provider(handler);
        provider.RequestCompleted += _ => throw new InvalidOperationException("Observer failed");
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        Assert.Equal("{}", await provider.TranslateRawAsync(Request(), default));
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics!.Outcome);
        Assert.Null(metrics.FirstReasoningMilliseconds); // Non-streaming cannot reveal its start time.
        Assert.Equal("sensitive-thought".Length, metrics.ReasoningCharacters);
        Assert.Equal(2, metrics.ContentTokens);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task Low_Image_Guidance_Is_Short_And_Can_Be_Disabled_For_A_True_Baseline()
    {
        var firstHandler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        var secondHandler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        var request = Request() with { ImagePng = [1, 2, 3] };
        await Provider(firstHandler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low }).TranslateRawAsync(request, default);
        await Provider(secondHandler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low, UseBriefTranslationGuidance = false }).TranslateRawAsync(request, default);
        Assert.Contains(DeepSeekTranslationProvider.BriefTranslationGuidance, firstHandler.Body);
        Assert.DoesNotContain(DeepSeekTranslationProvider.BriefTranslationGuidance, secondHandler.Body);
        Assert.True(DeepSeekTranslationProvider.BriefTranslationGuidance.Length < 200);
        Assert.Contains("\"reasoning_effort\":\"low\"", firstHandler.Body);
        Assert.Contains("\"max_tokens\":16384", firstHandler.Body);
    }

    [Theory]
    [InlineData(VisionRequestTask.Translation, ThinkingLevel.Low, 512, 16384, "vision-translation")]
    [InlineData(VisionRequestTask.Placement, ThinkingLevel.None, 8192, 8192, "vision-placement")]
    [InlineData(VisionRequestTask.Placement, ThinkingLevel.None, 128, 256, "vision-placement")]
    public async Task Parallel_Vision_Task_Selects_Its_Prompt_And_An_Honest_Output_Budget(
        VisionRequestTask task, ThinkingLevel level, int requested, int actual, string kind)
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.High, UseBriefTranslationGuidance = false });
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await provider.TranslateRawAsync(Request() with { IsDirectVision = true, ImagePng = [1, 2, 3],
            ImageSize = new(1000, 1000), VisionTask = task, ThinkingOverride = level, MaxOutputTokensOverride = requested }, default);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(actual, payload.RootElement.GetProperty("max_tokens").GetInt32());
        var system = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.StartsWith(task == VisionRequestTask.Translation ? ParallelVisionTranslation.TranslationPrompt : ParallelVisionTranslation.PlacementPrompt, system);
        Assert.Contains("Omit already-target-language text, photo/copyright credits", system);
        Assert.Equal(level, metrics!.RequestedThinking);
        Assert.Equal(kind, metrics.TaskKind);
        Assert.Equal(actual, metrics.MaxTokens);
    }

    [Fact]
    public async Task Parallel_Translation_Streams_Anchored_Text_Without_Coordinates()
    {
        var value = """{"blocks":[{"id":"p1","regions":[],"source":"Hello world","end":"Hello world","translation":"你好世界"}]}""";
        var handler = new Handler(new ScriptedStream([(Sse(new { choices = new[] { new { delta = new { content = value }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 0)]));
        var sink = new CollectingProgress();
        await Provider(handler).TranslateRawStreamingAsync(Request() with { IsDirectVision = true,
            VisionTask = VisionRequestTask.Translation, ImagePng = [1, 2], ImageSize = new(1000, 1000) }, sink, default);
        var block = Assert.Single(sink.Last!.ImageSegments!);
        Assert.Equal("你好世界", block.Translation);
        Assert.Equal("Hello world", block.SourceText);
        Assert.False(block.Bounds.IsUsable);
        Assert.False(sink.Last.IsLayoutUpdate);
    }

    [Fact]
    public async Task Placement_Metadata_Has_One_Normalized_Unit_And_No_Pixel_Image_Size()
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        await Provider(handler).TranslateRawAsync(Request() with { IsDirectVision = true, VisionTask = VisionRequestTask.Placement,
            ImagePng = [1, 2], ImageSize = new(1254, 1254),
            VisionRegions = [new("r1", new(1000, 300, 200, 500), default)] }, default);
        using var payload = JsonDocument.Parse(handler.Body!);
        var text = payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        using var metadata = JsonDocument.Parse(text);
        Assert.False(metadata.RootElement.TryGetProperty("imageSize", out _));
        Assert.Equal("normalized-1000", metadata.RootElement.GetProperty("coordinateSystem").GetString());
        var box = metadata.RootElement.GetProperty("regions")[0].GetProperty("box").EnumerateArray().Select(point => point.GetInt32()).ToArray();
        Assert.Equal(new[] { 797, 239, 957, 638 }, box);
        Assert.All(box, point => Assert.InRange(point, 0, 1000));
        Assert.DoesNotContain("1254", text);
    }

    [Fact]
    public async Task Placement_Without_Image_Size_Omits_Region_Pixel_Boxes_Instead_Of_Guessing_A_Unit()
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        await Provider(handler).TranslateRawAsync(Request() with { IsDirectVision = true, VisionTask = VisionRequestTask.Placement,
            ImagePng = [1, 2], VisionRegions = [new("r1", new(1000, 300, 200, 500), default)] }, default);
        using var payload = JsonDocument.Parse(handler.Body!);
        using var metadata = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.False(metadata.RootElement.GetProperty("regions")[0].TryGetProperty("box", out _));
    }

    [Fact]
    public async Task Parallel_Placement_Streams_Layout_And_No_Translation()
    {
        var value = """{"blocks":[{"id":"p1","regions":[],"source":"Hello world","box":[100,100,300,200],"area":[90,90,310,210],"flow":"h"}]}""";
        var handler = new Handler(new ScriptedStream([(Sse(new { choices = new[] { new { delta = new { content = value }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 0)]));
        var sink = new CollectingProgress();
        await Provider(handler).TranslateRawStreamingAsync(Request() with { IsDirectVision = true,
            VisionTask = VisionRequestTask.Placement, ImagePng = [1, 2], ImageSize = new(1000, 1000) }, sink, default);
        var block = Assert.Single(sink.Last!.ImageSegments!);
        Assert.True(sink.Last.IsLayoutUpdate);
        Assert.True(block.Bounds.IsUsable);
        Assert.Equal("", block.Translation);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Distinct_From_Reasoning_Timeout()
    {
        var handler = new Handler(new ScriptedStream([(Sse(new { choices = new[] { new { delta = new { reasoning_content = "count only" } } } }), 0), ("data: [DONE]\n\n", 1000)]));
        var provider = Provider(handler, new DeepSeekOptions { Timeout = TimeSpan.FromSeconds(2) });
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), cancellation.Token));
        Assert.Equal(DeepSeekRequestOutcome.Cancelled, metrics!.Outcome);
    }

    [Fact]
    public async Task Interrupted_Raw_Response_Is_Not_Reported_As_A_Success()
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"finish_reason":"aborted","message":{"content":"{}"}}]}""");
        var provider = Provider(handler);
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => provider.TranslateRawAsync(Request(), default));
        Assert.Equal(DeepSeekRequestOutcome.Interrupted, metrics!.Outcome);
        Assert.Equal("aborted", metrics.FinishReason);
    }

    [Fact]
    public async Task Stream_Read_Io_Failure_Is_Diagnosed_As_A_Network_Error()
    {
        var provider = Provider(new Handler(new FailingReadStream()));
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await Assert.ThrowsAsync<TranslationUnavailableException>(() => provider.TranslateRawStreamingAsync(Request(), new ProgressSink(), default));
        Assert.Equal(DeepSeekRequestOutcome.NetworkError, metrics!.Outcome);
    }

    [Fact]
    public async Task Responsive_Retry_Produces_Actual_Low_Then_None_Payloads_And_Two_Safe_Metrics()
    {
        var handler = new RetryHandler();
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new Secret(), new DeepSeekOptions
        { ThinkingLevel = ThinkingLevel.Low, FirstContentTimeout = TimeSpan.FromMilliseconds(50), Timeout = TimeSpan.FromSeconds(2) });
        var metrics = new List<DeepSeekRequestMetrics>(); provider.RequestCompleted += metrics.Add;
        var wrapper = new ResponsiveThinkingTranslationProvider(provider, ThinkingLevel.Low);
        var disclosed = 0; wrapper.FastRetryStarted += () => disclosed++;
        Assert.Equal("{\"blocks\":[]}", await wrapper.TranslateRawStreamingAsync(Request() with { ImagePng = [1, 2] }, new ProgressSink(), default));
        Assert.Equal(1, disclosed);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(new[] { ThinkingLevel.Low, ThinkingLevel.None }, metrics.Select(value => value.RequestedThinking));
        Assert.Equal(new[] { "low", "none" }, metrics.Select(value => value.ReasoningEffort));
        Assert.Equal(DeepSeekRequestOutcome.FirstContentTimeout, metrics[0].Outcome);
        Assert.Null(metrics[0].FirstContentMilliseconds);
        Assert.Equal(DeepSeekRequestOutcome.Success, metrics[1].Outcome);
        Assert.True(metrics[1].ElapsedMilliseconds >= 80); // None is not capped by the 50 ms first-content budget.
        Assert.DoesNotContain("count only", JsonSerializer.Serialize(metrics));
    }

    [Fact]
    public async Task Page_Text_Streams_By_Input_Id_With_Open_Translation_Strings_And_Actual_None_Parameters()
    {
        // Deliberately return page-2 first: DOM identity must not use output index.
        var fragments = new[] { "{\"blocks\":[{\"id\":\"page-2\",\"translation\":\"天", "气", "晴", "\"},{\"id\":\"page-1\",\"translation\":\"你", "好\"}]}" };
        var chunks = fragments.Select(text => (Sse(new { choices = new[] { new { delta = new { content = text } } } }), 1)).Append(
            (Sse(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 0)).ToArray();
        var handler = new Handler(new ScriptedStream(chunks));
        var provider = Provider(handler, new DeepSeekOptions { ThinkingLevel = ThinkingLevel.Low });
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        var sink = new HistoryProgress();
        var request = new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "", [
            new("page-1", "Hello", 1, default, 0), new("page-2", "The sky is clear", 1, default, 1)])
        { IsPageText = true, ThinkingOverride = ThinkingLevel.None };
        await provider.TranslateRawStreamingAsync(request, sink, default);
        Assert.Equal(new[] { "天", "天气", "天气晴" }, sink.Values.Take(3).Select(update => Assert.Single(update.ImageSegments!).Translation));
        Assert.All(sink.Values.Take(3), update => Assert.Equal("page-2", Assert.Single(update.ImageSegments!).Id));
        Assert.False(sink.Values[0].ImageSegments![0].IsComplete);
        Assert.False(sink.Values[2].ImageSegments![0].IsComplete);
        Assert.True(sink.Values[3].ImageSegments!.Single(block => block.Id == "page-2").IsComplete);
        var final = sink.Values.Last().ImageSegments!;
        Assert.Equal(new[] { "page-1", "page-2" }, final.Select(block => block.Id));
        Assert.Equal(new[] { "Hello", "The sky is clear" }, final.Select(block => block.SourceText));
        Assert.All(final, block => Assert.True(block.Bounds.IsEmpty));
        Assert.All(sink.Values, update => Assert.Equal("", update.StreamingTail));
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("disabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("none", payload.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(DeepSeekTranslationProvider.PageTextPrompt, payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("messages")[1].GetProperty("content").ValueKind);
        Assert.DoesNotContain("image_url", handler.Body);
        Assert.Equal("page-text", metrics!.TaskKind);
    }

    [Fact]
    public async Task Page_Text_Flag_Rejects_Accidental_Image_Fields_Before_Any_Request()
    {
        var handler = new Handler(new ScriptedStream([]));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(handler).TranslateRawStreamingAsync(
            Request() with { IsPageText = true, ImagePng = [1, 2] }, new ProgressSink(), default));
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task Ordinary_Text_Streaming_Does_Not_Start_Using_Page_Dom_Segments()
    {
        var handler = new Handler(new ScriptedStream([(Sse(new { choices = new[] { new { delta = new { content = "{\"blocks\":[{\"id\":\"b1\",\"translation\":\"你好\"}]}" }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 0)]));
        var sink = new HistoryProgress();
        await Provider(handler).TranslateRawStreamingAsync(Request(), sink, default);
        Assert.All(sink.Values, update => Assert.Null(update.ImageSegments));
        Assert.Equal("你好", sink.Values.Last().Translation);
    }

    [Fact]
    public async Task Page_Content_Exposes_Readable_Japanese_After_One_Http_Decode_And_RoundTrips_Special_Json_Characters()
    {
        const string source = "\uFEFF木曜日にAoiさんへ \"確認\" C:\\notes\\memo literal \\u0041\r\n<script>& café 😀";
        const string context = "引用 \"金曜日\" と path\\context\t確認";
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        var provider = Provider(handler);
        DeepSeekRequestMetrics? metrics = null; provider.RequestCompleted += value => metrics = value;
        await provider.TranslateRawAsync(new("auto", "zh-CN", TranslationStyle.Natural, context,
            [new("node-special", source, 1, default, 0)]) { IsPageText = true, ThinkingOverride = ThinkingLevel.None }, default);
        // Decode exactly the HTTP envelope once, as the server does. The model's
        // actual user.content should contain Japanese glyphs, not escaped glyphs.
        using var envelope = JsonDocument.Parse(handler.Body!);
        var messages = envelope.RootElement.GetProperty("messages");
        var content = messages[1].GetProperty("content").GetString()!;
        Assert.Contains("木曜日", content);
        Assert.DoesNotContain("\\u6728", content, StringComparison.OrdinalIgnoreCase);
        using var internalJson = JsonDocument.Parse(content);
        Assert.Equal(source, internalJson.RootElement.GetProperty("blocks")[0].GetProperty("text").GetString());
        Assert.Equal(context, internalJson.RootElement.GetProperty("context").GetString());
        Assert.Equal(DeepSeekTranslationProvider.PageTextPrompt, messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("image_url", handler.Body);
        var safeMetrics = JsonSerializer.Serialize(metrics);
        Assert.DoesNotContain("木曜日", safeMetrics);
        Assert.DoesNotContain("Aoi", safeMetrics);
        Assert.DoesNotContain("node-special", safeMetrics);
    }

    [Fact]
    public async Task Ordinary_Text_Keeps_Its_Original_Prompt_And_Internal_Encoding()
    {
        var handler = new Handler(new ScriptedStream([]), raw: """{"choices":[{"message":{"content":"{}"}}]}""");
        await Provider(handler).TranslateRawAsync(Request() with { Blocks = [new("b1", "木曜日", 1, default, 0)] }, default);
        using var envelope = JsonDocument.Parse(handler.Body!);
        var messages = envelope.RootElement.GetProperty("messages");
        Assert.Equal(DeepSeekTranslationProvider.SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Contains("\\u6728", messages[1].GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_Prompt_Is_Short_And_Preserves_Per_Node_Semantics()
    {
        Assert.InRange(DeepSeekTranslationProvider.PageTextPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, 80, 120);
        Assert.Contains("ONLY its own text", DeepSeekTranslationProvider.PageTextPrompt);
        Assert.Contains("do not move or copy", DeepSeekTranslationProvider.PageTextPrompt);
        Assert.Contains("do not infer gender", DeepSeekTranslationProvider.PageTextPrompt);
    }

    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "", [new("b1", "source-private-913", 1, default, 0)]);
    private static DeepSeekTranslationProvider Provider(Handler handler, DeepSeekOptions? options = null) => new(new HttpClient(handler), new Secret(), options ?? new DeepSeekOptions());
    private static string Sse(object chunk) => "data: " + JsonSerializer.Serialize(chunk) + "\n\n";
    private sealed class Secret : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake-secret-740");
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ProgressSink : IProgress<TranslationStreamUpdate> { public void Report(TranslationStreamUpdate value) { } }
    private sealed class CollectingProgress : IProgress<TranslationStreamUpdate>
    { internal TranslationStreamUpdate? Last; public void Report(TranslationStreamUpdate value) => Last = value; }
    private sealed class HistoryProgress : IProgress<TranslationStreamUpdate>
    { internal readonly List<TranslationStreamUpdate> Values = []; public void Report(TranslationStreamUpdate value) => Values.Add(value); }
    private sealed class Handler(Stream stream, string? raw = null) : HttpMessageHandler
    {
        internal string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = raw is null ? new StreamContent(stream) : new StringContent(raw, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RetryHandler : HttpMessageHandler
    {
        internal readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var chunks = Bodies.Count == 1
                ? new[] { (Sse(new { choices = new[] { new { delta = new { reasoning_content = "count only" } } } }), 0), ("data: [DONE]\n\n", 1000) }
                : new[] { (Sse(new { choices = new[] { new { delta = new { content = "{\"blocks\":[]}" }, finish_reason = "stop" } } }) + "data: [DONE]\n\n", 90) };
            return new(HttpStatusCode.OK) { Content = new StreamContent(new ScriptedStream(chunks)) };
        }
    }
    private class ScriptedStream((string Text, int DelayMilliseconds)[] chunks) : Stream
    {
        private int _index, _offset;
        private readonly byte[][] _chunks = chunks.Select(chunk => Encoding.UTF8.GetBytes(chunk.Text)).ToArray();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length) return 0;
            if (_offset == 0 && chunks[_index].DelayMilliseconds > 0) await Task.Delay(chunks[_index].DelayMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, _chunks[_index].Length - _offset);
            _chunks[_index].AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _chunks[_index].Length) { _offset = 0; _index++; }
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FailingReadStream : ScriptedStream
    {
        internal FailingReadStream() : base([]) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Fake transport read failure"));
    }
}
