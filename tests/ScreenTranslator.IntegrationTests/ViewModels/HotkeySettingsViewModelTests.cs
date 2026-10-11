using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class HotkeySettingsViewModelTests
{
    [Fact]
    public void Disabled_Global_Hotkey_Can_Still_Start_Recording()
    {
        var viewModel = new HotkeySettingsViewModel
        {
            IsEnabled = false,
        };

        Assert.True(viewModel.BeginRecordingCommand.CanExecute(null));

        viewModel.BeginRecordingCommand.Execute(null);

        Assert.True(viewModel.IsRecording);
        Assert.Equal("正在录制…", viewModel.RecordButtonText);
    }

    [Fact]
    public void Repeated_Start_Does_Not_Restart_An_Active_Recording()
    {
        var viewModel = new HotkeySettingsViewModel();
        var startedCount = 0;
        viewModel.RecordingStarted += (_, _) => startedCount++;

        viewModel.BeginRecordingCommand.Execute(null);
        viewModel.BeginRecordingCommand.Execute(null);

        Assert.Equal(1, startedCount);
        Assert.True(viewModel.IsRecording);
        Assert.True(viewModel.CancelRecordingCommand.CanExecute(null));

        viewModel.CancelRecordingCommand.Execute(null);

        Assert.False(viewModel.IsRecording);
    }
}
