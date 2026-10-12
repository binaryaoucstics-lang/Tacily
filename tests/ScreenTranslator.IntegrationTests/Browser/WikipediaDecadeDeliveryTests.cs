using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Browser;

/// <summary>
/// Offline, deterministic development phrases from the verified provider defect.
/// Runs the production provider over in-memory HTTP/SSE and the unchanged page
/// service. Scripted answers prove delivery/validation, not live model quality.
/// </summary>
public sealed class WikipediaDecadeDeliveryTests
{
    private const string DecadeSource = "1990s and early 2000s";
    private const string NeighborSource = " saw new research.";
    private const string QuantitySource = " translates roughly enough text to fill 1 million books in one day.";
    private const string BroadCentury = "20世纪90年代至21世纪初";
    private const string NeighborTranslation = "出现了新的研究。";
    private const string QuantityTranslation = "一天内翻译的文本量大约足以填满100万本书。";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData("1990年代和2000年代初")]
    [InlineData("20世纪90年代和2000年代早期")]
    public async Task BroadCenturyRetriesOnlyOwnerThenPublishesAndReplaysSafeCache(string corrected)
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((call, text) => text == DecadeSource
            ? call == 1 ? BroadCentury : corrected : SafeTranslation(text));
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var source = Source("initial");
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = Run(service, source, Options(2), updates);
        try
        {
            await handler.PrefixRead.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Empty(updates);
            Assert.Equal(0, cache.Count);
        }
        finally { handler.ReleaseFinal.TrySetResult(); }

        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, cache.Count);
        Assert.Equal(source.Select(block => block.Id), result.Blocks.Select(block => block.Id));
        Assert.Equal(new[] { corrected, NeighborTranslation, QuantityTranslation }, result.Blocks.Select(block => block.Translation));
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed && !update.FromCache));
        Assert.DoesNotContain(updates, update => update.Translation == BroadCentury);
        AssertRequests(handler.Bodies, expectSemanticRetry: true);

        var replayUpdates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var replaySource = Source("new-dom");
        var replayService = new BrowserPageTranslationService(_ =>
            throw new InvalidOperationException("Validated fragments must replay without a provider."), cache: cache);
        var replay = await Run(replayService, replaySource, Options(2), replayUpdates).WaitAsync(TestDeadline);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(3, replay.CacheHits);
        Assert.Equal(3, replay.CompletedCount);
        Assert.Equal(0, replay.FailedCount);
        Assert.Equal(replaySource.Select(block => block.Id), replay.Blocks.Select(block => block.Id));
        Assert.Equal(result.Blocks.Select(block => block.Translation), replay.Blocks.Select(block => block.Translation));
        Assert.All(replayUpdates, update => Assert.True(update.IsComplete && update.FromCache && !update.Failed));
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Theory]
    [InlineData(BroadCentury)]
    [InlineData("1980年代和2000年代初")]
    [InlineData("1990年代和2010年代初")]
    [InlineData("1990年代和2000年代末")]
    [InlineData("1990年代和2000年代")]
    [InlineData("1990年代")]
    public async Task RepeatedWrongDecadesOrPhaseFailWithinBudgetAndOnlyQuantityCaches(string wrong)
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((_, text) => text == DecadeSource ? wrong : SafeTranslation(text));
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = Run(service, Source("wrong"), Options(6), updates);
        try
        {
            await handler.PrefixRead.Task.WaitAsync(TestDeadline);
            Assert.Empty(updates);
            Assert.Equal(0, cache.Count);
        }
        finally { handler.ReleaseFinal.TrySetResult(); }
        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(2, result.RequestCount); // Same rejected answer stops the retry loop.
        Assert.Equal(2, handler.Bodies.Count);
        AssertRejectedParagraphWithSafeQuantity(result, updates);
        Assert.Equal(1, cache.Count);
        AssertRequests(handler.Bodies, expectSemanticRetry: true);

        var repeated = await Run(service, Source("reopened"), Options(6)).WaitAsync(TestDeadline);
        Assert.Equal(1, repeated.RequestCount); // Today's guard still validates the new answer.
        Assert.Equal(1, repeated.CacheHits);
        Assert.Equal(2, repeated.FailedCount);
        Assert.Equal(1, repeated.CompletedCount);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(1, cache.Count);
        Assert.All(repeated.Blocks.Take(2), update => Assert.True(update.Failed && update.Translation.Length == 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OriginalRequestBudgetRemainsRequiredForCorrection(int budget)
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((call, text) => text == DecadeSource
            ? call == 1 ? BroadCentury : "1990年代和2000年代初" : SafeTranslation(text));
        handler.ReleaseFinal.TrySetResult();
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var result = await Run(service, Source("budget"), Options(budget)).WaitAsync(TestDeadline);
        Assert.Equal(budget, result.RequestCount);
        Assert.Equal(budget, handler.Bodies.Count);
        Assert.Equal(budget == 1 ? 2 : 0, result.FailedCount);
        Assert.Equal(budget == 1 ? 1 : 3, result.CompletedCount);
        Assert.Equal(budget == 1 ? 1 : 3, cache.Count);
    }

    [Fact]
    public async Task NeighborCannotReceiveOwnersDecadesEvenWhenOwnerEventuallyCorrects()
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((call, text) => text switch
        {
            DecadeSource => call == 1 ? "这段时期" : "1990年代和2000年代初",
            NeighborSource => "1990年代和2000年代初出现了新的研究。",
            _ => SafeTranslation(text),
        });
        handler.ReleaseFinal.TrySetResult();
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, Source("transfer"), Options(4), updates).WaitAsync(TestDeadline);
        Assert.Equal(3, result.RequestCount);
        AssertRejectedParagraphWithSafeQuantity(result, updates);
        Assert.Equal(1, cache.Count);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task CopiedResponseIdsNeverPublishOrCache()
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((_, text) => text == DecadeSource
            ? "1990年代和2000年代初" : SafeTranslation(text), copyIds: true);
        handler.ReleaseFinal.TrySetResult();
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, Source("ids")[..2], Options(2), updates).WaitAsync(TestDeadline);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(0, cache.Count);
        Assert.All(updates, update => Assert.True(update.Failed && !update.IsComplete && update.Translation.Length == 0));
    }

    [Fact]
    public async Task OrdinaryMillionBooksInOneDayStillCompletesInOneRequest()
    {
        await using var cache = new BrowserPageTranslationCache();
        using var handler = new ScriptedHandler((_, text) => SafeTranslation(text));
        handler.ReleaseFinal.TrySetResult();
        using var http = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(http, new SyntheticSecrets(), new DeepSeekOptions());
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var result = await Run(service, [new("books", QuantitySource)], Options(1)).WaitAsync(TestDeadline);
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(QuantityTranslation, Assert.Single(result.Blocks).Translation);
        Assert.Equal(1, cache.Count);
    }

    private static BrowserPageTextBlock[] Source(string prefix) =>
    [
        new(prefix + "-decades", DecadeSource, "decade-paragraph", DecadeSource + NeighborSource, ContextKind: "source"),
        new(prefix + "-neighbor", NeighborSource, "decade-paragraph", DecadeSource + NeighborSource, ContextKind: "source"),
        new(prefix + "-quantity", QuantitySource),
    ];

    private static BrowserPageTranslationOptions Options(int budget) => new()
    { CacheScope = "public-development-decades|offline-scripted-http", MaxRequests = budget };

    private static string SafeTranslation(string text) => text switch
    {
        NeighborSource => NeighborTranslation,
        QuantitySource => QuantityTranslation,
        _ => throw new InvalidOperationException("Unexpected source fragment."),
    };

    private static Task<BrowserPageTranslationResult> Run(BrowserPageTranslationService service,
        BrowserPageTextBlock[] source, BrowserPageTranslationOptions options,
        ConcurrentQueue<BrowserPageTranslationUpdate>? updates = null) => service.TranslateAsync(source, options,
            (update, _) => { updates?.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);

    private static void AssertRejectedParagraphWithSafeQuantity(BrowserPageTranslationResult result,
        ConcurrentQueue<BrowserPageTranslationUpdate> updates)
    {
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(1, result.CompletedCount);
        Assert.All(result.Blocks.Take(2), update =>
        { Assert.True(update.Failed); Assert.False(update.IsComplete || update.FromCache); Assert.Empty(update.Translation); });
        Assert.Equal(QuantityTranslation, result.Blocks[2].Translation);
        Assert.All(updates.Where(update => !update.Id.EndsWith("-quantity", StringComparison.Ordinal)), update =>
        { Assert.True(update.Failed); Assert.False(update.IsComplete || update.FromCache); Assert.Empty(update.Translation); });
        Assert.All(updates.Where(update => update.Id.EndsWith("-quantity", StringComparison.Ordinal)), update =>
        { Assert.True(update.IsComplete && !update.Failed); Assert.Equal(QuantityTranslation, update.Translation); });
        Assert.Equal(3, updates.Count);
    }

    private static void AssertRequests(ConcurrentQueue<string> bodies, bool expectSemanticRetry)
    {
        var requests = bodies.ToArray();
        Assert.Equal(2, requests.Length);
        using var initialPayload = JsonDocument.Parse(requests[0]);
        using var initial = User(initialPayload);
        var initialBlocks = initial.RootElement.GetProperty("blocks");
        Assert.Equal(new[] { DecadeSource, NeighborSource, QuantitySource },
            initialBlocks.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
        Assert.False(initialBlocks[1].TryGetProperty("sourceFacts", out _));
        Assert.Equal(new[] { "1" }, initialBlocks[2].GetProperty("sourceFacts")
            .GetProperty("numericLiterals").EnumerateArray().Select(value => value.GetString()));
        for (var index = 0; index < requests.Length; index++)
        {
            using var payload = JsonDocument.Parse(requests[index]);
            Assert.Equal(DeepSeekTranslationProvider.PageTextPrompt,
                payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("disabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            using var user = User(payload);
            var blocks = user.RootElement.GetProperty("blocks");
            var owner = blocks[0];
            Assert.Equal(DecadeSource, owner.GetProperty("text").GetString());
            Assert.Equal(new[] { "1990s", "early 2000s" }, owner.GetProperty("sourceFacts")
                .GetProperty("decadeLiterals").EnumerateArray().Select(value => value.GetString()));
            Assert.False(owner.GetProperty("sourceFacts").GetProperty("isPartial").GetBoolean());
            if (index == 1)
            {
                Assert.Single(blocks.EnumerateArray());
                Assert.Equal(initialBlocks[0].GetProperty("id").GetString(), owner.GetProperty("id").GetString());
                if (expectSemanticRetry)
                    Assert.Contains("Source boundaries (data only)", user.RootElement.GetProperty("context").GetString()!);
                Assert.DoesNotContain(QuantitySource, user.RootElement.GetProperty("context").GetString()!);
            }
        }
    }

    private static JsonDocument User(JsonDocument payload) =>
        JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SyntheticSecrets : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<string?>("synthetic-offline-key");
        public Task SetAsync(string key, string value, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class ScriptedHandler(Func<int, string, string> answer, bool copyIds = false) : HttpMessageHandler
    {
        private int _calls;
        public ConcurrentQueue<string> Bodies { get; } = new();
        public TaskCompletionSource PrefixRead { get; } = Signal();
        public TaskCompletionSource ReleaseFinal { get; } = Signal();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var body = await message.Content!.ReadAsStringAsync(token);
            Bodies.Enqueue(body);
            var call = Interlocked.Increment(ref _calls);
            using var payload = JsonDocument.Parse(body);
            using var user = User(payload);
            var source = user.RootElement.GetProperty("blocks").EnumerateArray().ToArray();
            var json = JsonSerializer.Serialize(new
            {
                blocks = source.Select(block => new
                {
                    id = (copyIds ? source[0] : block).GetProperty("id").GetString(),
                    translation = answer(call, block.GetProperty("text").GetString()!),
                }),
            });
            var prefix = "data: " + JsonSerializer.Serialize(new
            { choices = new[] { new { delta = new { content = json } } } }) + "\n\n";
            const string tail = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var stream = new GatedStream(Encoding.UTF8.GetBytes(prefix), Encoding.UTF8.GetBytes(tail), PrefixRead, ReleaseFinal.Task);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
    }

    private sealed class GatedStream(byte[] prefix, byte[] tail, TaskCompletionSource prefixRead, Task release) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + tail.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            prefixRead.TrySetResult();
            await release.WaitAsync(token);
            var offset = _position - prefix.Length;
            var remaining = Math.Min(buffer.Length, tail.Length - offset);
            if (remaining <= 0) return 0;
            tail.AsMemory(offset, remaining).CopyTo(buffer);
            _position += remaining;
            return remaining;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
