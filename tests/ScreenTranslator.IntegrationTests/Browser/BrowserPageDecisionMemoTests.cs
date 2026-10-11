using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserPageDecisionMemoTests
{
    private static PageTextSample[] Samples => [new("The original source paragraph is about a quiet river and a wooden bridge.")];
    private static PageTranslationAssessment Positive => new(true, "free-form reason must not persist", .97, "en", false)
        { ReasonCode = "foreign-script", EligibleBlocks = Samples };

    [Fact]
    public async Task NewMemoAfterRestartRestoresProgramDecisionWithoutCallingJudge()
    {
        using var directory = new TestDirectory();
        PageTextSample[] french = [new("Cette bibliothèque est ouverte pour les visiteurs. Les lecteurs peuvent trouver des articles dans les collections avec des informations pour votre famille.")];
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var judge = new PageLanguageJudge();
            var memo = new BrowserPageDecisionMemo(persistentCache: cache);
            var first = await memo.AssessAsync(french, "en", BrowserPageJudgementMode.Rules, null,
                token => judge.JudgeAsync(french, "en", BrowserPageJudgementMode.Rules, token), CancellationToken.None);
            Assert.Equal("program-foreign-language", first.Assessment.ReasonCode); Assert.False(first.CacheHit);
            Assert.Equal(1, memo.JudgementCalls);
            await cache.FlushAsync();
        }
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var judge = new PageLanguageJudge();
            var memo = new BrowserPageDecisionMemo(persistentCache: cache);
            var restored = await memo.AssessAsync(french, "en", BrowserPageJudgementMode.Rules, null,
                token => judge.JudgeAsync(french, "en", BrowserPageJudgementMode.Rules, token), CancellationToken.None);
            Assert.True(restored.CacheHit); Assert.True(restored.Assessment.NeedsTranslation);
            Assert.Empty(restored.Assessment.EligibleBlocks);
            Assert.Equal(0, restored.Assessment.ElapsedMilliseconds);
            Assert.Equal(0, memo.JudgementCalls); Assert.Equal(1, memo.CacheHits);
        }
    }

    [Fact]
    public async Task PersistedDecisionContainsNoOriginalSamplesEligibleBlocksOrFreeFormReason()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserPageDecisionMemo(persistentCache: cache);
        await memo.AssessAsync(Samples, "zh-CN", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromResult(Positive), CancellationToken.None);
        await cache.FlushAsync();
        var key = System.IO.Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories)));
        var serialized = (await cache.GetManyAsync([key]))[key];
        Assert.DoesNotContain("original source paragraph", serialized); Assert.DoesNotContain("free-form reason", serialized);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(new[] { "confidence", "detectedLanguage", "isMixedTargetLanguage", "needsTranslation", "reasonCode", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task OriginalSamplesKindsHintsStatisticsTargetModeModelAndPolicyAllIsolateDecisions()
    {
        await using var cache = new BrowserPageTranslationCache();
        var calls = 0;
        Task<PageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        var memo = new BrowserPageDecisionMemo(persistentCache: cache);
        await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        Assert.True((await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit);
        Assert.Equal(1, calls);
        await memo.AssessAsync([new(Samples[0].Text + " Changed.")], "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        await memo.AssessAsync([Samples[0] with { Kind = "heading" }], "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        await memo.AssessAsync([Samples[0] with { LanguageHint = "fr" }], "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, new(TotalCharacters: 80, LatinCharacters: 60), Judge, CancellationToken.None);
        await memo.AssessAsync(Samples, "ja", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        await new BrowserPageDecisionMemo(persistentCache: cache, policyVersion: "changed-policy")
            .AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        Assert.Equal(7, calls);
    }

    [Theory]
    [InlineData("model-uncertain")]
    [InlineData("local-unavailable")]
    [InlineData("local-timeout")]
    [InlineData("same-script-ambiguous")]
    [InlineData("han-only-ambiguous")]
    [InlineData("unsupported-target-model")]
    [InlineData("unexpected-error")]
    public async Task UncertainFailureAndUnavailableOutcomesAreNeverStickyOrPersisted(string code)
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserPageDecisionMemo(persistentCache: cache); var calls = 0;
        var uncertain = Positive with { NeedsTranslation = false, ReasonCode = code };
        Task<PageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(uncertain); }
        Assert.False((await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit);
        Assert.False((await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit);
        await cache.FlushAsync(); Assert.Equal(2, calls); Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("target-body", false)]
    [InlineData("mixed-target-body", true)]
    [InlineData("insufficient-body", false)]
    public async Task CertainNegativePolicyDecisionsAreReused(string code, bool mixed)
    {
        var memo = new BrowserPageDecisionMemo(); var calls = 0;
        Task<PageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(
            new PageTranslationAssessment(false, "keep original", .98, code == "insufficient-body" ? "und" : "zh", mixed) { ReasonCode = code }); }
        await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        var cached = await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        Assert.True(cached.CacheHit); Assert.False(cached.Assessment.NeedsTranslation);
        Assert.Equal(mixed, cached.Assessment.IsMixedTargetLanguage); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailureAndCanceledLateJudgeCannotCreateACachedDecision()
    {
        await using var cache = new BrowserPageTranslationCache();
        var memo = new BrowserPageDecisionMemo(persistentCache: cache);
        await Assert.ThrowsAsync<IOException>(() => memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromException<PageTranslationAssessment>(new IOException("local model failed")), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var started = Signal(); var release = Signal();
        var active = memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, async _ =>
        { started.TrySetResult(); await release.Task; return Positive; }, cancellation.Token);
        await started.Task; cancellation.Cancel(); release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(0, memo.CacheHits); Assert.Equal(0, cache.Count);
        var retry = await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromResult(Positive), CancellationToken.None);
        Assert.False(retry.CacheHit); Assert.Equal(3, memo.JudgementCalls);
    }

    [Fact]
    public async Task SharedCacheClearAloneInvalidatesWarmMemoAndPreventsLateJudgeWritingBack()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserPageDecisionMemo(persistentCache: cache);
        var started = Signal(); var release = Signal();
        var active = memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, async _ =>
        { started.TrySetResult(); await release.Task; return Positive; }, CancellationToken.None);
        await started.Task; await cache.ClearAsync(); release.TrySetResult(); await active; await cache.FlushAsync();
        Assert.Equal(0, cache.Count); Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        Assert.False((await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromResult(Positive), CancellationToken.None)).CacheHit);
        await cache.ClearAsync();
        Assert.False((await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromResult(Positive), CancellationToken.None)).CacheHit);
        Assert.Equal(3, memo.JudgementCalls);
    }

    [Fact]
    public async Task InvalidPersistedDecisionFallsBackToRealJudge()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var first = new BrowserPageDecisionMemo(persistentCache: cache);
        await first.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, _ => Task.FromResult(Positive), CancellationToken.None);
        await cache.FlushAsync();
        var key = System.IO.Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories)));
        cache.Set(key, "{\"version\":1,\"reasonCode\":\"model-uncertain\",\"needsTranslation\":true,\"detectedLanguage\":\"en\",\"confidence\":0.99}");
        var second = new BrowserPageDecisionMemo(persistentCache: cache);
        var result = await second.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null,
            _ => Task.FromResult(Positive), CancellationToken.None);
        Assert.False(result.CacheHit); Assert.Equal(1, second.JudgementCalls);
    }

    [Fact]
    public async Task WarmDecisionLruIsBoundedAndExplicitClearRequiresNewJudge()
    {
        var memo = new BrowserPageDecisionMemo(maximumEntries: 2); var calls = 0;
        Task<PageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        async Task<bool> Assess(string text) => (await memo.AssessAsync([new(text)], "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit;
        Assert.False(await Assess("Alpha paragraph")); Assert.False(await Assess("Beta paragraph")); Assert.True(await Assess("Alpha paragraph"));
        Assert.False(await Assess("Gamma paragraph")); Assert.False(await Assess("Beta paragraph")); Assert.Equal(4, calls);
        memo.Clear(); Assert.False(await Assess("Beta paragraph")); Assert.Equal(5, calls);
    }

    [Fact]
    public async Task HtmlWhitespaceHydrationKeepsOriginalWordDecisionButActualWordChangeRejudges()
    {
        var calls = 0; var memo = new BrowserPageDecisionMemo();
        Task<PageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        await memo.AssessAsync(Samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None);
        var hydrated = new[] { Samples[0] with { Text = Samples[0].Text.Replace("source paragraph", "source\n\tparagraph", StringComparison.Ordinal) } };
        Assert.True((await memo.AssessAsync(hydrated, "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit);
        Assert.False((await memo.AssessAsync([Samples[0] with { Text = Samples[0].Text.Replace("river", "lake", StringComparison.Ordinal) }],
            "zh", BrowserPageJudgementMode.Rules, null, Judge, CancellationToken.None)).CacheHit);
        Assert.Equal(2, calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "work", "decision-memo-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
