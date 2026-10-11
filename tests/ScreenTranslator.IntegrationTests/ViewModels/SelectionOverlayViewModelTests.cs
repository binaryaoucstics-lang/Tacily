using System.Windows;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class SelectionOverlayViewModelTests
{
    [Fact]
    public void Mode_Can_Only_Toggle_While_Not_Dragging()
    {
        var state = new CaptureModeState();
        var viewModel = new SelectionOverlayViewModel(state);

        Assert.True(viewModel.TryToggleMode());
        Assert.Equal(CaptureMode.Multiple, state.Mode);

        viewModel.BeginSelection(new Point(10, 10));
        Assert.False(viewModel.TryToggleMode());
        Assert.Equal(CaptureMode.Multiple, state.Mode);
    }
}
