using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class AtlasTranslationTests
{
    private static readonly ImageAtlasRegion[] Regions = [
        new("p1", new PixelRect(40, 30, 600, 50), new PixelRect(10, 34, 600, 50)),
        new("p2", new PixelRect(44, 1500, 560, 80), new PixelRect(10, 118, 560, 80))];
    private const string Result = """{"blocks":[{"id":"p2","sourceText":"Second","translation":"第二段"},{"id":"p1","sourceText":"Title","translation":"标题","bbox":[0,0,1000,1000]}]}""";

    [Fact]
    public void Original_Detector_Coordinates_Are_Used_Even_If_Vlm_Returns_Bogus_Coordinates()
    {
        var result = new TranslationResponseValidator().ParseAtlas(Result, Regions);
        Assert.Equal(new[] { "p1", "p2" }, result.Blocks.Select(block => block.Id));
        Assert.Equal(Regions[0].SourceBounds, result.Blocks[0].Bounds);
        Assert.Equal(Regions[1].SourceBounds, result.Blocks[1].Bounds);
    }

    [Fact]
    public void Atlas_Groups_Stream_Without_A_Coordinate_Field()
    {
        const string prefix = """{"blocks":[{"id":"p1","sourceText":"Title","translation":"标题"},{"id":"p2","translation":"第""";
        var completed = ImageTranslationStreamParser.ReadCompletedAtlasBlocks(prefix, Regions);
        Assert.Single(completed);
        Assert.Equal(Regions[0].SourceBounds, completed[0].Bounds);
    }

    [Fact]
    public async Task Format_Repair_Keeps_The_Single_Atlas_And_Original_Mapping()
    {
        var provider = new Provider("{}", Result);
        var request = new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "",
            Regions.Select((region, index) => new OcrBlock(region.Id, "", 1, region.SourceBounds, index)).ToArray())
        { ImagePng = [1, 2, 3], AtlasRegions = Regions, ImageSize = new PixelSize(620, 208) };
        var result = await new TranslationOrchestrator(provider, new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(request.ImagePng, provider.Requests[1].ImagePng);
        Assert.Equal(Regions, provider.Requests[1].AtlasRegions);
    }

    [Fact]
    public void Empty_NonText_Region_Is_Skipped_Without_Losing_Valid_Text()
    {
        const string response = """{"blocks":[{"id":"p1","sourceText":"Title","translation":"标题"},{"id":"p2","sourceText":"","translation":""}]}""";
        Assert.Single(new TranslationResponseValidator().ParseAtlas(response, Regions).Blocks);
    }

    [Fact]
    public void Grouping_Deduplicates_Overlapping_Tiles_And_Keeps_Distant_Paragraphs_Separate()
    {
        var lines = new[] {
            new TextRegion(new PixelRect(20, 20, 400, 25), 0.9), new TextRegion(new PixelRect(21, 21, 398, 24), 0.8),
            new TextRegion(new PixelRect(20, 53, 380, 25), 0.9), new TextRegion(new PixelRect(20, 900, 400, 25), 0.9) };
        var grouped = TextRegionGrouper.Group(lines, new PixelSize(1000, 1200));
        Assert.Equal(2, grouped.Count);
        Assert.Equal(new PixelRect(14, 14, 412, 70), grouped[0]);
        Assert.Equal(new PixelRect(14, 894, 412, 37), grouped[1]);
    }

    private sealed class Provider(params string[] replies) : ITranslationProvider
    {
        private readonly Queue<string> _queue = new(replies);
        public List<TranslationRequest> Requests { get; } = [];
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(_queue.Dequeue()); }
    }
}
