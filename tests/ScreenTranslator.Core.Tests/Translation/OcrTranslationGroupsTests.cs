using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class OcrTranslationGroupsTests
{
    private static TranslationRequest Request() => new("auto", "zh-CN", TranslationStyle.Natural, "",
        [new("p1", "ええっ?!", 1, default, 0), new("p2", "真央くんが", 1, default, 1), new("p3", "誘拐された?!", 1, default, 2),
            new("p4", "今気付いた人", 1, default, 3)])
    {
        IsOcrDocument = true,
        AtlasRegions = [new("p1", default, default), new("p2", new(300, 100, 30, 150), default) { TextBounds = [new(300, 100, 30, 150)] },
            new("p3", new(240, 100, 30, 200), default) { TextBounds = [new(240, 100, 30, 200)] }, new("p4", default, default)]
    };
    private const string Valid = """{"blocks":[{"ids":["p1","p2","p3"],"translation":"诶？！真央被绑架了？！","sourceText":"忽略模型原文","bbox":[0,0,1000,1000]},{"ids":["p4"],"translation":"刚反应过来的人"}]}""";

    [Fact]
    public async Task MergesDialogueAndKeepsCaptionWithLocalSourceAndDisjointHighlightBoxes()
    {
        var provider = new Provider(Valid);
        var result = await new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(Request(), CancellationToken.None);
        Assert.Equal(2, result.Blocks.Count);
        var dialogue = result.Blocks[0];
        Assert.Equal("p1", dialogue.Id);
        Assert.Equal(new[] { "p1", "p2", "p3" }, dialogue.SourceIds);
        Assert.Equal("ええっ?!\n真央くんが\n誘拐された?!", dialogue.SourceText);
        Assert.Equal(new PixelRect(240, 100, 90, 200), dialogue.Bounds);
        Assert.Equal(new[] { new PixelRect(300, 100, 30, 150), new PixelRect(240, 100, 30, 200) }, dialogue.TextBounds);
        Assert.False(result.Blocks[1].Bounds.IsUsable);
        Assert.Empty(result.Blocks[1].TextBounds);
        Assert.Single(provider.Requests);
    }

    [Theory]
    [InlineData("""{"blocks":[{"ids":["p1","p3"],"translation":"错误"},{"ids":["p2","p4"],"translation":"错误"}]}""")]
    [InlineData("""{"blocks":[{"ids":["p1","p2"],"translation":"甲"},{"ids":["p2","p3","p4"],"translation":"乙"}]}""")]
    [InlineData("""{"blocks":[{"ids":["p1","p2","p3"],"translation":"漏字"}]}""")]
    [InlineData("""{"blocks":[{"ids":["new"],"translation":"未知"}]}""")]
    [InlineData("""{"blocks":[{"ids":["p2","p1","p3","p4"],"translation":"倒序"}]}""")]
    [InlineData("""{"blocks":[{"ids":[],"translation":"空组"}]}""")]
    [InlineData("""{"blocks":[{"ids":["p1",2],"translation":"坏字段"}]}""")]
    [InlineData("""{"blocks":[{"ids":["p1","p2","p3","p4"],"translation":""}]}""")]
    public void RejectsSkippedDuplicatedMissingUnknownOrReorderedIds(string json) =>
        Assert.Throws<TranslationFormatException>(() => OcrTranslationGroups.Parse(json, Request()));

    [Fact]
    public void StreamingCommitsCompleteGroupsInOrderAndLeavesUnfinishedTranslationAsTail()
    {
        const string json = """{"blocks":[{"ids":["p1","p2","p3"],"translation":"诶？！真央被绑架了？！"},{"ids":["p4"],"translation":"刚反应""";
        var groups = ImageTranslationStreamParser.ReadCompletedOcrBlocks(json, Request());
        Assert.Single(groups);
        Assert.Equal(new[] { "p1", "p2", "p3" }, groups[0].SourceIds);
        Assert.Equal("刚反应", ImageTranslationStreamParser.ReadTranslationTail(json));
        Assert.Empty(ImageTranslationStreamParser.ReadCompletedOcrBlocks("""{"blocks":[{"ids":["p1","p3"],"translation":"错组"}]}""", Request()));
    }

    [Fact]
    public async Task RepairsOnceWithShortInstructionWithoutResendingBadResponse()
    {
        var bad = """{"blocks":[{"ids":["p1","p3"],"translation":"BAD_SENTINEL"}]}""";
        var provider = new Provider(bad, Valid);
        var result = await new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(Request(), CancellationToken.None);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Contains("FORMAT_REPAIR", provider.Requests[1].Context);
        Assert.DoesNotContain("BAD_SENTINEL", provider.Requests[1].Context);
        Assert.True(provider.Requests[1].Context.Length < 160);
    }

    [Fact]
    public void PromptIsShortAndDoesNotRetainTheOldBanOnMerging()
    {
        Assert.True(DeepSeekTranslationProvider.OcrGroupingPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 65);
        Assert.DoesNotContain("Do not add, remove, merge", DeepSeekTranslationProvider.OcrGroupingPrompt);
        Assert.Contains("consecutive", DeepSeekTranslationProvider.OcrGroupingPrompt);
    }

    private sealed class Provider(params string[] replies) : ITranslationProvider
    {
        private int _next;
        public List<TranslationRequest> Requests { get; } = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
        { Requests.Add(request); return Task.FromResult(replies[_next++]); }
    }
}
