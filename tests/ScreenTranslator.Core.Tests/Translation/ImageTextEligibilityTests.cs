using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ImageTextEligibilityTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("12345 ## ...")]
    [InlineData("１００ ＵＳＤ")]
    [InlineData("π = 3.14159")]
    [InlineData("x² + y² = 25")]
    [InlineData("E=mc²")]
    [InlineData("https://example.com/comic")]
    [InlineData("artist@example.org")]
    [InlineData("@artist_001")]
    [InlineData("© 2026 Reuters")]
    [InlineData("Photo: Sarah Meyssonnier/Reuters")]
    [InlineData("Sarah Meyssonnier/Reuters")]
    [InlineData("SarahMeyssonnier/路透社")]
    [InlineData("图片来源：路透社")]
    [InlineData("Getty Images")]
    [InlineData("## やたっ♪")]
    [InlineData("© 2026 Reuters\nPhoto: Sarah Meyssonnier/Reuters")]
    [InlineData("CC BY-NC-SA 4.0")]
    public void ExplicitNoiseAndCreditsAreNotTranslationContent(string text)
    {
        Assert.Empty(ImageTextEligibility.ReadableContent(text));
    }

    [Theory]
    [InlineData("Hi!")]
    [InlineData("Wait!")]
    [InlineData("HELP!")]
    [InlineData("No!")]
    [InlineData("え？")]
    [InlineData("はい")]
    [InlineData("やたっ♪")]
    [InlineData("Alice")]
    [InlineData("WARNING")]
    [InlineData("A1出口")]
    [InlineData("The answer is 1")]
    [InlineData("2 people escaped")]
    [InlineData("My name is Sarah Meyssonnier")]
    [InlineData("Please contact Reuters")]
    [InlineData("Read this equation x + y = 3")]
    [InlineData("## もう遊んでいいよね？")]
    public void OrdinaryShortSpeechNamesAndMeaningfulCaptionsRemain(string text)
    {
        Assert.Equal(text, ImageTextEligibility.ReadableContent(text));
    }

    [Theory]
    [InlineData("法国内政部欢迎学生。 Sarah Meyssonnier/Reuters", "法国内政部欢迎学生。")]
    [InlineData("Students gathered outside the school. Sarah Meyssonnier/Reuters", "Students gathered outside the school.")]
    [InlineData("已经是中文的图片说明 Sarah Meyssonnier/路透社", "已经是中文的图片说明")]
    [InlineData("Students gathered outside the school.\nPhoto: Sarah Meyssonnier/Reuters", "Students gathered outside the school.")]
    public void InlinePhotographerCreditDoesNotDeleteTheRealCaption(string text, string caption)
    {
        Assert.Equal(caption, ImageTextEligibility.ReadableContent(text));
    }

    [Theory]
    [InlineData("你好，我们已经到家了。", "zh-Hans", true)]
    [InlineData("Hello world", "zh-Hans", false)]
    [InlineData("魔法使いさん", "zh-Hans", false)]
    [InlineData("もう遊んでいいよね？", "ja-JP", true)]
    [InlineData("你好，欢迎回来。", "en-US", false)]
    [InlineData("Bienvenue à tous", "en-US", false)]
    [InlineData("Hi!", "en-US", true)]
    [InlineData("안녕하세요", "ko-KR", true)]
    public void TargetLanguageIsJudgedFromCompleteOriginalOnly(string original, string target, bool expected)
    {
        Assert.Equal(expected, ImageTextEligibility.IsAlreadyTargetLanguage(original, target));
    }

    [Fact]
    public void HanOnlyCaptionsInJapaneseContextAreAmbiguous()
    {
        Assert.False(ImageTextEligibility.IsAlreadyTargetLanguage("天下無敵", "zh-CN", preserveHanAmbiguity: true));
        Assert.True(ImageTextEligibility.IsAlreadyTargetLanguage("我们现在可以出发了", "zh-CN", preserveHanAmbiguity: true));
    }
}
