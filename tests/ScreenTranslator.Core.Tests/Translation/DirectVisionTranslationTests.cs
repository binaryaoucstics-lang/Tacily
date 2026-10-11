using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class DirectVisionTranslationTests
{
    [Fact]
    public void SmallAsideOwnsItsGlyphsInsteadOfInvalidatingLargeParagraph()
    {
        ImageAtlasRegion[] regions=[new("r1",new(0,0,200,200),default){PreciseWords=true,
            TextBounds=[new(10,10,170,20),new(10,40,130,20),new(145,150,35,20)]}];
        const string json="""{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,1000],"translation":"长段落"},{"id":"p2","regions":["r1"],"ink":[700,700,1000,1000],"translation":"抱歉"}]}""";
        var result=DirectVisionTranslation.Parse(json,new(200,200),regions);
        Assert.Equal(2,result.Blocks.Count);Assert.Equal(2,result.Blocks[0].TextBounds.Count);Assert.Single(result.Blocks[1].TextBounds);
        Assert.Equal(result.Blocks.Select(b=>b.Bounds),DirectVisionTranslation.ReadStreaming(json,new(200,200),regions).Select(b=>b.Bounds));
    }
    [Fact]
    public void DecorativePunctuationAndIsolatedNumbersDoNotBecomeEraseTiles()
    {
        const string json="""{"blocks":[{"id":"p1","translation":"?"},{"id":"p2","translation":"70"},{"id":"p3","translation":"你好"}]}""";
        Assert.Equal("p3",Assert.Single(DirectVisionTranslation.Parse(json).Blocks).Id);
        Assert.Single(DirectVisionTranslation.ReadStreaming(json));
    }
    [Fact]
    public void SharedNativeGlyphSelectionIsStillRejected()
    {
        ImageAtlasRegion[] regions=[new("r1",new(0,0,200,200),default){PreciseWords=true,TextBounds=[new(10,10,180,20),new(10,30,20,140),new(50,70,100,20)]}];
        const string json="""{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,1000],"translation":"甲"},{"id":"p2","regions":["r1"],"ink":[0,0,1000,1000],"translation":"乙"}]}""";
        // An identical native glyph selection remains an error.
        Assert.Throws<TranslationFormatException>(()=>DirectVisionTranslation.Parse(json,new(200,200),regions));
    }

    [Fact]
    public void SeparateInkInsideOverlappingParagraphEnvelopesCanStreamTogether()
    {
        ImageAtlasRegion[] regions=[new("r1",new(0,0,200,200),default){PreciseWords=true,TextBounds=[new(10,10,180,20),new(10,40,20,140),new(60,80,90,20)]}];
        const string json="""{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,200],"translation":"甲"},{"id":"p2","regions":["r1"],"ink":[0,180,180,1000],"translation":"乙"},{"id":"p3","regions":["r1"],"ink":[250,350,850,550],"translation":"丙"}]}""";
        var result=DirectVisionTranslation.Parse(json,new(200,200),regions);
        Assert.Equal(3,result.Blocks.Count);Assert.Equal(3,DirectVisionTranslation.ReadStreaming(json,new(200,200),regions).Count);
    }

    [Fact]
    public void FocusedKnownCropCanLocateItsFullTextWithoutRelaxingFullPageGuard()
    {
        const string json="""{"blocks":[{"id":"p1","box":[0,0,1000,1000],"area":[0,0,1000,1000],"translation":"你\\n好"}]}""";
        Assert.False(DirectVisionTranslation.Parse(json,new(200,100)).Blocks[0].Bounds.IsUsable);
        var cropped=DirectVisionTranslation.Parse(json,new(200,100),focused:true).Blocks[0];
        Assert.Equal(new PixelRect(0,0,200,100),cropped.Bounds);Assert.Equal("你 好",cropped.Translation);
        Assert.Equal(cropped.Bounds,DirectVisionTranslation.ReadStreaming(json,new(200,100),focused:true)[0].Bounds);
    }
    [Fact]
    public void TwoSpeakersInOnePreciseCropKeepTheirOwnWords()
    {
        ImageAtlasRegion[] regions=[new("r1",new(100,100,200,200),default){PreciseWords=true,IsParagraph=true,
            TextBounds=[new(110,110,170,30),new(180,220,80,25)]}];
        const string response="""{"blocks":[{"id":"p1","regions":["r1"],"ink":[0,0,1000,250],"translation":"你好"},{"id":"p2","regions":["r1"],"ink":[300,550,850,800],"translation":"再见"}]}""";
        var blocks=DirectVisionTranslation.Parse(response,new(500,500),regions).Blocks;
        Assert.Equal(new PixelRect(110,110,170,30),blocks[0].Bounds);
        Assert.Equal(new PixelRect(180,220,80,25),blocks[1].Bounds);
        Assert.Equal(blocks.Select(b=>b.Bounds),DirectVisionTranslation.ReadStreaming(response,new(500,500),regions).Select(b=>b.Bounds));
    }
    [Fact]
    public void FirstTranslatedCharacterAlreadyHasPlacementWithoutAnySourceTranscription()
    {
        const string json = """{"blocks":[{"id":"p1","box":[100,200,300,400],"area":[80,180,330,450],"flow":"h","translation":"你""";
        var block = Assert.Single(DirectVisionTranslation.ReadStreaming(json, new(2000, 3000)));
        Assert.Equal("你", block.Translation);
        Assert.Empty(block.SourceText);
        Assert.Equal(new PixelRect(200, 600, 400, 600), block.Bounds);
        Assert.Equal(new PixelRect(160, 540, 500, 810), block.LayoutBounds);
        Assert.True(block.HasVisionPlacement);
        Assert.False(block.IsVertical);
        Assert.False(block.IsComplete);
        var final = DirectVisionTranslation.Parse(json + "好\"}]}", new(2000, 3000));
        Assert.Equal(block.Bounds, final.Blocks[0].Bounds);
        Assert.True(final.Blocks[0].IsComplete);
    }

    [Fact]
    public void CompleteBubbleAndPartialNextBubbleAreDisplayedOnlyOnce()
    {
        const string json = """{"blocks":[{"id":"p1","box":[100,100,200,200],"area":[80,80,220,220],"flow":"h","translation":"一"},{"id":"p2","box":[300,300,400,400],"area":[280,280,420,420],"flow":"v","translation":"二""";
        Assert.Equal(new[] { "一", "二" }, DirectVisionTranslation.ReadStreaming(json).Select(block => block.Translation));
        Assert.Equal(2, DirectVisionTranslation.ReadStreaming(json + "\"}]}").Count);
        Assert.Empty(DirectVisionTranslation.ReadStreaming("{\"blocks\":[{\"id\":\"p1\",\"box\":[100,200,"));
    }

    [Fact]
    public void EscapedQuotationBracesAndSplitUnicodeDoNotInventBlocks()
    {
        const string json = "{\"blocks\":[{\"id\":\"p1\",\"box\":[10,20,30,50],\"area\":[10,20,30,50],\"flow\":\"h\",\"translation\":\"\\u4f60\\uD83D";
        var first = Assert.Single(DirectVisionTranslation.ReadStreaming(json));
        Assert.Equal("你", first.Translation);
        Assert.Empty(first.SourceText);
        var final = DirectVisionTranslation.Parse(json + "\\uDE00\"}]}");
        Assert.Equal("你😀", final.Blocks[0].Translation);
    }

    [Theory]
    [InlineData("{\"blocks\":[{\"id\":\"p2\",\"sourceText\":\"Hi\",\"translation\":\"你好\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"p1\",\"translation\":null}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"p1\",\"sourceText\":\"Hi\",\"translation\":\"\"}]}")]
    [InlineData("{\"blocks\":[{\"id\":\"p1\",\"sourceText\":\"A\",\"translation\":\"甲\"},{\"id\":\"p1\",\"sourceText\":\"B\",\"translation\":\"乙\"}]}")]
    public void InvalidGroupsAreRejected(string json) => Assert.Throws<TranslationFormatException>(() => DirectVisionTranslation.Parse(json));

    [Fact]
    public void EmptyImageIsNoTextAndPromptStaysCompact()
    {
        Assert.Throws<TranslationNoTextException>(() => DirectVisionTranslation.Parse("{\"blocks\":[]}"));
        Assert.True(DirectVisionTranslation.Prompt.Length < 1450);
        Assert.Contains("complete speech bubble", DirectVisionTranslation.Prompt);
        Assert.Contains("No original text", DirectVisionTranslation.Prompt);
        Assert.Contains("INTEGER 0..1000", DirectVisionTranslation.Prompt);
    }

    [Theory]
    [InlineData("[-1,20,40,60]")]
    [InlineData("[10,20,1040,60]")]
    [InlineData("[40,20,10,60]")]
    [InlineData("[0,0,1000,1000]")]
    [InlineData("[10,20,30]")]
    [InlineData("null")]
    public void UnsafeCoordinatesNeverPreventTranslationOrEraseThePage(string box)
    {
        var json = "{\"blocks\":[{\"id\":\"p1\",\"box\":" + box + ",\"area\":[10,20,40,60],\"flow\":\"h\",\"translation\":\"你好\"}]}";
        var block = Assert.Single(DirectVisionTranslation.Parse(json, new(1000, 1000)).Blocks);
        Assert.Equal("你好", block.Translation);
        Assert.False(block.Bounds.IsUsable);
        Assert.False(block.LayoutBounds.IsUsable);
    }

    [Fact]
    public void ForeignLayoutAreaFallsBackToItsOwnTextAndTranscriptionIsDiscarded()
    {
        const string json = """{"blocks":[{"id":"p1","box":[100,100,200,300],"area":[700,600,900,900],"flow":"v","sourceText":"ignored","translation":"你好"}]}""";
        var block = Assert.Single(DirectVisionTranslation.Parse(json, new(1000, 1000)).Blocks);
        Assert.Equal(block.Bounds, block.LayoutBounds);
        Assert.Empty(block.SourceText);
        Assert.True(block.IsVertical);
    }

    [Fact]
    public void RoomyLayoutCanShiftWithinBubbleWithoutContainingOriginalTextCenter()
    {
        const string json = """{"blocks":[{"id":"p1","box":[100,100,180,220],"area":[100,200,380,350],"flow":"h","translation":"你好"}]}""";
        var block = Assert.Single(DirectVisionTranslation.Parse(json, new(1000, 1000)).Blocks);
        Assert.Equal(new PixelRect(100, 200, 280, 150), block.LayoutBounds);
    }

    [Fact]
    public async Task VisionCoordinatesSurviveLateLocalDetectionWithoutTextMatching()
    {
        var updates = new List<TranslationStreamUpdate>();
        var layout = new ParallelVisionLayout(new Callback(updates.Add), CancellationToken.None);
        var block = new TranslatedBlock("p1", "", "你好", new(30, 40, 100, 160))
            { HasVisionPlacement = true, LayoutBounds = new(20, 30, 150, 180) };
        layout.Publish(new("你好") { ImageSegments = [block] });
        await layout.LocateAsync(_ => Task.FromResult<IReadOnlyList<OcrBlock>>([new("l1", "", 1, new(200, 200, 60, 60), 0)]));
        Assert.Equal(block.Bounds, updates[^1].ImageSegments![0].Bounds);
        Assert.Equal(block.LayoutBounds, updates[^1].ImageSegments![0].LayoutBounds);
        Assert.True(updates[^1].IsLayoutUpdate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalGeometryWorksWhetherItArrivesBeforeOrAfterTranslation(bool layoutFirst)
    {
        var updates = new List<TranslationStreamUpdate>();
        var release = new TaskCompletionSource<IReadOnlyList<OcrBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var layout = new ParallelVisionLayout(new Callback(updates.Add), CancellationToken.None);
        var lookup = layout.LocateAsync(_ => release.Task);
        if (layoutFirst) { release.SetResult([new("l1", "Hello world", 1, new(10, 20, 130, 40), 0)]); await lookup; }
        layout.Publish(new("你") { ImageSegments = [new("p1", "Hello world", "你", default)], StreamingTail = "" });
        Assert.Equal("你", updates[^1].Translation);
        var final = layout.Complete(new([new("p1", "Hello world", "你好", default)]));
        if (!layoutFirst)
        {
            Assert.False(lookup.IsCompleted);
            Assert.False(final.Blocks[0].Bounds.IsUsable);
            release.SetResult([new("l1", "Hello world", 1, new(10, 20, 130, 40), 0)]);
            await lookup;
            Assert.True(updates[^1].IsLayoutUpdate);
            Assert.Equal("你好", updates[^1].Translation);
        }
        Assert.Equal(new PixelRect(10, 20, 130, 40), updates[^1].ImageSegments![0].Bounds);
    }

    [Fact]
    public async Task FailedOrCancelledLocatorCannotBlockOrOverwriteTranslation()
    {
        using var cancellation = new CancellationTokenSource();
        var updates = new List<TranslationStreamUpdate>();
        var layout = new ParallelVisionLayout(new Callback(updates.Add), cancellation.Token);
        await layout.LocateAsync(_ => Task.FromException<IReadOnlyList<OcrBlock>>(new InvalidOperationException()));
        layout.Publish(new("你好"));
        var count = updates.Count;
        var release = new TaskCompletionSource<IReadOnlyList<OcrBlock>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = layout.LocateAsync(_ => release.Task);
        cancellation.Cancel();
        release.SetResult([]);
        await pending;
        layout.Publish(new("过时"));
        Assert.Equal(count, updates.Count);
    }

    [Fact]
    public async Task FailedBubbleGroundingFallsBackWithoutInterruptingCloudStream()
    {
        var updates = new List<TranslationStreamUpdate>();
        var layout = new ParallelVisionLayout(new Callback(updates.Add), CancellationToken.None,
            (_, _) => throw new InvalidOperationException("Optional local repair failed"));
        await layout.LocateAsync(_ => Task.FromResult<IReadOnlyList<OcrBlock>>([new("l1", "Hello world", 1, new(10, 20, 130, 40), 0)]));
        layout.Publish(new("你") { ImageSegments = [new("p1", "Hello world", "你", default) { IsComplete = false }] });
        Assert.Equal("你", updates[^1].Translation);
        Assert.Equal(new PixelRect(10, 20, 130, 40), updates[^1].ImageSegments![0].Bounds);
        Assert.False(updates[^1].ImageSegments![0].IsComplete);
    }

    [Fact]
    public void RepeatedDialogueAtDistinctLocationsRemainsUnlocatedDuringStreaming()
    {
        var regions = WholeImageTextMatcher.Match([new("p1", "Thank you", 1, default, 0)],
            [new("l1", "Thank you", 1, new(10, 20, 120, 40), 0), new("l2", "Thank you", 1, new(10, 200, 120, 40), 1)]);
        Assert.Empty(regions[0].TextBounds);
    }

    [Fact]
    public void UpgradeSelectsFastVisionOnceAndThenKeepsUserChoices()
    {
        var upgraded = (new AppSettings { Version = 6, InputMode = TranslationInputMode.TextOnly, ThinkingLevel = ThinkingLevel.High }).Migrate(out _);
        Assert.Equal(TranslationInputMode.VisionOnly, upgraded.InputMode);
        Assert.Equal(ThinkingLevel.None, upgraded.ThinkingLevel);
        var custom = (upgraded with { InputMode = TranslationInputMode.TextOnly, ThinkingLevel = ThinkingLevel.Max }).Migrate(out _);
        Assert.Equal(TranslationInputMode.VisionOnly, custom.InputMode);
        Assert.Equal(ThinkingLevel.Max, custom.ThinkingLevel);
    }

    private sealed class Callback(Action<TranslationStreamUpdate> action) : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) => action(value); }
}
