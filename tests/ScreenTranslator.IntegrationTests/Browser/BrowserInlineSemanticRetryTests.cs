using System.Collections.Concurrent;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserInlineSemanticRetryTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(2)]
    public async Task PublicWikipediaDateTransferRetriesOwningNodesThenCommitsAndCaches(int batchSize)
    {
        var (source, observed, corrected) = Wikipedia();
        Assert.NotEmpty(PageTextSemanticGuard.FindUnsafeIds(source, observed));
        Assert.Empty(PageTextSemanticGuard.FindUnsafeIds(source, corrected));
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var provider = new Provider((request, progress, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            if (retry)
            {
                Assert.Single(request.Blocks);
                Assert.Contains("private terminology", request.Context);
                using var context = JsonDocument.Parse(request.Context[(request.Context.IndexOf('\n') + 1)..]);
                Assert.Equal("private terminology", context.RootElement.GetProperty("userTranslationContext").GetString());
                Assert.Equal(TranslationStyle.Literal, request.Style);
                Assert.DoesNotContain(source[0].Context!, request.Context);
                if (request.Blocks[0].Text == source[3].Text)
                {
                    Assert.Contains("1951", request.Context);
                    Assert.DoesNotContain("1954", request.Context);
                }
            }
            var answers = retry ? corrected : observed;
            foreach (var block in request.Blocks)
                progress.Report(new("prefix") { ImageSegments = [new(block.Id, block.Text, answers[IdFor(source, block)][..2], default)] });
            return Task.FromResult(Json(request, block => answers[IdFor(source, block)]));
        });
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var options = new BrowserPageTranslationOptions { MaxBatchBlocks = batchSize, MaxRequests = 12,
            Context = "private terminology", Style = TranslationStyle.Literal };
        var result = await Run(service, source, options, updates);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.RequestCount > (source.Length + batchSize - 1) / batchSize);
        Assert.All(result.Blocks, block => Assert.Equal(corrected[block.Id], block.Translation));
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed));
        var replay = await Run(new BrowserPageTranslationService(_ => throw new InvalidOperationException("Cache should replay."), cache: cache),
            source, options);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(source.Length, replay.CacheHits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task PersistentInvalidDatesStayWithinBudgetWithoutPublishingInlinePrefixesAndKeepOtherParagraph(int budget)
    {
        var (source, observed, _) = Wikipedia();
        BrowserPageTextBlock[] blocks = [.. source, new("safe", "The entrance is blue.", "other")];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var provider = new Provider((request, progress, _) =>
        {
            progress.Report(new("prefix") { ImageSegments = request.Blocks.Select(block =>
                new TranslatedBlock(block.Id, block.Text, "尚未核验", default)).ToArray() });
            return Task.FromResult(Json(request, block => block.Text == blocks[^1].Text ? "入口是蓝色。" : observed[IdFor(source, block)]));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, blocks, new() { MaxRequests = budget }, updates);
        Assert.InRange(result.RequestCount, 1, budget);
        Assert.Equal(result.RequestCount, provider.Requests.Count);
        if (budget > 1) Assert.Equal(2, result.RequestCount);
        Assert.Equal(source.Length, result.FailedCount);
        Assert.Equal("入口是蓝色。", result.Blocks.Single(block => block.Id == "safe").Translation);
        Assert.Equal(1, service.CacheCount);
        Assert.All(updates.Where(update => update.Id != "safe"), update =>
        { Assert.True(update.Failed); Assert.Empty(update.Translation); });
    }

    [Fact]
    public async Task GroupGuardDetectsDateTransferAcrossSeparateRequestsAndCorrectsItBeforePublishing()
    {
        BrowserPageTextBlock[] source = [new("receiver", "The demonstration", "inline"), new("owner", "took place in 1954.", "inline")];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => block.Text == source[0].Text
                ? retry ? "演示" : "1954年的演示" : retry ? "于1954年举行。" : "举行。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, source, new() { MaxBatchBlocks = 1, MaxRequests = 4 }, updates);
        Assert.Equal(4, result.RequestCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("演示", result.Blocks[0].Translation);
        Assert.Equal("于1954年举行。", result.Blocks[1].Translation);
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task SplitUnsafeNodeIsResetAsAWholeAndNeedsBudgetForAllRetryParts(int budget, bool succeeds)
    {
        var text = new string('A', 62) + ". " + "The demonstration was held in 1954.";
        BrowserPageTextBlock[] source = [new("long", text, "inline", Context: text, ContextKind: "source"),
            new("tail", "and research continued.", "inline", Context: text, ContextKind: "source")];
        var provider = new Provider((request, progress, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            progress.Report(new("prefix") { ImageSegments = request.Blocks.Select(block => new TranslatedBlock(block.Id, block.Text, "暂存", default)).ToArray() });
            return Task.FromResult(Json(request, block => block.Text.Contains("1954", StringComparison.Ordinal)
                ? retry ? "演示于1954年举行。" : "演示于1955年举行。" : "研究继续。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, source, new() { MaxPartCharacters = 64, MaxBatchBlocks = 1, MaxRequests = budget }, updates);
        Assert.True(result.RequestCount <= budget);
        Assert.Equal(succeeds ? 0 : 2, result.FailedCount);
        Assert.All(updates, update => Assert.True(update.IsComplete || update.Failed));
        if (succeeds)
        {
            Assert.Equal(5, result.RequestCount);
            Assert.Contains("1954", result.Blocks[0].Translation);
            Assert.DoesNotContain("1955", result.Blocks[0].Translation);
        }
        else Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task CancellationDuringSemanticRetryDrainsProviderAndDoesNotCacheProvisionalGroup()
    {
        var (source, observed, _) = Wikipedia();
        using var cancellation = new CancellationTokenSource();
        var drained = false;
        var provider = new Provider(async (request, _, token) =>
        {
            if (!request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal))
                return Json(request, block => observed[IdFor(source, block)]);
            try { cancellation.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return string.Empty; }
            finally { drained = true; }
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync(source, new(),
            (update, _) => { updates.Enqueue(update); return ValueTask.CompletedTask; }, cancellation.Token));
        Assert.True(drained);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(updates);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task CorrectedParagraphWaitsForAllNodesWhileCompletedOtherGroupRemainsCachedAfterTransportFailure()
    {
        var (source, observed, _) = Wikipedia();
        BrowserPageTextBlock[] blocks = [new("safe", "The entrance is blue.", "other"), .. source];
        var provider = new Provider((request, _, _) =>
        {
            if (request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal))
                throw new InvalidOperationException("Offline simulated transport failure");
            return Task.FromResult(Json(request, block => block.Text == blocks[0].Text ? "入口是蓝色。" : observed[IdFor(source, block)]));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, blocks, new(), updates));
        Assert.Equal(1, service.CacheCount);
        Assert.DoesNotContain(updates, update => update.Id != "safe" && update.Translation.Length > 0);
        var replay = await Run(service, [blocks[0]], new());
        Assert.Equal(0, replay.RequestCount);
    }

    [Fact]
    public async Task RepeatedGuardUnsafeSourceOnlyAnswerSkipsKnownUselessRetriesOnNextInvocation()
    {
        BrowserPageTextBlock[] source = [new("name", "Please contact Mira Chen.", "inline"),
            new("date", "before noon on Thursday", "inline"), new("other", "The entrance is blue.", "other")];
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block => block.Text == source[0].Text
            ? "请在周四正午前联系米拉。" : block.Text == source[1].Text ? "，" : "入口是蓝色。")));
        var service = new BrowserPageTranslationService(_ => provider);
        var options = new BrowserPageTranslationOptions { MaxBatchBlocks = 1, MaxRequests = 6 };
        var first = await Run(service, source, options);
        var second = await Run(service, source, options);
        Assert.Equal(4, first.RequestCount);
        Assert.Equal(2, second.RequestCount);
        Assert.Equal(2, second.FailedCount);
        Assert.Equal(1, second.CacheHits);
    }

    [Fact]
    public async Task NegativeHistoryStillChecksFreshCorrectedAnswersAndCachesRecovery()
    {
        var (source, observed, corrected) = Wikipedia();
        var recover = false;
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request,
            block => (recover ? corrected : observed)[IdFor(source, block)])));
        var service = new BrowserPageTranslationService(_ => provider);
        var first = await Run(service, source, new());
        Assert.Equal(2, first.RequestCount);
        Assert.Equal(source.Length, first.FailedCount);
        Assert.Equal(0, service.CacheCount);
        var repeat = await Run(service, source, new());
        Assert.Equal(1, repeat.RequestCount);
        Assert.Equal(source.Length, repeat.FailedCount);

        recover = true;
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var recovered = await Run(service, source, new(), updates);
        Assert.Equal(1, recovered.RequestCount);
        Assert.Equal(0, recovered.FailedCount);
        Assert.All(updates, update => Assert.Equal(corrected[update.Id], update.Translation));
        var cached = await Run(service, source, new());
        Assert.Equal(0, cached.RequestCount);
        Assert.Equal(source.Length, cached.CacheHits);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("scope")]
    [InlineData("style")]
    [InlineData("source")]
    [InlineData("neighbor")]
    public async Task NegativeHistoryIsIsolatedByExistingSourceContextAndScopeKeys(string change)
    {
        BrowserPageTextBlock[] source = [new("date", "The event began in 1951.", "paragraph", Context: "first hint")];
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "活动于1952年开始。")));
        var service = new BrowserPageTranslationService(_ => provider);
        var options = new BrowserPageTranslationOptions();
        Assert.Equal(2, (await Run(service, source, options)).RequestCount);
        Assert.Equal(1, (await Run(service, source, options)).RequestCount);
        if (change == "context") options = options with { Context = "user terminology" };
        if (change == "scope") options = options with { CacheScope = "different policy scope" };
        if (change == "style") options = options with { Style = TranslationStyle.Literal };
        if (change == "source") source = [source[0] with { Text = "The next event began in 1951." }];
        if (change == "neighbor") source = [source[0] with { Context = "second hint" }];
        var isolated = await Run(service, source, options);
        Assert.Equal(2, isolated.RequestCount);
        Assert.Equal(1, isolated.FailedCount);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task RejectionHistoryEvictsOldHashesAtItsFiniteBound()
    {
        BrowserPageTextBlock[] source = [new("date", "The event began in 1951.")];
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "活动于1952年开始。")));
        var service = new BrowserPageTranslationService(_ => provider);
        for (var index = 0; index < 257; index++)
        {
            var result = await Run(service, source, new() { Context = "context " + index });
            Assert.Equal(2, result.RequestCount);
            Assert.Equal(1, result.FailedCount);
        }
        Assert.Equal(1, (await Run(service, source, new() { Context = "context 256" })).RequestCount);
        Assert.Equal(2, (await Run(service, source, new() { Context = "context 0" })).RequestCount);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task VaryingUnsafeRetryAnswersRemainBoundedByRequestCap()
    {
        BrowserPageTextBlock[] source = [new("date", "The event began in 1951.")];
        var attempt = 0;
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request,
            _ => "活动于" + (1960 + Interlocked.Increment(ref attempt)) + "年开始。")));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, source, new() { MaxRequests = 5 });
        Assert.Equal(5, result.RequestCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, service.CacheCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuantityTransferAcrossGroupIdsStaysProvisionalUntilBothOwningNodesAreSafe(bool corrected)
    {
        BrowserPageTextBlock[] source = [new("receiver", "Bring bottles", "a"),
            new("owner", "with capacity 250 ml", "b"), new("safe", "The entrance is blue.", "c")];
        var provider = new Provider((request, _, _) =>
        {
            var retry = corrected && request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => block.Text == source[0].Text ? retry ? "带瓶子" : "带250毫升的瓶子"
                : block.Text == source[1].Text ? retry ? "容量为250毫升" : "容量合适" : "入口是蓝色。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, source, new() { MaxRequests = 5 });
        Assert.Equal(corrected ? 0 : 2, result.FailedCount);
        Assert.Equal(corrected ? 3 : 1, service.CacheCount);
        if (corrected) Assert.Equal("带瓶子", result.Blocks[0].Translation);
    }

    [Fact]
    public async Task IndependentlyInvalidParagraphsRecoverAndFailSeparatelyAndCacheTheCorrectedParagraph()
    {
        BrowserPageTextBlock[] source = [new("recover", "The event began in 1951.", "a"),
            new("fail", "The event began in 1985.", "b")];
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => block.Text == source[0].Text
                ? retry ? "活动于1951年开始。" : "活动于1952年开始。" : "活动于1986年开始。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, source, new(), updates);
        Assert.Equal(3, result.RequestCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("活动于1951年开始。", result.Blocks[0].Translation);
        Assert.True(result.Blocks[1].Failed);
        Assert.Equal(1, service.CacheCount);
        Assert.DoesNotContain(updates, update => update.Id == "recover" && update.Failed);
        var replay = await Run(service, source, new());
        Assert.Equal(1, replay.CacheHits);
        Assert.Equal(1, replay.RequestCount);
        Assert.Equal(1, replay.FailedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransfersWithIndependentlyUnsafeReceiversDoNotLinkAnUnrelatedInvalidParagraph(bool corrected)
    {
        BrowserPageTextBlock[] source = [new("owner", "with capacity 250 ml", "a"),
            new("receiver", "Bring 3 bottles", "b"), new("unrelated", "The event began in 1985.", "c"),
            new("recover", "The event began in 1951.", "d")];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => block.Text == source[0].Text
                ? retry ? "容量为250毫升" : "容量合适"
                : block.Text == source[1].Text ? retry ? corrected ? "带3个瓶子" : "带3个250毫升的瓶子" : "带250毫升的瓶子"
                : block.Text == source[2].Text ? "活动于1986年开始。"
                : retry ? "活动于1951年开始。" : "活动于1952年开始。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, source, new() { MaxRequests = 8 }, updates);
        Assert.InRange(result.RequestCount, 1, 8);
        Assert.Equal(corrected ? 1 : 3, result.FailedCount);
        Assert.Equal("活动于1951年开始。", result.Blocks[3].Translation);
        Assert.Equal(corrected ? 3 : 1, service.CacheCount);
        Assert.DoesNotContain(updates, update => update.Id == "recover" && update.Failed);
        Assert.DoesNotContain(updates, update => update.Id == "receiver" && update.Translation.Contains("250", StringComparison.Ordinal));
        Assert.Equal(0, (await Run(service, [source[3]], new())).RequestCount);
    }

    [Fact]
    public async Task SeparateTransferComponentsRecoverOrFailWithoutMergingTheirParagraphs()
    {
        BrowserPageTextBlock[] source = [new("receiver-a", "Bring bottles", "a"),
            new("owner-a", "with capacity 250 ml", "b"), new("receiver-b", "Use switches", "c"),
            new("owner-b", "with 7 inputs", "d"), new("unrelated", "The event began in 1985.", "e")];
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => block.Text == source[0].Text ? retry ? "带瓶子" : "带250毫升的瓶子"
                : block.Text == source[1].Text ? retry ? "容量为250毫升" : "容量合适"
                : block.Text == source[2].Text ? "使用7个开关"
                : block.Text == source[3].Text ? "输入合适" : "活动于1986年开始。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Run(service, source, new() { MaxRequests = 8 });
        Assert.Equal(5, result.RequestCount);
        Assert.Equal(3, result.FailedCount);
        Assert.Equal("带瓶子", result.Blocks[0].Translation);
        Assert.Equal("容量为250毫升", result.Blocks[1].Translation);
        Assert.Equal(2, service.CacheCount);
        var cached = await Run(service, source[..2], new());
        Assert.Equal(2, cached.CacheHits);
        Assert.Equal(0, cached.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WeekdayCorrectionCannotHideTransferredQuantityAfterItsOwnerRecovers(bool removeTransferredQuantity)
    {
        // The owner is retried first. The receiver's changed retry answer fixes
        // Monday, but must also remove the quantity owned by the other paragraph.
        BrowserPageTextBlock[] source = [new("owner", "with capacity 250 ml", "a"),
            new("receiver", "Bring bottles on Monday", "b"), new("safe", "The entrance is blue.", "c")];
        var initial = new Dictionary<string, string>
        {
            ["owner"] = "容量合适", ["receiver"] = "周二带250毫升的瓶子", ["safe"] = "入口是蓝色。"
        };
        var assessment = PageTextSemanticGuard.Assess(source, initial);
        Assert.Contains(new PageTextNumericTransfer("owner", "receiver"), assessment.NumericTransfers);
        Assert.Contains("receiver", assessment.UnsafeIds);
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => !retry ? initial[IdFor(source, block)]
                : block.Text == source[0].Text ? "容量为250毫升"
                : removeTransferredQuantity ? "周一带瓶子" : "周一带250毫升的瓶子"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, source, new() { MaxRequests = 6 }, updates);
        Assert.Equal(removeTransferredQuantity ? 3 : 4, result.RequestCount);
        Assert.Equal(removeTransferredQuantity ? 0 : 2, result.FailedCount);
        Assert.Equal(removeTransferredQuantity ? 3 : 1, service.CacheCount);
        Assert.Equal("入口是蓝色。", result.Blocks[2].Translation);
        Assert.DoesNotContain(updates, update => update.Id == "receiver" && update.Translation.Contains("250", StringComparison.Ordinal));
        if (removeTransferredQuantity)
        {
            Assert.Equal("周一带瓶子", result.Blocks[1].Translation);
            Assert.Equal("容量为250毫升", result.Blocks[0].Translation);
            var replay = await Run(service, source, new());
            Assert.Equal(0, replay.RequestCount);
            Assert.Equal(3, replay.CacheHits);
        }
        else
        {
            Assert.All(result.Blocks.Take(2), block => { Assert.True(block.Failed); Assert.Empty(block.Translation); });
            Assert.All(updates.Where(update => update.Id != "safe"), update =>
            { Assert.True(update.Failed); Assert.Empty(update.Translation); });
        }
    }

    [Fact]
    public async Task PartialCachedFragmentMustPassTheCurrentParagraphGuardBeforePublication()
    {
        var (source, observed, corrected) = Wikipedia();
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => (retry ? corrected : observed)[IdFor(source, block)]));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        // The existing singleton guard cannot see a year transferred from an absent
        // owning node. Replaying its range in the whole paragraph must revalidate it.
        var partial = await Run(service, [source[3]], new());
        Assert.Equal(0, partial.FailedCount);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var whole = await Run(service, source, new() { MaxRequests = 6 }, updates);
        Assert.Equal(0, whole.FailedCount);
        Assert.Equal(corrected[source[3].Id], whole.Blocks[3].Translation);
        Assert.All(updates, update =>
        {
            Assert.True(update.IsComplete && !update.Failed);
            Assert.Equal(corrected[update.Id], update.Translation);
        });
    }

    [Fact]
    public async Task MalformedSemanticRetryRemainsProvisionalAndCanRecoverWithinBudget()
    {
        var (source, observed, corrected) = Wikipedia();
        var malformed = false;
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            if (retry && !malformed) { malformed = true; return Task.FromResult("{invalid"); }
            return Task.FromResult(Json(request, block => (retry ? corrected : observed)[IdFor(source, block)]));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, source, new() { MaxRequests = 5 }, updates);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(4, result.RequestCount);
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed));
    }

    [Fact]
    public async Task PreviouslyRejectedDateReceiverCannotReuseItsAnswerWhenTheOwnerCorrectsItsOwnDate()
    {
        var (source, observed, corrected) = Wikipedia();
        // Retry the owning date first, so the later repeated receiver cannot
        // become acceptable merely because the owner has restored its own date.
        source = [source[5], .. source.Where(block => block.Id != "inline-6")];
        var provider = new Provider((request, _, _) =>
        {
            var retry = request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal);
            return Task.FromResult(Json(request, block => retry && IdFor(source, block) == "inline-6"
                ? corrected["inline-6"] : observed[IdFor(source, block)]));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await Run(service, source, new() { MaxRequests = 5 }, updates);
        Assert.Equal(3, result.RequestCount);
        Assert.Equal(7, result.FailedCount);
        Assert.Equal(0, service.CacheCount);
        Assert.Contains(provider.Requests, request => request.Context.Contains("Source boundaries (data only)", StringComparison.Ordinal)
            && request.Blocks.Any(block => IdFor(source, block) == "inline-6"));
        Assert.All(updates, update => { Assert.True(update.Failed); Assert.Empty(update.Translation); });
    }

    private static (BrowserPageTextBlock[] Source, Dictionary<string, string> Observed, Dictionary<string, string> Corrected) Wikipedia()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(BrowserInlineSemanticRetryTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eng", "website-qa", "fixtures", "wikipedia-inline-dates.json")))
            directory = directory.Parent;
        var fixture = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Public date fixture missing."),
            "eng", "website-qa", "fixtures", "wikipedia-inline-dates.json");
        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var items = document.RootElement.GetProperty("blocks").EnumerateArray().ToArray();
        var source = items.Select(item => JsonSerializer.Deserialize<BrowserPageTextBlock>(item.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        var observed = items.ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("observedTranslation").GetString()!);
        var corrected = new Dictionary<string, string>(observed)
        {
            ["inline-4"] = "由迈克尔·扎列奇纳克教授领导的机器翻译研究团队于1951年跟进，并公开展示了其",
            ["inline-6"] = "系统于1954年展示。机器翻译研究项目在日本"
        };
        return (source, observed, corrected);
    }

    private static string IdFor(IEnumerable<BrowserPageTextBlock> source, OcrBlock block) => source.Single(item => item.Text == block.Text).Id;
    private static string Json(TranslationRequest request, Func<OcrBlock, string> translate) =>
        JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = translate(block) }) });
    private static Task<BrowserPageTranslationResult> Run(BrowserPageTranslationService service, IReadOnlyList<BrowserPageTextBlock> source,
        BrowserPageTranslationOptions options, ConcurrentQueue<BrowserPageTranslationUpdate>? updates = null) =>
        service.TranslateAsync(source, options, (update, _) => { updates?.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);

    private sealed class Provider(Func<TranslationRequest, IProgress<TranslationStreamUpdate>, CancellationToken, Task<string>> handler)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Use streaming test entry point.");
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        { Requests.Enqueue(request); return handler(request, progress, token); }
    }
}
