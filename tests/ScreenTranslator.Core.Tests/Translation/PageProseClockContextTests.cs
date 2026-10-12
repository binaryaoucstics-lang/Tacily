using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageProseClockContextTests
{
    private const string ObservedSource = "More innovations during this time included MOSES, the open-source statistical MT engine (2007), a text/SMS translation service for mobiles in Japan (2008), and a mobile phone with built-in speech-to-speech translation functionality for English, Japanese and Chinese (2009). In 2012, Google announced that";
    // Both retained public development outputs, without a new provider request.
    private const string ObservedBatch = "这一时期的更多创新包括开源统计机器翻译引擎MOSES（2007年）、日本面向手机的文本/短信翻译服务（2008年），以及内置英语、日语和中文语音到语音翻译功能的手机（2009年）。2012年，谷歌宣布";
    private const string ObservedSingle = "这一时期的更多创新包括：开源统计机器翻译引擎MOSES（2007年）、日本面向手机的文本/短信翻译服务（2008年），以及内置英、日、中三语语音到语音翻译功能的手机（2009年）。2012年，谷歌宣布";

    [Theory]
    [InlineData(ObservedBatch)]
    [InlineData(ObservedSingle)]
    public void RetainedPublicOutputsKeepTheirYearsWithoutAcquiringAClock(string translation)
    {
        Assert.Empty(Check(ObservedSource, translation));
        Assert.Empty(Check(translation, ObservedSource));
        foreach (var year in new[] { "2007", "2008", "2009", "2012" })
            Assert.Equal(new[] { "node" }, Check(ObservedSource,
                translation.Replace(year, "2013", StringComparison.Ordinal)));
        Assert.Equal(new[] { "node" }, Check(ObservedSource, translation + "。凌晨一时开始。"));
    }

    [Theory]
    [InlineData("During this period, innovations continued.", "这一时期的创新仍在继续。")]
    [InlineData("During this period, innovations continued.", "這一時期的創新仍在繼續。")]
    [InlineData("During this era, innovations continued.", "这一时代的创新仍在继续。")]
    [InlineData("They arrived at the same time.", "他们在同一时间到达。")]
    [InlineData("They arrived at the same moment.", "他們在同一時刻到達。")]
    [InlineData("We remember that stretch of time.", "我们记得那一时段。")]
    [InlineData("We remember that time.", "我们记得那一时光。")]
    [InlineData("Paused temporarily.", "一时停止。")]
    [InlineData("Paused temporarily.", "一時停止しています。")]
    public void LexicalTimeWordsDoNotCreateProtectedClockFacts(string source, string translation)
    {
        Assert.False(PageTextSemanticGuard.HasProtectedFacts(translation));
        Assert.Empty(Check(source, translation));
        Assert.Empty(Check(translation, source));
        Assert.Equal(new[] { "node" }, Check(translation, "凌晨一时开始。"));
    }

    [Theory]
    [InlineData("Start at 1 a.m.", "一时开始。", "二时开始。")]
    [InlineData("Start at 1 a.m.", "一時に開始します。", "二時に開始します。")]
    [InlineData("Start at 1 a.m.", "凌晨一时开始。", "凌晨二时开始。")]
    [InlineData("Start at 1 p.m.", "下午一時開始。", "上午一時開始。")]
    [InlineData("Start at 1 a.m.", "午前一時に開始。", "午後一時に開始。")]
    [InlineData("Pause at 1 a.m.", "午前一時停止します。", "午前二時停止します。")]
    [InlineData("Pause at 1 a.m.", "凌晨一时停止。", "凌晨二时停止。")]
    [InlineData("Start at 1 p.m.", "午後一時に開始。", "午前一時に開始。")]
    [InlineData("Start at 1:30 a.m.", "一时半开始。", "一时二十分开始。")]
    [InlineData("Start at 1:30 p.m.", "下午一点半开始。", "下午一点二十分开始。")]
    [InlineData("Start at 1:15 a.m.", "一時十五分に開始。", "一時十六分に開始。")]
    [InlineData("Start at 1:15 a.m.", "凌晨一点十五分开始。", "凌晨一点十六分开始。")]
    [InlineData("Start at 1 a.m.", "1时开始。", "2时开始。")]
    [InlineData("Start at 1 a.m.", "１時に開始。", "２時に開始。")]
    [InlineData("Start at 13:15.", "13:15开始。", "13:16开始。")]
    [InlineData("Start at 1 a.m.", "1 a.m. 开始。", "1 p.m. 开始。")]
    [InlineData("Start at 13 h.", "13 h 开始。", "14 h 开始。")]
    public void ExplicitClocksStillRequireTheirOriginalHourPeriodAndMinute(
        string source, string translation, string changed)
    {
        Assert.True(PageTextSemanticGuard.HasProtectedFacts(translation));
        Assert.Empty(Check(source, translation));
        Assert.Equal(new[] { "node" }, Check(source, changed));
        Assert.Equal(new[] { "node" }, Check(source, "开始。"));
        Assert.Equal(new[] { "node" }, Check("Start.", translation));
        Assert.Equal(new[] { "node" }, Check(translation, "Start."));
    }

    [Theory]
    [InlineData("一时。")]
    [InlineData("一時")]
    [InlineData("一时，随后开始。")]
    [InlineData("一时 期间举行活动。")]
    [InlineData("一時から開始します。")]
    [InlineData("一時代々木で集合。")]
    [InlineData("一时光线变暗。")]
    [InlineData("一时期间举行活动。")]
    [InlineData("十一时开始。")]
    [InlineData("十一時に開始します。")]
    public void BareClocksAndWordBoundariesRetainAmbiguousSourceBehavior(string source)
    {
        var hour = source.StartsWith("十一", StringComparison.Ordinal) ? 11 : 1;
        Assert.True(PageTextSemanticGuard.HasProtectedFacts(source));
        Assert.Empty(Check(source, $"Start at {hour} a.m."));
        Assert.Empty(Check(source, $"Start at {hour} p.m."));
        Assert.Equal(new[] { "node" }, Check(source, $"Start at {hour + 1} a.m."));
        Assert.Equal(new[] { "node" }, Check(source, "Start."));
    }

    [Theory]
    [InlineData("这一时期，凌晨一时开始。")]
    [InlineData("這一時期，午前一時に開始。")]
    [InlineData("这一时期；一时开始。")]
    [InlineData("这一时期一时开始。")]
    [InlineData("这一时期\n一时开始。")]
    [InlineData("凌晨一时开始。这一时期创新不断。")]
    public void ProseNextToARealClockDoesNotHideOrDuplicateIt(string translation)
    {
        Assert.Empty(Check("During this period, start at 1 a.m.", translation));
        Assert.Equal(new[] { "node" }, Check("During this period, start at 2 a.m.", translation));
        Assert.Equal(new[] { "node" }, Check("During this period, innovations continued.", translation));
    }

    [Fact]
    public void ProseNextToDatesWeekdaysAndQuantitiesStillProtectsEachFact()
    {
        const string source = "During this period, meet on August 1, 2026, Saturday at 1:30 p.m. Bring 12 sensors.";
        const string translation = "这一时期，在2026年8月1日星期六下午一时半见面，带十二个传感器。";
        Assert.Empty(Check(source, translation));
        foreach (var (before, after) in new[]
        {
            ("8月1日", "8月2日"), ("星期六", "星期日"), ("下午", "上午"),
            ("一时半", "一时十五分"), ("十二个", "十一个")
        })
            Assert.Equal(new[] { "node" }, Check(source, translation.Replace(before, after, StringComparison.Ordinal)));
    }

    [Fact]
    public void ProseDoesNotMaskClocksMovedBetweenNodes()
    {
        BrowserPageTextBlock[] source = [new("prose", "During this period, innovations continued."), new("clock", "Start at 1 a.m.")];
        var translations = new Dictionary<string, string>
        {
            ["prose"] = "这一时期的创新仍在继续。", ["clock"] = "凌晨一时开始。"
        };
        Assert.Empty(PageTextSemanticGuard.Assess(source, translations).UnsafeIds);
        translations["prose"] += "凌晨一时开始。";
        translations["clock"] = "开始。";
        Assert.Equal(new[] { "prose", "clock" }, PageTextSemanticGuard.Assess(source, translations).UnsafeIds);
    }

    [Fact]
    public void RetainedYearsMovedToProseNodeStillReportTheNumericTransfer()
    {
        BrowserPageTextBlock[] source = [new("prose", "During this period, innovations continued."), new("year", "Released in 2007.")];
        var assessment = PageTextSemanticGuard.Assess(source, new Dictionary<string, string>
        {
            ["prose"] = "这一时期的创新始于2007年。", ["year"] = "已发布。"
        });
        Assert.Equal(new[] { "prose", "year" }, assessment.UnsafeIds);
        Assert.Equal(new PageTextNumericTransfer("year", "prose"), Assert.Single(assessment.NumericTransfers));
    }

    private static IReadOnlyList<string> Check(string source, string translation)
        => PageTextSemanticGuard.FindUnsafeIds([new("node", source)], new Dictionary<string, string> { ["node"] = translation });
}
