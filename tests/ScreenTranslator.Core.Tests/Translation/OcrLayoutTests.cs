using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class OcrLayoutTests
{
    [Fact]
    public void Vertical_Columns_Keep_Context_And_Separate_Bubbles_Read_Right_To_Left()
    {
        TextRegion[] regions = [new(new(660, 30, 24, 200), .9), new(new(625, 35, 24, 170), .9),
            new(new(200, 35, 24, 190), .9), new(new(660, 400, 24, 180), .9)];
        var grouped = TextRegionGrouper.GroupDetailed(regions, new(800, 600));
        Assert.Equal(3, grouped.Count);
        Assert.All(grouped, group => Assert.True(group.IsVertical));
        Assert.Equal(2, grouped[0].TextBounds.Count);
        Assert.Equal(660, grouped[0].TextBounds[0].X);
        Assert.Equal(625, grouped[0].TextBounds[1].X);
        Assert.Equal(200, grouped[1].TextBounds[0].X);
        Assert.Equal(400, grouped[2].TextBounds[0].Y);
    }

    [Fact]
    public void Individually_Detected_Vertical_Characters_Are_Not_Reflowed_Into_Horizontal_Lines()
    {
        var boxes = from x in new[] { 160, 130 } from y in new[] { 30, 58, 86, 114 }
                    select new TextRegion(new PixelRect(x, y, 20, 20), .9);
        var group = Assert.Single(TextRegionGrouper.GroupDetailed(boxes, new(300, 300)));
        Assert.True(group.IsVertical);
        Assert.Equal(8, group.TextBounds.Count);
        Assert.Equal(new[] { 160, 160, 160, 160, 130, 130, 130, 130 }, group.TextBounds.Select(box => box.X));
    }

    [Fact]
    public void Contained_Ruby_Text_Is_Not_Removed_As_A_Tile_Duplicate()
    {
        TextRegion[] regions = [new(new(100, 50, 100, 240), .9), new(new(105, 55, 98, 238), .8), new(new(160, 60, 12, 120), .8)];
        Assert.Equal(2, TextRegionGrouper.Deduplicate(regions).Count);
    }

    [Fact]
    public void LongVerticalColumnAndPartiallyOverlappingTileFragmentsAreRecognizedOnce()
    {
        TextRegion[] regions = [new(new(600, 50, 32, 2900), .9), new(new(601, 50, 31, 970), .95),
            new(new(600, 900, 32, 1010), .94), new(new(600, 1800, 32, 1150), .93),
            new(new(555, 55, 32, 2800), .9)];
        var group = Assert.Single(TextRegionGrouper.GroupDetailed(regions, new(800, 3200)));
        Assert.Equal(2, group.TextBounds.Count);
        Assert.Equal(new PixelRect(600, 50, 32, 2900), group.TextBounds[0]);
        Assert.Equal(555, group.TextBounds[1].X);
    }

    [Fact]
    public void TileSeamFragmentsJoinTransitivelyWithoutMergingSeparateBubbles()
    {
        TextRegion[] regions = [new(new(300, 50, 30, 1000), .9), new(new(301, 950, 29, 1000), .9),
            new(new(300, 1850, 30, 1000), .9), new(new(300, 3000, 30, 120), .9)];
        var groups = TextRegionGrouper.GroupDetailed(regions, new(600, 3300));
        Assert.Equal(2, groups.Count);
        Assert.Equal(new PixelRect(300, 50, 30, 2800), Assert.Single(groups[0].TextBounds));
    }

    [Fact]
    public void HighConfidencePartialColumnNeverDropsTheRestOfItsLongerDetection()
    {
        TextRegion[] regions = [new(new(300, 50, 30, 2200), .98), new(new(300, 50, 30, 2900), .85)];
        Assert.Equal(new PixelRect(300, 50, 30, 2900), Assert.Single(TextRegionGrouper.Deduplicate(regions)).Bounds);
    }

    [Fact]
    public void Full_Page_Model_Order_Replaces_Geometry_Only_When_All_Crops_Are_Aligned()
    {
        OcrBlock[] crops = [new("p1", "第二段", 1, new(10, 10, 200, 40), 0), new("p2", "第一段", 1, new(10, 100, 200, 40), 1)];
        var ordered = OcrReadingOrder.Align("# 第一段\n\n第二段", crops);
        Assert.Equal(new[] { "p2", "p1" }, ordered.Select(block => block.Id));
        Assert.Equal(new[] { 0, 1 }, ordered.Select(block => block.ReadingOrder));
        Assert.Equal(crops[1].BoundsInCapturePixels, ordered[0].BoundsInCapturePixels);
        Assert.Equal(crops, OcrReadingOrder.Align("只有第一段", crops));
    }

    [Fact]
    public void Stream_Order_And_Highlight_Use_Input_Ids_Not_Model_Emission_Order()
    {
        ImageAtlasRegion[] regions = [new("p1", new(10, 20, 200, 80), default) { TextBounds = [new(16, 26, 188, 20), new(16, 60, 188, 20)] },
            new("p2", new(400, 20, 50, 200), default)];
        const string json = """{"blocks":[{"id":"p2","sourceText":"B","translation":"乙"},{"id":"p1","sourceText":"A","translation":"甲"},{"id":"p3","translation":"下""";
        var blocks = ImageTranslationStreamParser.ReadCompletedAtlasBlocks(json, regions);
        Assert.Equal(new[] { "p1", "p2" }, blocks.Select(block => block.Id));
        Assert.Equal(regions[0].TextBounds, blocks[0].TextBounds);
        Assert.Equal("下", ImageTranslationStreamParser.ReadTranslationTail(json));
    }

    [Fact]
    public async Task Ocr_Translation_Needs_No_Source_Text_Or_Coordinates_From_Llm()
    {
        var request = new TranslationRequest("auto", "zh-CN", TranslationStyle.Natural, "",
            [new OcrBlock("p1", "Hello", 1, new(100, 200, 300, 30), 0)])
        { IsOcrDocument = true, AtlasRegions = [new("p1", new(100, 200, 300, 30), default) { TextBounds = [new(106, 206, 288, 18)] }] };
        var result = await new TranslationOrchestrator(new Provider(), new TranslationResponseValidator()).TranslateAsync(request, CancellationToken.None);
        var block = Assert.Single(result.Blocks);
        Assert.Equal("Hello", block.SourceText);
        Assert.Equal(request.AtlasRegions[0].TextBounds, block.TextBounds);
        Assert.Equal("你好", block.Translation);
    }

    private sealed class Provider : ScreenTranslator.Core.Abstractions.ITranslationProvider
    {
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("""{"blocks":[{"id":"p1","translation":"你好","bbox":[0,0,1,1]}]}""");
    }
}
