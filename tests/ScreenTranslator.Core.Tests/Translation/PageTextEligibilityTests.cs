using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextEligibilityTests
{
    // Synthetic per-node regressions, not a website corpus or page verdict.
    [Theory]
    [InlineData("研究人员介绍，IPC用于描述处理器每个周期执行指令的能力，改进架构有助于提高计算效率。")]
    [InlineData("报道介绍了PAC-3系统的技术升级，相关研究将继续关注设备性能、维护成本以及后续测试结果。")]
    [InlineData("技术团队公布了CR450动车组的试验进展，车辆设计围绕运行效率和乘客体验持续改进。")]
    [InlineData("国产AB-123设备完成了连续运行测试，研究团队正在核对记录并准备下一阶段的技术评估。")]
    [InlineData("技术团队公布了ＣＲ４５０动车组的试验进展，车辆设计围绕运行效率和乘客体验持续改进。")]
    public void ChineseTechnicalNewsRequiresLexicallyRecognizedRuns(string text)
    {
        foreach (var source in new[] { text, string.Concat(Enumerable.Repeat(text, 8)) })
        {
            foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hant", "Chinese" })
            {
                Assert.False(PageTextEligibility.NeedsTranslation(source, target));
                Assert.False(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: true));
                Assert.True(PageTextEligibility.NeedsTranslation(source, target, foreignHanContext: true));
            }
            Assert.True(PageTextEligibility.NeedsTranslation(source, "English"));
        }
    }

    [Theory]
    [InlineData("研究团队持续改进设备，HELP，这是外国访客发出的求助信息，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，STOP，这是外国访客发出的指令，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，NO WAR，这是外国访客发出的呼吁，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，XYZ是原文中的未知外语内容，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，Read the IPC guide，这是外国访客的建议，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，Alice介绍了CR450的新方案，请继续记录原文。")]
    [InlineData("研究团队持续改进设备，PAC-3の説明需要结合日语原文进行理解。")]
    [InlineData("研究团队持续改进设备，CR450ﾆｭｰｽ需要结合日语原文进行理解。")]
    [InlineData("研究团队持续改进设备，AB12CDE是原文中的未知内容，请继续记录原文。")]
    public void LongChineseSurroundingsDoNotSuppressForeignWordsOrDialogue(string text)
    {
        foreach (var source in new[] { text, string.Concat(Enumerable.Repeat("研究团队继续记录设备运行情况。", 50)) + text })
        {
            Assert.True(PageTextEligibility.NeedsTranslation(source, "简体中文"));
            Assert.True(PageTextEligibility.NeedsTranslation(source, "简体中文", explicitSelection: true));
        }
    }

    [Theory]
    [InlineData("CCTV13")]
    [InlineData("CCTV-13")]
    [InlineData("CR450")]
    [InlineData("PAC-3")]
    [InlineData("AB_123")]
    [InlineData("R7")]
    [InlineData("ＡＢ－１２３")]
    public void BareStructuredIdentifiersAreAutomaticMetadataOnly(string text)
    {
        foreach (var target in new[] { "简体中文", "English", "ja-JP" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(text, target));
            Assert.True(PageTextEligibility.NeedsTranslation(text, target, explicitSelection: true));
        }
    }

    [Theory]
    [InlineData("HELP")]
    [InlineData("STOP")]
    [InlineData("NO WAR")]
    [InlineData("IPC")]
    [InlineData("PAC")]
    [InlineData("CR")]
    [InlineData("Alice")]
    [InlineData("London")]
    [InlineData("Apple")]
    [InlineData("Quantum")]
    [InlineData("AB12CDE")]
    [InlineData("MODEL123")]
    [InlineData("CR 450")]
    [InlineData("Help 2 people")]
    [InlineData("PAC-3 is a technical identifier.")]
    [InlineData("CR450：Read the report.")]
    public void IdentifierStructureDoesNotRejectNamesNounsOrSentences(string text)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文"));

    [Theory]
    [InlineData("1920x1080")]
    [InlineData("3840 × 2160 px")]
    [InlineData("１９２０ｘ１０８０")]
    [InlineData("1080p")]
    [InlineData("4K")]
    [InlineData("60 fps")]
    [InlineData("300 ppi")]
    [InlineData("100 Mbps")]
    [InlineData("FPS")]
    [InlineData("Gbps")]
    public void CompleteResolutionAndUnitLabelsRemainNoiseEvenWhenSelected(string text)
    {
        Assert.False(PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.False(PageTextEligibility.NeedsTranslation(text, "English", explicitSelection: true));
    }

    [Theory]
    [InlineData("京ICP备12345678号-1")]
    [InlineData("沪ICP证123456号")]
    [InlineData("粵ICP備12345678號-2")]
    [InlineData("京公网安备11010102012345号")]
    [InlineData("Registration No. 123456")]
    [InlineData("Company registration number: AB-123456")]
    [InlineData("5 min read")]
    [InlineData("5-minute read")]
    [InlineData("Reading time: 5 minutes")]
    [InlineData("Estimated reading time: 1 hour.")]
    [InlineData("Read time: 12 mins")]
    [InlineData("阅读时间：5分钟")]
    [InlineData("預計閱讀時間：5分鐘")]
    public void CompleteRegistrationAndReadingTimeLabelsAreAutomaticMetadataOnly(string text)
    {
        Assert.False(PageTextEligibility.NeedsTranslation(text, "English"));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "English", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true, explicitSelection: true));
    }

    [Theory]
    [InlineData("The display supports 1920x1080 at 60 fps.")]
    [InlineData("Choose 4K for the next video.")]
    [InlineData("The company registration number is AB-123456.")]
    [InlineData("京ICP备12345678号-1 is the registration displayed on this page.")]
    [InlineData("Reading time: 5 minutes to understand the experiment.")]
    [InlineData("5 min read before you answer.")]
    [InlineData("I read for 5 minutes.")]
    [InlineData("Read time travel stories")]
    [InlineData("IPC registration information")]
    [InlineData("Jul 13")]
    [InlineData("13 Jul")]
    [InlineData("The report was published on October 9, 2026.")]
    public void MetadataSyntaxNeverRemovesAdjacentProseOrLocalizableMonthLabels(string text)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("1")]
    [InlineData("2026")]
    [InlineData("120%")]
    [InlineData("１２５０％")]
    [InlineData("100 USD")]
    [InlineData("１２ ＫＧ")]
    [InlineData("-12.5 ml")]
    [InlineData("USD")]
    [InlineData("MB")]
    [InlineData("°C")]
    [InlineData("kg")]
    [InlineData("π = 3.14159")]
    [InlineData("x² + y² = 25")]
    [InlineData("E=mc²")]
    [InlineData("https://example.com/read?chapter=1")]
    [InlineData("ftp://example.com/book.txt")]
    [InlineData("www.example.org")]
    [InlineData("example.com/reader")]
    [InlineData("artist@example.org")]
    [InlineData("mailto:artist@example.org")]
    [InlineData(@"C:\models\page.xml")]
    [InlineData(@"\\server\books\chapter.txt")]
    [InlineData(@".\models\page.xml")]
    [InlineData("/tmp/page.json")]
    [InlineData("2026-10-09 13:45 UTC")]
    [InlineData("14:30 GMT+08:00")]
    [InlineData("August 1, 2026")]
    [InlineData("1st August 2026")]
    [InlineData("ISBN: 978-1-4028-9462-6")]
    [InlineData("DOI: 10.1234/example")]
    [InlineData("© 2026 Example News")]
    [InlineData("All rights reserved.")]
    [InlineData("Photo: Mira Chen")]
    [InlineData("Image credit: Sarah Meyssonnier")]
    [InlineData("Photo by Eric S. Lesser/AP")]
    [InlineData("Credit: Reuters")]
    [InlineData("Sarah Meyssonnier/Reuters")]
    [InlineData("Sarah Meyssonnier/路透社")]
    [InlineData("Sarah Meyssonnier/美联社")]
    [InlineData("Sarah Meyssonnier/美聯社")]
    [InlineData("Sarah Meyssonnier/法新社")]
    [InlineData("Sarah Meyssonnier/盖蒂")]
    [InlineData("Sarah Meyssonnier/盖蒂图片")]
    [InlineData("Sarah Meyssonnier/蓋蒂圖片")]
    [InlineData("Photo: 路透社")]
    public void WholeValuesAreFilteredWithoutDroppingProseUnits(string? text)
        => Assert.False(PageTextEligibility.NeedsTranslation(text, "简体中文"));

    [Theory]
    [InlineData("sign in")]
    [InlineData("Read more.")]
    [InlineData("Privacy policy")]
    [InlineData("Mira Chen")]
    [InlineData("The Financial Times")]
    [InlineData("told The Financial Times")]
    [InlineData("Hello")]
    [InlineData("Stop!")]
    [InlineData("Help!")]
    [InlineData("A")]
    [InlineData("I")]
    [InlineData("m")]
    [InlineData("Alice")]
    [InlineData("CIA")]
    [InlineData("PDF")]
    [InlineData("Reuters")]
    [InlineData("WARNING")]
    [InlineData("Read")]
    [InlineData("March")]
    [InlineData("Friday")]
    [InlineData("Friday at 14:30")]
    [InlineData("The price is 100 USD.")]
    [InlineData("The price is $100 billion.")]
    [InlineData("$100 billion")]
    [InlineData("Read this equation x + y = 3")]
    [InlineData("Debt = income")]
    [InlineData("Hello=world")]
    [InlineData("Email artist@example.org to reserve a seat.")]
    [InlineData("See https://example.com/read for details.")]
    [InlineData("I cannot find C:\\models\\page.xml")]
    [InlineData("Please sign in before you read more.")]
    [InlineData("The main menu was a disappointment.")]
    [InlineData("Photo: a student waits outside the school.")]
    [InlineData("Photo: Mira Chen explains the experiment.")]
    [InlineData("A caption explaining what the photo shows. Sarah Meyssonnier/Reuters")]
    [InlineData("Students have returned to the school. Sarah Meyssonnier/路透社")]
    [InlineData("法国内政部欢迎学生。 Sarah Meyssonnier/路透社")]
    [InlineData("© 2026 Reuters\nStudents have returned to the school.")]
    [InlineData("Copyright is complicated, and this article explains it.")]
    [InlineData("Photo: Alice")]
    [InlineData("もう遊んでいいよね？")]
    [InlineData("え？")]
    [InlineData("Aoiさん")]
    [InlineData("## もう遊んでいいよね？")]
    [InlineData("やたっ♪")]
    public void BodyFragmentsNamesAndRealSentencesRemain(string text)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, "zh-Hans"));

    [Theory]
    [InlineData("法国内政部欢迎学生。")]
    [InlineData("你好")]
    [InlineData("天下無敵")]
    [InlineData("金融時報")]
    [InlineData("中文 120%")]
    [InlineData("𠀀")]
    public void HanOnlyValuesAreSkippedForChineseUnlessJapaneseContextExists(string text)
    {
        Assert.False(PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-Hant")]
    [InlineData("Chinese")]
    [InlineData("繁体中文")]
    public void ChineseTargetAliasesUseSameGate(string target)
        => Assert.False(PageTextEligibility.NeedsTranslation("已经是中文。", target));

    // Public development examples only; kept identical to the extension fixtures.
    [Theory]
    [InlineData("WTT中国大满贯赛：王曼昱女单卫冕")]
    [InlineData("“张雪机车”德比斯获WSBK葡萄牙站次回合第三")]
    [InlineData("新华社权威AI时政资讯智能体“新华语典”")]
    [InlineData("APEC“中国年”")]
    [InlineData("AI时代需要更多数控铣技术人才")]
    [InlineData("智界R7焕新上市 售价23.98万-31.98万元")]
    [InlineData("新华网B站号")]
    [InlineData("A股三季报披露将启幕 业绩预告勾勒盈利图景")]
    [InlineData("ＡＰＥＣ“中国年”")]
    [InlineData("𠀀𠀋𠮷AI")]
    [InlineData("中文技术支持说明方案AI CPU")]
    public void ReadableChineseWithShortAbbreviationsIsSkippedOnlyForChinese(string text)
    {
        foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hans", "zh-Hant", "Chinese", "繁体中文" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(text, target));
            Assert.False(PageTextEligibility.NeedsTranslation(text, target, explicitSelection: true));
            Assert.True(PageTextEligibility.NeedsTranslation(text, target, foreignHanContext: true));
        }
        foreach (var target in new[] { "English", "en-US", "ja-JP", "ko-KR", "fr-FR" })
            Assert.True(PageTextEligibility.NeedsTranslation(text, target));
    }

    // Synthetic computing/science boundaries; mirrored in the extension tests.
    [Theory]
    [InlineData("API")]
    [InlineData("GPU")]
    [InlineData("USB")]
    [InlineData("SSD")]
    [InlineData("RAM")]
    [InlineData("DNS")]
    [InlineData("SQL")]
    [InlineData("HTML")]
    [InlineData("CSS")]
    [InlineData("JSON")]
    [InlineData("XML")]
    [InlineData("HTTP")]
    [InlineData("TCP")]
    [InlineData("UDP")]
    [InlineData("PDF")]
    [InlineData("DNA")]
    [InlineData("RNA")]
    [InlineData("MRI")]
    public void CommonTechnicalAcronymsRequireReadableChineseContext(string acronym)
    {
        foreach (var text in new[] { $"中文说明{acronym}", $"中文技术说明方案 {acronym}" })
        {
            foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hans", "zh-Hant", "Chinese", "繁体中文" })
            {
                Assert.False(PageTextEligibility.NeedsTranslation(text, target));
                Assert.False(PageTextEligibility.NeedsTranslation(text, target, explicitSelection: true));
                Assert.True(PageTextEligibility.NeedsTranslation(text, target, foreignHanContext: true));
            }
            foreach (var target in new[] { "English", "en-US", "ja-JP", "ko-KR", "fr-FR" })
                Assert.True(PageTextEligibility.NeedsTranslation(text, target));
        }
        foreach (var text in new[] { acronym, $"{acronym}李明", $"Read the {acronym} guide for 中文说明.",
            $"中文接口技术支持说明方案 USE {acronym}" })
        {
            Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文"));
            Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", explicitSelection: true));
        }
    }

    [Theory]
    [InlineData("API使用指南", false)]
    [InlineData("API接口", true)]
    [InlineData("中文说明方案 API", false)]
    [InlineData("中文说明方 API", true)]
    [InlineData("中文说明方案 HTTP", true)]
    [InlineData("中文技术说明方案 HTTP", false)]
    [InlineData("中文技术说明方 HTTP", true)]
    [InlineData("中文接口技术支持说明方案API GPU", false)]
    [InlineData("中文接口技术支持说明方API GPU", true)]
    [InlineData("中文接口技术支持说明方案DNA/RNA", false)]
    [InlineData("中文接口技术支持说明方DNA/RNA", true)]
    [InlineData("GPU NEWS 中文接口技术支持说明方案", true)]
    [InlineData("中文说明APIGPU", true)]
    [InlineData("中文说明Api", true)]
    [InlineData("中文说明ＡＰＩ", false)]
    [InlineData("中文接口技术支持说明方案API2 GPU", true)]
    [InlineData("中文技术说明方案APIの説明", true)]
    [InlineData("中文技术说明方案GPU한", true)]
    public void TechnicalAcronymsPreserveChineseRatiosAndForeignPhraseBoundaries(string text, bool needsTranslation)
    {
        Assert.Equal(needsTranslation, PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.Equal(needsTranslation, PageTextEligibility.NeedsTranslation(text, "简体中文", explicitSelection: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "English"));
    }

    [Theory]
    [InlineData("\u0085APEC中国年", false)]
    [InlineData("APEC中国年\u0085", false)]
    [InlineData("\ufeffAPEC中国年", false)]
    [InlineData("APEC中国年\ufeff", false)]
    [InlineData("\ufeff \u0085APEC中国年\u0085 \ufeff", false)]
    [InlineData("APEC\u0085中国年", true)]
    [InlineData("APEC\ufeff中国年", false)]
    [InlineData("AP\ufeffEC中国年", true)]
    public void PerNodeUnicodeTrimDistinguishesEdgeAndInternalWhitespace(string text, bool needsTranslation)
    {
        Assert.Equal(needsTranslation, PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "English"));
    }

    [Theory]
    [InlineData("\u0085")]
    [InlineData("\ufeff")]
    [InlineData("\ufeff \u0085 \ufeff")]
    [InlineData("\u0085100 USD\u0085")]
    [InlineData("\ufeff100 USD\ufeff")]
    [InlineData("\u0085https://example.com\u0085")]
    [InlineData("\ufeffhttps://example.com\ufeff")]
    public void UnicodeEdgeTrimPreservesNoiseAndMetadataBoundaries(string text)
    {
        Assert.False(PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.False(PageTextEligibility.NeedsTranslation(text, "English", explicitSelection: true));
    }

    [Theory]
    [InlineData("全世界的人民大声疾呼 NO WAR")]
    [InlineData("请大家帮助这个受困的人 HELP")]
    [InlineData("请大家帮助这个受困的人HELP")]
    [InlineData("请大家帮助这个受困的人 HELP NOW")]
    [InlineData("中文说明文章A B")]
    [InlineData("中文技术支持说明方案AI GO")]
    [InlineData("这是一个包含多个普通大写英文单词的很长中文说明 NO WAR")]
    public void OrdinaryUppercaseCommandsAndPhrasesRemainEligibleRegardlessOfHanShare(string text)
    {
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文"));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", explicitSelection: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", foreignHanContext: true));
        Assert.True(PageTextEligibility.NeedsTranslation(text, "English"));
    }

    [Theory]
    [InlineData("Wang defeated 王曼昱 in the WTT final.")]
    [InlineData("AI NEWS FROM 王曼昱")]
    [InlineData("STOP 王曼昱!")]
    [InlineData("GO 王曼昱")]
    [InlineData("AI 中国年")]
    [InlineData("AI\u0085中国年")]
    [InlineData("English 中文说明")]
    [InlineData("你好 Hello")]
    [InlineData("AI李明")]
    [InlineData("APEC中国")]
    [InlineData("中文说明ABCDE")]
    [InlineData("中文说明AB12CDE")]
    [InlineData("中文说明文A B")]
    [InlineData("中文技术支持说明方AI CPU")]
    [InlineData("123456789 AI李明 😀😀😀")]
    [InlineData("这是中文标题包含api")]
    [InlineData("这是中文标题包含éAI")]
    [InlineData("这是中文标题包含АI")]
    [InlineData("这是中文标题包含ΩAI")]
    [InlineData("中文说明々AI")]
    [InlineData("这是中文标题AI\u0307")]
    [InlineData("AI中国年のニュース")]
    [InlineData("AI中国年ﾆｭｰｽ")]
    [InlineData("AI中国年한")]
    public void ForeignProseScriptsAndInsufficientChineseEvidenceRemainEligible(string text)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文"));

    [Theory]
    [InlineData("Hello world", "en-US")]
    [InlineData("Bienvenue à tous", "en-US")]
    [InlineData("你好", "ja-JP")]
    [InlineData("こんにちは", "ja-JP")]
    [InlineData("안녕하세요", "ko-KR")]
    public void SharedScriptsDoNotProveSameLanguageForOtherTargets(string text, string target)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, target));

    [Theory]
    [InlineData("form")]
    [InlineData("editable")]
    [InlineData("code")]
    public void PrivateNonBodyKindsAreSkippedOnlyForAutomaticNodes(string kind)
    {
        Assert.False(PageTextEligibility.NeedsTranslation("Continue", "简体中文", kind: kind));
        Assert.True(PageTextEligibility.NeedsTranslation("Continue", "简体中文", kind: kind, explicitSelection: true));
        Assert.True(PageTextEligibility.NeedsTranslation("Continue", "简体中文", kind: "body"));
    }

    [Theory]
    [InlineData("Features", "navigation")]
    [InlineData("Pricing", "navigation")]
    [InlineData("Sign Out", "button")]
    [InlineData("Read more", "navigation")]
    [InlineData("Dashboard", "button")]
    [InlineData("How does the free trial work?", "button")]
    public void AdmittedPagesAlsoTranslatePublicControls(string text, string kind)
        => Assert.True(PageTextEligibility.NeedsTranslation(text, "简体中文", kind: kind));

    [Fact]
    public void ExplicitSelectionCanTranslateControlsButCannotUploadNativeTextOrNoise()
    {
        Assert.True(PageTextEligibility.NeedsTranslation("Read more.", "简体中文", explicitSelection: true));
        Assert.True(PageTextEligibility.NeedsTranslation("Photo: Mira Chen", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("已经是中文。", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("120%", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("https://example.com", "简体中文", explicitSelection: true));
    }

    [Fact]
    public void ChineseCaptionAndCreditInSeparateNodesBothRemainNative()
    {
        Assert.False(PageTextEligibility.NeedsTranslation("法国内政部欢迎学生。", "简体中文"));
        Assert.False(PageTextEligibility.NeedsTranslation("Sarah Meyssonnier/路透社", "简体中文"));
    }

    [Fact]
    public void ExplicitSelectionOverridesWholeCreditGateWithoutOverridingNoiseGates()
    {
        Assert.True(PageTextEligibility.NeedsTranslation("Sarah Meyssonnier/路透社", "简体中文", explicitSelection: true));
        Assert.True(PageTextEligibility.NeedsTranslation("Photo: 路透社", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("2026-10-09 13:45 UTC", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("100 USD", "简体中文", explicitSelection: true));
        Assert.False(PageTextEligibility.NeedsTranslation("artist@example.org", "简体中文", explicitSelection: true));
    }

    [Theory]
    [InlineData("もう遊んでいいよね？", true)]
    [InlineData("ﾔﾀｯ", true)]
    [InlineData("天下無敵", false)]
    [InlineData("Mira Chen", false)]
    [InlineData(null, false)]
    public void JapaneseContextHelperSupportsKanaWithoutGuessingHan(string? text, bool expected)
        => Assert.Equal(expected, PageTextEligibility.HasJapaneseScript(text));
}
