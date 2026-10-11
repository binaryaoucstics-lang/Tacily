using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class ContinuousResultsViewModelTests
{
    [Fact]
    public void Collection_Updates_Status_And_Clear_Command()
    {
        var viewModel = new ContinuousResultsViewModel();

        viewModel.Results.Add(new TranslationResultViewModel());

        Assert.Equal("已完成 1 项", viewModel.StatusText);
        Assert.True(viewModel.ClearAllCommand.CanExecute(null));
    }

    [Fact]
    public void Clear_Command_Raises_Request()
    {
        var viewModel = new ContinuousResultsViewModel();
        var raised = false;
        viewModel.Results.Add(new TranslationResultViewModel());
        viewModel.ClearAllRequested += (_, _) => raised = true;

        viewModel.ClearAllCommand.Execute(null);

        Assert.True(raised);
    }
}
