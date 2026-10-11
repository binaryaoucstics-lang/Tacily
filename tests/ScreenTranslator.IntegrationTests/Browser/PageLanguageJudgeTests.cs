using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class PageLanguageJudgeTests
{
    private const string English = "The article explains how you can read public pages with the original layout. This method keeps your links and the controls intact while translating the text.";
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