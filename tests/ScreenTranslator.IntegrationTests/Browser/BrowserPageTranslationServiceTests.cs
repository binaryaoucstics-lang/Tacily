using System.Collections.Concurrent;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserPageTranslationServiceTests
{
    [Theory]
    [InlineData("Tune in next time for more Blue News at 11!", "下次继续收看11点的蓝色新闻！", true)]
    [InlineData("今日は１１時に漫画第3話を公開します！", "今天11点公开漫画第三话！", true)]
    [InlineData("一時停止しています。後で再開します。", "暂时停止，稍后恢复。", false)]
    [InlineData("11/8(日)に開催します。受講料は500円です。", "11/8（周日）举行，听课费500日元。", true)]
    [InlineData("11/8(日)に開催します。受講料は500円です。", "11月8日（周日）举行，听课费500日元。", true)]
    public async Task EquivalentSocialClocksAndJapaneseOrdinalsCommitAfterStreamingAndReplayWithoutNewRequest(
        string original, string translated, bool hasFacts)
    {
        var prefixReported = NewSignal();
        var prefixVisible = NewSignal();
        var release = NewSignal();
        var provider = new Provider(async (request, progress, token) =>
        {
            progress.Report(new("prefix") { ImageSegments = request.Blocks.Select(block =>
                new TranslatedBlock(block.Id, block.Text, translated[..4], default) { IsComplete = false }).ToArray() });
            prefixReported.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(request, _ => translated);
        });
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        BrowserPageTextBlock[] source = [new("tweet", original, "tweet-group")];
        var job = service.TranslateAsync(source, new(), (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.IsComplete && !update.Failed) prefixVisible.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        try
        {
            await prefixReported.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(job.IsCompleted);
            Assert.Equal(0, cache.Count);
            if (hasFacts) Assert.Empty(updates);
            else
            {
                await prefixVisible.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Contains(updates, update => !update.IsComplete && update.Translation == translated[..4]);
            }
        }
        finally { release.TrySetResult(); }
        var result = await job.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(translated, Assert.Single(result.Blocks).Translation);
        Assert.True(result.Blocks.Single().IsComplete);
        Assert.DoesNotContain(updates, update => update.Failed);
        if (hasFacts) Assert.All(updates, update => Assert.True(update.IsComplete && update.Translation == translated));
        Assert.Equal(1, cache.Count);
        Assert.Single(provider.Requests);
        var restored = await Translate(service, [new("new-tweet", original, "new-tweet-group")], new());
        Assert.Equal(0, restored.RequestCount);
        Assert.Equal(1, restored.CacheHits);
        Assert.Equal(translated, Assert.Single(restored.Blocks).Translation);
        Assert.Single(provider.Requests);
    }
    [Fact]
    public async Task WholeUpdatedPhraseIsTranslatedByTheProviderAndReplaysAsOneNaturalSentence()
    {
        const string phrase = "Updated about 19 hours ago";
        var provider = new Provider((request, _, _) => {
            Assert.Equal(phrase, Assert.Single(request.Blocks).Text);
            return Task.FromResult(Json(request, _ => "约19小时前更新"));
        });
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        BrowserPageTextBlock[] source = [new("updated", phrase, "updated", Context: phrase, ContextKind: "source")];
        var result = await Translate(service, source, new());
        Assert.Equal(1, result.RequestCount); Assert.Equal("约19小时前更新", Assert.Single(result.Blocks).Translation); Assert.Equal(0, result.FailedCount);
        var replay = await Translate(new BrowserPageTranslationService(_ => throw new InvalidOperationException("Reuse the whole phrase."), cache: cache), source, new());
        Assert.Equal(0, replay.RequestCount); Assert.Equal(1, replay.CacheHits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateShapedSentenceFragmentsMustNotUseTheStandaloneLocalDateFormatter(bool withRanges)
    {
        const string context = "Published on September 1, 2026 after testing.", date = "September 1, 2026";
        var provider = new Provider((request, _, _) => {
            Assert.Equal(date, Assert.Single(request.Blocks).Text);
            return Task.FromResult(Json(request, _ => "2026年9月1日"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var block = new BrowserPageTextBlock("date", date, "sentence", Context: context, ContextKind: "source");
        if (withRanges) block = block with { SourceGroupHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(context))).ToLowerInvariant(),
            SourceGroupLength = context.Length, SourceStart = context.IndexOf(date, StringComparison.Ordinal), SourceLength = date.Length };
        var result = await Translate(service, [block], new());
        Assert.Equal(1, result.RequestCount); Assert.Equal("2026年9月1日", Assert.Single(result.Blocks).Translation); Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task CalendarLabelsTranslateLocallyWithoutConstructingAProviderOrSendingAnApiRequest()
    {
        var service = new BrowserPageTranslationService(_ => throw new InvalidOperationException("Calendar labels must stay local."));
        BrowserPageTextBlock[] source = [new("short", "Sep 1", "short"), new("full", "Apr 10, 2025", "full"), new("relative", "about 15 hours ago", "relative")];
        var result = await Translate(service, source, new());
        Assert.Equal(0, result.RequestCount); Assert.Equal(0, result.FailedCount);
        Assert.Equal(new[] { "9月1日", "2025年4月10日", "约15小时前" }, result.Blocks.Select(block => block.Translation));
        Assert.All(result.Blocks, block => Assert.True(block.IsComplete));
    }

    [Fact]
    public async Task AbbreviatedDateInProseCannotRevokeAnIndependentLandingPageDescription()
    {
        const string description = "Showcase a bold landing page with stylized title and button";
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block => block.Text == description
            ? "展示一个带有风格化标题和按钮的醒目落地页" : "9月1日更新")));
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        BrowserPageTextBlock[] source = [new("description", description, "description"), new("date", "Updated Sep 1", "date")];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync(source, new(), (update, _) => { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, result.FailedCount); Assert.Equal(2, result.CompletedCount); Assert.DoesNotContain(updates, update => update.Failed);
        var replay = await Translate(new BrowserPageTranslationService(_ => throw new InvalidOperationException("A completed description must replay."), cache: cache), source, new());
        Assert.Equal(0, replay.RequestCount); Assert.Equal(2, replay.CacheHits);
    }

    private static readonly (string Source, string Translation)[] MoreVpnTitles =
    [
        ("What is a dedicated IP VPN?", "什么是专用IP VPN？"),
        ("Dedicated IP vs shared IP — which should I choose?", "专用IP与共享IP——我该选哪一种？"),
        ("Is a dedicated IP recommended for online banking and 2FA?", "专用IP适合用于网上银行和双重身份验证吗？"),
        ("How fast is activation for a dedicated IP VPN?", "专用IP VPN的开通速度有多快？"),
        ("Which ports are available for the VPN connection?", "VPN连接可以使用哪些端口？"),
        ("What VPN protocols are supported?", "支持哪些VPN协议？"),
        ("Can multiple devices use the same dedicated IP?", "多台设备能否使用同一个专用IP？"),
        ("Can I change my dedicated IP location later?", "以后可以更改专用IP的位置吗？"),
        ("Does a dedicated IP help with ad verification and geo-testing?", "专用IP是否有助于广告验证和地理位置测试？"),
        ("How do I whitelist my dedicated IP with third-party services?", "如何在第三方服务中将专用IP加入白名单？")
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoreVpnTenActualArticleTitlesStreamCommitAndReplayWithFreshDomIds(bool legacySharedGroup)
    {
        var expected = MoreVpnTitles.ToDictionary(pair => pair.Source, pair => pair.Translation, StringComparer.Ordinal);
        var prefixObserved = NewSignal();
        var prefixReported = NewSignal();
        var release = NewSignal();
        var provider = new Provider(async (request, progress, token) =>
        {
            progress.Report(new("正在翻译") { ImageSegments = request.Blocks.Select(block =>
                new TranslatedBlock(block.Id, block.Text, "正在翻译", default) { IsComplete = false }).ToArray() });
            prefixReported.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(request, block => expected[block.Text]);
        });
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var source = MoreVpnTitles.Select((pair, index) => new BrowserPageTextBlock("title-" + index, pair.Source,
            legacySharedGroup ? "list" : "card-" + index)).ToArray();
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync(source, new(), (update, _) =>
        { updates.Enqueue(update); if (!update.IsComplete && !update.Failed) prefixObserved.TrySetResult(); return ValueTask.CompletedTask; }, CancellationToken.None);
        try
        {
            await Task.WhenAll(prefixReported.Task, prefixObserved.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(job.IsCompleted);
            Assert.Equal(0, cache.Count);
            // The 2FA card is factual even when its independent siblings share a
            // legacy list group; the ordinary questions still stream immediately.
            Assert.DoesNotContain(updates, update => update.Id == "title-2");
            Assert.Contains(updates, update => !update.IsComplete && update.Id != "title-2");
        }
        finally { release.TrySetResult(); }
        var result = await job.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(10, result.Blocks.Count); Assert.All(result.Blocks, block => Assert.True(block.IsComplete && !block.Failed));
        Assert.Contains(updates, update => !update.IsComplete && !update.Failed);
        Assert.DoesNotContain(updates, update => update.Failed);
        Assert.All(updates.Where(update => update.Id == "title-2"), update =>
        { Assert.True(update.IsComplete); Assert.Equal(MoreVpnTitles[2].Translation, update.Translation); });
        for (var index = 0; index < source.Length; index++)
            Assert.Equal(MoreVpnTitles[index].Translation, result.Blocks.Single(block => block.Id == source[index].Id).Translation);
        var rebound = source.Select((block, index) => block with { Id = "reloaded-" + index,
            GroupId = legacySharedGroup ? "rebuilt-list" : "rebuilt-card-" + index }).ToArray();
        var freshService = new BrowserPageTranslationService(_ => throw new InvalidOperationException("A cache hit must not open a provider."), cache: cache);
        var replay = await Translate(freshService, rebound, new());
        Assert.Equal(0, replay.RequestCount); Assert.Equal(10, replay.CacheHits);
        Assert.All(replay.Blocks, block => Assert.True(block.IsComplete && block.FromCache && !block.Failed));
    }

    [Fact]
    public async Task ChangedAuthenticationRejectsOnlyItsIndependentCardWhileOtherNineTitlesRemainComplete()
    {
        var expected = MoreVpnTitles.ToDictionary(pair => pair.Source, pair => pair.Translation, StringComparer.Ordinal);
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block => block.Text.Contains("2FA", StringComparison.Ordinal)
            ? "专用IP适合用于网上银行和3FA吗？" : expected[block.Text])));
        var service = new BrowserPageTranslationService(_ => provider);
        var source = MoreVpnTitles.Select((pair, index) => new BrowserPageTextBlock("title-" + index, pair.Source, "card-" + index)).ToArray();
        var result = await Translate(service, source, new());
        Assert.Equal("title-2", Assert.Single(result.Blocks, block => block.Failed).Id);
        Assert.Equal(9, result.Blocks.Count(block => block.IsComplete && !block.Failed));
    }

    [Fact]
    public async Task PublicTopBarButtonsAndExpandedTrialAnswerPassProductionTranslationAndCache()
    {
        (string Source, string Translation)[] pairs =
        [
            ("Features", "功能"), ("Pricing", "价格"), ("How It Works", "使用方式"), ("FAQ", "常见问题"),
            ("Blog", "博客"), ("Contact", "联系"), ("Dashboard", "控制面板"), ("Sign Out", "退出登录"),
            ("2. How does the free trial work?", "2. 免费试用是如何进行的？"),
            ("Eligible new customers may start with a three-day free trial. Payment information is required to begin the trial. The selected paid subscription starts automatically when the trial ends unless it is canceled beforehand. The price, billing frequency, and scheduled first charge are shown before checkout is completed.",
                "符合条件的新客户可先享受三天免费试用。开始试用前需要提供付款信息。除非提前取消，否则试用结束后会自动开始所选的付费订阅。价格、付款频率和首次扣款时间会在结账完成前显示。")
        ];
        var expected = pairs.ToDictionary(pair => pair.Source, pair => pair.Translation, StringComparer.Ordinal);
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block => expected[block.Text])));
        var service = new BrowserPageTranslationService(_ => provider);
        var source = pairs.Select((pair, index) => new BrowserPageTextBlock("public-" + index, pair.Source, "control-" + index)).ToArray();
        var result = await Translate(service, source, new());
        Assert.All(result.Blocks, block => Assert.True(block.IsComplete && !block.Failed));
        Assert.Equal(1, result.RequestCount);
        for (var index = 0; index < source.Length; index++)
            Assert.Equal(pairs[index].Translation, result.Blocks.Single(block => block.Id == source[index].Id).Translation);
        Assert.Equal(0, (await Translate(service, source, new())).RequestCount);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task BatchesAreReallyConcurrentAndFirstDomUpdateArrivesBeforeCompletion(int concurrency)
    {
        var threeStarted = NewSignal();
        var release = NewSignal();
        var firstVisible = NewSignal();
        var calls = 0;
        var running = 0;
        var maximum = 0;
        var provider = new Provider(async (request, progress, token) =>
        {
            var concurrent = Interlocked.Increment(ref running);
            InterlockedExtensions.Max(ref maximum, concurrent);
            if (Interlocked.Increment(ref calls) == concurrency) threeStarted.TrySetResult();
            try
            {
                progress.Report(new("前") { ImageSegments = request.Blocks.Select(block =>
                    new TranslatedBlock(block.Id, block.Text, "前", default) { IsComplete = false }).ToArray() });
                await release.Task.WaitAsync(token);
                return Json(request, _ => "前译文");
            }
            finally { Interlocked.Decrement(ref running); }
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var translation = service.TranslateAsync(Blocks(concurrency * 2), new() { MaxBatchBlocks = 1, Parallelism = concurrency }, (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.IsComplete && update.Translation == "前") firstVisible.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        try
        {
            await Task.WhenAll(threeStarted.Task, firstVisible.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(translation.IsCompleted);
            Assert.Equal(concurrency, maximum);
        }
        finally { release.TrySetResult(); await translation.WaitAsync(TimeSpan.FromSeconds(8)); }
        var result = await translation.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(concurrency * 2, result.RequestCount);
        Assert.All(result.Blocks, update => Assert.True(update.IsComplete));
        Assert.All(provider.Requests, request =>
        {
            Assert.True(request.IsPageText);
            Assert.Equal(ThinkingLevel.None, request.ThinkingOverride);
            Assert.Null(request.ImagePng);
        });
    }

    [Fact]
    public async Task LongNodePiecesMayFinishOutOfOrderButReplacementRemainsInSourceOrder()
    {
        var firstWaiting = NewSignal();
        var laterFinished = NewSignal();
        var releaseFirst = NewSignal();
        var laterCount = 0;
        var provider = new Provider(async (request, progress, token) =>
        {
            var source = Assert.Single(request.Blocks).Text;
            var translation = source[0] == 'A' ? "甲" : source[0] == 'B' ? "乙" : "丙";
            if (source[0] == 'A')
            { firstWaiting.TrySetResult(); await releaseFirst.Task.WaitAsync(token); }
            else if (Interlocked.Increment(ref laterCount) == 2) laterFinished.TrySetResult();
            progress.Report(new(translation) { ImageSegments = [new(request.Blocks[0].Id, source, translation, default)] });
            return Json(request, _ => translation);
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var text = new string('A', 64) + new string('B', 64) + new string('C', 64);
        var job = service.TranslateAsync([new("dom-original", text)], new()
        { MaxPartCharacters = 64, MaxBatchBlocks = 1 }, (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        await Task.WhenAll(firstWaiting.Task, laterFinished.Task).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(updates);
        releaseFirst.TrySetResult();
        var result = await job.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("甲乙丙", Assert.Single(result.Blocks).Translation);
        Assert.All(updates, update =>
        { Assert.Equal("dom-original", update.Id); Assert.StartsWith(update.Translation, "甲乙丙"); });
        Assert.Equal(1, service.CacheCount);
    }

    [Fact]
    public async Task CancellationRevokesLateCallbacksAndDrainsAllStartedRequests()
    {
        var started = NewSignal();
        var ended = 0;
        var calls = 0;
        var provider = new Provider(async (request, progress, token) =>
        {
            if (Interlocked.Increment(ref calls) == 3) started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                // Deliberately misbehaving callback after cancellation must be ignored.
                progress.Report(new("late") { ImageSegments = [new(request.Blocks[0].Id, "", "late", default)] });
                Interlocked.Increment(ref ended);
            }
            return Json(request, _ => "late");
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = 0;
        using var cancellation = new CancellationTokenSource();
        var job = service.TranslateAsync(Blocks(10), new() { MaxBatchBlocks = 1, Parallelism = 3 }, (_, _) =>
        { Interlocked.Increment(ref updates); return ValueTask.CompletedTask; }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(3, calls);
        Assert.Equal(3, ended);
        Assert.Equal(0, updates);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task CachedWholeNodesUseOriginalIdAndAreIsolatedByTargetStyleAndContext()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "翻译")));
        var service = new BrowserPageTranslationService(_ => provider);
        var options = new BrowserPageTranslationOptions();
        await Translate(service, [new("first", "An original sentence.")], options);
        var cached = await Translate(service, [new("new-dom-id", "An original sentence.")], options);
        Assert.Equal(0, cached.RequestCount);
        Assert.Equal(1, cached.CacheHits);
        Assert.Equal("new-dom-id", Assert.Single(cached.Blocks).Id);
        await Translate(service, [new("new-dom-id", "An original sentence.")], options with { TargetLanguage = "日本語" });
        await Translate(service, [new("new-dom-id", "An original sentence.")], options with { Style = TranslationStyle.Literal });
        await Translate(service, [new("new-dom-id", "An original sentence.")], options with { Context = "Computing terminology" });
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task SameParagraphWithDifferentNeighbourContextIsNotReused()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "翻译")));
        var service = new BrowserPageTranslationService(_ => provider);
        await Translate(service, [new("a", "A river.", "g"), new("b", "The bank.", "g")], new());
        var second = await Translate(service, [new("c", "An investment.", "g"), new("d", "The bank.", "g")], new());
        Assert.Equal(0, second.CacheHits);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task InvalidFinalJsonRollsBackItsNodesAndDoesNotCacheStreamPrefixes()
    {
        var provider = new Provider((request, progress, _) =>
        {
            progress.Report(new("暂") { ImageSegments = [new(request.Blocks[0].Id, "", "暂", default)] });
            return Task.FromResult("{\"blocks\":[]}");
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync(Blocks(2), new(), (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, result.FailedCount);
        Assert.All(Blocks(2), block => Assert.Contains(updates, update => update.Id == block.Id && update.Failed));
        Assert.Equal(0, service.CacheCount);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedTranslationStringCannotCommitBeforeWholeResponseValidation(bool duplicateField)
    {
        var prefixSeen = NewSignal();
        var release = NewSignal();
        var provider = new Provider(async (request, progress, token) =>
        {
            progress.Report(new("已闭合的完整字符串") { ImageSegments =
                [new(request.Blocks[0].Id, request.Blocks[0].Text, "已闭合的完整字符串", default) { IsComplete = true }] });
            await release.Task.WaitAsync(token);
            return duplicateField
                ? Json(request, _ => "译文").Replace("\"translation\":", "\"translation\":\"duplicate\",\"translation\":")
                : "{\"blocks\":[{\"id\":\"" + request.Blocks[0].Id + "\",\"translation\":\"已闭合的完整字符串\"}]}";
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var job = service.TranslateAsync(Blocks(2), new(), (update, _) =>
        {
            updates.Enqueue(update);
            if (!update.Failed) prefixSeen.TrySetResult();
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        await prefixSeen.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.All(updates, update => Assert.False(update.IsComplete));
        Assert.Equal(0, service.CacheCount);
        release.TrySetResult();
        var result = await job;
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(0, service.CacheCount);
        Assert.All(Blocks(2), block => Assert.Contains(updates, update => update.Id == block.Id && update.Failed));
    }

    [Fact]
    public async Task MalformedBatchRetriesInSmallerGroupsAndCommitsWithoutManualTranslation()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(request.Blocks.Count > 4
            ? "{\"blocks\":[]}" : Json(request, block => "中文：" + block.Text)));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Translate(service, Blocks(9), new());
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(result.Blocks, update => Assert.True(update.IsComplete));
        var cached = await Translate(service, Blocks(9), new());
        Assert.Equal(9, cached.CacheHits);
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task PermanentFormatFailureKeepsIndependentBatchesAndDoesNotExceedRetryBudget()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(request.Blocks.Any(block => block.Text.Contains("paragraph 0", StringComparison.Ordinal))
            ? "{\"blocks\":[]}" : Json(request, block => "中文：" + block.Text)));
        var service = new BrowserPageTranslationService(_ => provider);
        var blocks = Enumerable.Range(0, 12).Select(index => new BrowserPageTextBlock("b" + index,
            "An English paragraph " + index + " can be translated independently.")).ToArray();
        var result = await Translate(service, blocks, new() { MaxBatchBlocks = 1, MaxRequests = 13 });
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(13, provider.Requests.Count);
        Assert.Equal(11, result.Blocks.Count(update => update.IsComplete));
        Assert.Equal(11, service.CacheCount);
    }

    [Fact]
    public async Task SimultaneousDocumentsShareTenProviderSlots()
    {
        var current = 0; var maximum = 0; var started = NewSignal(); var release = NewSignal();
        var provider = new Provider(async (request, _, token) =>
        {
            var count = Interlocked.Increment(ref current);
            lock (release) maximum = Math.Max(maximum, count);
            if (count == 10) started.TrySetResult();
            try { await release.Task.WaitAsync(token); return Json(request, block => "中文：" + block.Text); }
            finally { Interlocked.Decrement(ref current); }
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var jobs = Enumerable.Range(0, 3).Select(index => Translate(service, Blocks(12),
            new() { MaxBatchBlocks = 1, Context = "document " + index, Parallelism = 10 })).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(10, current);
        release.TrySetResult(); await Task.WhenAll(jobs);
        Assert.Equal(10, maximum);
        Assert.All(jobs, job => Assert.Equal(0, job.Result.FailedCount));
    }

    [Fact]
    public async Task MovedWeekdayAndPunctuationOutputRestoreWholeInlineGroupButKeepIndependentSuccess()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block =>
            block.Text.StartsWith("Please contact", StringComparison.Ordinal) ? "请在周四正午前联系米拉。"
                : block.Text.StartsWith("before noon", StringComparison.Ordinal) ? "，" : "入口是蓝色。")));
        var service = new BrowserPageTranslationService(_ => provider);
        BrowserPageTextBlock[] source = [new("name", "Please contact Mira Chen.", "inline"),
            new("date", "before noon on Thursday", "inline"), new("other", "The entrance is blue.", "other")];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync(source, new() { MaxBatchBlocks = 1 }, (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, result.Blocks.Count(block => block.Failed));
        Assert.Equal("入口是蓝色。", Assert.Single(result.Blocks, block => block.IsComplete).Translation);
        Assert.Equal(1, service.CacheCount);
        Assert.Contains(updates, update => update.Id == "name" && update.Failed);
        Assert.Contains(updates, update => update.Id == "date" && update.Failed);
        var repeat = await Translate(service, source, new() { MaxBatchBlocks = 1 });
        Assert.Equal(2, repeat.RequestCount);
        Assert.Equal(1, repeat.CacheHits);
    }

    [Fact]
    public async Task QuantityMovedIntoDifferentSemanticGroupIsNotCommittedOrCached()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block =>
            block.Text.StartsWith("Bring", StringComparison.Ordinal) ? "带250毫升的瓶子"
                : block.Text.StartsWith("with capacity", StringComparison.Ordinal) ? "容量合适" : "入口是蓝色。")));
        var service = new BrowserPageTranslationService(_ => provider);
        BrowserPageTextBlock[] source = [new("receiver", "Bring bottles", "a"),
            new("owner", "with capacity 250 ml", "b"), new("good", "The entrance is blue.", "c")];
        var result = await Translate(service, source, new());
        Assert.Equal(new[] { "receiver", "owner" }, result.Blocks.Where(block => block.Failed).Select(block => block.Id));
        Assert.True(Assert.Single(result.Blocks, block => block.Id == "good").IsComplete);
        Assert.Equal(1, service.CacheCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticationAndRateLimitsStopQueuedBatchesWithoutRetries(bool authentication)
    {
        var allStarted = NewSignal();
        var requests = 0;
        var ended = 0;
        var provider = new Provider(async (_, _, token) =>
        {
            var number = Interlocked.Increment(ref requests);
            if (number == 3) allStarted.TrySetResult();
            try
            {
                await allStarted.Task.WaitAsync(token);
                if (number == 1)
                {
                    if (authentication) throw new TranslationAuthenticationException("Invalid key");
                    throw new TranslationRateLimitException("Rate limit");
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return string.Empty;
            }
            finally { Interlocked.Increment(ref ended); }
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var job = Translate(service, Blocks(12), new() { MaxBatchBlocks = 1, Parallelism = 3 });
        if (authentication) await Assert.ThrowsAsync<TranslationAuthenticationException>(() => job);
        else await Assert.ThrowsAsync<TranslationRateLimitException>(() => job);
        Assert.Equal(3, requests);
        Assert.Equal(3, ended);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task RequestBudgetAndSnapshotBoundsAreValidatedBeforeProviderCreation()
    {
        var created = 0;
        var service = new BrowserPageTranslationService(_ =>
        { created++; throw new InvalidOperationException("Must not create a provider."); });
        await Assert.ThrowsAsync<TranslationConfigurationException>(() => Translate(service, Blocks(10),
            new() { MaxBatchBlocks = 1, MaxRequests = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Translate(service, Blocks(129), new()));
        await Assert.ThrowsAsync<ArgumentException>(() => Translate(service,
            [new("a", new string('x', 32_769))], new()));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task CacheIsBoundedAndEvictsLeastRecentlyUsedWholeNodes()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "译")));
        var service = new BrowserPageTranslationService(_ => provider, maximumCacheEntries: 2);
        await Translate(service, [new("a", "Alpha")], new());
        await Translate(service, [new("b", "Beta")], new());
        await Translate(service, [new("a2", "Alpha")], new());
        await Translate(service, [new("c", "Gamma")], new());
        var evicted = await Translate(service, [new("b2", "Beta")], new());
        Assert.Equal(1, evicted.RequestCount);
        Assert.Equal(2, service.CacheCount);
    }

    [Fact]
    public async Task SemanticGroupsAreKeptTogetherWhenTheyFitOneBoundedRequest()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "译文")));
        var service = new BrowserPageTranslationService(_ => provider);
        var source = new[]
        {
            new BrowserPageTextBlock("a", new string('A', 70), "first"),
            new BrowserPageTextBlock("b", new string('B', 40), "second"),
            new BrowserPageTextBlock("c", new string('C', 40), "second"),
        };
        await Translate(service, source, new() { MaxBatchCharacters = 128 });
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests, request => request.Blocks.Count == 1 && request.Blocks[0].Text[0] == 'A');
        Assert.Contains(provider.Requests, request => request.Blocks.Count == 2
            && request.Blocks[0].Text[0] == 'B' && request.Blocks[1].Text[0] == 'C');
        Assert.All(provider.Requests, request => Assert.True(request.Blocks.Sum(block => block.Text.Length) <= 128));
    }

    [Fact]
    public async Task SplittingDoesNotLoseSourceCharactersOrSendHalfSurrogatePairs()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "译")));
        var service = new BrowserPageTranslationService(_ => provider);
        var source = new string('A', 63) + "😀" + new string('B', 130);
        await Translate(service, [new("long-emoji", source)], new() { MaxPartCharacters = 64 });
        var parts = Assert.Single(provider.Requests).Blocks;
        Assert.Equal(source, string.Concat(parts.Select(block => block.Text)));
        Assert.All(parts, block =>
        {
            Assert.True(block.Text.Length <= 64);
            Assert.False(char.IsHighSurrogate(block.Text[^1]));
            Assert.False(char.IsLowSurrogate(block.Text[0]));
        });
    }

    [Fact]
    public async Task CallbackFailureCancelsAndDrainsOtherProviders()
    {
        var ended = 0;
        var provider = new Provider(async (request, progress, token) =>
        {
            try
            {
                progress.Report(new("前") { ImageSegments = [new(request.Blocks[0].Id, "", "前", default)] });
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return string.Empty;
            }
            finally { Interlocked.Increment(ref ended); }
        });
        var service = new BrowserPageTranslationService(_ => provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(Blocks(3),
            new() { MaxBatchBlocks = 1 }, (_, _) => throw new InvalidOperationException("Bridge disconnected"),
            CancellationToken.None));
        Assert.Equal(provider.Requests.Count, ended);
        Assert.Equal(0, service.CacheCount);
    }

    private static Task<BrowserPageTranslationResult> Translate(BrowserPageTranslationService service,
        IReadOnlyList<BrowserPageTextBlock> blocks, BrowserPageTranslationOptions options)
        => service.TranslateAsync(blocks, options, (_, _) => ValueTask.CompletedTask, CancellationToken.None);

    private static BrowserPageTextBlock[] Blocks(int count) => Enumerable.Range(0, count)
        .Select(index => new BrowserPageTextBlock("dom-" + index, "Original paragraph " + (char)('A' + index % 26) + ".")) .ToArray();

    private static string Json(TranslationRequest request, Func<OcrBlock, string> translation)
        => JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = translation(block) }) });

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Provider(Func<TranslationRequest, IProgress<TranslationStreamUpdate>, CancellationToken, Task<string>> handler)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
            => TranslateRawStreamingAsync(request, new EmptyProgress(), token);
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        { Requests.Enqueue(request); return handler(request, progress, token); }
    }
    private sealed class EmptyProgress : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) { } }

    private static class InterlockedExtensions
    {
        public static void Max(ref int value, int candidate)
        {
            var current = Volatile.Read(ref value);
            while (candidate > current)
            {
                var previous = Interlocked.CompareExchange(ref value, candidate, current);
                if (previous == current) return;
                current = previous;
            }
        }
    }
}
