using System.Windows;
using System.Windows.Input;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Windows;

public partial class ContinuousSidePanelWindow : Window
{
    private readonly ContinuousResultsViewModel _viewModel;

    public ContinuousSidePanelWindow(
        ContinuousResultsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CloseRequested += OnCloseRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void Header_OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
