using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextCompletenessGuardTests
{
    // Independent development fixtures. The frozen report supplies only the
    // final noun, not the original paragraph; this is a synthetic long analogue.
    public const string UniversitySource = "The university brings together students and researchers from many backgrounds. "
        + "Its teachers connect classroom study with practical work, encouraging students to ask questions, "
        + "share ideas and explore unfamiliar subjects. Across its departments, students can join seminars, "
        + "work in laboratories and learn from public events. Visitors can also explore the library, meet "
        + "members of the community and discover the full range of educational offerings.";
    public const string UniversityResidue = "这所大学汇聚了来自不同背景的学生和研究人员。教师将课堂学习与实践相结合，"
        + "鼓励学生提出问题、交流想法并探索陌生领域。学生可以参加研讨会、在实验室工作，"
        + "并通过公共活动学习。访客还可以探索图书馆、认识社区成员，了解全方位的教育offerings。";
    public const string UniversityTranslation = "这所大学汇聚了来自不同背景的学生和研究人员。教师将课堂学习与实践相结合，"
        + "鼓励学生提出问题、交流想法并探索陌生领域。学生可以参加研讨会、在实验室工作，"
        + "并通过公共活动学习。访客还可以探索图书馆、认识社区成员，了解全方位的教育项目。";

    [Fact]
    public void LongParagraphFinalNounRequestsRepairWithoutPretendingToKnowItsMeaning()
    {
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess(UniversitySource, UniversityResidue));
        Assert.Equal(PageTextCompleteness.Clear, Assess(UniversitySource, UniversityTranslation));
    }

    [Theory]
    [InlineData("Please read the announcement.", "请阅读announcement。")]
    [InlineData("They shared their experiences.", "他们分享了自己的experiences。")]
    [InlineData("We welcome suggestions.", "我们欢迎suggestions。")]
    [InlineData("The garden has a pathway.", "花园有一条pathway。")]
    [InlineData("Meet students' needs and teachers' expectations.", "满足学生的needs和教师的期望。")]
    [InlineData("opportunities", "更多的opportunities")]
    [InlineData("Use redis for storage.", "使用redis进行存储。")]
    [InlineData("Train embeddings for retrieval.", "训练embeddings用于检索。")]
    public void UnmarkedSingleWordsAreAmbiguousRatherThanPermanentFailures(string source, string answer)
        => Assert.Equal(PageTextCompleteness.RetainedTerm, Assess(source, answer));

    [Theory]
    [InlineData("Read the rest of the announcement.", "请阅读the rest of the announcement。")]
    [InlineData("The guide is for our visitors.", "这份指南is for our visitors。")]
    [InlineData("We have the answer for your questions.", "我们有have the answer for your questions。")]
    public void RetainedSourceGrammarPhraseIsStrongerEvidence(string source, string answer)
        => Assert.Equal(PageTextCompleteness.UntranslatedProse, Assess(source, answer));

    [Theory]
    [InlineData("Ada Lovelace works with MIT and NASA.", "Ada Lovelace与MIT和NASA合作。")]
    [InlineData("OpenAI uses an API.", "OpenAI使用API。")]
    [InlineData("Run `redis get key` using the API.", "使用API运行`redis get key`。")]
    [InlineData("Open https://example.test/offerings for details.", "详情见https://example.test/offerings。")]
    [InlineData("Write to C:\\data\\offerings.txt and ./local/cache.json.", "写入C:\\data\\offerings.txt和./local/cache.json。")]
    [InlineData("Call load_cache and service.fetch(), then notify a@example.test.", "调用load_cache和service.fetch()，再通知a@example.test。")]
    [InlineData("Use std::vector and printf() here.", "这里使用std::vector和printf()。")]
    [InlineData("The literal term is 'offerings'.", "字面词是'offerings'。")]
    [InlineData("Keep \"the rest of the text\" as a literal.", "保留字面值\"the rest of the text\"。")]
    [InlineData("Use “redis” and 「cache」 as labels.", "使用“redis”和「cache」作为标签。")]
    [InlineData("Research in vitro models.", "研究体外模型。")]
    [InlineData("Translate the garden description.", "花园介绍使用xylophone一词。")]
    public void LiteralNameAndNonoverlapEvidenceDoesNotRejectLatinLetters(string source, string answer)
        => Assert.Equal(PageTextCompleteness.Clear, Assess(source, answer));

    [Fact]
    public void QuotingInAnswerCannotLaunderUnquotedSourceProse()
    {
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess("Explore the offerings.", "探索这些\"offerings\"。"));
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess("Compare 'offerings' with other offerings.", "比较'offerings'与其他offerings。"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void DoesNotImposeChineseScriptRulesOnOtherTargets(string target)
        => Assert.Equal(PageTextCompleteness.Clear, PageTextCompletenessGuard.Assess("Read the rest of the guide.", "Read the rest of the guide.", target));

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("Chinese")]
    [InlineData("繁體中文")]
    public void ChineseTargetAliasesShareTheSameSourceOverlapCheck(string target)
        => Assert.Equal(PageTextCompleteness.RetainedTerm, PageTextCompletenessGuard.Assess("Explore the offerings.", "探索这些offerings。", target));

    [Fact]
    public void OversizeInputIsUnknownAndNeverTruncatedIntoAProseVerdict()
    {
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess(new string('x', 32_769), "译文"));
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess("source", new string('译', 131_073)));
    }

    [Theory]
    [InlineData("中文：Original paragraph A.")]
    [InlineData("Original paragraph A.")]
    [InlineData("café")]
    public void SparseHanLabelsAndOtherLanguagesAreOutsidePartialChineseProseScope(string answer)
        => Assert.Equal(PageTextCompleteness.Clear, Assess("Original paragraph A. café", answer));

    [Fact]
    public void AccentNeighboursAndPartialWordSubstringsAreNotSourcePhraseEvidence()
    {
        Assert.Equal(PageTextCompleteness.Clear, Assess("Use café as a label.", "使用这个café标签。"));
        Assert.Equal(PageTextCompleteness.RetainedTerm, Assess("Read the rest of thematically arranged text, and the guide.", "阅读其中the rest of the。"));
    }

    [Fact]
    public void PagePromptRequiresCoverageAndPreservesFactsAndLegitimateTerms()
    {
        var prompt = DeepSeekTranslationProvider.PageTextPrompt;
        Assert.Contains("普通叙述须全部翻译", prompt);
        Assert.Contains("自查每个保留原词", prompt);
        Assert.Contains("长段末尾及单复数词", prompt);
        Assert.Contains("技术术语、URL、代码、路径和引用字面词", prompt);
        Assert.Contains("Each ID translates ONLY its own text", prompt);
        Assert.Contains("dates, weekdays, negation and names", prompt);
    }

    private static PageTextCompleteness Assess(string source, string answer)
        => PageTextCompletenessGuard.Assess(source, answer, "简体中文");
}
