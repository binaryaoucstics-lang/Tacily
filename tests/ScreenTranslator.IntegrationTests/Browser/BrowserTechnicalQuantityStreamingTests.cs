using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

/// <summary>
/// Synthetic technical prose exercises the actual streaming/final-guard/cache
/// path. No remote model, user browser, user source or API key is accessed.
/// </summary>
public sealed class BrowserTechnicalQuantityStreamingTests
{
    private const string Source = "embeddinggemma-2 has under 1B parameters. Its variants have 740M, 270M, 170M, and 300M parameters and produce 768-dimensional embeddings.";
    private const string Translation = "embeddinggemma-2 的参数量不到十亿。其不同版本分别有7.4亿、2.7亿、1.7亿和3亿个参数，并输出768维嵌入向量。";
    private static BrowserPageTranslationOptions Options => new() { CacheScope = "isolated-technical-quantity-stream-cache-095" };

    [Fact]
    public async Task EquivalentTechnicalMagnitudesRemainTranslatedAfterStreamCompletesAndPersistedCacheReplaysWithoutProvider()
    {
        using var directory = new TestDirectory();
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var visible = NewSignal(); var release = NewSignal();
            var provider = new StreamingProvider(Translation, release);
            var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
            var service = new BrowserPageTranslationService(_ => provider, cache: cache);
            var job = service.TranslateAsync([Block("initial-source")], Options, (update, _) =>
            {
                updates.Enqueue(update);
                if (!update.IsComplete && !update.Failed && update.Translation.Length > 0) visible.TrySetResult();
                return ValueTask.CompletedTask;
            }, CancellationToken.None);
            try
            {
                var deadline = visible.Task.WaitAsync(TimeSpan.FromSeconds(8));
                if (await Task.WhenAny(job, deadline) == job)
                { await job; throw new InvalidOperationException("No streaming prefix was observed before completion."); }
                await deadline;
                Assert.False(job.IsCompleted);
                Assert.Contains(updates, update => !update.IsComplete && !update.Failed && update.Translation.Length > 0);
                Assert.Equal(0, cache.Count);
            }
            finally
            {
                release.TrySetResult();
                await job.WaitAsync(TimeSpan.FromSeconds(8));
            }
            var result = await job;
            Assert.Equal(1, result.RequestCount); Assert.Single(provider.Requests);
            var complete = Assert.Single(result.Blocks);
            Assert.True(complete.IsComplete); Assert.False(complete.Failed); Assert.Equal(Translation, complete.Translation);
            Assert.DoesNotContain(updates, update => update.Failed);
            Assert.Contains(updates, update => update.IsComplete && update.Translation == Translation);
            await cache.FlushAsync();
        }
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        var providerCreations = 0; var replayUpdates = new List<BrowserPageTranslationUpdate>();
        var reloaded = new BrowserPageTranslationService(_ =>
        { providerCreations++; throw new InvalidOperationException("Completed technical prose must be restored from cache."); }, cache: restarted);
        var replay = await reloaded.TranslateAsync([Block("reloaded-new-dom-id")], Options, (update, _) =>
        { replayUpdates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, providerCreations); Assert.Equal(0, replay.RequestCount); Assert.Equal(1, replay.CacheHits);
        var restored = Assert.Single(replay.Blocks);
        Assert.True(restored.IsComplete && restored.FromCache && !restored.Failed);
        Assert.Equal(Translation, restored.Translation);
        Assert.All(replayUpdates, update => Assert.True(update.IsComplete && update.FromCache && !update.Failed));
    }

    [Theory]
    [InlineData("7.4亿", "7.5亿")]
    [InlineData("十亿", "一亿")]
    [InlineData("768维", "786维")]
    public async Task IncorrectTechnicalQuantityStillRollsBackAfterStreamingAndIsNotCached(string correct, string incorrect)
    {
        await using var cache = new BrowserPageTranslationCache();
        var visible = NewSignal(); var release = NewSignal();
        var provider = new StreamingProvider(Translation.Replace(correct, incorrect, StringComparison.Ordinal), release);
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync([Block("incorrect-result")], Options, (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.IsComplete && !update.Failed && update.Translation.Length > 0) visible.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        try
        {
            var deadline = visible.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(job, deadline) == job)
            { await job; throw new InvalidOperationException("No streaming prefix was observed before validation."); }
            await deadline;
            Assert.False(job.IsCompleted);
            Assert.Contains(updates, update => !update.IsComplete && !update.Failed && update.Translation.Length > 0);
        }
        finally
        {
            release.TrySetResult();
            await job.WaitAsync(TimeSpan.FromSeconds(8));
        }
        var result = await job;
        Assert.Equal(1, result.RequestCount); Assert.Equal(0, result.CacheHits);
        var failed = Assert.Single(result.Blocks);
        Assert.True(failed.Failed); Assert.False(failed.IsComplete); Assert.Empty(failed.Translation);
        Assert.Contains(updates, update => update.Failed && update.Translation.Length == 0);
        Assert.Equal(0, cache.Count);
        var retry = await service.TranslateAsync([Block("retry-new-id")], Options,
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, retry.RequestCount); Assert.Equal(0, retry.CacheHits); Assert.Equal(2, provider.Requests.Count);
        Assert.True(Assert.Single(retry.Blocks).Failed); Assert.Equal(0, cache.Count);
    }

    private static BrowserPageTextBlock Block(string id) => new(id, Source, "technical-paragraph", Source,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source))), Source.Length, 0, Source.Length, ContextKind: "source");
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StreamingProvider(string translation, TaskCompletionSource release)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) =>
            TranslateRawStreamingAsync(request, new EmptyProgress(), token);
        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Requests.Enqueue(request);
            var prefix = translation[..Math.Min(20, translation.Length)];
            progress.Report(new(prefix) { ImageSegments = request.Blocks.Select(block =>
                new TranslatedBlock(block.Id, block.Text, prefix, default) { IsComplete = false }).ToArray() });
            await release.Task.WaitAsync(token);
            return JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation }) });
        }
    }

    private sealed class EmptyProgress : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) { } }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "work", "technical-quantity-stream-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
