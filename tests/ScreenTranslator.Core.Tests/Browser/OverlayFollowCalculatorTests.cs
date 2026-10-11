using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Browser;

public sealed class OverlayFollowCalculatorTests
{
    [Theory]
    [InlineData(100, 1.25, 1.25, 100)]
    [InlineData(100, 1.50, 1.25, 120)]
    [InlineData(-80, 1.00, 2.00, -40)]
    public void Css_Delta_Uses_Device_And_Monitor_Scale(
        double css,
        double dpr,
        double monitorScale,
        double expectedDip) =>
        Assert.Equal(
            expectedDip,
            OverlayFollowCalculator.ToDip(css, dpr, monitorScale),
            3);

    [Theory]
    [InlineData(double.NaN, 1, 1)]
    [InlineData(double.PositiveInfinity, 1, 1)]
    [InlineData(1, 0.49, 1)]
    [InlineData(1, 8.01, 1)]
    [InlineData(1, 1, 0)]
    public void Css_Delta_Rejects_NonFinite_Or_OutOfRange_Values(
        double css,
        double dpr,
        double monitorScale) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayFollowCalculator.ToDip(css, dpr, monitorScale));

    [Fact]
    public void Root_Scroll_Moves_Block_And_Hides_It_Outside_Selection()
    {
        var update = OverlayFollowCalculator.ApplyRootScroll(
            new DipRect(100, 100, 200, 40),
            selection: new DipRect(80, 80, 260, 180),
            deltaXDip: 0,
            deltaYDip: 240);

        Assert.Equal(new DipRect(100, -140, 200, 40), update.Bounds);
        Assert.False(update.IsVisible);
        Assert.Null(update.VisibleBounds);
        Assert.True(update.WasMoved);
    }

    [Fact]
    public void Root_Scroll_Clips_Partially_Visible_Block_To_Selection()
    {
        var update = OverlayFollowCalculator.ApplyRootScroll(
            new DipRect(100, 100, 200, 40),
            selection: new DipRect(80, 80, 260, 180),
            deltaXDip: 0,
            deltaYDip: 35);

        Assert.True(update.IsVisible);
        Assert.Equal(new DipRect(100, 80, 200, 25), update.VisibleBounds);
    }

    [Fact]
    public void Nested_Scroll_Moves_Only_Block_Whose_Center_Is_In_Container()
    {
        var selection = new DipRect(0, 0, 500, 500);
        var container = new DipRect(100, 100, 200, 200);

        var inside = OverlayFollowCalculator.ApplyNestedScroll(
            new DipRect(120, 120, 80, 40),
            selection,
            container,
            deltaXDip: 0,
            deltaYDip: 25);
        var outside = OverlayFollowCalculator.ApplyNestedScroll(
            new DipRect(10, 10, 80, 40),
            selection,
            container,
            deltaXDip: 0,
            deltaYDip: 25);

        Assert.True(inside.WasMoved);
        Assert.Equal(95, inside.Bounds.Y);
        Assert.False(outside.WasMoved);
        Assert.Equal(10, outside.Bounds.Y);
    }

    [Fact]
    public void Nested_Scroll_Clips_To_Selection_And_Container()
    {
        var update = OverlayFollowCalculator.ApplyNestedScroll(
            new DipRect(110, 110, 100, 40),
            selection: new DipRect(80, 80, 100, 200),
            scrollContainer: new DipRect(100, 100, 200, 200),
            deltaXDip: 0,
            deltaYDip: 20);

        Assert.True(update.IsVisible);
        Assert.Equal(new DipRect(110, 100, 70, 30), update.VisibleBounds);
    }
}
