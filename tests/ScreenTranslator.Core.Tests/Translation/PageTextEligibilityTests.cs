using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextEligibilityTests
{
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
