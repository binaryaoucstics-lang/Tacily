using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class ChatGptSettingsTests
{
    private static readonly ChatGptAccount AccountA = new(Guid.NewGuid().ToString("N"), "test@example.com · A", true, true, false);
    private static readonly ChatGptAccount AccountB = new(Guid.NewGuid().ToString("N"), "test@example.com · B", true, true, false);
    private static TranslationSettingsViewModel Ready() => new() { SelectedProvider = "OpenAI", SelectedAuthentication = "ChatGPT 登录（使用订阅额度）" };
    private static TranslationModelList Catalog(string id = "eligible-model") => new([id]) { Choices = [new(id, "Eligible model")] };

    [Fact]
    public void OAuth_Mode_Hides_Key_And_Endpoint_And_Never_Uses_An_Existing_Key_As_A_Login()
    {
        var vm = Ready(); vm.ApplySavedApiKey("fake-api-key");
        Assert.True(vm.UsesChatGptPlan); Assert.False(vm.UsesApiKey); Assert.False(vm.CanEditEndpoint);
        Assert.False(vm.TestConnectionCommand.CanExecute(null)); Assert.True(vm.SignInChatGptCommand.CanExecute(null));
        vm.SelectedProvider = "Claude";
        Assert.False(vm.UsesChatGptPlan); Assert.True(vm.UsesApiKey); Assert.False(vm.IsOpenAi);
        Assert.True(vm.CanEditEndpoint);
    }

    [Fact]
    public async Task Sign_In_Loads_Account_Model_Names_And_Benchmark_Uses_The_Selected_Slug()
    {
        var vm = Ready();
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA]);
        ChatGptSignInRequest? auth = null; TranslationModelListRequest? list = null; TranslationConnectionTestRequest? benchmark = null;
        vm.ChatGptSigner = (request, _) => { auth = request; return Task.FromResult(new ChatGptSignInResult(AccountA, false)); };
        vm.ModelListLoader = (request, _) => { list = request; return Task.FromResult(Catalog()); };
        vm.ConnectionTester = (request, _) => { benchmark = request; return Task.FromResult(new ConnectionTestResult(true, "ok")); };
        await vm.SignInChatGptCommand.ExecuteAsync(null);
        Assert.Null(auth!.AccountId); Assert.False(auth.RequestConsent);
        Assert.Equal(AccountA.Id, vm.ChatGptAccountId); Assert.True(vm.CanUseChatGptPlan);
        Assert.True(list!.UseChatGptPlan); Assert.Equal(AccountA.Id, list.ChatGptAccountId);
        Assert.Equal("https://api.openai.com/v1", list.BaseUrl);
        Assert.Equal("Eligible model", Assert.Single(vm.ChatGptModels).DisplayName);
        Assert.Equal("eligible-model", vm.SelectedModel); Assert.True(vm.TestConnectionCommand.CanExecute(null));
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(benchmark!.UseChatGptPlan); Assert.Equal(AccountA.Id, benchmark.ChatGptAccountId);
        Assert.Equal("eligible-model", benchmark.Model); Assert.Empty(benchmark.ApiKey);
    }

    [Fact]
    public async Task Adding_An_Account_Leaves_The_Current_Account_Active_Until_Validated_And_Disables_Editing()
    {
        var vm = Ready(); vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA, AccountB]);
        await vm.RefreshChatGptAccountsAsync(); vm.SelectedChatGptAccount = AccountA;
        var completion = new TaskCompletionSource<ChatGptSignInResult>();
        vm.ChatGptSigner = (request, _) => { Assert.Null(request.AccountId); return completion.Task; };
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog());
        var task = vm.AddChatGptAccountCommand.ExecuteAsync(null);
        Assert.Equal(AccountA.Id, vm.ChatGptAccountId); Assert.True(vm.IsChatGptBusy);
        Assert.False(vm.CanEditProvider); Assert.False(vm.SaveCommand.CanExecute(null));
        vm.AddPresetCommand.Execute(null); Assert.Empty(vm.Presets);
        completion.SetResult(new(AccountB, false)); await task;
        Assert.Equal(AccountB.Id, vm.ChatGptAccountId); Assert.False(vm.IsChatGptBusy); Assert.True(vm.CanEditProvider);
    }

    [Fact]
    public async Task Account_Switch_Cancels_And_Discards_The_Old_Model_List()
    {
        var vm = Ready(); vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA, AccountB]);
        await vm.RefreshChatGptAccountsAsync(); vm.SelectedChatGptAccount = AccountA;
        var completion = new TaskCompletionSource<TranslationModelList>(); CancellationToken previous = default;
        vm.ModelListLoader = (request, token) =>
        { if (request.ChatGptAccountId == AccountB.Id) return Task.FromResult(Catalog("current-account-model")); previous = token; return completion.Task; };
        var task = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.SelectedChatGptAccount = AccountB;
        Assert.True(previous.IsCancellationRequested);
        completion.SetResult(Catalog("wrong-account-model")); await task;
        Assert.Equal("current-account-model", Assert.Single(vm.ChatGptModels).Id); Assert.DoesNotContain("wrong-account-model", vm.Models);
    }

    [Fact]
    public async Task OAuth_Preset_Copies_Account_Metadata_Without_Copying_An_Api_Key_And_Restores_Defaults()
    {
        var vm = Ready(); vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA, AccountB]);
        await vm.RefreshChatGptAccountsAsync(); vm.SelectedChatGptAccount = AccountA; vm.SelectedModel = "default-model";
        vm.CopyPresetCommand.Execute(null);
        Assert.Null(vm.PendingCopySecretName);
        Assert.True(vm.UsesChatGptPlan); Assert.Equal(AccountA.Id, vm.ChatGptAccountId);
        vm.SelectedChatGptAccount = AccountB; vm.SelectedModel = "preset-model";
        var saved = vm.CollectProviderSettings(new());
        Assert.Equal(AccountA.Id, saved.ChatGptAccountId);
        Assert.Equal(AccountB.Id, Assert.Single(saved.TranslationProviderPresets).ChatGptAccountId);
        var restored = new TranslationSettingsViewModel(); restored.ApplyProviderSettings(saved.Migrate(out _));
        Assert.True(restored.UsesChatGptPlan); Assert.Equal(AccountB.Id, restored.ChatGptAccountId);
        vm.DefaultPresetCommand.Execute(null);
        Assert.Equal(AccountA.Id, vm.ChatGptAccountId); Assert.Equal("default-model", vm.SelectedModel);
    }

    [Fact]
    public async Task Missing_Consent_And_Usage_Pause_Have_Separate_Recovery_Actions()
    {
        var vm = Ready(); var account = AccountA with { CanUsePlan = false };
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([account]);
        await vm.RefreshChatGptAccountsAsync(); vm.SelectedChatGptAccount = account;
        Assert.True(vm.ShowChatGptConsent); Assert.False(vm.CanUseChatGptPlan);
        account = AccountA with { UsagePaused = true }; await vm.RefreshChatGptAccountsAsync();
        Assert.False(vm.ShowChatGptConsent); Assert.True(vm.IsChatGptUsagePaused);
        Assert.False(vm.TestConnectionCommand.CanExecute(null)); Assert.True(vm.ResumeChatGptUsageCommand.CanExecute(null));
        string? resumed = null; vm.ChatGptUsageResumer = id => resumed = id;
        vm.ResumeChatGptUsageCommand.Execute(null); Assert.Equal(AccountA.Id, resumed);
    }

    [Fact]
    public void Existing_Settings_Default_To_Api_Key_And_Invalid_Auth_Modes_Are_Normalized()
    {
        var old = new AppSettings { Version = 14, TranslationProvider = TranslationProviderKind.OpenAI }.Migrate(out _);
        Assert.Equal(TranslationAuthenticationMode.ApiKey, old.OpenAiAuthenticationMode);
        Assert.False(TranslationProviderConfiguration.UsesChatGptPlan(old));
        var normalized = (old with { OpenAiAuthenticationMode = (TranslationAuthenticationMode)99,
            TranslationProviderPresets = [new() { Provider = TranslationProviderKind.Claude, AuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth }] }).Migrate(out _);
        Assert.Equal(TranslationAuthenticationMode.ApiKey, normalized.OpenAiAuthenticationMode);
        Assert.Equal(TranslationAuthenticationMode.ApiKey, normalized.TranslationProviderPresets[0].AuthenticationMode);
    }
}
