using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests;

public sealed class BrowserPageCompletenessTests
{
    private const string LongSource = "The university brings together students and researchers from many backgrounds. "
        + "Its teachers connect classroom study with practical work, encouraging students to ask questions, "
        + "share ideas and explore unfamiliar subjects. Across its departments, students can join seminars, "
        + "work in laboratories and learn from public events. Visitors can also explore the library, meet "
        + "members of the community and discover the full range of educational offerings.";
    private const string LongBad = "大学汇聚了不同背景的学生和研究人员，将课堂学习与实践相结合，鼓励学生提问、交流和探索。"
        + "学生可以参加研讨会、在实验室工作并参加公共活动。访客可以探索图书馆、认识社区成员并了解教育offerings。";
    private const string LongGood = "大学汇聚了不同背景的学生和研究人员，将课堂学习与实践相结合，鼓励学生提问、交流和探索。"
        + "学生可以参加研讨会、在实验室工作并参加公共活动。访客可以探索图书馆、认识社区成员并了解教育项目。";

    [Theory]
    [InlineData(LongSource, LongBad, LongGood)]
    [InlineData("Please read the announcement.", "请阅读announcement。", "请阅读公告。")]
    [InlineData("They shared their experiences.", "他们分享了experiences。", "他们分享了经历。")]
    [InlineData("We welcome suggestions.", "我们欢迎suggestions。", "我们欢迎建议。")]
    public async Task RepairsOrdinaryResidueWithinExistingBudgetAndCachesOnlyCorrectedAnswer(string source, string bad, string good)
    {
        var attempt = 0;
        var provider = new Provider((request, progress, _) =>
        {
            var answer = Interlocked.Increment(ref attempt) == 1 ? bad : good;
            progress.Report(new(answer) { ImageSegments = [new(request.Blocks[0].Id, source, answer, default)] });
            return Task.FromResult(Json(request, _ => answer));
        });
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        BrowserPageTextBlock[] blocks = [new("original-id", source)];
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var options = new BrowserPageTranslationOptions { MaxRequests = 2 };
        var result = await service.TranslateAsync(blocks, options, (update, _) => { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(good, Assert.Single(result.Blocks).Translation);
        Assert.DoesNotContain(updates, update => update.IsComplete && update.Translation == bad);
        Assert.All(provider.Requests, request =>
        {
            Assert.True(request.IsPageText);
            Assert.Equal(ThinkingLevel.None, request.ThinkingOverride);
            Assert.Equal(source, Assert.Single(request.Blocks).Text);
        });
        Assert.Contains("Self-check every retained source word", provider.Requests.Last().Context);
        Assert.Equal(source, blocks[0].Text);
        var replay = await Translate(new BrowserPageTranslationService(_ => throw new InvalidOperationException("Validated cache expected."), cache: cache),
            [new("new-id", source)], options);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(1, replay.CacheHits);
        Assert.Equal(good, Assert.Single(replay.Blocks).Translation);
    }

    [Theory]
    [InlineData("Ada Lovelace works with MIT and NASA.", "Ada Lovelace与MIT和NASA合作。")]
    [InlineData("Use `redis get key` through the API.", "通过API使用`redis get key`。")]
    [InlineData("Open https://example.test/offerings and read ./docs/guide.md.", "打开https://example.test/offerings并阅读./docs/guide.md。")]
    [InlineData("Keep \"the rest of the guide\" as a literal.", "保留字面值\"the rest of the guide\"。")]
    public async Task SourceLiteralNamesAndAcronymsDoNotSpendRepairRequests(string source, string answer)
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => answer)));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Translate(service, [new("literal", source)], new() { MaxRequests = 2 });
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(answer, Assert.Single(result.Blocks).Translation);
    }

    [Theory]
    [InlineData("Use redis for storage.", "使用redis进行存储。")]
    [InlineData("Train embeddings for retrieval.", "训练embeddings用于检索。")]
    [InlineData(LongSource, LongBad)]
    [InlineData("We welcome suggestions.", "我们欢迎suggestions。")]
    public async Task RepeatedAmbiguousTermIsHonestUnknownWithoutPoisoningCacheOrRejectionHistory(string source, string answer)
    {
        var diagnostics = new List<string>();
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => answer)));
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache, diagnostic: diagnostics.Add);
        var options = new BrowserPageTranslationOptions { MaxRequests = 4 };
        var result = await Translate(service, [Identity("term", source)], options);
        Assert.Equal(2, result.RequestCount); // At most one term review, not all four requests.
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(answer, Assert.Single(result.Blocks).Translation);
        Assert.Equal(0, cache.Count); // Neither exact cache nor paragraph artifact.
        Assert.Contains(diagnostics, line => line.Contains("reason=retained_term_unresolved"));
        Assert.Contains(diagnostics, line => line.Contains("reason=service_complete_with_unknown_terms count=1"));
        Assert.DoesNotContain(diagnostics, line => line.Contains(source) || line.Contains(answer));
        var again = await Translate(service, [Identity("term-again", source)], options);
        Assert.Equal(2, again.RequestCount);
        Assert.Equal(0, again.CacheHits);
        Assert.Equal(0, again.FailedCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task NoSpareRequestReportsUnknownWithoutInventingAClassification()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "使用redis进行存储。")));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Translate(service, [new("term", "Use redis for storage.")], new() { MaxRequests = 1 });
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("使用redis进行存储。", Assert.Single(result.Blocks).Translation);
        Assert.Equal(0, service.CacheCount);
    }

    [Fact]
    public async Task SplitLongNodeIsReviewedAsAWholeWithinTheSameFiniteBudget()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, block =>
        {
            var first = seen.Add(block.Text);
            return block.Text.Contains("offerings", StringComparison.Ordinal)
                ? first ? "访客可以了解教育offerings。" : "访客可以了解教育项目。"
                : "大学鼓励学生探索并参与教学活动。";
        })));
        var service = new BrowserPageTranslationService(_ => provider);
        var options = new BrowserPageTranslationOptions { MaxPartCharacters = 128, MaxBatchBlocks = 1, MaxRequests = 16, Parallelism = 1 };
        var result = await Translate(service, [new("split", LongSource)], options);
        Assert.Equal(0, result.FailedCount);
        Assert.True(seen.Count > 1);
        Assert.Equal(2 * seen.Count, result.RequestCount);
        Assert.InRange(result.RequestCount, 2, options.MaxRequests);
        Assert.DoesNotContain("offerings", Assert.Single(result.Blocks).Translation);
        Assert.Equal(LongSource, string.Concat(provider.Requests.Take(seen.Count).SelectMany(request => request.Blocks).Select(block => block.Text)));
        var replay = await Translate(service, [new("rebuilt", LongSource)], options);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(1, replay.CacheHits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ClearProseResidueFailsWithinBudgetAndCannotPoisonExactOrArtifactCache(int budget)
    {
        const string source = "Please read the rest of the announcement.", bad = "请阅读the rest of the announcement。";
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => bad)));
        await using var cache = new BrowserPageTranslationCache();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new List<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync([Identity("prose", source)], new() { MaxRequests = budget },
            (update, _) => { updates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.InRange(result.RequestCount, 1, Math.Min(budget, 2));
        Assert.Equal(1, result.FailedCount);
        Assert.DoesNotContain(updates, update => update.IsComplete);
        Assert.True(Assert.Single(result.Blocks).Failed);
        Assert.Equal(0, cache.Count);
        var corrected = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "请阅读公告的其余部分。")));
        var recovered = await Translate(new BrowserPageTranslationService(_ => corrected, cache: cache), [Identity("new", source)], new());
        Assert.Equal(1, recovered.RequestCount);
        Assert.Equal(0, recovered.CacheHits);
        Assert.Equal(0, recovered.FailedCount);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("legacy")]
    [InlineData("artifact")]
    [InlineData("artifact-merged")]
    public async Task StaleCandidatesAreCheckedBeforePublicationAndRepaired(string kind)
    {
        const string source = "They shared their experiences.", bad = "他们分享了experiences。", good = "他们分享了经历。";
        var block = kind.StartsWith("artifact", StringComparison.Ordinal) ? Identity("stale", source) : new BrowserPageTextBlock("stale", source);
        BrowserPageTextBlock[] blocks = [block];
        var options = new BrowserPageTranslationOptions { MaxRequests = 1 };
        await using var cache = new BrowserPageTranslationCache();
        Seed(cache, kind, block, blocks, options, bad);
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => good)));
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var updates = new List<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync(blocks, options, (update, _) => { updates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(0, result.FailedCount);
        Assert.DoesNotContain(updates, update => update.Translation == bad);
        Assert.Equal(good, Assert.Single(result.Blocks).Translation);
        var replay = await Translate(new BrowserPageTranslationService(_ => throw new InvalidOperationException(), cache: cache), blocks, options);
        Assert.Equal(0, replay.RequestCount);
        Assert.Equal(1, replay.CacheHits);
    }

    [Fact]
    public async Task PartialGroupCacheResidueIsRejectedBeforeItsNeighbourFinishes()
    {
        BrowserPageTextBlock[] blocks = [new("a", "They shared their experiences.", "p"), new("b", "We welcome visitors.", "p")];
        var options = new BrowserPageTranslationOptions { MaxRequests = 1 };
        await using var cache = new BrowserPageTranslationCache();
        Seed(cache, "exact", blocks[0], blocks, options, "他们分享了experiences。");
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request,
            block => block.Text.StartsWith("They", StringComparison.Ordinal) ? "他们分享了经历。" : "我们欢迎访客。")));
        var updates = new List<BrowserPageTranslationUpdate>();
        var service = new BrowserPageTranslationService(_ => provider, cache: cache);
        var result = await service.TranslateAsync(blocks, options, (update, _) => { updates.Add(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(2, Assert.Single(provider.Requests).Blocks.Count);
        Assert.Equal(0, result.FailedCount);
        Assert.DoesNotContain(updates, update => update.Translation.Contains("experiences", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FactErrorsRemainFailuresEvenWhenALowercaseTermIsAmbiguous()
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => "我们使用redis连接13个客户端。")));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Translate(service, [new("facts", "We use redis with 12 clients.")], new() { MaxRequests = 2 });
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, service.CacheCount);
        Assert.InRange(result.RequestCount, 1, 2);
    }

    [Theory]
    [InlineData("fr", "L'université présente ses offerings.")]
    [InlineData("en", "The university presents its offerings.")]
    [InlineData("ja", "大学のofferingsをご覧ください。")]
    public async Task OtherTargetsDoNotInheritChineseCompletenessPolicy(string target, string answer)
    {
        var provider = new Provider((request, _, _) => Task.FromResult(Json(request, _ => answer)));
        var service = new BrowserPageTranslationService(_ => provider);
        var result = await Translate(service, [new("other", "The university presents its offerings.")], new() { TargetLanguage = target, MaxRequests = 2 });
        Assert.Equal(1, result.RequestCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task CancellationDuringCompletenessRetryCannotPublishOrCacheTheUnverifiedAnswer()
    {
        using var cancellation = new CancellationTokenSource();
        var attempt = 0;
        var provider = new Provider((request, _, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 2)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Json(request, _ => "我们欢迎suggestions。"));
        });
        var service = new BrowserPageTranslationService(_ => provider);
        var updates = new List<BrowserPageTranslationUpdate>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync([new("cancel", "We welcome suggestions.")],
            new() { MaxRequests = 2 }, (update, _) => { updates.Add(update); return ValueTask.CompletedTask; }, cancellation.Token));
        Assert.DoesNotContain(updates, update => update.IsComplete);
        Assert.Equal(0, service.CacheCount);
    }

    private static BrowserPageTextBlock Identity(string id, string source) => new(id, source, "paragraph", SourceGroupHash: Hash(source),
        SourceGroupLength: source.Length, SourceStart: 0, SourceLength: source.Length);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // Seed historical candidates without altering production cache contracts.
    private static void Seed(BrowserPageTranslationCache cache, string kind, BrowserPageTextBlock block,
        BrowserPageTextBlock[] blocks, BrowserPageTranslationOptions options, string answer)
    {
        if (kind == "exact")
        {
            var key = (string)typeof(BrowserPageTranslationService).GetMethod("CacheKey", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [block, blocks, options, false])!;
            cache.Set(key, answer);
        }
        else if (kind == "legacy")
        {
            var keys = (IReadOnlyList<string>)typeof(BrowserPageTranslationService).GetMethod("LegacyCacheKeys", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [block, blocks, options, false])!;
            cache.Set(keys[0], answer);
        }
        else
        {
            var artifacts = typeof(BrowserPageTranslationService).Assembly.GetType("ScreenTranslator.App.Services.Browser.BrowserParagraphTranslationArtifacts")!;
            var key = (string)artifacts.GetMethod("Key", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [block, options, "\nforeignHanContext=False", false])!;
            object[] ranges = kind == "artifact-merged"
                ? [new { start = 0, length = 18, sourceHash = Hash(block.Text[..18]), translation = "他们分享了" },
                    new { start = 18, length = block.Text.Length - 18, sourceHash = Hash(block.Text[18..]), translation = "experiences。" }]
                : [new { start = 0, length = block.Text.Length, sourceHash = Hash(block.Text), translation = answer }];
            cache.Set(key, JsonSerializer.Serialize(new { version = 1, groupHash = block.SourceGroupHash, groupLength = block.SourceGroupLength, ranges }));
        }
    }

    private static Task<BrowserPageTranslationResult> Translate(BrowserPageTranslationService service, BrowserPageTextBlock[] blocks, BrowserPageTranslationOptions options)
        => service.TranslateAsync(blocks, options, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    private static string Json(TranslationRequest request, Func<OcrBlock, string> answer)
        => JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = answer(block) }) });
    private sealed class Provider(Func<TranslationRequest, IProgress<TranslationStreamUpdate>, CancellationToken, Task<string>> run)
        : ITranslationProvider, IStreamingTranslationProvider
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
            => TranslateRawStreamingAsync(request, new NoProgress(), token);
        public Task<string> TranslateRawStreamingAsync(TranslationRequest request, IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        { Requests.Enqueue(request); return run(request, progress, token); }
    }
    private sealed class NoProgress : IProgress<TranslationStreamUpdate> { public void Report(TranslationStreamUpdate value) { } }
}
