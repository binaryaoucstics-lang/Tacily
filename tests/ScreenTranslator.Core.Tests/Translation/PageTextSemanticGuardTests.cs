using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextSemanticGuardTests
{
    [Theory]
    // Original Stanford phrase: 晚期 must not lose the source's late phase.
    [InlineData("in the late 19th century", "在19世纪晚期")]
    [InlineData("early 19th century", "19世纪早期")]
    [InlineData("mid-19th century", "19世纪中期")]
    [InlineData("mid-19th century", "十九世纪中叶")]
    [InlineData("mid-19th century", "十九世紀中葉")]
    [InlineData("late 19th century", "19世纪后期")]
    [InlineData("late 19th century", "十九世紀後期")]
    [InlineData("early 1980s", "20世纪80年代早期")]
    [InlineData("early 2000s", "二〇〇〇年代早期")]
    [InlineData("mid-1980s", "1980年代中期")]
    [InlineData("mid-1980s", "20世纪80年代中叶")]
    [InlineData("mid-1980s", "二十世紀八十年代中葉")]
    [InlineData("late 1980s", "20世纪80年代晚期")]
    [InlineData("late 1980s", "1980年代后期")]
    [InlineData("late 1980s", "一九八〇年代後期")]
    public void ExplicitChinesePhaseSynonymsPreservePreciseCalendarFacts(string source, string translation)
    {
        Assert.True(PageTextSemanticGuard.HasProtectedFacts(source));
        Assert.True(PageTextSemanticGuard.HasProtectedFacts(translation));
        Assert.Empty(Check(source, translation));
        Assert.Empty(Check(translation, source));
    }

    [Theory]
    [InlineData("early 19th century", "19世纪中叶")]
    [InlineData("early 19th century", "19世纪晚期")]
    [InlineData("mid-19th century", "19世纪早期")]
    [InlineData("mid-19th century", "19世纪后期")]
    [InlineData("late 19th century", "19世纪早期")]
    [InlineData("late 19th century", "19世紀中葉")]
    [InlineData("early 1980s", "1980年代中叶")]
    [InlineData("early 1980s", "1980年代晚期")]
    [InlineData("mid-1980s", "1980年代早期")]
    [InlineData("mid-1980s", "1980年代後期")]
    [InlineData("late 1980s", "1980年代早期")]
    [InlineData("late 1980s", "1980年代中葉")]
    [InlineData("late 19th century", "19世纪")]
    [InlineData("19th century", "19世纪晚期")]
    [InlineData("late 1980s", "1980年代")]
    [InlineData("1980s", "1980年代后期")]
    [InlineData("in the late 19th century", "在20世纪晚期")]
    [InlineData("late 1980s", "20世纪90年代晚期")]
    [InlineData("early 2000s", "21世纪早期")]
    public void ExplicitChinesePhasesCannotChangeOrReplaceCalendarPrecision(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Theory]
    // Hand-authored regressions for the observed Wikipedia and Stanford failures.
    [InlineData("in the 1960s, was used by Xerox to translate technical manuals (1978). Beginning in the late 1980s, as", "在20世纪60年代，被施乐公司用于翻译技术手册（1978年）。从20世纪80年代末开始，随着")]
    [InlineData("1990s and early 2000s", "1990年代和2000年代初")]
    [InlineData("The 7 Schools", "七所学校")]
    [InlineData("The 7 Schools", "七所學校")]
    [InlineData("1960s", "20世纪60年代")]
    [InlineData("1960s", "二十世纪六十年代")]
    [InlineData("1960s", "二十世紀六十年代")]
    [InlineData("1960s", "一九六〇年代")]
    [InlineData("early 2000s", "二〇〇〇年代初期")]
    [InlineData("early 2000s", "21世纪00年代初")]
    [InlineData("late 1980s", "一九八零年代末")]
    [InlineData("mid-1980s", "20世纪80年代中期")]
    [InlineData("in the 1960's", "1960年代")]
    [InlineData("1960s–1980s", "20世纪60年代至20世纪80年代")]
    [InlineData("1960s-1980s", "1960年代至1980年代")]
    [InlineData("20th century", "二十世纪")]
    [InlineData("early 21st century", "21世纪初")]
    [InlineData("七所学校", "The 7 Schools")]
    [InlineData("3 ships and 4 vehicles", "三艘船和四辆车")]
    [InlineData("2 buildings", "两栋楼")]
    public void AcceptsPreciseDecadesCenturiesAndExplicitChineseClassifiers(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("in the 1960s, was used by Xerox to translate technical manuals (1978). Beginning in the late 1980s, as", "在21世纪60年代，被施乐公司用于翻译技术手册（1978年）。从20世纪80年代末开始，随着")]
    [InlineData("1990s and early 2000s", "20世纪90年代至21世纪初")]
    [InlineData("early 2000s", "21世纪初")]
    [InlineData("early 2000s", "2000年代")]
    [InlineData("early 2000s", "2000年代末")]
    [InlineData("late 1980s", "20世纪80年代初")]
    [InlineData("1960s", "21世纪60年代")]
    [InlineData("1960s", "20世纪70年代")]
    [InlineData("1960s", "1960年")]
    [InlineData("1960s", "1961年代")]
    [InlineData("1960s", "1960年代及1970年代")]
    [InlineData("1960s–1980s", "1960年代至1990年代")]
    [InlineData("1960s–1980s", "1960年代")]
    [InlineData("20th century", "21世纪")]
    [InlineData("early 21st century", "21世纪")]
    [InlineData("The 7 Schools", "八所学校")]
    [InlineData("The 7 Schools", "学校")]
    [InlineData("七所学校", "The 8 Schools")]
    public void RejectsChangedMissingOrVagueDecadesAndSchoolCounts(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Theory]
    [InlineData("Wait for 2000s.", "等待2000秒。")]
    [InlineData("The task took 2000s.", "任务花了两千秒。")]
    [InlineData("Timeout: 2000s", "超时：2000秒")]
    [InlineData("2000s elapsed.", "经过了两千秒。")]
    [InlineData("A 2000s delay.", "延迟2000秒。")]
    [InlineData("The 2000s timeout expired.", "2000秒的超时已到。")]
    [InlineData("The timeout is set to 2000s.", "超时设为两千秒。")]
    [InlineData("The task completed in 2000s.", "任务在两千秒内完成。")]
    [InlineData("-2000s elapsed.", "经过了负两千秒。")]
    [InlineData("Wait for 2000 s.", "等待两千秒。")]
    [InlineData("During the 2000s, adoption grew.", "在2000年代，使用率有所增长。")]
    [InlineData("Wait for 2000s; adoption grew in the 2000s.", "等待两千秒；使用率在2000年代增长。")]
    public void DistinguishesSecondsFromCalendarDecadesUsingLocalContext(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("Wait for 2000s.", "等待2000年代。")]
    [InlineData("The task took 2000s.", "任务花了2000年代。")]
    [InlineData("Timeout: 2000s", "超时：2000年代")]
    [InlineData("A 2000s delay.", "延迟2000年代。")]
    [InlineData("The timeout is set to 2000s.", "超时设为2000年代。")]
    [InlineData("The task completed in 2000s.", "任务在2000年代完成。")]
    [InlineData("Wait for 2000s.", "等待200秒。")]
    [InlineData("During the 2000s, adoption grew.", "2000秒内使用率增长。")]
    [InlineData("During the 2000s, adoption grew.", "在21世纪初，使用率增长。")]
    public void RejectsDurationChangesAndCalendarDurationSubstitution(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Theory]
    [InlineData("late 1980s", true)]
    [InlineData("二十世纪六十年代", true)]
    [InlineData("二〇〇〇年代初", true)]
    [InlineData("21世纪初", true)]
    [InlineData("七所学校", true)]
    [InlineData("Wait for 2000s.", true)]
    [InlineData("所有学校", false)]
    [InlineData("统一思想，唯一选择。", false)]
    [InlineData("万一下雨，千万不要出门。", false)]
    public void ProtectedFactsUsesTheSameCalendarAndQuantityParser(string source, bool expected)
        => Assert.Equal(expected, PageTextSemanticGuard.HasProtectedFacts(source));

    [Theory]
    [InlineData("in the late 1980s", "在20世纪80年代末")]
    [InlineData("early 2000s", "在二〇〇〇年代初")]
    [InlineData("The 7 Schools", "七所学校")]
    [InlineData("in the late 19th century", "在19世纪晚期")]
    [InlineData("early 19th century", "19世纪早期")]
    [InlineData("mid-19th century", "十九世紀中葉")]
    [InlineData("late 19th century", "十九世紀後期")]
    [InlineData("early 1980s", "20世纪80年代早期")]
    [InlineData("mid-1980s", "1980年代中叶")]
    [InlineData("late 1980s", "1980年代后期")]
    public void EquivalentNewFactsStillProtectTheirOwnerAndActualReceiver(string ownerText, string equivalent)
    {
        BrowserPageTextBlock[] source = [new("owner", ownerText), new("receiver", "Read more"), new("unrelated", "Contact us")];
        var translations = new Dictionary<string, string>
        {
            ["owner"] = "更多信息", ["receiver"] = equivalent, ["unrelated"] = "联系我们"
        };
        var assessment = PageTextSemanticGuard.Assess(source, translations);
        Assert.Equal(new[] { "owner", "receiver" }, assessment.UnsafeIds);
        Assert.Equal(new[] { new PageTextNumericTransfer("owner", "receiver") }, assessment.NumericTransfers);

        translations["owner"] = equivalent;
        translations["receiver"] = "了解更多";
        assessment = PageTextSemanticGuard.Assess(source, translations);
        Assert.Empty(assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Theory]
    [InlineData("late 19th century", "19世纪早期")]
    [InlineData("early 19th century", "19世纪中叶")]
    [InlineData("mid-1980s", "1980年代後期")]
    public void WrongPhaseOnAnotherNodeDoesNotFabricateAnEquivalentTransfer(string ownerText, string wrongPhase)
    {
        BrowserPageTextBlock[] source = [new("owner", ownerText), new("receiver", "Read more")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["owner"] = "更多信息", ["receiver"] = wrongPhase });
        Assert.Equal(new[] { "owner", "receiver" }, assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Fact]
    public void WrongCenturyOnAnotherNodeDoesNotFabricateAnEquivalentTransfer()
    {
        BrowserPageTextBlock[] source = [new("owner", "in the 1960s"), new("receiver", "Read more")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["owner"] = "更多信息", ["receiver"] = "在21世纪60年代" });
        Assert.Equal(new[] { "owner", "receiver" }, assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Fact]
    public void ExistingEquivalentFactOnAnotherNodeIsNotAReceivingTransfer()
    {
        BrowserPageTextBlock[] source = [new("owner", "1960s"), new("existing", "20世纪60年代")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["owner"] = "更多信息", ["existing"] = "1960年代" });
        Assert.Equal(new[] { "owner" }, assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Fact]
    public void SwappedDecadesReportBothDirectedTransfers()
    {
        BrowserPageTextBlock[] source = [new("a", "early 2000s"), new("b", "late 1980s")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["a"] = "20世纪80年代末", ["b"] = "二〇〇〇年代初" });
        Assert.Equal(new[] { "a", "b" }, assessment.UnsafeIds);
        Assert.Equal(new[] { new PageTextNumericTransfer("a", "b"), new PageTextNumericTransfer("b", "a") }, assessment.NumericTransfers);
    }

    [Fact]
    public void EqualDurationAndDecadeDigitsDoNotCreateATransferEdge()
    {
        BrowserPageTextBlock[] source = [new("calendar", "in the 2000s"), new("duration", "Wait for 2000s.")];
        var assessment = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["calendar"] = "等待2000秒", ["duration"] = "等待2000秒" });
        Assert.Equal(new[] { "calendar" }, assessment.UnsafeIds);
        Assert.Empty(assessment.NumericTransfers);
    }

    [Theory]
    [InlineData("Tune in next time for more Blue News at 11!", "下次继续收看11点的蓝色新闻！")]
    [InlineData("Meet at 11 tonight.", "今晚11点见。")]
    [InlineData("The stock was quoted at 11 dollars per share.", "这只股票的报价为每股11美元。")]
    [InlineData("We drove at 11 miles per hour.", "我们的行驶速度为每小时11英里。")]
    [InlineData("Meet at 3:30 tomorrow.", "明天下午三点半见。")]
    [InlineData("今日は１１時に公開します！", "今天11点公开！")]
    [InlineData("一時停止しています。後で再開します。", "暂时停止，稍后恢复。")]
    [InlineData("Read chapter 2, part 3.", "阅读第二章第三部分。")]
    [InlineData("漫画第3話を公開しました！", "漫画第三话发布了！")]
    [InlineData("Tianwen-3 targets 2028 and returns in 2031.", "天问三号目标2028年发射，2031年返回。")]
    [InlineData("Voyager-2 Mars mission.", "旅行者二号火星任务。")]
    [InlineData("La réunion est le 3 mars 2026.", "会议定于2026年3月3日。")]
    [InlineData("11/8(日)開催。受講料500円。", "11月8日（星期日）举行，听课费500日元。")]
    [InlineData("11/8(日)開催。受講料500円。", "11/8（周日）举行，听课费500日元。")]
    [InlineData("１１／８（日）開催。", "11月8日周日举行。")]
    [InlineData("11月8日(日)に開催します。", "于11月8日星期日举行。")]
    [InlineData("The Tianwen-3 Mars Sample Return mission targets 2028 and plans to return in 2031, delivering planetary and lunar samples together.", "天问三号火星采样返回任务目标为2028年，计划于2031年返回，在同一年带回行星和卫星样本。")]
    public void EquivalentSocialPostClocksOrdinalsAndJapaneseWordsDoNotRevokeTranslations(string source,string translation)
        => Assert.Empty(Check(source,translation));
    [Theory]
    [InlineData("More news at 11!", "10点继续播报新闻！")]
    [InlineData("More news at 11 p.m.!", "上午11点继续播报新闻！")]
    [InlineData("一時停止しています。", "1点停止。")]
    [InlineData("Read chapter 2.", "阅读第三章。")]
    [InlineData("Tianwen-3 returns in 2031.", "天问四号在2031年返回。")]
    [InlineData("Voyager-2 Mars mission.", "旅行者三号火星任务。")]
    [InlineData("11/8(日)開催。受講料500円。", "11月9日（星期日）举行，听课费500日元。")]
    [InlineData("11/8(日)開催。", "11月8日（星期一）举行。")]
    [InlineData("11/8(日)開催。", "11/8（周一）举行。")]
    public void SocialPostFactChangesStillFail(string source,string translation) => Assert.NotEmpty(Check(source,translation));
    [Fact]
    public void AbbreviatedCardDateDoesNotRevokeAnUnrelatedLandingPageDescription()
    {
        BrowserPageTextBlock[] source = [new("description", "Showcase a bold landing page with stylized title and button", "description"), new("date", "Sep 1", "date")];
        var values = new Dictionary<string, string> { ["description"] = "展示一个带有风格化标题和按钮的醒目落地页", ["date"] = "9月1日" };
        Assert.Empty(PageTextSemanticGuard.FindUnsafeIds(source, values));
        values["date"] = "9月2日";
        Assert.Equal(new[] { "date" }, PageTextSemanticGuard.FindUnsafeIds(source, values));
    }

    [Theory]
    [InlineData("Jul 13", "7月13日")]
    [InlineData("Sep. 1, 2026", "2026年9月1日")]
    [InlineData("1 Sept 2026", "2026年9月1日")]
    [InlineData("Apr 10, 2025", "2025年4月10日")]
    public void AcceptsEquivalentAbbreviatedMonthNames(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("before noon on Thursday", "请在周四中午前")]
    [InlineData("木曜日の正午までに", "请在星期四正午之前")]
    [InlineData("avant jeudi à midi", "周四中午之前")]
    [InlineData("We ordered 12 sensors; 9 arrived and 3 remain.", "订购了十二个传感器，九个已到，还有三个。")]
    [InlineData("Take 2 bottles of 250 millilitres.", "带两瓶容量二百五十毫升的瓶子。")]
    [InlineData("Start Friday at 14:30.", "周五下午两点半开始。")]
    [InlineData("Le laboratoire ferme à 16 h.", "实验室十六点关门。")]
    [InlineData("Come at 4 p.m.", "下午四点过来。")]
    [InlineData("Keep it for 30 days.", "保留三十天。")]
    [InlineData("12", "十二")]
    [InlineData("August 1, 2026", "2026年8月1日")]
    [InlineData("12 mars 2026", "2026年三月十二日")]
    [InlineData("2026-08-01", "2026年8月1日")]
    [InlineData("Children younger than 12 may join only when an adult stays with them.", "12岁以下儿童只有在成人陪同的情况下才能参加。")]
    [InlineData("Aoiさん", "Aoi女士")]
    public void AcceptsFactEquivalentArabicChineseAndCalendarFormatsWithoutGuessingNames(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("木曜日の正午までに", "周二正午前")]
    [InlineData("before noon on Thursday", "，")]
    [InlineData("contact Mira Chen", "请在周四中午前联系米拉")]
    [InlineData("14:30 Friday", "周五14:50")]
    [InlineData("Keep it for 30 days.", "保留三天。")]
    [InlineData("2026-08-01", "2026年1月8日")]
    public void RejectsChangedMissingOrIntroducedNodeFacts(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Fact]
    public void NumbersMovedBetweenInlineNodesRejectBothOwnerAndReceiver()
    {
        BrowserPageTextBlock[] source = [new("a", "Bring bottles"), new("b", "with capacity 250 ml")];
        var translations = new Dictionary<string, string> { ["a"] = "带250毫升的瓶子", ["b"] = "容量适宜" };
        Assert.Equal(new[] { "a", "b" }, PageTextSemanticGuard.FindUnsafeIds(source, translations));
    }

    [Fact]
    public void WeekdaysMustStayOnTheirOwnNodeEvenWhenWholeGroupStillContainsCorrectDay()
    {
        BrowserPageTextBlock[] source = [new("a", "contact Mira"), new("b", "before noon on Thursday")];
        var translations = new Dictionary<string, string> { ["a"] = "周四正午前联系米拉", ["b"] = "，" };
        Assert.Equal(new[] { "a", "b" }, PageTextSemanticGuard.FindUnsafeIds(source, translations));
    }

    [Theory]
    [InlineData("More than 90 billion euros ($100 billion).", "超过900亿欧元（约1000亿美元）。")]
    [InlineData("USD 1.25 million", "125万美元")]
    [InlineData("2 trillion US dollars", "2万亿美元")]
    [InlineData("EUR 30 trillion", "三十万亿欧元")]
    [InlineData("€1,250.50", "1250.50欧元")]
    [InlineData("1,234,567.89 dollars", "1234567.89美元")]
    [InlineData("1.234.567,89 euros", "1234567.89欧元")]
    [InlineData("1 250 euros", "一千二百五十欧元")]
    [InlineData("$1,000", "1000美元")]
    [InlineData("1.5 billion euros", "十五亿欧元")]
    [InlineData("1.25 trillion euros", "一万二千五百亿欧元")]
    [InlineData("The cost is 1.25 million euros.", "费用为一点二五百万欧元。")]
    [InlineData("12 million people", "1200万人")]
    [InlineData("9 billion", "九十亿")]
    [InlineData("12.5 percent", "百分之十二点五")]
    [InlineData("Debt is 120% of GDP.", "债务为GDP的百分之一百二十。")]
    [InlineData("The rate rose by 0.5 percentage points.", "利率上升了零点五个百分点。")]
    [InlineData("There are 1,000 sensors.", "有一千个传感器。")]
    [InlineData("The ratio is 3:2.", "比例为三比二。")]
    [InlineData("Item 1: contact the laboratory.", "第1项：联系实验室。")]
    [InlineData("90 billion euros($100billion)", "900亿欧元(1000亿美元)")]
    [InlineData("1.5 million dollars", "1.5百万美元")]
    [InlineData("90 billion euros", "9百亿欧元")]
    [InlineData("12.5 percentage points", "12.5個百分點")]
    [InlineData("3.5 metres", "三点五米")]
    [InlineData("1.5 hours", "一点五小时")]
    [InlineData("12.5", "十二点五")]
    [InlineData("-12.5%", "百分之负十二点五")]
    [InlineData("−12.5%", "负十二点五%")]
    [InlineData("-$100", "负一百美元")]
    [InlineData("$-100", "负一百美元")]
    [InlineData("-1.5 million dollars", "负一点五百万美元")]
    [InlineData("If it rains, stay home.", "万一下雨，就待在家里。")]
    [InlineData("万一下雨，就待在家里。", "If it rains, stay home.")]
    [InlineData("千万不要丢失钥匙。", "Do not lose your keys.")]
    public void AcceptsEquivalentMoneyScalesSeparatorsPercentagesAndNonClockColons(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("90 billion euros", "90亿欧元")]
    [InlineData("$100 billion", "100亿美元")]
    [InlineData("USD 1.25 million", "125万欧元")]
    [InlineData("€1,250.50", "1250.05欧元")]
    [InlineData("$1,000", "1美元")]
    [InlineData("2 trillion dollars", "2亿美元")]
    [InlineData("30 trillion euros", "三十亿欧元")]
    [InlineData("12.5%", "百分之一点二五")]
    [InlineData("12.5%", "12.5")]
    [InlineData("0.5 percentage points", "0.5%")]
    [InlineData("1,000 sensors", "一个传感器")]
    [InlineData("3:2", "三比一")]
    [InlineData("-12.5%", "12.5%")]
    [InlineData("-$100", "$100")]
    [InlineData("-1.5 million dollars", "1.5百万美元")]
    [InlineData("90 billion euros", "9十亿欧元")]
    [InlineData("$100", "100美元，约90欧元")]
    [InlineData("12.5%", "12.5%，另加20%")]
    public void RejectsMoneyCurrencyScalePrecisionAndPercentageChanges(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Fact]
    public void NewsParagraphSplitAroundAnInlineLinkKeepsEquivalentMoneyFacts()
    {
        BrowserPageTextBlock[] source =
        [
            new("before-link", "In France, interest payments are one of the biggest expenses in the government’s budget, and could eat up more than 90 billion euros ($100 billion) next year, more than planned spending on defense and education, according to the French finance ministry. Emmanuel Moulin, the governor of the French central bank, recently "),
            new("link", "told The Financial Times"),
            new("after-link", " that the country was at risk of being ‘strangled by interest rates’ if it doesn’t improve its public finances.")
        ];
        var translations = new Dictionary<string, string>
        {
            ["before-link"] = "据法国财政部称，利息支出是法国政府预算中最大的开支之一，明年可能超过900亿欧元（约1000亿美元），超过国防和教育的计划支出。法国央行行长埃马纽埃尔·穆兰最近",
            ["link"] = "对《金融时报》表示",
            ["after-link"] = "，如果不改善公共财政，该国就有被利率勒住脖子的风险。"
        };
        Assert.Empty(PageTextSemanticGuard.FindUnsafeIds(source, translations));

        translations["before-link"] = translations["before-link"].Replace("900亿", "90亿", StringComparison.Ordinal);
        Assert.Equal(new[] { "before-link" }, PageTextSemanticGuard.FindUnsafeIds(source, translations));
    }

    [Fact]
    public void EquivalentScaledMoneyMovedBetweenInlineNodesStillRejectsBoth()
    {
        BrowserPageTextBlock[] source = [new("a", "The budget is"), new("b", "90 billion euros")];
        var translations = new Dictionary<string, string> { ["a"] = "预算为900亿欧元", ["b"] = "，" };
        Assert.Equal(new[] { "a", "b" }, PageTextSemanticGuard.FindUnsafeIds(source, translations));
    }

    [Theory]
    [InlineData("Models under 1B parameters.", "参数量低于10亿的模型。")]
    [InlineData("Models under 1B parameters.", "参数量低于十亿的模型。")]
    [InlineData("The model has 740M total parameters, combining a 270M parameter text model with modular vision (170M) and audio (300M) encoders.", "该模型共7.4亿参数，由2.7亿参数的文本模型、1.7亿参数的视觉编码器和3亿参数的音频编码器组成。")]
    [InlineData("The model has 740M parameters.", "模型拥有七亿四千万个参数。")]
    [InlineData("The model has 740M parameters.", "模型拥有740M参数。")]
    [InlineData("The model has 740M parameters.", "模型参数为七点四亿。")]
    [InlineData("A 1.5B parameter model.", "模型参数为十五亿。")]
    [InlineData("270M parameters (130M transformer + 140M embedder)", "2.7亿参数（转换器1.3亿，嵌入器1.4亿）")]
    [InlineData("An 8K token context window.", "八千词元的上下文窗口。")]
    [InlineData("An 8k token context window.", "8000个token的上下文窗口。")]
    [InlineData("A 1.5T parameter model.", "1.5万亿参数的模型。")]
    [InlineData("A 768-dimensional vector space.", "七百六十八维向量空间。")]
    [InlineData("A 768-dimensional vector space.", "768维向量空间。")]
    [InlineData("Unifies 4 modalities.", "统一四种模态。")]
    [InlineData("It understands 100+ languages.", "它理解一百多种语言。")]
    [InlineData("Enabling a 6x reduction.", "实现六倍缩减。")]
    [InlineData("The cable is 3m long.", "线缆长三米。")]
    [InlineData("The file is 12MB; the buffer is 8KB.", "文件为12MB，缓冲区为8KB。")]
    [InlineData("The packet is 32B long.", "数据包长32字节。")]
    [InlineData("The packet is 32B long; the model has 1B parameters.", "数据包长32字节；模型有10亿参数。")]
    [InlineData("A 1B parameter model is saved in this file.", "这个文件保存了一个10亿参数的模型。")]
    [InlineData("Revision 1B2 has 740M parameters.", "1B2版本拥有7.4亿参数。")]
    [InlineData("The grant is $1M.", "资助金额为100万美元。")]
    public void AcceptsEquivalentModelCardMagnitudeAndDimensionNotation(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("Models under 1B parameters.", "参数量低于1亿的模型。")]
    [InlineData("Models under 1B parameters.", "参数量低于1个的模型。")]
    [InlineData("The model has 740M parameters.", "该模型共740个参数。")]
    [InlineData("The model has 740M parameters.", "该模型共7.5亿参数。")]
    [InlineData("The model has 740M parameters.", "该模型共七百四十万个参数。")]
    [InlineData("The model has 740M parameters.", "模型参数为七点五亿。")]
    [InlineData("A 1.5B parameter model.", "模型参数为一点五亿。")]
    [InlineData("An 8K token context window.", "八百词元的上下文窗口。")]
    [InlineData("A 768-dimensional vector space.", "七百六十七维向量空间。")]
    [InlineData("The cable is 3m long.", "线缆长300万米。")]
    [InlineData("The packet is 32B long.", "数据包长64字节。")]
    [InlineData("The file is 12MB.", "文件为1200万MB。")]
    [InlineData("The grant is $1M.", "资助金额为100万欧元。")]
    public void StillRejectsChangedMagnitudeDimensionAndCurrency(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));

    [Fact]
    public void EquivalentParameterCountsCannotMoveToAnotherInlineNode()
    {
        BrowserPageTextBlock[] source = [new("a", "The text model has 270M parameters"), new("b", " and the vision encoder has 170M parameters.")];
        var translations = new Dictionary<string, string> { ["a"] = "文本模型具有1.7亿参数", ["b"] = "视觉编码器具有2.7亿参数。" };
        Assert.Equal(new[] { "a", "b" }, PageTextSemanticGuard.FindUnsafeIds(source, translations));
    }

    private static IReadOnlyList<string> Check(string source, string translation)
        => PageTextSemanticGuard.FindUnsafeIds([new("node", source)], new Dictionary<string, string> { ["node"] = translation });

    [Theory]
    [InlineData("Is a dedicated IP recommended for online banking and 2FA?", "专用IP适合用于网上银行和双重身份验证吗？")]
    [InlineData("Enable 2FA.", "启用双因素认证。")]
    [InlineData("Enable 2FA.", "启用双因子验证。")]
    [InlineData("Enable 2FA.", "启用两步验证。")]
    [InlineData("Enable 2FA.", "启用二次验证。")]
    [InlineData("Enable 2FA.", "Enable two-factor authentication.")]
    [InlineData("Two-factor authentication protects accounts.", "双重认证保护账户。")]
    [InlineData("2FA is required for 2 accounts.", "两个账户都需要双重身份验证。")]
    [InlineData("Please verify the result twice.", "请对结果进行双重验证。")]
    public void TwoFactorAuthenticationIsAConceptRatherThanAnUntranslatedBareDigit(string source, string translation)
        => Assert.Empty(Check(source, translation));

    [Theory]
    [InlineData("Enable 2FA.", "启用3FA。")]
    [InlineData("Enable 2FA.", "启用普通认证。")]
    [InlineData("2FA is required for 2 accounts.", "一个账户需要双重身份验证。")]
    public void AuthenticationAndOtherQuantitiesStillCannotBeChangedOrDropped(string source, string translation)
        => Assert.Equal(new[] { "node" }, Check(source, translation));
}
