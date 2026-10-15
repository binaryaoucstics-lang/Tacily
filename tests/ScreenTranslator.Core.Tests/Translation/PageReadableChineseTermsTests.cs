using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

// Synthetic offline fixtures mirrored in page-readable-chinese-terms.test.js.
// No website/corpus quality claim follows from these node regressions.
public sealed class PageReadableChineseTermsTests
{
    public static TheoryData<string> Readable => new()
    {
        "CCTV.直播", "关于CCTV.com", "TECH.频道", "CCTV-5+直播",
        "关于example.net的使用说明", "关于mirror.physics.example.edu", "关于ＣＣＴＶ．ｃｏｍ",
        "请查看 docs.example.dev 的技术文档，核对版本后继续操作。",
        "研究人员讨论了AI、OpenAI与Hilbert空间的最新进展，说明了模型训练所依赖的数学方法。",
        "团队将PyTorch用于模型训练，并说明了每个计算步骤。",
        "我们在JavaScript环境中运行这个程序，并核对所有输出结果。",
        "研究人员在Hilbert空间中研究算子，并给出了完整的证明过程。",
        "关于Noether定理的讲座已经发布，读者可以查阅相关材料。",
        "研究人员用Schrodinger方程解释实验结果，并逐项核对测量记录。",
        "Hilbert的研究成果为后续数学工作提供了重要基础。",
        "团队介绍了iPhone的接口设计，并解释了相关产品功能。",
        "OpenAI发布了新的研究报告，详细介绍了计算方法和评估过程。",
        "请核对RTX-5090Pro设备的运行记录，然后继续执行中文操作说明。",
        "Android16系统支持中文界面，用户可以查看完整的设置说明。",
        "设备编号ZX_2048+已经记录，研究人员将继续进行可靠性测试。",
        "研究人员记录了CCTV-5+频道的节目安排，并核对发布时刻。",
        "𠀀Hilbert𠮷𠀋", "关于Hilbert空间",
        "研究人员在𝓗ilbert空间中研究算子，并给出了完整的证明过程。"
    };

