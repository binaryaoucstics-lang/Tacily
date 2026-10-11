using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class TranslationSettingsViewModelTests
{
    [Fact]
    public void Low_Wait_Limit_Defaults_On_And_Is_Editable_Only_For_Low_Thinking()
    {
        var viewModel = new TranslationSettingsViewModel();
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, change) => changes.Add(change.PropertyName);

        Assert.True(viewModel.BoundedLowThinkingEnabled);
        Assert.False(viewModel.CanBoundLowThinkingWait);
        viewModel.ThinkingLevel = "低";
        viewModel.ThinkingLevel = "低";

        Assert.True(viewModel.CanBoundLowThinkingWait);
        Assert.Equal([nameof(TranslationSettingsViewModel.ThinkingLevel), nameof(TranslationSettingsViewModel.CanBoundLowThinkingWait), nameof(TranslationSettingsViewModel.ThinkingDescription)], changes);

        viewModel.BoundedLowThinkingEnabled = false;
        viewModel.ThinkingLevel = "高";
        Assert.False(viewModel.CanBoundLowThinkingWait);
        Assert.False(viewModel.BoundedLowThinkingEnabled);

        viewModel.ThinkingLevel = "低";
        Assert.True(viewModel.CanBoundLowThinkingWait);
        Assert.False(viewModel.BoundedLowThinkingEnabled);
    }



    [Fact]
    public void ApplySavedApiKey_Keeps_Only_A_Masked_Preview()
    {
        var viewModel = new TranslationSettingsViewModel();

        viewModel.ApplySavedApiKey("sk-1234567890abd4");

        Assert.True(viewModel.HasSavedApiKey);
        Assert.Equal("************abd4", viewModel.SavedApiKeyMask);
        Assert.Equal(string.Empty, viewModel.ApiKey);
        Assert.True(viewModel.ShowSavedApiKey);
        Assert.False(viewModel.ShowApiKeyEditor);
        Assert.True(viewModel.TestConnectionCommand.CanExecute(null));
    }

    [Fact]
    public void Begin_And_Cancel_Edit_Restores_Saved_Preview()
    {
        var viewModel = new TranslationSettingsViewModel();
        viewModel.ApplySavedApiKey("sk-1234567890abd4");

        viewModel.BeginApiKeyEditCommand.Execute(null);
        viewModel.ApiKey = "replacement";

        Assert.True(viewModel.IsEditingApiKey);
        Assert.True(viewModel.ShowApiKeyEditor);
        Assert.False(viewModel.ShowSavedApiKey);

        viewModel.CancelApiKeyEditCommand.Execute(null);

        Assert.False(viewModel.IsEditingApiKey);
        Assert.Equal(string.Empty, viewModel.ApiKey);
        Assert.True(viewModel.ShowSavedApiKey);
    }

    [Fact]
    public void Missing_Saved_Key_Leaves_The_Editor_Visible()
    {
        var viewModel = new TranslationSettingsViewModel();

        viewModel.ApplySavedApiKey(null);

        Assert.False(viewModel.HasSavedApiKey);
        Assert.Equal(string.Empty, viewModel.SavedApiKeyMask);
        Assert.True(viewModel.ShowApiKeyEditor);
        Assert.False(viewModel.TestConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestConnection_Leaves_Stored_Key_Resolution_To_The_Controller()
    {
        var viewModel = new TranslationSettingsViewModel();
        viewModel.ApplySavedApiKey("sk-1234567890abd4");
        TranslationConnectionTestRequest? observedRequest = null;
        viewModel.ConnectionTester = (request, _) =>
        {
            observedRequest = request;
            return Task.FromResult(
                new ConnectionTestResult(true, "连接成功"));
        };

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(observedRequest);
        Assert.Equal(string.Empty, observedRequest.ApiKey);
        Assert.True(viewModel.ConnectionSucceeded);
    }
}
