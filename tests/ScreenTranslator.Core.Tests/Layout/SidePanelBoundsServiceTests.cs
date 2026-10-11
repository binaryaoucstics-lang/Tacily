using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Tests.Layout;

public sealed class SidePanelBoundsServiceTests
{
    [Fact]
    public void Place_Clamps_Long_Panel_To_Eighty_Percent_Of_Work_Area()
    {
        var bounds = SidePanelBoundsService.Place(
            source: new DipRect(900, 300, 300, 200),
            workArea: new DipRect(0, 0, 1920, 1040),
            desired: new DipSize(392, 2000),
            previous: null);

        Assert.Equal(832, bounds.Height);
        Assert.True(bounds.Y + bounds.Height <= 1040);
    }

    [Fact]
    public void Place_Prefers_Previous_User_Position_And_Size()
    {
        var bounds = SidePanelBoundsService.Place(
            source: new DipRect(900, 300, 300, 200),
            workArea: new DipRect(0, 0, 1920, 1040),
            desired: new DipSize(392, 520),
            previous: new WindowPlacement(40, 60, 480, 640));

        Assert.Equal(new DipRect(40, 60, 480, 640), bounds);
    }

    [Fact]
    public void Place_Clamps_Offscreen_Previous_Placement_After_Monitor_Removal()
    {
        var bounds = SidePanelBoundsService.Place(
            source: new DipRect(400, 300, 200, 100),
            workArea: new DipRect(0, 0, 1280, 720),
            desired: new DipSize(392, 520),
            previous: new WindowPlacement(2400, -800, 420, 600));

        Assert.Equal(860, bounds.X);
        Assert.Equal(0, bounds.Y);
        Assert.Equal(576, bounds.Height);
    }

    [Fact]
    public void Clamp_Keeps_The_Complete_Title_And_Window_In_Work_Area()
    {
        var bounds = SidePanelBoundsService.Clamp(
            new DipRect(-500, 690, 392, 520),
            new DipRect(0, 0, 1280, 720));

        Assert.Equal(0, bounds.X);
        Assert.Equal(200, bounds.Y);
        Assert.True(bounds.X + bounds.Width <= 1280);
        Assert.True(bounds.Y + bounds.Height <= 720);
    }
}
