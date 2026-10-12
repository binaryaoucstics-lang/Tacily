using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class PageLanguageJudgeTests
{
    private const string English = "The article explains how you can read public pages with the original layout. This method keeps your links and the controls intact while translating the text.";
    [Theory]
    [InlineData(100, 900, 50, true)]
    [InlineData(600, 400, 50, false)]
    [InlineData(600, 400, 60, false)]
    [InlineData(600, 400, 70, true)]
    public async Task ConfiguredThresholdReachesTheProgramJudgement(int chinese, int foreign, int threshold, bool expected)
    {
        var statistics = new PageTextStatistics(TotalCharacters: 1000, HanCharacters: chinese, LatinCharacters: foreign,
            HanProseCharacters: chinese, LatinProseCharacters: foreign, NonChineseProseCharacters: foreign);
        var result = await new PageLanguageJudge().JudgeAsync([new(English)], "zh", BrowserPageJudgementMode.Rules,
            default, statistics, threshold);
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Fact]
    public async Task OmittedThresholdUsesTheExplicitFixedDefault()
    {
        var judge = new PageLanguageJudge();
        var statistics = new PageTextStatistics(1000, 100, 0, 900, 2, 100, 900, 0, 900);
        var implicitDefault = await judge.JudgeAsync([new(English)], "zh", BrowserPageJudgementMode.Rules, default, statistics);
        var explicitDefault = await judge.JudgeAsync([new(English)], "zh", BrowserPageJudgementMode.Rules, default, statistics,
            PageTranslationDecision.DefaultChineseProseShareThresholdPercent);
        Assert.Equal(explicitDefault.NeedsTranslation, implicitDefault.NeedsTranslation);
        Assert.Equal(explicitDefault.ReasonCode, implicitDefault.ReasonCode);
        Assert.Equal(explicitDefault.IsMixedTargetLanguage, implicitDefault.IsMixedTargetLanguage);
    }

    [Fact]
    public async Task ChineseMinorityAdmitsThePageAndPreservesChineseBlockFiltering()
    {
        const string chinese = "这是已经可以阅读的中文正文。我们可以保留这段文字和原有的页面布局。";
        var decision = await new PageLanguageJudge().JudgeAsync([new(English), new(English), new(chinese)],
            "zh-CN", BrowserPageJudgementMode.Rules, default, chineseProseShareThresholdPercent:
            PageTranslationDecision.DefaultChineseProseShareThresholdPercent);
        Assert.True(decision.NeedsTranslation);
        Assert.False(decision.IsMixedTargetLanguage);
        Assert.False(PageTextEligibility.NeedsTranslation(chinese, "zh-CN"));
        Assert.True(PageTextEligibility.NeedsTranslation(English, "zh-CN"));
    }

    [Fact]
    public async Task ThresholdSeparatesLocalAndSharedDecisionMemosWhileEquivalentBoundsReuseThem()
    {
        await using var cache = new BrowserPageTranslationCache();
        var memo = new BrowserPageDecisionMemo(persistentCache: cache);
        PageTextSample[] samples = [new(English)];
        var calls = 0;
        Task<PageTranslationAssessment> Judge(CancellationToken _) {
            calls++;
            return Task.FromResult(new PageTranslationAssessment(true, "foreign", .99, "en", false) { ReasonCode = "foreign-script" });
        }
        Task<(PageTranslationAssessment Assessment, bool CacheHit)> Assess(BrowserPageDecisionMemo instance, int threshold) =>
            instance.AssessAsync(samples, "zh", BrowserPageJudgementMode.Rules, null, Judge, default, threshold);
        Assert.False((await Assess(memo, 50)).CacheHit);
        Assert.True((await Assess(memo, 50)).CacheHit);
        Assert.False((await Assess(memo, 70)).CacheHit);
        var restarted = new BrowserPageDecisionMemo(persistentCache: cache);
        Assert.True((await Assess(restarted, 70)).CacheHit);
        Assert.False((await Assess(restarted, 90)).CacheHit);
        Assert.True((await Assess(restarted, 100)).CacheHit);
        Assert.Equal(3, calls);
    }
    [Theory]
    [InlineData("zh", true)]
    [InlineData("en", false)]
    [InlineData("ja", true)]
    public async Task ProgramDecisionCompletesWithoutModelFiles(string target, bool expected)
    {
        var result = await new PageLanguageJudge().JudgeAsync([new(English)], target, BrowserPageJudgementMode.Rules, default);
        Assert.Equal(expected, result.NeedsTranslation);
        Assert.InRange(result.ElapsedMilliseconds, 0, 500);
    }
    [Fact]
    public async Task ParallelPageJudgmentsDoNotSerializeBehindAModelLoader()
    {
        var judge = new PageLanguageJudge();
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => judge.JudgeAsync([new(English)], "zh", BrowserPageJudgementMode.Rules, default)));
        Assert.All(results, result => Assert.True(result.NeedsTranslation));
    }
    [Fact]
    public async Task CancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PageLanguageJudge().JudgeAsync([new(English)], "zh", BrowserPageJudgementMode.Rules, cancellation.Token));
    }
}
