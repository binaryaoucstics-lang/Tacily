using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ImageTranslationDecisionTests
{
    [Theory]
    [InlineData("Do not open the door!", "latin")]
    [InlineData("もう遊んでいいよね？", "ja")]
    [InlineData("まだいける！！", "ja")]
    [InlineData("안녕하세요 세상", "ko")]
    [InlineData("Добро пожаловать", "cyrillic")]
    [InlineData("مرحبا بالعالم", "ar-script")]
    [InlineData("Καλημέρα κόσμε", "el-script")]
    [InlineData("Bienvenue à tous", "latin")]
    public void ReadableForeignScriptStartsTranslation(string text, string language)
    {
        var result = Assess(text);
        Assert.True(result.NeedsTranslation);
        Assert.Equal("foreign-readable-text", result.ReasonCode);
        Assert.Equal(language, result.DetectedLanguage);
        Assert.Single(result.TextRegions);
        Assert.Equal(text, Assert.Single(result.RecognizedText).Text);
    }

    [Theory]
    [InlineData("我们出发吧！", "target-or-han-ambiguous")]
    [InlineData("天下無敵", "target-or-han-ambiguous")]
    [InlineData("1", "unreadable-or-noise")]
    [InlineData("100 USD", "unreadable-or-noise")]
    [InlineData("ACME", "insufficient-evidence")]
    [InlineData("π = 3.14159", "unreadable-or-noise")]
    [InlineData("12345 ## ...", "unreadable-or-noise")]
    [InlineData("♪ 😀 ◇ ★", "unreadable-or-noise")]
    public void TargetTextAndNoiseDoNotStartPaidTranslation(string text, string code)
    {
        var result = Assess(text);
        Assert.False(result.NeedsTranslation);
        Assert.Equal(code, result.ReasonCode);
    }

    [Theory]
    [InlineData(.3)]
    [InlineData(.71)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WeakOrInvalidOcrCannotStartTranslation(double confidence)
    {
        var result = ImageTranslationDecision.Assess([Block("HELP ME ESCAPE", confidence)], "简体中文");
        Assert.False(result.NeedsTranslation);
        Assert.True(double.IsFinite(result.Confidence));
    }

    [Fact]
    public void ShortConfidentLinesCanProvideCombinedReadableEvidence()
    {
        var result = ImageTranslationDecision.Assess([Block("Help me"), Block("escape now")], "zh-Hans");
        Assert.True(result.NeedsTranslation);
    }

    [Fact]
    public void ChineseWithOneSmallLatinLogoDoesNotTrigger()
    {
        var result = ImageTranslationDecision.Assess([Block("欢迎来到我们的商店"), Block("ACME")], "Chinese");
        Assert.False(result.NeedsTranslation);
    }

    [Theory]
    [InlineData("Do not open the door", "en-US", "same-script-ambiguous")]
    [InlineData("Bienvenue à tous", "English", "same-script-ambiguous")]
    [InlineData("もう遊んでいいよね", "ja-JP", "target-script")]
    [InlineData("안녕하세요", "ko-KR", "target-script")]
    [InlineData("Welcome aboard", "made-up", "unsupported-target")]
    public void SameScriptAndUnknownTargetsAreConservative(string text, string target, string code)
    {
        var result = ImageTranslationDecision.Assess([Block(text)], target);
        Assert.False(result.NeedsTranslation);
        Assert.Equal(code, result.ReasonCode);
    }

    [Fact]
    public void ChineseRequiresTranslationForEnglishTarget()
    {
        Assert.True(ImageTranslationDecision.Assess([Block("欢迎来到这里")], "en").NeedsTranslation);
    }

    [Fact]
    public void MissingTextRemainsLocalNegative()
    {
        var result = ImageTranslationDecision.Assess([], "简体中文");
        Assert.False(result.NeedsTranslation);
        Assert.Equal("no-text", result.ReasonCode);
    }

    [Theory]
    [InlineData("Sarah Meyssonnier/Reuters")]
    [InlineData("Photo: Sarah Meyssonnier/Reuters")]
    [InlineData("© 2026 Reuters")]
    [InlineData("Getty Images")]
    [InlineData("https://example.org")]
    [InlineData("x² + y² = 25")]
    public void CreditAndTechnicalMarksNeverStartCloudTranslation(string text)
    {
        var assessment = Assess(text);
        Assert.False(assessment.NeedsTranslation);
        Assert.Empty(assessment.RecognizedText);
    }

    [Fact]
    public void UserNewsCaptionWithEnglishPhotographerStaysLocal()
    {
        var assessment = ImageTranslationDecision.Assess([Block("周三，法国警察和抗议者在巴黎政治学院附近发生冲突。"),
            Block("Sarah Meyssonnier/路透社")], "zh-Hans");
        Assert.False(assessment.NeedsTranslation);
        Assert.DoesNotContain(assessment.RecognizedText, block => block.Text.Contains("Sarah", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Hi!")]
    [InlineData("No!")]
    [InlineData("Wait!")]
    [InlineData("え？")]
    [InlineData("はい")]
    public void ConfidentRealShortDialogueRemainsEligible(string text)
    {
        Assert.True(Assess(text).NeedsTranslation);
    }

    [Fact]
    public void ForeignCaptionBesideCreditStillTranslates()
    {
        var result = ImageTranslationDecision.Assess([Block("Police confronted protesters outside the school."), Block("Sarah Meyssonnier/Reuters")], "zh-Hans");
        Assert.True(result.NeedsTranslation);
        Assert.Equal("Police confronted protesters outside the school.", Assert.Single(result.RecognizedText).Text);
    }

    private static ImageTranslationAssessment Assess(string text) => ImageTranslationDecision.Assess([Block(text)], "简体中文");
    private static OcrBlock Block(string text, double confidence = .97) => new("one", text, confidence, new PixelRect(5, 5, 200, 40), 0);
}
