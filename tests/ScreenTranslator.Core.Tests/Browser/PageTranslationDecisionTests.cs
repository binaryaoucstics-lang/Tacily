using ScreenTranslator.Core.Browser;

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
    public void GenuineChineseAndEnglishProseRemainsReadableMixedContent()
    {
        var result = PageTranslationDecision.Inspect([new(Chinese), new(English)], "zh").RuleDecision;
        Assert.False(result.NeedsTranslation); Assert.True(result.IsMixedTargetLanguage);
    }

    [Fact]
    public void ChineseNavigationAndHeadingDoNotOverrideEnglishBody()
    {
        var result = PageTranslationDecision.Inspect([new("中文产品官方网站", "title"), new("翻译此页面", "navigation"), new(English)], "zh").RuleDecision;
        Assert.True(result.NeedsTranslation); Assert.False(result.IsMixedTargetLanguage);
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
    public void WholePageChineseProseVetoesASparseEnglishSample()
    {
        var result = PageTranslationDecision.Inspect([new(English)], "zh",
            new(TotalCharacters: 4000, HanProseCharacters: 100, LatinProseCharacters: 2000)).RuleDecision;
        Assert.False(result.NeedsTranslation); Assert.True(result.IsMixedTargetLanguage);
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
}
