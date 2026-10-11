using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class VisionSemanticsTests
{
    [Fact]
    public void CompleteForeignSpeechSurvivesWhileChineseNoiseAndCreditsDoNotEnterRendering()
    {
        TranslatedBlock[] blocks = [Block("Hi!", "嗨"), Block("我已经回来了", "我已经回来了"),
            Block("Sarah Meyssonnier/Reuters", "萨拉·梅索尼耶/路透社"), Block("1", "一")];
        Assert.Equal("Hi!", Assert.Single(VisionSemantics.Filter(blocks, "zh-Hans")).SourceText);
    }

    [Fact]
    public void TranslatedChineseIsNotEvidenceThatTheSourceWasChinese()
    {
        var block = Block("Please leave immediately!", "请马上离开！");
        Assert.Same(block, Assert.Single(VisionSemantics.Filter([block], "zh-Hans")));
    }

    [Fact]
    public void HanOnlyHeadAndTailDoNotRevealTheMiddleOfJapaneseParagraph()
    {
        var block = Block("魔法使", "魔法使者是我们") with { SourceAnchorTail = "世界" };
        Assert.Same(block, Assert.Single(VisionSemantics.Filter([block], "zh-Hans", sourceIsComplete: false)));
    }

    [Fact]
    public void JapaneseContextKeepsAmbiguousHanOnlyCaptions()
    {
        var title = Block("天下無敵", "天下无敌");
        Assert.Equal(2, VisionSemantics.Filter([title, Block("また明日ね", "明天见")], "zh-Hans").Count);
    }

    [Fact]
    public void ExactReliableOriginalTargetTextCorroboratesNoSourceAnchors()
    {
        var block = Block("", "你好，欢迎回来") with { Bounds = new(10, 10, 240, 40) };
        OcrBlock[] local = [Line("你好，欢迎回来", block.Bounds), Line("", block.Bounds, id: "detected")];
        Assert.Empty(VisionSemantics.Filter([block], "zh-Hans", local, sourceIsComplete: false));
    }

    [Fact]
    public void OneTargetLineCannotSuppressAnUnknownForeignPartOfParagraph()
    {
        var block = Block("", "欢迎回来。请马上离开") with { Bounds = new(10, 10, 240, 100) };
        OcrBlock[] local = [Line("你好，欢迎回来", new(10, 10, 240, 40)), Line("", new(10, 65, 240, 40), id: "detected")];
        Assert.Single(VisionSemantics.Filter([block], "zh-Hans", local, sourceIsComplete: false));
    }

    [Fact]
    public void WideEnvelopeDoesNotHideUnknownNativeTextBetweenKnownTargetLines()
    {
        var block = Block("", "欢迎回来") with { Bounds = new(10, 10, 240, 100) };
        OcrBlock[] local = [Line("你好", new(10, 10, 240, 20)), Line("欢迎回来", new(10, 90, 240, 20), id: "known2"),
            Line("", new(10, 45, 240, 20), id: "detected")];
        Assert.Single(VisionSemantics.Filter([block], "zh-Hans", local, sourceIsComplete: false));
    }

    [Fact]
    public void WeakChineseOcrAndNeighbouringTextCannotEraseForeignSpeech()
    {
        var block = Block("", "请帮帮我") with { Bounds = new(10, 10, 240, 40) };
        Assert.Single(VisionSemantics.Filter([block], "zh-Hans", [Line("请帮帮我", block.Bounds, .7)], false));
        Assert.Single(VisionSemantics.Filter([block], "zh-Hans", [Line("请帮帮我", new(260, 10, 240, 40))], false));
    }

    [Fact]
    public void FullRegionReadingHintsCanSuppressAlreadyChineseNativeRegion()
    {
        var request = new TranslationRequest("auto", "zh-Hans", TranslationStyle.Natural, "", [])
        { IsDirectVision = true, VisionRegions = [new("r1", new(10, 10, 240, 40), default)],
            VisionReadingHints = [new("r1", "我们已经到了", .99) { Kind = "complete-source" }] };
        var block = Block("", "我们已经到了") with { Bounds = new(10, 10, 240, 40), SourceIds = ["r1"] };
        Assert.Empty(VisionSemantics.Filter([block], request));
    }

    [Fact]
    public void OrdinaryReadingHintMayCoverOnlyOneLineOfItsLargeCrop()
    {
        var request = new TranslationRequest("auto", "zh-Hans", TranslationStyle.Natural, "", [])
        { IsDirectVision = true, VisionRegions = [new("r1", new(10, 10, 240, 100), default)],
            VisionReadingHints = [new("r1", "我们已经到了", .99)] };
        var block = Block("", "Please help me escape") with { Bounds = new(10, 10, 240, 100), SourceIds = ["r1"] };
        Assert.Single(VisionSemantics.Filter([block], request));
    }

    [Fact]
    public void PersonalJapaneseLetterSignatureIsNotPhotoCredit()
    {
        var request = new TranslationRequest("auto", "zh-Hans", TranslationStyle.Natural, "", [])
        { VisionReadingHints = [new("r1", "モト子より", .99) { Kind = "signature", Name = "モト子" }] };
        var block = Block("モト子より", "来自元子") with { SourceIds = ["r1"] };
        Assert.Single(VisionSemantics.Filter([block], "zh-Hans"));
        Assert.Equal("モト子 留", VisionSemantics.Apply(block, request).Translation);
    }

    private static TranslatedBlock Block(string source, string translation) => new("p1", source, translation, new(10, 10, 240, 40));
    private static OcrBlock Line(string text, PixelRect bounds, double confidence = .99, string id = "known") => new(id, text, confidence, bounds, 0);
}
