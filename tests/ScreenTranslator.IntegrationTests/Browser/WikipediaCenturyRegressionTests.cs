using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

/// <summary>
/// Public Wikipedia Machine translation origins prose, split at inline links.
/// Offline regression through the unchanged production service and a fake provider;
/// this is not evidence of a live browser/model run or formal page evaluation.
/// </summary>
public sealed class WikipediaCenturyRegressionTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);
    private static readonly string[] Texts =
    [
        "The origins of machine translation can be traced back to ",
        "Al-Kindi",
        ", a ninth-century Arabic cryptographer who developed techniques for systemic language translation, including ",
        "cryptanalysis",
        ", frequency analysis, and probability and statistics, which are used in modern machine translation. ",
        "The idea of machine translation later appeared in the 17th century. In 1629, ",
        "René Descartes",
        " proposed a ",
        "universal language",
        ", with equivalent ideas in different tongues sharing one symbol."
    ];
    private static readonly string[] Translations =
    [
        "机器翻译的起源可以追溯到",
        "肯迪",
        "，他是9世纪的阿拉伯密码学家，开发了系统语言翻译技术，包括",
        "密码分析",
        "、频率分析以及概率与统计，这些技术用于现代机器翻译。",
        "机器翻译的想法后来在17世纪出现。1629年，",
        "勒内·笛卡尔",
        "提出了一种",
        "通用语言",
        "，让不同语言中相同的思想共享一个符号。"
    ];

    [Theory]
    [InlineData("ninth-century")]
    [InlineData("9th-century")]
    public async Task SafeInlineParagraphCompletesOnceAndReplaysValidatedCache(string ordinal)
    {
        await using var cache = new BrowserPageTranslationCache();
        var provider = new OriginsProvider();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var source = Source("initial", ordinal);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync(source, Options(), (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        try
        {
            await provider.PrefixReported.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Empty(updates);
            Assert.Equal(0, cache.Count);
        }
        finally { provider.ReleaseFinal.TrySetResult(); }

        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(Texts.Length, result.CompletedCount);
        Assert.Equal(0, result.FailedCount);
        AssertTranslations(source, result);
        Assert.Equal(Texts.Length, updates.Count);
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed && !update.FromCache));
        Assert.DoesNotContain(updates, update => update.Translation.Contains("10世纪", StringComparison.Ordinal));
        var request = Assert.Single(provider.Requests);
        Assert.True(request.IsPageText);
        Assert.Equal(ThinkingLevel.None, request.ThinkingOverride);
        Assert.Null(request.ImagePng);
        Assert.Equal(source.Select(block => block.Text), request.Blocks.Select(block => block.Text));

        var replaySource = Source("new-dom", ordinal);
        var replayUpdates = new List<BrowserPageTranslationUpdate>();
        var replayService = new BrowserPageTranslationService(_ =>
            throw new InvalidOperationException("Validated origins nodes must replay without a provider."), cache: cache);
        var replay = await replayService.TranslateAsync(replaySource, Options(), (update, _) =>
        { replayUpdates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None).WaitAsync(TestDeadline);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(Texts.Length, replay.CacheHits);
        Assert.Equal(Texts.Length, replay.CompletedCount);
        Assert.Equal(0, replay.FailedCount);
        AssertTranslations(replaySource, replay);
        Assert.Equal(Texts.Length, replayUpdates.Count);
        Assert.All(replayUpdates, update => Assert.True(update.FromCache && update.IsComplete && !update.Failed));
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("9世纪", "10世纪")]
    [InlineData("17世纪", "18世纪")]
    [InlineData("1629", "1630")]
    public async Task WrongCalendarFactsNeverPublishOrCacheAndRetainBoundedRetries(string correct, string wrong)
    {
        await using var cache = new BrowserPageTranslationCache();
        var provider = new OriginsProvider(correct, wrong);
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync(Source("initial"), Options(), (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        try
        {
            await provider.PrefixReported.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Empty(updates);
            Assert.Equal(0, cache.Count);
        }
        finally { provider.ReleaseFinal.TrySetResult(); }

        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(2, provider.Requests.Count);
        AssertRejected(result);
        Assert.Equal(0, cache.Count);
        Assert.Equal(Texts.Length, updates.Count);
        Assert.All(updates, update =>
        { Assert.True(update.Failed); Assert.False(update.IsComplete); Assert.Empty(update.Translation); });

        var repeated = await service.TranslateAsync(Source("new-dom"), Options(), (_, _) => ValueTask.CompletedTask,
            CancellationToken.None).WaitAsync(TestDeadline);
        Assert.Equal(1, repeated.RequestCount);
        Assert.Equal(3, provider.Requests.Count);
        AssertRejected(repeated);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task StandaloneWrittenCenturyStagesUnsafePrefixBeforeFinalValidation()
    {
        await using var cache = new BrowserPageTranslationCache();
        var provider = new OriginsProvider();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        // No inline group/context can supply staging here: source fact recognition
        // must protect the written century before any provider prefix is displayed.
        BrowserPageTextBlock[] source = [new("standalone", Texts[2])];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync(source, Options(), (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        try
        {
            await provider.PrefixReported.Task.WaitAsync(TestDeadline);
            Assert.False(job.IsCompleted);
            Assert.Empty(updates);
            Assert.Equal(0, cache.Count);
        }
        finally { provider.ReleaseFinal.TrySetResult(); }
        var result = await job.WaitAsync(TestDeadline);
        Assert.Equal(1, result.RequestCount);
        var final = Assert.Single(result.Blocks);
        Assert.True(final.IsComplete && !final.Failed);
        Assert.Equal(Translations[2], final.Translation);
        Assert.Equal(final, Assert.Single(updates));
        Assert.Equal(1, cache.Count);
    }

    private static BrowserPageTranslationOptions Options() => new()
    { CacheScope = "public-wikipedia-origins|offline-fake-provider|calendar-regression" };

    private static BrowserPageTextBlock[] Source(string prefix, string ordinal = "ninth-century")
    {
        var texts = Texts.Select(text => text.Replace("ninth-century", ordinal, StringComparison.Ordinal)).ToArray();
        var paragraph = string.Concat(texts);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paragraph)));
        var start = 0;
        return texts.Select((text, index) =>
        {
            var block = new BrowserPageTextBlock($"{prefix}-{index}", text, prefix + "-origins",
                paragraph[..Math.Min(512, paragraph.Length)], hash, paragraph.Length, start, text.Length, ContextKind: "source");
            start += text.Length;
            return block;
        }).ToArray();
    }

    private static string Translation(string text)
    {
        var index = Array.IndexOf(Texts, text.Replace("9th-century", "ninth-century", StringComparison.Ordinal));
        return index >= 0 ? Translations[index] : throw new InvalidOperationException("Unexpected source node or split part.");
    }

    private static void AssertTranslations(BrowserPageTextBlock[] source, BrowserPageTranslationResult result)
    {
        Assert.Equal(source.Select(block => block.Id), result.Blocks.Select(block => block.Id));
        Assert.All(source, block =>
        {
            var final = Assert.Single(result.Blocks, update => update.Id == block.Id);
            Assert.True(final.IsComplete && !final.Failed);
            Assert.Equal(Translation(block.Text), final.Translation);
        });
    }

    private static void AssertRejected(BrowserPageTranslationResult result)
    {
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(0, result.CompletedCount);
        Assert.Equal(Texts.Length, result.FailedCount);
        Assert.All(result.Blocks, update =>
        { Assert.True(update.Failed); Assert.False(update.IsComplete || update.FromCache); Assert.Empty(update.Translation); });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OriginsProvider(string? correct = null, string? wrong = null)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public TaskCompletionSource PrefixReported { get; } = Signal();
        public TaskCompletionSource ReleaseFinal { get; } = Signal();
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
            => throw new InvalidOperationException("This regression requires the streaming path.");

        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Requests.Enqueue(request);
            // Deliberately unsafe complete-looking factual prefix, followed by a
            // safe (or deliberately wrong) final. Flags never claim completion.
            progress.Report(new("")
            {
                ImageSegments = request.Blocks.Select(block =>
                    new TranslatedBlock(block.Id, block.Text,
                        Translation(block.Text).Replace("9世纪", "10世纪", StringComparison.Ordinal), default)
                    { IsComplete = false }).ToArray()
            });
            PrefixReported.TrySetResult();
            await ReleaseFinal.Task.WaitAsync(token);
            return JsonSerializer.Serialize(new
            {
                blocks = request.Blocks.Select(block => new
                {
                    id = block.Id,
                    translation = correct is null ? Translation(block.Text)
                        : Translation(block.Text).Replace(correct, wrong!, StringComparison.Ordinal)
                })
            });
        }
    }
}
