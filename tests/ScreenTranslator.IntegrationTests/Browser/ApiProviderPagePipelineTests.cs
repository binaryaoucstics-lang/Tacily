using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class ApiProviderPagePipelineTests
{
    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Real_Service_And_Adapter_Stream_Three_Batches_Before_Final_And_Cache_Completed_Results(TranslationProviderKind provider)
    {
        var threeStarted = Signal();
        var firstVisible = Signal();
        var release = Signal();
        var handler = new ApiHandler(provider, threeStarted, release.Task);
        using var http = new HttpClient(handler);
        var settings = new AppSettings { TranslationProvider = provider, ThinkingLevel = ThinkingLevel.High };
        var service = new BrowserPageTranslationService(_ => TranslationProviderFactory.Create(http, new Secrets(), settings));
        BrowserPageTextBlock[] blocks = [new("dom1", "Hello world from a public test article."),
            new("dom2", "Hello world from a public test article."), new("dom3", "Hello world from a public test article.")];
        var options = new BrowserPageTranslationOptions { MaxBatchBlocks = 1, Parallelism = 3 };
        var job = service.TranslateAsync(blocks, options, (update, _) =>
        {
            if (!update.IsComplete && update.Translation.Length > 0) firstVisible.TrySetResult();
            return ValueTask.CompletedTask;
        }, default);
        await Task.WhenAll(threeStarted.Task, firstVisible.Task).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(job.IsCompleted);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(3, handler.KeysReadPayloads.Count);
        Assert.All(handler.KeysReadPayloads, body =>
        {
            using var json = JsonDocument.Parse(body);
            var thinking = json.RootElement.GetProperty(provider == TranslationProviderKind.OpenAI ? "reasoning" : "thinking");
            Assert.Equal(provider == TranslationProviderKind.OpenAI ? "none" : "between_tools",
                thinking.GetProperty(provider == TranslationProviderKind.OpenAI ? "effort" : "type").GetString());
        });
        release.TrySetResult();
        var result = await job.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(3, result.RequestCount);
        Assert.All(result.Blocks, block => { Assert.True(block.IsComplete); Assert.Equal("来自公开测试文章的你好世界。", block.Translation); });
        Assert.Equal(1, service.CacheCount); // Identical source/context is shared; DOM IDs remain per-node.
        var cached = await service.TranslateAsync(blocks, options, (_, _) => ValueTask.CompletedTask, default);
        Assert.Equal(3, cached.CacheHits);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Valid_JSON_With_Truncated_Server_Status_Retracts_Prefix_And_Is_Not_Cached(TranslationProviderKind provider)
    {
        var started = Signal();
        var firstVisible = Signal();
        var release = Signal();
        var handler = new ApiHandler(provider, started, release.Task, incomplete: true);
        using var http = new HttpClient(handler);
        var service = new BrowserPageTranslationService(_ => TranslationProviderFactory.Create(http, new Secrets(), new() { TranslationProvider = provider }));
        var updates = new System.Collections.Concurrent.ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync([new("dom1", "Hello world.")], new(), (update, _) =>
        { updates.Enqueue(update); if (update.Translation.Length > 0) firstVisible.TrySetResult(); return ValueTask.CompletedTask; }, default);
        await firstVisible.Task.WaitAsync(TimeSpan.FromSeconds(8));
        release.TrySetResult();
        var result = await job.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, service.CacheCount);
        Assert.Equal(2, handler.Calls);
        Assert.Contains(updates, update => update.Failed && !update.IsComplete && update.Translation == "");
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI)]
    [InlineData(TranslationProviderKind.Claude)]
    public async Task Cancelling_While_SSE_Body_Is_Waiting_Drains_All_Provider_Reads_And_Caches_Nothing(TranslationProviderKind provider)
    {
        var threeStarted = Signal();
        var firstVisible = Signal();
        var release = Signal();
        var handler = new ApiHandler(provider, threeStarted, release.Task);
        using var http = new HttpClient(handler);
        var settings = new AppSettings { TranslationProvider = provider };
        var service = new BrowserPageTranslationService(_ => TranslationProviderFactory.Create(http, new Secrets(), settings));
        using var cancelled = new CancellationTokenSource();
        BrowserPageTextBlock[] blocks = [new("dom1", "Hello world."), new("dom2", "Hello world."), new("dom3", "Hello world.")];
        var job = service.TranslateAsync(blocks, new() { MaxBatchBlocks = 1, Parallelism = 3 }, (update, _) =>
        { if (update.Translation.Length > 0) firstVisible.TrySetResult(); return ValueTask.CompletedTask; }, cancelled.Token);
        await Task.WhenAll(threeStarted.Task, firstVisible.Task).WaitAsync(TimeSpan.FromSeconds(8));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(3, handler.Streams.Count);
        Assert.All(handler.Streams, stream => Assert.True(stream.CancellationObserved));
        Assert.Equal(0, service.CacheCount);
        Assert.Equal(3, handler.Calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Event(string name, object data) => "event: " + name + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";

    private sealed class ApiHandler(TranslationProviderKind kind, TaskCompletionSource threeStarted, Task release, bool incomplete = false) : HttpMessageHandler
    {
        internal int Calls;
        internal System.Collections.Concurrent.ConcurrentBag<string> KeysReadPayloads { get; } = [];
        internal System.Collections.Concurrent.ConcurrentBag<GatedStream> Streams { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var body = await message.Content!.ReadAsStringAsync(token);
            KeysReadPayloads.Add(body);
            using var json = JsonDocument.Parse(body);
            var content = json.RootElement.GetProperty(kind == TranslationProviderKind.OpenAI ? "input" : "messages")[0].GetProperty("content");
            using var metadata = JsonDocument.Parse(content[0].GetProperty("text").GetString()!);
            var id = metadata.RootElement.GetProperty("blocks")[0].GetProperty("id").GetString()!;
            var prefix = "{\"blocks\":[{\"id\":\"" + id + "\",\"translation\":\"来自公开测试";
            var tail = "文章的你好世界。\"}]}";
            var full = prefix + tail;
            string first, last;
            if (kind == TranslationProviderKind.OpenAI)
            {
                first = Event("response.output_text.delta", new { type = "response.output_text.delta", delta = prefix });
                last = Event("response.output_text.delta", new { type = "response.output_text.delta", delta = tail })
                    + Event(incomplete ? "response.incomplete" : "response.completed", new { type = incomplete ? "response.incomplete" : "response.completed", response = new { status = incomplete ? "incomplete" : "completed",
                        output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = full } } } } } });
            }
            else
            {
                first = Event("message_start", new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } })
                    + Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
                    + Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = prefix } });
                last = Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = tail } })
                    + Event("content_block_stop", new { type = "content_block_stop", index = 0 })
                    + Event("message_delta", new { type = "message_delta", delta = new { stop_reason = incomplete ? "max_tokens" : "end_turn" } })
                    + Event("message_stop", new { type = "message_stop" });
            }
            var stream = new GatedStream(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(last), release);
            Streams.Add(stream);
            if (Interlocked.Increment(ref Calls) == 3) threeStarted.TrySetResult();
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
    }

    private sealed class GatedStream(byte[] prefix, byte[] tail, Task release) : Stream
    {
        private int _position;
        internal bool CancellationObserved;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + tail.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position < prefix.Length)
            {
                var size = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, size).CopyTo(buffer); _position += size; return size;
            }
            try { await release.WaitAsync(token); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            var offset = _position - prefix.Length;
            if (offset >= tail.Length) return 0;
            var remaining = Math.Min(buffer.Length, tail.Length - offset);
            tail.AsMemory(offset, remaining).CopyTo(buffer); _position += remaining; return remaining;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Secrets : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<string?>("test-only-key");
        public Task SetAsync(string key, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
