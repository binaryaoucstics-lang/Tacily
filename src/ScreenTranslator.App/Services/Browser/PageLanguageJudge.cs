using System.Diagnostics;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.Services.Browser;

public sealed class PageLanguageJudge
{
    public Task<PageTranslationAssessment> JudgeAsync(IReadOnlyList<PageTextSample> blocks,
        string targetLanguage, BrowserPageJudgementMode mode, CancellationToken cancellationToken,
        PageTextStatistics? statistics = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var decision = PageTranslationDecision.Inspect(blocks, targetLanguage, statistics).RuleDecision;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(decision with { ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
    }
}