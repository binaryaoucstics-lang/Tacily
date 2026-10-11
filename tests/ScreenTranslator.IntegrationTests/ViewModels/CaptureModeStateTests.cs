using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class CaptureModeStateTests
{
    [Fact]
    public void Defaults_To_Single_And_Toggles_Both_Ways()
    {
        var state = new CaptureModeState();

        Assert.Equal(CaptureMode.Single, state.Mode);
        Assert.Contains("单击自动选段", state.InstructionText);

        state.Toggle();
        Assert.Equal(CaptureMode.Multiple, state.Mode);
        Assert.Contains("多条框选", state.InstructionText);

        state.Toggle();
        Assert.Equal(CaptureMode.Single, state.Mode);
    }

    [Fact]
    public void Two_Selection_ViewModels_Observe_The_Same_Mode()
    {
        var state = new CaptureModeState(CaptureMode.Multiple);
        var first = new SelectionOverlayViewModel(state);
        var second = new SelectionOverlayViewModel(state);

        first.ModeState.Toggle();

        Assert.Equal(CaptureMode.Single, second.ModeState.Mode);
    }
}