    [Theory]
    [MemberData(nameof(Readable))]
    public void BoundTermsRemainReadableChinese(string source)
    {
        foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hant", "Chinese" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(source, target));
            Assert.False(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: true));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, foreignHanContext: true));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, foreignHanContext: true, explicitSelection: true));
        }
        Assert.True(PageTextEligibility.NeedsTranslation(source, "English"));
    }

    public static TheoryData<string> Foreign => new()
    {
        "研究人员表示，Read the OpenAI report，然后继续核对中文记录。",
        "这里的操作要求是 Please restart Android16 now，请先保存文件。",
        "研究人员记录了PLEASE STOP这条英文命令，并保留原文供读者查阅。",
        "研究人员写下了“Help”，这是外国访客发出的求助信息，请保留原文。",
        "请在终端输入 npm install 然后继续执行中文操作说明。",
        "请保持lowercase foreign prose这段文字完整。",
        "团队比较了OpenAI and Hilbert的方案，并记录所有细节。",
        "研究团队持续改进设备，Alice介绍了CR450的新方案，请继续记录原文。",
        "研究人员在Hilbert空间中阅读Read the report这条指令。",
        "研究人员在Hilbert空间中讨论了café方法，并继续保留原文。",
        "研究人员在Hilbert空间中核对한文记录，并继续保留原文。",
        "研究人员在Hilbert\u0307空间中讨论数学方法，并继续保留原文。",
        "研究人员在Hіlbert空间中讨论数学方法，并继续保留原文。",
        "研究人员在Hilbert\u200d空间中讨论数学方法，并继续保留原文。",
        "研究人员在OpenAI\u0307环境中讨论计算方法，并继续保留原文。",
        "Hilbertの定理について説明します", "OpenAIﾆｭｰｽ", "OpenAI와 연구 결과",
        "OpenAI", "Hilbert", "PyTorch", "Hilbert proved the theorem.",
        "OpenAI released a model for 中文说明.", "Read docs.example.dev before continuing 中文说明。",
        "HELP", "NO WAR", "STOP 王曼昱", "关于MODEL123设备", "关于AB12CDE设备",
        "English 中文说明", "中文说明Api", "关于example.c的说明"
    };

    [Theory]
    [MemberData(nameof(Foreign))]
    public void ForeignContentRemainsEligibleRegardlessOfExtraChinese(string source)
    {
        foreach (var value in new[] { source, string.Concat(Enumerable.Repeat("研究团队继续记录所有实验情况。", 20)) + "，" + source })
        {
            Assert.True(PageTextEligibility.NeedsTranslation(value, "简体中文"));
            Assert.True(PageTextEligibility.NeedsTranslation(value, "简体中文", explicitSelection: true));
        }
    }

    [Theory]
    [InlineData("CCTV-5+")]
    [InlineData("ZX_2048+")]
    [InlineData("RTX-5090Pro")]
    [InlineData("Android16")]
    [InlineData("AB12#")]
    [InlineData("ＡＢ１２＋")]
    public void LiteralIdSuffixesAreAutomaticMetadataWithSelectionPreserved(string source)
    {
        foreach (var target in new[] { "简体中文", "English", "ja-JP" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(source, target));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: true));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, foreignHanContext: true, explicitSelection: true));
        }
    }

    [Theory]
    [InlineData("\u0085关于Hilbert空间\ufeff", false)]
    [InlineData("关于\u0085Hilbert\u0085空间的研究报告已经发布，读者可以查阅证明。", false)]
    [InlineData("关于\ufeffHilbert空间", true)]
    [InlineData("关于Hilbert\ufeff空间", true)]
    [InlineData("关于Hil\ufeffbert空间", true)]
    public void BindingsUseUnicodeScalarHanAndWhitespaceButPreserveInternalBom(string source, bool needsTranslation)
        => Assert.Equal(needsTranslation, PageTextEligibility.NeedsTranslation(source, "简体中文"));

    // Plain TitleCase is also normal command casing. Chinese neighbours,
    // possessive 的 and extra Han must not make these action words names.
    public static TheoryData<string> Actions => new()
    {
        "请Help我们完成这项工作", "请Delete这些文件", "请Read这份研究报告", "请Stop所有任务",
        "请Retry这次提交", "请Cancel当前操作", "请选择Accept继续操作", "请Execute这段程序",
        "系统要求Download这些文件后再继续", "用户必须Save这份研究报告以保留记录",
        "可以Run这段程序来核对计算结果", "接下来Build这个项目并检查输出",
        "操作员需要Copy这些记录以完成交接", "维护人员应该Restart这台设备并观察结果",
        "报告完成后Send给所有成员", "提交前Verify所有计算结果以保证完整",
        "请选择Update继续安装过程", "现在Connect这台设备以读取数据",
        "Help的意思是帮助，请按英文提示操作", "Read的操作需要先选择文件",
        "请\u0085Delete\u0085这些文件后继续", "请Ｄｅｌｅｔｅ这些文件后继续",
        "请𝑅𝑒𝑎𝑑这份研究报告后继续", "请Cancel𠀀𠮷𠀋", "请在OpenAI环境中Execute这段程序",
        "团队建议Retry这次提交，然后请Noether核对所有结果"
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void CommandCasingIsNotProperNameEvidence(string source)
    {
        foreach (var value in new[] { source, string.Concat(Enumerable.Repeat("研究团队继续记录所有实验情况。", 20)) + source })
        foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hant", "Chinese" })
        foreach (var selection in new[] { false, true })
            Assert.True(PageTextEligibility.NeedsTranslation(value, target, explicitSelection: selection));
    }

    public static TheoryData<string> IndependentNames => new()
    {
        "请Hilbert核对这份研究报告", "请Noether继续研究这个问题",
        "团队请Alice介绍全部实验结果", "研究人员用Gauss定理计算曲面积分",
        "请Turing解释这段程序的计算过程", "我们请Euler核对所有公式并保留记录",
        "请在QuickRead环境中查看这份研究报告", "团队将AutoSave用于保护所有工作记录",
        "请OpenAI继续研究这个问题", "请核对Read2Pro设备的运行记录",
        "请查看accept.example.dev的完整说明", "关于retry.example.net的技术文档",
        "请\u0085Noether\u0085核对这份研究报告，逐项检查计算过程并保存所有结果", "请Ｎｏｅｔｈｅｒ核对这份研究报告"
    };

    [Theory]
    [MemberData(nameof(IndependentNames))]
    public void NamesAndLiteralsRetainTheirBindings(string source)
    {
        foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hant", "Chinese" })
        foreach (var selection in new[] { false, true })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: selection));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, foreignHanContext: true, explicitSelection: selection));
        }
        Assert.True(PageTextEligibility.NeedsTranslation(source, "English"));
    }

    // Independently authored numbered headings/actions, mirrored in JS.
    public static TheoryData<string> NumberedContentWords => new()
    {
        "Chapter", "Section", "Lesson", "Part", "Question", "Exercise", "Step", "Volume",
        "Appendix", "Article", "Example", "Figure", "Module", "Page", "Paragraph", "Problem",
        "Scene", "Stage", "Table", "Task", "Topic", "Unit", "Worksheet", "Summary", "Introduction",
        "Chapters", "Appendices", "Instructions", "Warning", "Solution", "Overview",
        "Help", "Read", "Delete", "Run", "Save", "Retry", "Stop", "Start", "Execute", "Download", "Accept", "Verify"
    };

    [Fact]
    public void ReportedNumericHeadingsAndMixedTechnicalContextRemainTranslatable()
    {
        foreach (var token in new[] { "Chapter2", "Section7", "Lesson1", "Part3", "Question4", "Exercise5", "Step2", "Volume6" })
        foreach (var source in new[] { token, $"请阅读{token}并完成练习", $"团队用OpenAI与Hilbert解释{token}的全部内容",
            $"请在AI环境中阅读{token}并核对说明", $"请阅读{token} and Summary9并完成练习" })
        foreach (var selection in new[] { false, true })
            Assert.True(PageTextEligibility.NeedsTranslation(source, "简体中文", explicitSelection: selection));
    }

    [Theory]
    [MemberData(nameof(NumberedContentWords))]
    public void NumberedHeadingsAndActionsRemainProse(string word)
    {
        var casings = new[] { word, word.ToLowerInvariant(), word.ToUpperInvariant(),
            string.Concat(word.Select((c, i) => i % 2 == 1 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c))) };
        foreach (var casing in casings)
        foreach (var separator in new[] { "", "-", "_", " ", "\u0085", "\u00a0" })
        foreach (var number in new[] { "2", "17", "１２", "٢" })
        foreach (var token in new[] { casing + separator + number,
            string.Concat(casing.Select(c => (char)(c + 0xfee0))) + separator + number })
        {
            var sources = new[] { token, $"请阅读{token}并完成练习", $"请{token}的内容继续说明",
                $"𠀀{token}𠮷𠀋", string.Concat(Enumerable.Repeat("研究团队继续记录所有实验情况。", 20)) + $"请阅读{token}并完成练习" };
            foreach (var source in sources)
            foreach (var target in new[] { "简体中文", "zh-CN", "zh-Hant", "Chinese", "English", "ja-JP" })
            foreach (var selection in new[] { false, true })
            {
                Assert.True(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: selection), source);
                Assert.True(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: selection, foreignHanContext: true), source);
            }
        }
    }

    [Fact]
    public void NumericContentGuardProtectsWholeWordsButPreservesProductsAndDomains()
    {
        foreach (var source in new[] { "Android16", "RTX-5090Pro", "ZX_2048+", "Read2Pro", "Step2Pro", "Go2Pro", "READ2Pro" })
        foreach (var target in new[] { "简体中文", "English", "ja-JP" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(source, target));
            Assert.True(PageTextEligibility.NeedsTranslation(source, target, explicitSelection: true));
            var embedded = $"请核对{source}设备的完整运行记录";
            Assert.False(PageTextEligibility.NeedsTranslation(embedded, "Chinese"));
            Assert.True(PageTextEligibility.NeedsTranslation(embedded, "Chinese", foreignHanContext: true));
        }
        foreach (var source in new[] { "请在QuickRead环境中查看记录", "团队将AutoSave用于保护记录",
            "关于chapter2.example.dev的完整说明", "关于step.example.net的完整说明" })
        foreach (var selection in new[] { false, true })
        {
            Assert.False(PageTextEligibility.NeedsTranslation(source, "简体中文", explicitSelection: selection));
            Assert.True(PageTextEligibility.NeedsTranslation(source, "简体中文", explicitSelection: selection, foreignHanContext: true));
        }
        foreach (var source in new[] { "请READ2继续操作", "请STEP_2继续阅读", "请RUN-2继续操作", "请Help2+继续操作",
            "请Read2#继续操作", "请𝑅𝑒𝑎𝑑２继续操作", "请Chapter\u0307２继续阅读", "请Section\ufeff2继续阅读" })
            Assert.True(PageTextEligibility.NeedsTranslation(source, "简体中文"));
        foreach (var kind in new[] { "form", "editable", "code" })
        {
            Assert.False(PageTextEligibility.NeedsTranslation("Chapter2", "简体中文", kind: kind));
            Assert.True(PageTextEligibility.NeedsTranslation("Chapter2", "简体中文", kind: kind, explicitSelection: true));
        }
    }
}
