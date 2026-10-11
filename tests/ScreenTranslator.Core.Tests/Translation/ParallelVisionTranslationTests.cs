using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ParallelVisionTranslationTests
{
    [Fact]
    public void PrimaryStreamsItsFirstTranslatedGlyphWithoutLayout()
    {
        const string json = """{"blocks":[{"id":"p1","source":"タイミーで女神","end":"やるんだ","regions":[],"translation":"打""";
        var block = Assert.Single(ParallelVisionTranslation.ReadStreaming(json, new(1000, 1000), [], VisionRequestTask.Translation));
        Assert.Equal("打", block.Translation); Assert.Equal("タイミーで女神", block.SourceText);
        Assert.Equal("やるんだ", block.SourceAnchorTail); Assert.False(block.IsComplete); Assert.False(block.Bounds.IsUsable);
    }

    [Fact]
    public void SourceAnchorsAreParsedSafelyAcrossEscapesAndPartialUnicode()
    {
        const string json = "{\"blocks\":[{\"id\":\"p1\",\"source\":\"Hi \\\"friend\\\"\",\"end\":\"Hi \\\"friend\\\"\",\"translation\":\"\\u4f60\\uD83D";
        var first = Assert.Single(ParallelVisionTranslation.ReadStreaming(json, new(1000,1000), [], VisionRequestTask.Translation));
        Assert.Equal("你", first.Translation);
        var final = ParallelVisionTranslation.Parse(json + "\\uDE00\"}]}", new(1000,1000), [], VisionRequestTask.Translation);
        Assert.Equal("你😀", final.Blocks[0].Translation);
    }

    [Fact]
    public void AuxiliaryStyleAndPositionsRemainBounded()
    {
        const string json = """{"blocks":[{"id":"p1","source":"HELLO WORLD","box":[100,200,250,350],"area":[90,190,260,360],"flow":"h","style":{"family":"brush","bold":true,"angle":99,"skew":-9,"outline":5,"hollow":true,"color":"#e04020","outlineColor":"invalid"}}]}""";
        var block = Assert.Single(ParallelVisionTranslation.Parse(json,new(2000,1000),[],VisionRequestTask.Placement).Blocks);
        Assert.Equal(new PixelRect(200,200,300,150),block.Bounds);
        Assert.Empty(block.Translation); Assert.True(block.HasVisionPlacement);
        Assert.Equal(VisionFontFamily.Brush,block.TypographyHint!.Family);
        Assert.Equal(25,block.TypographyHint.Angle); Assert.Equal(-.3f,block.TypographyHint.Skew);
        Assert.Equal(.15f,block.TypographyHint.Outline); Assert.Equal("#E04020",block.TypographyHint.ColorHex);
        Assert.Null(block.TypographyHint.OutlineColorHex);
    }

    [Theory]
    [InlineData("[0,0,1000,1000]")]
    [InlineData("[-1,1,200,200]")]
    [InlineData("[100,100,50,200]")]
    [InlineData("[1,1,2001,200]")]
    public void UnsafeAssistantPositionCannotErasePage(string box)
    {
        var json="{\"blocks\":[{\"id\":\"p1\",\"source\":\"Hello\",\"box\":"+box+",\"area\":[1,1,20,20]}]}";
        var block=Assert.Single(ParallelVisionTranslation.Parse(json,new(1000,1000),[],VisionRequestTask.Placement).Blocks);
        Assert.False(block.Bounds.IsUsable); Assert.False(block.LayoutBounds.IsUsable);
    }

    [Fact]
    public void SavedAssistantPixelLikeCoordinatesAreRejectedRatherThanGuessed()
    {
        // Actual API failure from the retained 1254px en-comic baseline, not synthesized coordinates.
        const string json="""{"blocks":[{"id":"p1","source":"THEN LET US\nPACK TONIGHT.","box":[655,60,1105,220],"area":[675,80,1085,200],"flow":"h"}]}""";
        var block=Assert.Single(ParallelVisionTranslation.Parse(json,new(1254,1254),[],VisionRequestTask.Placement).Blocks);
        Assert.False(block.Bounds.IsUsable);Assert.False(block.LayoutBounds.IsUsable);Assert.False(block.HasVisionPlacement);
        Assert.Equal("THEN LET US\nPACK TONIGHT.",block.SourceText);
        Assert.Contains("NOT pixels",ParallelVisionTranslation.PlacementPrompt);
        Assert.Contains("x=1000 right",ParallelVisionTranslation.PlacementPrompt);
    }

    [Fact]
    public void IndependentParagraphIdsNeverJoinWrongSpeakers()
    {
        var primary = new[]{TranslationBlock("p1","Hello world","你好"),TranslationBlock("p2","Goodbye world","再见")};
        var auxiliary = new[]{Placement("p1","Goodbye world",new(50,200,100,30)),Placement("p2","Hello world",new(50,50,100,30))};
        var merged=ParallelVisionTranslation.Fuse(primary,auxiliary);
        Assert.Equal(auxiliary[1].Bounds,merged[0].Bounds); Assert.Equal(auxiliary[0].Bounds,merged[1].Bounds);
        Assert.Equal(primary.Select(b=>b.Translation),merged.Select(b=>b.Translation));
    }

    [Fact]
    public void KanaWidthCaseAndPunctuationDoNotChangeSourceMatching()
    {
        var primary=TranslationBlock("p1","ﾀｲﾐｰで女神やるんだ…","打零工当女神");
        var placement=Placement("p9","たいみーで女神やるんだ",new(30,40,100,150));
        Assert.Equal(placement.Bounds,Assert.Single(ParallelVisionTranslation.Fuse([primary],[placement])).Bounds);
        Assert.Equal("HELLOWORLD",ParallelVisionTranslation.NormalizeAnchor("Hello, world!"));
    }

    [Fact]
    public void RepeatedShortDialogueRemainsUnplacedUnlessStableRegionsDisambiguate()
    {
        var main=TranslationBlock("p1","Yes","是");
        var a=Placement("p1","Yes",new(10,10,70,30)) with {SourceIds=["r1"]};
        var b=Placement("p2","Yes",new(10,150,70,30)) with {SourceIds=["r2"]};
        Assert.False(Assert.Single(ParallelVisionTranslation.Fuse([main],[a,b])).Bounds.IsUsable);
        Assert.Equal(b.Bounds,Assert.Single(ParallelVisionTranslation.Fuse([main with {SourceIds=["r2"]}],[a,b])).Bounds);
    }

    [Fact]
    public void LocalFullParagraphProvidesFallbackForFirstAndLastAnchors()
    {
        var main = TranslationBlock("p1","This is a lo","长句") with{SourceAnchorTail="about a cat"};
        OcrBlock[] local=[new("l1","This is a long sentence about a cat",.99,new(30,40,300,30),0)];
        var block=Assert.Single(ParallelVisionTranslation.Fuse([main],[],local));
        Assert.Equal(local[0].BoundsInCapturePixels,block.Bounds); Assert.False(block.HasVisionPlacement);
    }

    [Fact]
    public void UnrelatedSourceDoesNotUseCoincidentalIdOrRegion()
    {
        var main=TranslationBlock("p1","Hello world","你好") with{SourceIds=["r1"]};
        var placement=Placement("p1","Goodbye world",new(10,10,150,30)) with{SourceIds=["r1"]};
        Assert.False(Assert.Single(ParallelVisionTranslation.Fuse([main],[placement])).Bounds.IsUsable);
    }

    [Fact]
    public void FragmentedAssistantParagraphUsesCorroboratedLocalLinesWithoutMergingSeparateBubbles()
    {
        var main=TranslationBlock("p1","This is a long dialogue about a small cat","长对白") with
        {SourceText="This is a lo",SourceAnchorTail="a small cat"};
        TranslatedBlock[] fragmented=[Placement("p1","This is a long dialogue",new(30,40,300,30)),Placement("p2","about a small cat",new(30,75,290,30))];
        OcrBlock[] local=[new("l1","This is a long dialogue",.99,new(30,40,300,30),0),new("l2","about a small cat",.99,new(30,75,290,30),1)];
        var merged=Assert.Single(ParallelVisionTranslation.Fuse([main],fragmented,local));
        Assert.Equal("长对白",merged.Translation);Assert.Equal(new PixelRect(30,40,300,65),merged.Bounds);
        Assert.False(merged.HasVisionPlacement);
        var distant=local.Select((l,i)=>i==0?l:l with{BoundsInCapturePixels=new(30,400,290,30)}).ToArray();
        Assert.False(Assert.Single(ParallelVisionTranslation.Fuse([main],fragmented,distant)).Bounds.IsUsable);
    }

    [Fact]
    public void SameAssistantInkCannotBeOwnedByTwoTranslationBlocks()
    {
        var main=TranslationBlock("p1","Hello world","你好");
        var placement=Placement("p9","Hello world",new(10,10,150,30));
        Assert.All(ParallelVisionTranslation.Fuse([main,main with{Id="p2"}],[placement]),b=>Assert.False(b.Bounds.IsUsable));
    }

    [Fact]
    public void PromptsDoNotAskPrimaryForFormattingOrExplicitReasoning()
    {
        Assert.True(ParallelVisionTranslation.TranslationPrompt.Length<1200);
        Assert.True(ParallelVisionTranslation.PlacementPrompt.Length<1500);
        Assert.Contains("ORIGINAL glyphs",ParallelVisionTranslation.TranslationPrompt);
        Assert.DoesNotContain("chain of thought",ParallelVisionTranslation.TranslationPrompt,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccidentalNumericOrPunctuationSegmentsDoNotDropRealDialogue()
    {
        const string json="""{"blocks":[{"id":"p1","source":"1","end":"1","translation":"一"},{"id":"p2","source":"?","end":"?","translation":"？"},{"id":"p3","source":"Hello world","end":"Hello world","translation":"你好"}]}""";
        Assert.Equal("p3",Assert.Single(ParallelVisionTranslation.Parse(json,new(1000,1000),[],VisionRequestTask.Translation).Blocks).Id);
        Assert.Equal("p3",Assert.Single(ParallelVisionTranslation.ReadStreaming(json,new(1000,1000),[],VisionRequestTask.Translation)).Id);
    }

    private static TranslatedBlock TranslationBlock(string id,string source,string translation)=>new(id,source,translation,default){SourceAnchorTail=source};
    private static TranslatedBlock Placement(string id,string source,PixelRect bounds)=>new(id,source,"",bounds)
    {TextBounds=[bounds],LayoutBounds=bounds,HasVisionPlacement=true,VisionInkBounds=bounds};
}

