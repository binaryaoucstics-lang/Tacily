using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationBenchmarkTests
{
    [Fact]
    public async Task RealStreamingRequestUsesOneSentenceCurrentThinkingAndActualUsage()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(client, new Secrets(), new() { ThinkingLevel = ThinkingLevel.None });
        var updates = new ProgressSink();
        var result = await TranslationBenchmark.RunAsync(provider, ThinkingLevel.None, CancellationToken.None, updates);
        Assert.Equal("测试译文", result.Translation); Assert.NotNull(result.FirstCharacterLatency);
        Assert.True(result.FirstCharacterLatency < result.Elapsed);
        Assert.Contains(updates.Text, text => text == "测试译文");
        Assert.Equal(28, result.OutputTokens); Assert.Equal(0, result.ReasoningTokens);
        Assert.Equal(1, handler.Calls);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains(TranslationBenchmark.SourceSentence, handler.Body!);
        Assert.Contains("zh-CN", handler.Body!);
    }

    [Fact]
    public async Task CompatibleBenchmarkExplicitlyRequestsUsageButNeverInventsItWhenMissing()
    {
        var handler = new Handler(false); using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(client, new Secrets(), new() { GenericChatCompletions = true, Model = "test-model" });
        var result = await TranslationBenchmark.RunAsync(provider, ThinkingLevel.Low, CancellationToken.None);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.True(payload.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Null(result.TokensPerSecond); Assert.Null(result.OutputTokens); Assert.Equal(BenchmarkRateKind.Unavailable, result.RateKind);
    }

    [Fact]
    public async Task InvalidTranslationDoesNotRetryAndMixTwoRequestsIntoOneMeasurement()
    {
        var handler = new Handler(answer: "{\"wrong\":\"格式错误\"}"); using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(client, new Secrets(), new());
        await Assert.ThrowsAsync<TranslationFormatException>(() => TranslationBenchmark.RunAsync(provider, ThinkingLevel.None, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(100, 60, false, 500, 2500, 6, 20d, BenchmarkRateKind.ContentGeneration)]
    [InlineData(100, null, true, 500, 2500, 6, 50d, BenchmarkRateKind.ContentGeneration)]
    [InlineData(100, null, false, 500, 2500, 5, 20d, BenchmarkRateKind.OverallOutput)]
    [InlineData(null, null, false, 500, 2500, 5, null, BenchmarkRateKind.Unavailable)]
    [InlineData(100, 120, false, 500, 2500, 5, null, BenchmarkRateKind.Unavailable)]
    [InlineData(100, 0, true, 500, 500, 5, null, BenchmarkRateKind.Unavailable)]
    public void TpsExcludesKnownReasoningAndTrailingUsageWaitOrIsLabeledOverall(int? output, int? reasoning, bool noReasoning,
        long first, long last, int totalSeconds, double? expected, BenchmarkRateKind kind)
    {
        var result = TranslationBenchmark.CalculateRate(output, reasoning, noReasoning, first, last, TimeSpan.FromSeconds(totalSeconds));
        Assert.Equal(expected, result.Tps); Assert.Equal(kind, result.Kind);
    }

    private sealed class ProgressSink : IProgress<TranslationStreamUpdate>
    { public List<string> Text { get; } = []; public void Report(TranslationStreamUpdate update) => Text.Add(update.Translation); }
    private sealed class Handler(bool usage = true, string answer = "{\"blocks\":[{\"id\":\"benchmark\",\"translation\":\"测试译文\"}]}") : HttpMessageHandler
    {
        public int Calls; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            var first = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = answer } } } });
            var tail = usage ? "data: {\"choices\":[],\"usage\":{\"completion_tokens\":28,\"completion_tokens_details\":{\"reasoning_tokens\":0}}}\n\n" : "";
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + first + "\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + tail + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class Secrets : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<string?>("test-only-key");
        public Task SetAsync(string key, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
