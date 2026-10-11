using System.Collections.Concurrent;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

/// <summary>
/// The reported article paragraph has a Financial Times anchor between two text
/// nodes. Its 90 billion euros / $100 billion must survive Chinese unit conversion
/// through the actual streaming service and semantic-group commit/cache path.
/// Uses no API credentials, network transport, browser profile or user cache.
/// </summary>
public sealed class FinancialParagraphRegressionTests
{
    private const string BeforeLink = "In France, interest payments are one of the biggest expenses in the government’s budget, and could eat up more than 90 billion euros ($100 billion) next year, more than planned spending on defense and education, according to the French finance ministry. Emmanuel Moulin, the governor of the French central bank, recently ";
    private const string Link = "told The Financial Times";
    private const string AfterLink = " that the country was at risk of being “strangled by interest rates” if it doesn’t improve its public finances.";
    private const string BeforeTranslation = "据法国财政部称，利息支出是法国政府预算中最大的开支之一，明年可能超过900亿欧元（约1000亿美元），超过国防和教育的计划支出。法国央行行长埃马纽埃尔·穆兰最近";
    private const string LinkTranslation = "对《金融时报》表示";
    private const string AfterTranslation = "，如果不改善公共财政，该国就有被利率勒住脖子的风险。";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task FinancialInlineParagraphStreamsCompletesAndReusesAllNodesAcrossNewDomIds()
    {
        await using var cache = new BrowserPageTranslationCache();
        var provider = new FinancialProvider(wrongEuroMagnitude: false);
        var creations = 0;
        var service = new BrowserPageTranslationService(_ =>
        { Interlocked.Increment(ref creations); return provider; }, cache: cache);
        var source = Source("first-dom", "first-paragraph");
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var prefixIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var allPrefixesVisible = Signal();
        var job = service.TranslateAsync(source, Options(), (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.Failed && !update.IsComplete && update.Translation.Length > 0)
            {
                prefixIds.TryAdd(update.Id, 0);
                if (prefixIds.Count == source.Length) allPrefixesVisible.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        try
        {
            await allPrefixesVisible.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Equal(0, cache.Count);
            Assert.All(updates, update =>
            { Assert.False(update.Failed); Assert.False(update.IsComplete); Assert.False(update.FromCache); });
        }
        finally { provider.ReleaseFinal.TrySetResult(); }

        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(1, creations);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, cache.Count);
        AssertFinalTranslations(source, result);
        Assert.All(updates, update => Assert.False(update.Failed));
        Assert.All(source, block =>
        {
            var complete = Assert.Single(updates, update => update.Id == block.Id && update.IsComplete);
            Assert.Equal(Translation(block.Text, wrongEuroMagnitude: false), complete.Translation);
            Assert.False(complete.FromCache);
            var forNode = updates.Where(update => update.Id == block.Id).ToArray();
            Assert.True(forNode.Length >= 2);
            Assert.True(forNode[^1].IsComplete);
            Assert.All(forNode, update => Assert.StartsWith(update.Translation, complete.Translation));
        });
        AssertPageOnlyRequest(Assert.Single(provider.Requests));

        // A new page snapshot and a recreated service use source/group content,
        // independent of transient DOM IDs or the paragraph's own identifier.
        var reopened = Source("replacement-dom", "replacement-paragraph");
        var cacheUpdates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var cachedService = new BrowserPageTranslationService(_ =>
            throw new InvalidOperationException("A complete cached paragraph must not create a provider."), cache: cache);
        var restored = await cachedService.TranslateAsync(reopened, Options(), (update, _) =>
        { cacheUpdates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None).WaitAsync(TestDeadline);
        Assert.Equal(0, restored.RequestCount);
        Assert.Equal(3, restored.CacheHits);
        Assert.Equal(3, restored.CompletedCount);
        Assert.Equal(0, restored.FailedCount);
        Assert.Equal(3, cacheUpdates.Count);
        AssertFinalTranslations(reopened, restored);
        Assert.All(cacheUpdates, update =>
        { Assert.True(update.FromCache); Assert.True(update.IsComplete); Assert.False(update.Failed); });
        Assert.All(restored.Blocks, update => Assert.True(update.FromCache));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task WrongEuroMagnitudeRetractsWholeInlineGroupAndCannotBeReusedFromCache()
    {
        await using var cache = new BrowserPageTranslationCache();
        var provider = new FinancialProvider(wrongEuroMagnitude: true);
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var source = Source("first-dom", "first-paragraph");
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var firstPrefix = Signal();
        var job = service.TranslateAsync(source, Options(), (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.Failed && !update.IsComplete && update.Translation.Length > 0) firstPrefix.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        try
        {
            await firstPrefix.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Equal(0, cache.Count);
        }
        finally { provider.ReleaseFinal.TrySetResult(); }

        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(1, result.RequestCount);
        AssertRejected(result);
        Assert.Equal(0, cache.Count);
        Assert.DoesNotContain(updates, update => update.IsComplete);
        Assert.All(source, block => Assert.Contains(updates,
            update => update.Id == block.Id && update.Failed && update.Translation.Length == 0));

        var reopened = Source("replacement-dom", "replacement-paragraph");
        var repeated = await service.TranslateAsync(reopened, Options(), (_, _) => ValueTask.CompletedTask,
            CancellationToken.None).WaitAsync(TestDeadline);
        Assert.Equal(1, repeated.RequestCount);
        Assert.Equal(0, repeated.CacheHits);
        AssertRejected(repeated);
        Assert.Equal(0, cache.Count);
        Assert.Equal(2, provider.Calls);
    }

    private static BrowserPageTextBlock[] Source(string prefix, string groupId) =>
    [
        new(prefix + "-before-link", BeforeLink, groupId, "article paragraph"),
        new(prefix + "-financial-times-link", Link, groupId, "article paragraph"),
        new(prefix + "-after-link", AfterLink, groupId, "article paragraph")
    ];

    private static BrowserPageTranslationOptions Options() => new()
    { CacheScope = "financial-regression|simulated-provider|semantic-guard-093" };

    private static string Translation(string source, bool wrongEuroMagnitude) => source switch
    {
        BeforeLink => wrongEuroMagnitude ? BeforeTranslation.Replace("900亿", "90亿", StringComparison.Ordinal) : BeforeTranslation,
        Link => LinkTranslation,
        AfterLink => AfterTranslation,
        _ => throw new InvalidOperationException("Unexpected source node or split part.")
    };

    private static void AssertFinalTranslations(BrowserPageTextBlock[] source, BrowserPageTranslationResult result)
    {
        Assert.Equal(source.Select(block => block.Id), result.Blocks.Select(block => block.Id));
        Assert.All(source, block =>
        {
            var final = Assert.Single(result.Blocks, update => update.Id == block.Id);
            Assert.True(final.IsComplete);
            Assert.False(final.Failed);
            Assert.Equal(Translation(block.Text, wrongEuroMagnitude: false), final.Translation);
        });
    }

    private static void AssertRejected(BrowserPageTranslationResult result)
    {
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(3, result.FailedCount);
        Assert.All(result.Blocks, update =>
        { Assert.True(update.Failed); Assert.False(update.IsComplete); Assert.False(update.FromCache); Assert.Empty(update.Translation); });
    }

    private static void AssertPageOnlyRequest(TranslationRequest request)
    {
        Assert.True(request.IsPageText);
        Assert.Equal(ThinkingLevel.None, request.ThinkingOverride);
        Assert.Null(request.ImagePng);
        Assert.Equal(new[] { BeforeLink, Link, AfterLink }, request.Blocks.Select(block => block.Text));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FinancialProvider(bool wrongEuroMagnitude) : ITranslationProvider, IStreamingTranslationProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource ReleaseFinal { get; } = Signal();
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();

        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
            => throw new InvalidOperationException("This regression requires the streaming provider path.");

        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Requests.Enqueue(request);
            progress.Report(new("")
            {
                ImageSegments = request.Blocks.Select(block =>
                {
                    var translated = Translation(block.Text, wrongEuroMagnitude);
                    return new TranslatedBlock(block.Id, block.Text, translated[..Math.Min(20, translated.Length - 1)], default)
                    { IsComplete = false };
                }).ToArray()
            });
            await ReleaseFinal.Task.WaitAsync(token);
            return JsonSerializer.Serialize(new
            { blocks = request.Blocks.Select(block => new { id = block.Id, translation = Translation(block.Text, wrongEuroMagnitude) }) });
        }
    }
}
