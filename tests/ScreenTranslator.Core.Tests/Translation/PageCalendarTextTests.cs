using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageCalendarTextTests
{
    [Theory]
    [InlineData("Sep 1", "9月1日")]
    [InlineData("Jul 13", "7月13日")]
    [InlineData("Sep. 1, 2026", "2026年9月1日")]
    [InlineData("1 Sept. 2026", "2026年9月1日")]
    [InlineData("Apr 10,2025", "2025年4月10日")]
    [InlineData("May 12", "5月12日")]
    [InlineData("Feb 29", "2月29日")]
    [InlineData("February 29, 2024", "2024年2月29日")]
    [InlineData("1 day ago", "1天前")]
    [InlineData("2 days ago", "2天前")]
    [InlineData("an hour ago", "1小时前")]
    [InlineData("about 15 hours ago", "约15小时前")]
    [InlineData("less than a minute ago", "不到1分钟前")]
    [InlineData("3 months ago", "3个月前")]
    [InlineData("2 days from now", "2天后")]
    [InlineData("yesterday", "昨天")]
    [InlineData("just now", "刚刚")]
    [InlineData(" \nJul 13\t ", " \n7月13日\t ")]
    [InlineData("Apr １０, ２０２５", "2025年4月10日")]
    public void LocalizesCompleteLabelsWithoutInventingYearOrCalendarTime(string source, string expected)
        => Assert.Equal(expected, PageCalendarText.TranslateLabel(source, "简体中文"));

    [Theory]
    [InlineData("The release is planned for May 12.")]
    [InlineData("Showcase a bold landing page with stylized title and button")]
    [InlineData("August")]
    [InlineData("Feb 30")]
    [InlineData("February 29, 2025")]
    [InlineData("Apr 0")]
    [InlineData("Apr 10, 0000")]
    [InlineData("12:30")]
    [InlineData("2026-10-10")]
    [InlineData("13:30 UTC")]
    [InlineData("june@example.org")]
    public void LeavesProseInvalidDatesAndAlreadyReadableNumericLabelsToTheirOwnPolicy(string source)
        => Assert.Null(PageCalendarText.TranslateLabel(source, "简体中文"));

    [Fact]
    public void RespectsTargetLanguageAndTraditionalUnits()
    {
        Assert.Null(PageCalendarText.TranslateLabel("Sep 1", "English"));
        Assert.Null(PageCalendarText.TranslateLabel(null, "简体中文"));
        Assert.Equal("約15小時前", PageCalendarText.TranslateLabel("about 15 hours ago", "zh-Hant"));
        Assert.Equal("2天後", PageCalendarText.TranslateLabel("2 days from now", "繁体中文"));
    }
}
