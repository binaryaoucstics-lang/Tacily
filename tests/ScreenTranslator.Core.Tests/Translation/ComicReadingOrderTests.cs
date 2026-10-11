using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class ComicReadingOrderTests
{
    [Fact]
    public void Reads_Right_Panel_With_Its_Caption_Before_Left_Panel_And_Bottom_Signature_Last()
    {
        PixelRect[] panels = [new(100, 100, 280, 300), new(400, 100, 280, 300), new(100, 430, 580, 200), new(100, 660, 580, 300)];
        TextRegionGroup[] groups = [Group(250, 120), Group(550, 120), Group(440, 350, false),
            Group(550, 450), Group(200, 470), Group(580, 680), Group(400, 800), Group(150, 720)];
        var ordered = ComicReadingOrder.Sort(groups, panels);
        Assert.Equal(new[] { 550, 440, 250, 550, 200, 580, 400, 150 }, ordered.Select(group => group.CropBounds.X));
    }

    [Fact]
    public void Unclip_Overlap_Does_Not_Split_The_Middle_Vertical_Column_Out_Of_A_Bubble()
    {
        TextRegion[] regions = [new(new(684, 159, 43, 124), .8), new(new(656, 158, 46, 159), .8), new(new(627, 159, 49, 193), .8)];
        var group = Assert.Single(TextRegionGrouper.GroupDetailed(regions, new(850, 1200)));
        Assert.Equal(new[] { 684, 656, 627 }, group.TextBounds.Select(bounds => bounds.X));
    }

    private static TextRegionGroup Group(int x, int y, bool vertical = true) => new(new(x, y, 50, 80), [new(x, y, 50, 80)], vertical);
}
