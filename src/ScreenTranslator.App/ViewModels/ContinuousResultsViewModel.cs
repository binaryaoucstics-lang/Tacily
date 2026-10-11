using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScreenTranslator.App.ViewModels;

public sealed class ContinuousResultsViewModel : ObservableObject
{
    private string _statusText = "等待翻译结果…";

    public ContinuousResultsViewModel()
    {
        ClearAllCommand = new RelayCommand(
            () => ClearAllRequested?.Invoke(this, EventArgs.Empty),
            () => Results.Count > 0);
        CloseCommand = new RelayCommand(
            () => CloseRequested?.Invoke(this, EventArgs.Empty));
        Results.CollectionChanged += (_, _) =>
        {
            ClearAllCommand.NotifyCanExecuteChanged();
            StatusText = Results.Count == 0
                ? "等待翻译结果…"
                : $"已完成 {Results.Count} 项";
        };
    }

    public ObservableCollection<TranslationResultViewModel> Results { get; } = [];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public RelayCommand ClearAllCommand { get; }

    public RelayCommand CloseCommand { get; }

    public event EventHandler? ClearAllRequested;

    public event EventHandler? CloseRequested;
}
