using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Browser;

public sealed class PageTranslationDecisionTests
{
    private const string English = "The library provides a simple way to translate public articles while keeping the original page layout intact. Independent paragraphs can be processed together without changing links or controls.";
    private const string Chinese = "这个页面介绍如何配置浏览器插件，并说明如何在保持网页布局的同时阅读文章。用户可以打开设置，选择目标语言，然后查看已经翻译的文字。";

    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("ja", true)]
    [InlineData("ko", true)]
    [InlineData("en", false)]
    public void EnglishBodyIsRecognizedWithoutAClassifier(string target, bool expected) =>
        Assert.Equal(expected, PageTranslationDecision.Inspect([new(English)], target).RuleDecision.NeedsTranslation);

    [Theory]
    [InlineData("zh", false)]
    [InlineData("en", true)]
    [InlineData("ko", true)]
    public void ChineseProseMatchesItsTargetScript(string target, bool expected) =>
        Assert.Equal(expected, PageTranslationDecision.Inspect([new(Chinese)], target).RuleDecision.NeedsTranslation);

    [Fact]
    public void ForeignDominantPageWithASmallChinesePassageTranslatesAtTheFixedDefault()
    {
        const string readableChinese = "这里的中文说明可以直接阅读。";
        var result = PageTranslationDecision.Inspect([new(readableChinese), new(English)], "zh",
            chineseProseShareThresholdPercent: PageTranslationDecision.DefaultChineseProseShareThresholdPercent).RuleDecision;
        Assert.True(result.NeedsTranslation);
        Assert.False(result.IsMixedTargetLanguage);
        Assert.Contains(result.EligibleBlocks, block => block.Text == readableChinese);
        Assert.False(PageTextEligibility.NeedsTranslation(readableChinese, "zh"));
        Assert.True(PageTextEligibility.NeedsTranslation(English, "zh"));
    }

    [Fact]
    public void ChineseDominantPageWithASmallEnglishPassageSkipsAtTheFixedDefault()
    {
        var result = PageTranslationDecision.Inspect([new(Chinese), new("The page keeps the original links.")], "zh",
            chineseProseShareThresholdPercent: PageTranslationDecision.DefaultChineseProseShareThresholdPercent).RuleDecision;
        Assert.False(result.NeedsTranslation);
        Assert.True(result.IsMixedTargetLanguage);
        Assert.Equal("mixed-target-body", result.ReasonCode);
    }

    [Fact]
    public void SeparateChineseAndEnglishPassagesUseTheirLetterShare()
    {
        var result = PageTranslationDecision.Inspect([new(Chinese), new(English)], "zh").RuleDecision;
        Assert.True(result.NeedsTranslation);
        Assert.False(result.IsMixedTargetLanguage);
    }

    [Fact]
    public void ChineseNavigationAndHeadingDoNotOverrideEnglishBody()
    {
        var result = PageTranslationDecision.Inspect([new(Chinese, "title"), new(Chinese, "heading"),
            new(Chinese, "navigation"), new(Chinese, "code"), new(English)], "zh").RuleDecision;
        Assert.True(result.NeedsTranslation); Assert.False(result.IsMixedTargetLanguage);
    }

    [Fact]
    public void ForeignHeadingsDoNotDiluteChineseBodyShare()
    {
        var result = PageTranslationDecision.Inspect([new(English, "title"), new(English, "heading"), new(Chinese)], "zh").RuleDecision;
        Assert.False(result.NeedsTranslation);
        Assert.Equal("mixed-target-body", result.ReasonCode);
    }

    [Theory]
    [InlineData("中文说明")]
    [InlineData("初音未来")]
    [InlineData("上海北京")]
    public void AMinorHanNameInsideEnglishProseDoesNotVetoThePage(string name)
    {
        var stats = new PageTextStatistics(TotalCharacters: 6000, ProseBlocks: 24, HanProseCharacters: name.Length, LatinProseCharacters: 5000);
        Assert.True(PageTranslationDecision.Inspect([new(English + " " + name)], "zh", stats).RuleDecision.NeedsTranslation);
        Assert.True(PageTranslationDecision.Inspect([new(English)], "zh", stats).RuleDecision.NeedsTranslation);
    }

    [Fact]
    public void MinorityChineseProseInsideForeignParagraphDoesNotVetoTranslation()
    {
        var result = PageTranslationDecision.Inspect([new(English + " 这里的中文说明可以直接阅读。")], "zh").RuleDecision;
        Assert.True(result.NeedsTranslation);
        Assert.False(result.IsMixedTargetLanguage);
    }

    [Theory]
    [InlineData("这是可读中文内容ABCD", false)]
    [InlineData("这是可读中文内容ABCDEFGH", false)]
    [InlineData("这是可读中文内容ABCDEFGHIJKLMNOPQRSTUVWXYZ", true)]
    [InlineData("新機能を公開しました。確認をお願いします。", true)]
    public void ParagraphChineseShareUsesLettersAndDoesNotMisclassifyJapanese(string text, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(string.Concat(Enumerable.Repeat(text, 3)))], "zh").RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Fact]
    public void WholePageMinorityChineseProseDoesNotVetoAnEnglishSample()
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(TotalCharacters: 4000, HanProseCharacters: 100, LatinProseCharacters: 2000)).RuleDecision;
        Assert.True(result.NeedsTranslation); Assert.False(result.IsMixedTargetLanguage);
    }

    [Fact]
    public void WholePageMajorityChineseProseSkipsASparseEnglishSample()
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: 2000, LatinProseCharacters: 100)).RuleDecision;
        Assert.False(result.NeedsTranslation); Assert.True(result.IsMixedTargetLanguage);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void FixedDefaultIncludesTheExactShareBoundary(int nonChineseLetters, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: 100, NonChineseProseCharacters: nonChineseLetters),
            PageTranslationDecision.DefaultChineseProseShareThresholdPercent).RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(70, true)]
    public void CustomThresholdControlsTheChineseShareGate(int threshold, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: 100, NonChineseProseCharacters: 100), threshold).RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Theory]
    [InlineData(int.MinValue, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(90, 90)]
    [InlineData(91, 90)]
    [InlineData(int.MaxValue, 90)]
    public void SuppliedThresholdIsClampedToPublicBounds(int supplied, int normalized)
    {
        Assert.Equal(normalized, PageTranslationDecision.NormalizeChineseProseShareThresholdPercent(supplied));
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: normalized * 10, NonChineseProseCharacters: (100 - normalized) * 10), supplied).RuleDecision;
        Assert.False(result.NeedsTranslation);
        Assert.Equal("mixed-target-body", result.ReasonCode);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void ShareGateRequiresAtLeastSixIdentifiedChineseLetters(int chineseLetters, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: chineseLetters, NonChineseProseCharacters: 6)).RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Theory]
    [InlineData(900, 0, true)]
    [InlineData(100, 9000, false)]
    public void FullPageNonChineseStatisticTakesPrecedenceOverLatinAndSampleCounts(
        int nonChineseLetters, int latinLetters, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: 100, LatinProseCharacters: latinLetters,
                NonChineseProseCharacters: nonChineseLetters)).RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Theory]
    [InlineData(300, 0)]
    [InlineData(0, 300)]
    [InlineData(150, 150)]
    public void OlderStatisticsFallBackToLatinAndKanaProse(int latinLetters, int kanaLetters)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: 200, LatinProseCharacters: latinLetters, KanaProseCharacters: kanaLetters)).RuleDecision;
        Assert.True(result.NeedsTranslation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingStatisticsFallBackToBoundedBodySamples(bool emptyStatistics)
    {
        PageTextStatistics? statistics = emptyStatistics ? new() : null;
        Assert.True(PageTranslationDecision.Inspect([new(Chinese), new(English)], "zh", statistics).RuleDecision.NeedsTranslation);
        Assert.False(PageTranslationDecision.Inspect([new(Chinese), new("The page keeps the original links.")],
            "zh", statistics).RuleDecision.NeedsTranslation);
    }

    [Fact]
    public void SampleShareCountsLettersRatherThanPunctuationOrDigits()
    {
        var result = PageTranslationDecision.Inspect([new("这是我的页面这是我的页面。"),
            new("abcd efgh ijkl! 1234567890 " + new string('!', 200))], "zh").RuleDecision;
        Assert.False(result.NeedsTranslation);
        Assert.Equal("mixed-target-body", result.ReasonCode);
    }

    [Fact]
    public void OtherScriptsAndJapaneseHanCountAsNonChineseSampleLetters()
    {
        var japanese = "このページでは文章を読みながら必要な情報を確認できます。画面の配置を維持して翻訳を表示します。";
        var russian = "Эта библиотека предоставляет статьи для читателей и сохраняет исходное расположение страницы.";
        foreach (var foreign in new[] { japanese, russian })
        {
            var result = PageTranslationDecision.Inspect([new("这里的中文说明可以直接阅读。"), new(foreign)], "zh").RuleDecision;
            Assert.True(result.NeedsTranslation);
            Assert.False(result.IsMixedTargetLanguage);
        }
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("ja", true)]
    [InlineData("ko", true)]
    public void ChineseShareSettingDoesNotAffectOtherTargets(string target, bool expected)
    {
        foreach (var threshold in new[] { 10, 50, 90 })
        {
            var result = PageTranslationDecision.Inspect([new(English)], target,
                new(HanProseCharacters: 9000, NonChineseProseCharacters: 1), threshold).RuleDecision;
            Assert.Equal(expected, result.NeedsTranslation);
            Assert.False(result.IsMixedTargetLanguage);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LargeStatisticsDoNotOverflowShareArithmetic(bool legacyStatistics, bool expected)
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(HanProseCharacters: int.MaxValue,
                LatinProseCharacters: legacyStatistics ? int.MaxValue : 0,
                KanaProseCharacters: legacyStatistics ? int.MaxValue : 0,
                NonChineseProseCharacters: legacyStatistics ? 0 : int.MaxValue)).RuleDecision;
        Assert.Equal(expected, result.NeedsTranslation);
    }

    [Fact]
    public void EnoughWholePageProseIsNotMissedWhenSamplesContainOnlyNavigation()
    {
        var result = PageTranslationDecision.Inspect([new("Home", "navigation")], "zh",
            new(TotalCharacters: 2000, LatinProseCharacters: 1500)).RuleDecision;
        Assert.True(result.NeedsTranslation);
    }

    [Fact]
    public void JapaneseHanAndKanaRemainJapanese()
    {
        var result = PageTranslationDecision.Inspect([new("このページでは文章を読みながら必要な情報を確認できます。画面の配置を維持して翻訳を表示します。")], "zh").RuleDecision;
        Assert.True(result.NeedsTranslation); Assert.Equal("ja", result.DetectedLanguage); Assert.False(result.IsMixedTargetLanguage);
    }

    [Fact]
    public void AQuotedJapaneseExampleDoesNotTurnAnEnglishArticleIntoJapanese()
    {
        var result = PageTranslationDecision.Inspect([new(English + " A quoted example says こんにちは世界.")], "en").RuleDecision;
        Assert.False(result.NeedsTranslation); Assert.Equal("en", result.DetectedLanguage);
    }

    [Theory]
    [InlineData("fr", "Cette bibliothèque est ouverte pour les visiteurs. Les lecteurs trouvent des articles dans les collections avec des informations pour votre famille.")]
    [InlineData("de", "Die Bibliothek ist für die Besucher geöffnet. Sie können die Artikel mit den Informationen lesen und diese Inhalte für Ihre Familie verwenden.")]
    [InlineData("es", "Esta biblioteca está abierta para los visitantes. Los lectores pueden leer los artículos con las explicaciones para sus familias y sobre esta colección.")]
    public void CommonWordsRecognizeLatinLanguagesWithoutWeights(string language, string text)
    {
        var foreign = PageTranslationDecision.Inspect([new(text, "body", "en")], "en").RuleDecision;
        Assert.True(foreign.NeedsTranslation); Assert.Equal(language, foreign.DetectedLanguage);
        Assert.False(PageTranslationDecision.Inspect([new(text)], language).RuleDecision.NeedsTranslation);
    }

    [Fact]
    public void HtmlLanguageDoesNotOverrideActualChineseProse() =>
        Assert.False(PageTranslationDecision.Inspect([new(Chinese, "body", "en")], "zh").RuleDecision.NeedsTranslation);

    [Fact]
    public void ChineseDocumentationWithEnglishTermsStillNeedsTranslationForAnEnglishTarget()
    {
        var result = PageTranslationDecision.Inspect([new(Chinese + " JavaScript API with the browser and the methods")], "en").RuleDecision;
        Assert.True(result.NeedsTranslation); Assert.Equal("zh", result.DetectedLanguage);
    }

    [Theory]
    [InlineData("https://reader.example/a-long-address-that-is-metadata-not-readable-prose")]
    [InlineData("Copyright 2026 Example Corporation. All rights reserved.")]
    [InlineData("const result = 1; var data = {}; return data;")]
    public void MetadataAndCodeDoNotActivateTranslation(string text) =>
        Assert.False(PageTranslationDecision.Inspect([new(text)], "zh").RuleDecision.NeedsTranslation);

    [Fact]
    public void SensitiveAndHiddenTextAreExcluded()
    {
        var evidence = PageTranslationDecision.Inspect([new(English, "form"), new(English, "editable"), new(English, "hidden"), new(English, "code")], "zh");
        Assert.Empty(evidence.RuleDecision.EligibleBlocks); Assert.False(evidence.RuleDecision.NeedsTranslation);
    }

    [Fact]
    public void UnknownSameScriptTextRemainsUnchanged()
    {
        var result = PageTranslationDecision.Inspect([new("Asterion meridiana pluvialis orionis velarium castellum solarium.")], "en").RuleDecision;
        Assert.False(result.NeedsTranslation); Assert.Equal("same-script-ambiguous", result.ReasonCode);
    }

    [Fact]
    public void SamplesAndProcessingRemainBounded()
    {
        var evidence = PageTranslationDecision.Inspect(Enumerable.Range(0, 2000).Select(_ => new PageTextSample(English + new string('a', 5000))).ToArray(), "en");
        Assert.InRange(evidence.RuleDecision.EligibleBlocks.Count, 1, 600);
        Assert.InRange(evidence.RuleDecision.EligibleBlocks.Sum(block => block.Text.Length), 1, 24_000);
    }

    [Fact]
    public void ChineseOutsideTheBoundedSampleDoesNotVetoForeignProse()
    {
        var samples = Enumerable.Repeat(new PageTextSample(English + new string('a', 4096)), 7)
            .Append(new PageTextSample(Chinese)).ToArray();
        var result = PageTranslationDecision.Inspect(samples, "zh").RuleDecision;
        Assert.True(result.NeedsTranslation);
        Assert.DoesNotContain(result.EligibleBlocks, block => block.Text == Chinese);
        Assert.Equal(PageTranslationDecision.MaximumSampleCharacters, result.EligibleBlocks.Sum(block => block.Text.Length));
    }
}
