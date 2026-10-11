using ScreenTranslator.App.Services.Overlays;

namespace ScreenTranslator.IntegrationTests.Overlays;

public sealed class OverlayVisibilityStateTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, false, true, false)]
    public void ShouldShow_Composes_All_Visibility_Inputs(
        bool userVisible,
        bool sourceWindowActive,
        bool trackingVisible,
        bool contextMenuOpen,
        bool expected)
    {
        var state = new OverlayVisibilityState
        {
            UserVisible = userVisible,
            SourceWindowActive = sourceWindowActive,
            TrackingVisible = trackingVisible,
            ContextMenuOpen = contextMenuOpen,
        };

        Assert.Equal(expected, state.ShouldShow);
    }
}
