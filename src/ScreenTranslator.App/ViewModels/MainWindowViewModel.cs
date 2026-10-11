using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScreenTranslator.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private string _statusText = "准备就绪";
    private bool _isCaptureAvailable = true;

    public MainWindowViewModel()
    {
        StartCaptureCommand = new RelayCommand(
            () => StartCaptureRequested?.Invoke(this, EventArgs.Empty),
            () => IsCaptureAvailable);
    }

    public string ProductName => "屏译";

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsCaptureAvailable
    {
        get => _isCaptureAvailable;
        set
        {
            if (SetProperty(ref _isCaptureAvailable, value))
            {
                StartCaptureCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public RelayCommand StartCaptureCommand { get; }

    public event EventHandler? StartCaptureRequested;
}
