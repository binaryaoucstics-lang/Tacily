using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class TranslationModelListSettingsTests
{
    private static TranslationSettingsViewModel Ready()
    {
        var vm = new TranslationSettingsViewModel(); vm.AddPresetCommand.Execute(null);
        vm.BaseUrl = "https://service.example/api/v1"; vm.ApplySavedApiKey("synthetic-key-1234"); return vm;
    }
    [Fact]
    public async Task ReadsUsingTheCurrentPresetAndLeavesSavedKeyResolutionToTheController()
    {
        var vm = Ready(); vm.SelectedModel = "existing-model";
        TranslationModelListRequest? observed = null;
        vm.ModelListLoader = (request, _) => { observed = request; return Task.FromResult(new TranslationModelList(["model-a", "model-b"])); };
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("", observed!.ApiKey); Assert.Equal(vm.ActiveSecretName, observed.SecretName);
        Assert.Equal(vm.BaseUrl, observed.BaseUrl); Assert.Equal(vm.ProviderKind, observed.Provider);
        Assert.Equal(["model-a", "model-b"], vm.Models); Assert.Equal("existing-model", vm.SelectedModel);
        Assert.True(vm.IsModelListOpen); Assert.Contains("2", vm.ModelListStatus); Assert.False(vm.IsLoadingModels);
        vm.SelectedModel = "model-b";
        Assert.Equal("model-b", TranslationProviderConfiguration.Model(vm.CollectProviderSettings(new AppSettings())));
    }
    [Fact]
    public async Task UsesAnUnsavedDraftKeyWithoutSavingItOrSendingAModel()
    {
        var vm = Ready(); vm.BeginApiKeyEdit(); vm.ApiKey = "synthetic-draft-key";
        vm.ModelListLoader = (request, _) => { Assert.Equal("synthetic-draft-key", request.ApiKey); Assert.DoesNotContain("synthetic", request.ToString()); return Task.FromResult(new TranslationModelList(["new-model"])); };
        await vm.LoadModelsCommand.ExecuteAsync(null); Assert.Equal("synthetic-draft-key", vm.ApiKey);
    }
    [Theory]
    [InlineData("preset")]
    [InlineData("url")]
    [InlineData("protocol")]
    [InlineData("key")]
    public async Task ChangesCancelOldRequestsAndDiscardTheirLateLists(string change)
    {
        var vm = Ready(); var completion = new TaskCompletionSource<TranslationModelList>(); CancellationToken old = default;
        vm.ModelListLoader = (_, token) => { old = token; return completion.Task; };
        var loading = vm.LoadModelsCommand.ExecuteAsync(null); Assert.True(vm.IsLoadingModels);
        switch (change) { case "preset": vm.AddPresetCommand.Execute(null); break; case "url": vm.BaseUrl = "https://other.example/v1"; break;
            case "protocol": vm.SelectedProtocol = "Anthropic Messages"; break; case "key": vm.ApiKey = "new-key"; break; }
        Assert.True(old.IsCancellationRequested); completion.SetResult(new(["wrong-service-model"])); await loading;
        Assert.DoesNotContain("wrong-service-model", vm.Models); Assert.False(vm.IsModelListOpen); Assert.DoesNotContain("已读取", vm.ModelListStatus);
    }
    [Fact]
    public async Task EditingTheModelDuringLoadingKeepsTheNewChoiceWhenTheListArrives()
    {
        var vm = Ready(); var completion = new TaskCompletionSource<TranslationModelList>(); vm.ModelListLoader = (_, _) => completion.Task;
        var loading = vm.LoadModelsCommand.ExecuteAsync(null); vm.SelectedModel = "typed-while-loading";
        completion.SetResult(new(["available-model"])); await loading; Assert.Equal("typed-while-loading", vm.SelectedModel);
    }
    [Fact]
    public async Task FailedAndEmptyCatalogsKeepManualModelAndDoNotExposeUnexpectedExceptions()
    {
        var vm = Ready(); vm.SelectedModel = "manual-model"; vm.ModelListLoader = (_, _) => throw new InvalidOperationException("synthetic-private-key");
        await vm.LoadModelsCommand.ExecuteAsync(null); Assert.DoesNotContain("synthetic", vm.ModelListStatus); Assert.Equal("manual-model", vm.SelectedModel);
        vm.ModelListLoader = (_, _) => Task.FromResult(new TranslationModelList([])); await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Contains("手动", vm.ModelListStatus); Assert.Equal("manual-model", vm.SelectedModel);
    }
    [Fact]
    public async Task CancelButtonAndSavingRetirePendingListsAndReenableTheReadButton()
    {
        var vm = Ready(); var completion = new TaskCompletionSource<TranslationModelList>(); vm.ModelListLoader = (_, _) => completion.Task;
        var loading = vm.LoadModelsCommand.ExecuteAsync(null); Assert.False(vm.LoadModelsCommand.CanExecute(null));
        vm.CancelLoadModelsCommand.Execute(null); completion.SetResult(new(["ignored-model"])); await loading;
        Assert.Contains("已取消", vm.ModelListStatus); Assert.True(vm.LoadModelsCommand.CanExecute(null));
        completion = new(); loading = vm.LoadModelsCommand.ExecuteAsync(null); vm.SetSaving(true); completion.SetResult(new(["ignored-model"])); await loading;
        Assert.DoesNotContain("ignored-model", vm.Models); Assert.False(vm.LoadModelsCommand.CanExecute(null)); vm.SetSaving(false); Assert.True(vm.LoadModelsCommand.CanExecute(null));
    }
    [Fact]
    public void OfficialFixedModelHasNoReadButtonAndLocalModelsDoNotRequireAKey()
    {
        var vm = new TranslationSettingsViewModel { ModelListLoader = (_, _) => Task.FromResult(new TranslationModelList([])) };
        Assert.False(vm.SupportsModelListing); Assert.False(vm.LoadModelsCommand.CanExecute(null));
        vm.AddPresetCommand.Execute(null); Assert.False(vm.LoadModelsCommand.CanExecute(null));
        vm.BaseUrl = "http://localhost:1234/v1"; Assert.True(vm.LoadModelsCommand.CanExecute(null));
    }
}
