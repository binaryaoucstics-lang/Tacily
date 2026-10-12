using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageCitationCalendarRangeTests
{
    private const string Citation = ". Paper presented at the 45th Annual Meeting of the Association for Computational Linguistics, June 23–30, 2007, Prague, Czech Republic.";

    [Theory]
    // Both retained actual public provider outputs, with no live provider calls.
    [InlineData("。论文发表于计算语言学协会第45届年会，2007年6月23日至30日，捷克共和国布拉格。")]
    [InlineData("论文发表于2007年6月23日至30日在捷克共和国布拉格举行的计算语言学协会第45届年会。")]
    public void RetainedPublicRangeTranslationsAreEquivalent(string translation)
    {
        Assert.Empty(Check(Citation, translation));
        Assert.Empty(Check(translation, Citation));
    }

    [Theory]
    [InlineData("June 23–30, 2007", "2007年6月23日至30日")]
    [InlineData("June 23-30, 2007", "二〇〇七年六月二十三日至三十日")]
    [InlineData("June 23 to June 30, 2007", "2007年6月23日到6月30日")]
    [InlineData("23–30 June 2007", "2007年6月23日—30日")]
    [InlineData("Jun. 23rd–30th, 2007", "2007年6月23日至30日")]
    [InlineData("June 23–30", "6月23日至30日")]
    [InlineData("February 28–29, 2024", "2024年2月28日至29日")]
    [InlineData("June 30–July 2, 2007", "2007年6月30日至7月2日")]
    public void ExplicitEndpointsAndOnlyExplicitYearsAreEquivalent(string source, string translation)
    {
        Assert.True(PageTextSemanticGuard.HasProtectedFacts(source));
        Assert.Empty(Check(source, translation));
        Assert.Empty(Check(translation, source));
    }

    [Theory]
    [InlineData("2007年6月24日至30日")]
    [InlineData("2007年6月23日至29日")]
    [InlineData("2007年7月23日至30日")]
    [InlineData("2008年6月23日至30日")]
    [InlineData("6月23日至30日")]
    [InlineData("2007年6月23日")]
    [InlineData("2007年6月30日")]
    [InlineData("2007年23日至30日")]
    [InlineData("2007年6月23日至32日")]
    [InlineData("2007年6月23日至7月30日")]
    [InlineData("2007年6月30日至23日")]
    [InlineData("2007年6月23日和30日")]
    [InlineData("6月23日至30日；共2007人")]
    public void ChangedMissingOrRepurposedRangeFactsFail(string translation)
        => Assert.Equal(["node"], Check("June 23–30, 2007", translation));

    [Theory]
    [InlineData("June 23–30", "2007年6月23日至30日")]
    [InlineData("June 23–30, 2007; Monday at 14:30", "2007年6月23日至30日，周二14:30")]
    [InlineData("June 23–30, 2007; Monday at 14:30", "2007年6月23日至30日，周一14:31")]
    [InlineData("June 23–30, 2007; Monday at 14:30", "2007年6月23日至30日，周一")]
    [InlineData("June 23–30, 2007; Monday at 14:30", "2007年6月23日至30日，14:30")]
    [InlineData("February 28–29, 2024", "2023年2月28日至29日")]
    [InlineData("June 23–30, 2007; July 1–2, 2008", "2008年6月23日至30日；2007年7月1日至2日")]
    public void RangeRecognitionKeepsOtherCalendarGuards(string source, string translation)
        => Assert.Equal(["node"], Check(source, translation));

    [Theory]
    [InlineData("12/05/2018", "2018年12月5日")]
    [InlineData("12/05/2018", "2018年5月12日")]
    [InlineData("12/05/2018", "12/05/2018（2018年12月5日）")]
    [InlineData("12/05/2018", "12/5/2018")]
    [InlineData("12/05/2018", "12/05/2019")]
    [InlineData("12/05/2018", "05/12/2018")]
    [InlineData("12/05/2018", "12-05-2018")]
    [InlineData("01.02.2020", "2020年1月2日")]
    [InlineData("１２／０５／２０１８", "12/05/2018")]
    [InlineData("12 / 05 / 2018", "12/05/2018")]
    public void AmbiguousDatesMustRemainLiteral(string source, string translation)
        => Assert.Equal(["node"], Check(source, translation));

    [Theory]
    [InlineData("12/05/2018")]
    [InlineData("01.02.2020")]
    [InlineData("05-12-2018")]
    [InlineData("１２／０５／２０１８")]
    [InlineData("12 / 05 / 2018")]
    public void AmbiguousLiteralCanRemainInsideTranslatedWords(string literal)
        => Assert.Empty(Check($"Article title – {literal}", $"文章标题 – {literal}"));

    [Theory]
    [InlineData("June 23–30, 2007", "6月23日至30日", "Place", "2007年的地点")]
    [InlineData("in 2007", "当年", "June 23–30", "2007年6月23日至30日")]
    [InlineData("June 23–30, 2007", "会议", "Place", "2007年6月23日至30日的地点")]
    [InlineData("June 23–30", "6月23日", "Place", "6月30日的地点")]
    [InlineData("12/05/2018", "日期", "Place", "地点12/05/2018")]
    [InlineData("June 23–30, 2007; 45 papers", "2007年6月23日至30日的论文", "Place", "45个地点")]
    [InlineData("June 23–30, 2007; 30 papers", "2007年6月23日至30日的论文", "Place", "30个地点")]
    [InlineData("June 23–30, 2007; 30 papers", "2007年6月23日至30日的论文", "July 30", "7月30日，共30个地点")]
    public void NumericAndCalendarTransfersRejectBothIds(string owner, string ownerTranslation, string receiver, string receiverTranslation)
    {
        BrowserPageTextBlock[] source = [new("owner", owner), new("receiver", receiver)];
        var assessment = PageTextSemanticGuard.Assess(source, new Dictionary<string, string>
        { ["owner"] = ownerTranslation, ["receiver"] = receiverTranslation });
        Assert.Equal(["owner", "receiver"], assessment.UnsafeIds);
        Assert.Equal([new PageTextNumericTransfer("owner", "receiver")], assessment.NumericTransfers);
    }

    [Fact]
    public void AdjacentYearDoesNotCompleteRangeAndLegitimateRangesDoNotInventTransfers()
    {
        BrowserPageTextBlock[] source = [new("year", "2007"), new("range", "June 23–30")];
        var translations = new Dictionary<string, string> { ["year"] = "2007", ["range"] = "6月23日至30日" };
        Assert.Empty(PageTextSemanticGuard.Assess(source, translations).UnsafeIds);
        Assert.Empty(PageTextSemanticGuard.Assess(source, translations).NumericTransfers);
        translations["range"] = "2007年6月23日至30日";
        Assert.Equal(["range"], PageTextSemanticGuard.Assess(source, translations).UnsafeIds);
    }

    [Theory]
    [InlineData("Sep 1", "9月2日", "显示一个按钮")]
    [InlineData("June 1–2, 2007", "2007年6月2日至3日", "显示一个按钮")]
    [InlineData("June 1–2, 2007", "2007年6月2日至3日", "显示一种按钮")]
    public void ChangedCalendarDoesNotOwnAnIndefiniteArticleInUnrelatedProse(string date, string changedDate, string description)
    {
        BrowserPageTextBlock[] source = [new("description", "Show a button"), new("date", date)];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["description"] = description, ["date"] = changedDate });
        Assert.Equal(["date"], assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Theory]
    [InlineData("Sep 1", "9月2日", "显示1个按钮")]
    [InlineData("Sep 1", "9月2日", "显示第一个按钮")]
    [InlineData("Sep 1", "9月2日", "一")]
    [InlineData("Sep 1", "9月2日", "显示一个按钮，宽一米")]
    [InlineData("Sep 1", "9月2日", "显示一个按钮，共1个")]
    [InlineData("Sep 2", "9月3日", "显示两个按钮")]
    [InlineData("June 1–2, 2007", "2007年6月2日至3日", "显示1个按钮")]
    [InlineData("1 button", "显示按钮", "显示一个按钮")]
    [InlineData("一个按钮", "显示按钮", "显示一个按钮")]
    [InlineData("1", "按钮", "9月1日")]
    [InlineData("一个按钮", "按钮", "9月1日")]
    public void ActualNumericAndCalendarOwnershipStillRejectsBothNodes(string owner, string changedOwner, string receiver)
    {
        BrowserPageTextBlock[] source = [new("owner", owner), new("receiver", "Show a button")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["owner"] = changedOwner, ["receiver"] = receiver });
        Assert.Equal(["owner", "receiver"], assessment.UnsafeIds);
        Assert.Equal([new PageTextNumericTransfer("owner", "receiver")], assessment.NumericTransfers);
    }

    private static IReadOnlyList<string> Check(string source, string translation) =>
        PageTextSemanticGuard.FindUnsafeIds([new("node", source)], new Dictionary<string, string> { ["node"] = translation });
}
