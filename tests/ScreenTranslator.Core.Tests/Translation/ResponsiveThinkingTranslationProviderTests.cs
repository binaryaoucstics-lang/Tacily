using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ResponsiveThinkingTranslationProviderTests
{
    [Fact]
    public async Task Reasoning_Only_First_Timeout_Makes_One_Disclosed_None_Request()
    {
        var inner = new Provider((request, _, _) => request.ThinkingOverride == ThinkingLevel.None
            ? Task.FromResult("{}") : Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")));
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low);
        var disclosed = 0; wrapper.FastRetryStarted += () => disclosed++;
        Assert.Equal("{}", await wrapper.TranslateRawStreamingAsync(Request(), new Progress(), default));
        Assert.Equal(2, inner.Requests.Count);
        Assert.Null(inner.Requests[0].ThinkingOverride);
        Assert.Equal(ThinkingLevel.None, inner.Requests[1].ThinkingOverride);
        Assert.Equal(1, disclosed);
    }

    [Fact]
    public async Task A_Second_First_Content_Timeout_Propagates_Without_A_Third_Request()
    {
        var inner = new Provider((_, _, _) => Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")));
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low);
        await Assert.ThrowsAsync<TranslationFirstContentTimeoutException>(() => wrapper.TranslateRawStreamingAsync(Request(), new Progress(), default));
        Assert.Equal(2, inner.Requests.Count);
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("rate-limit")]
    [InlineData("network")]
    [InlineData("format")]
    public async Task Other_Failures_Do_Not_Trigger_Compensation(string kind)
    {
        TranslationException failure = kind switch { "authentication" => new TranslationAuthenticationException("auth"),
            "rate-limit" => new TranslationRateLimitException("rate"), "network" => new TranslationUnavailableException("network"),
            _ => new TranslationFormatException("format") };
        var inner = new Provider((_, _, _) => Task.FromException<string>(failure));
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low);
        Assert.Same(failure, await Record.ExceptionAsync(() => wrapper.TranslateRawStreamingAsync(Request(), new Progress(), default)));
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(ThinkingLevel.High, true, true)]
    [InlineData(ThinkingLevel.None, true, true)]
    [InlineData(ThinkingLevel.Low, false, true)]
    [InlineData(ThinkingLevel.Low, true, false)]
    public async Task Requested_Mode_Text_Only_And_Disabled_Policy_Are_Respected(ThinkingLevel level, bool image, bool enabled)
    {
        var inner = new Provider((_, _, _) => Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")));
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low, enabled);
        await Assert.ThrowsAsync<TranslationFirstContentTimeoutException>(() => wrapper.TranslateRawStreamingAsync(
            Request() with { ThinkingOverride = level, ImagePng = image ? [1] : null }, new Progress(), default));
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Any_Answer_Or_Json_Prefix_Prevents_A_Second_Response(bool jsonPrefix)
    {
        var inner = new Provider((_, progress, _) =>
        {
            if (!jsonPrefix) progress.Report(new("已经返回的译文"));
            return Task.FromException<string>(new TranslationFirstContentTimeoutException("wait", hasReceivedContent: jsonPrefix));
        });
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low);
        await Assert.ThrowsAsync<TranslationFirstContentTimeoutException>(() => wrapper.TranslateRawStreamingAsync(Request(), new Progress(), default));
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Caller_Cancel_During_First_Timeout_Does_Not_Send_A_Replacement()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new Provider((_, _, _) => { cancellation.Cancel(); return Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")); });
        var wrapper = new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low);
        var disclosed = 0; wrapper.FastRetryStarted += () => disclosed++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapper.TranslateRawStreamingAsync(Request(), new Progress(), cancellation.Token));
        Assert.Single(inner.Requests);
        Assert.Equal(0, disclosed);
    }

    [Fact]
    public async Task Late_Progress_From_The_First_Attempt_Is_Ignored()
    {
        IProgress<TranslationStreamUpdate>? first = null;
        var inner = new Provider((request, progress, _) =>
        {
            if (request.ThinkingOverride != ThinkingLevel.None)
            { first = progress; return Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")); }
            first!.Report(new("stale answer"));
            progress.Report(new("new answer"));
            return Task.FromResult("{}");
        });
        var progress = new Progress();
        await new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low).TranslateRawStreamingAsync(Request(), progress, default);
        Assert.Equal(new[] { "new answer" }, progress.Values);
    }

    [Fact]
    public async Task Non_Streaming_Cannot_Automatically_Infer_First_Content_And_Does_Not_Retry()
    {
        var inner = new Provider((_, _, _) => Task.FromException<string>(new TranslationFirstContentTimeoutException("wait")));
        await Assert.ThrowsAsync<TranslationFirstContentTimeoutException>(() => new ResponsiveThinkingTranslationProvider(inner, ThinkingLevel.Low).TranslateRawAsync(Request(), default));
        Assert.Single(inner.Requests);
    }

    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "", []) { ImagePng = [1, 2] };
    private sealed class Progress : IProgress<TranslationStreamUpdate>
    { internal readonly List<string> Values = []; public void Report(TranslationStreamUpdate value) => Values.Add(value.Translation); }
    private sealed class Provider(Func<TranslationRequest, IProgress<TranslationStreamUpdate>, CancellationToken, Task<string>> send) : ITranslationProvider, IStreamingTranslationProvider
    {
        internal readonly List<TranslationRequest> Requests = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) => TranslateRawStreamingAsync(request, new Progress(), token);
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        { Requests.Add(request); return send(request, progress, token); }
    }
}
