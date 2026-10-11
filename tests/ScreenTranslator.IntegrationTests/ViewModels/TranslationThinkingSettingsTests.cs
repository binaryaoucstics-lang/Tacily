using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class TranslationThinkingSettingsTests
{
    [Fact]
    public void Nonreasoning_OpenAi_Model_Explains_That_No_Reasoning_Parameter_Is_Sent()
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = "gpt-4.1" };
        Assert.Equal(["关闭"], vm.ThinkingLevels);
        Assert.Contains("不支持独立思考", vm.ThinkingRecommendation);
        Assert.Contains("不发送 reasoning", vm.ThinkingDescription);
        Assert.DoesNotContain("reasoning.effort=none", vm.ThinkingDescription);
    }
    private static readonly ChatGptAccount AccountA = new("thinking-account-a", "Synthetic A", true, true, false);
    private static readonly ChatGptAccount AccountB = new("thinking-account-b", "Synthetic B", true, true, false);
    private static TranslationModelList Catalog(string id, IReadOnlyList<ThinkingLevel>? levels, ThinkingLevel? defaultLevel = null)
        => new([id]) { Choices = [new(id, id) { SupportedReasoningLevels = levels, DefaultReasoningLevel = defaultLevel }] };

    private static async Task<TranslationSettingsViewModel> OAuth(string model)
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = model,
            SelectedAuthentication = "ChatGPT 登录（使用订阅额度）" };
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA, AccountB]);
        await vm.RefreshChatGptAccountsAsync();
        vm.SelectedChatGptAccount = AccountA;
        return vm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sol61_Offers_All_Five_Levels_Without_Off_In_Both_Authentication_Modes(bool oauth)
    {
        var vm = oauth ? await OAuth("gpt-6.1-sol") : new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = "gpt-6.1-sol" };
        Assert.Equal(["低", "中", "高", "超高", "最高"], vm.ThinkingLevels);
        Assert.Equal("低", vm.ThinkingLevel);
        vm.ThinkingLevel = "中";
        Assert.Contains("medium", vm.ThinkingDescription);
        vm.ThinkingLevel = "超高";
        Assert.Contains("xhigh", vm.ThinkingDescription);
        Assert.DoesNotContain("建议关闭", vm.ThinkingRecommendation);
        Assert.False(vm.ShowDeepSeekThinking);
        Assert.False(vm.CanBoundLowThinkingWait);
    }

    [Theory]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    public async Task ApiKey_Offers_Off_And_OAuth_Uses_The_Current_Model_Metadata(string model)
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = model };
        Assert.Contains("关闭", vm.ThinkingLevels);
        vm = await OAuth(model);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(model, [ThinkingLevel.Medium, ThinkingLevel.XHigh], ThinkingLevel.Medium));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal(["中", "超高"], vm.ThinkingLevels);
        Assert.Equal("中", vm.ThinkingLevel);
        vm.SelectedAuthentication = "API Key";
        Assert.Contains("关闭", vm.ThinkingLevels);
        Assert.Equal("中", vm.ThinkingLevel);
        vm.SelectedProvider = "DeepSeek";
        Assert.Equal(["关闭", "低", "高", "最高"], vm.ThinkingLevels);
        Assert.True(vm.ShowDeepSeekThinking);
        vm.ThinkingLevel = "低";
        Assert.True(vm.CanBoundLowThinkingWait);
    }

    [Fact]
    public async Task Null_Capabilities_Fall_Back_But_Explicit_Empty_And_Unknown_Models_Use_Server_Default()
    {
        var vm = await OAuth("gpt-6.1-sol");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog("gpt-6.1-sol", null));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Contains("中", vm.ThinkingLevels);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog("gpt-6.1-sol", []));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal(["服务端默认"], vm.ThinkingLevels);
        Assert.Equal("服务端默认", vm.ThinkingLevel);
        Assert.Contains("不发送", vm.ThinkingDescription);
        vm.SelectedAuthentication = "API Key";
        vm.SelectedModel = "unknown-future-model";
        Assert.Equal(["服务端默认"], vm.ThinkingLevels);
        Assert.Contains("服务端默认", vm.ThinkingRecommendation);
    }

    [Fact]
    public async Task Reload_Preserves_A_Valid_User_Choice_And_Default_Is_Only_A_Fallback()
    {
        var vm = await OAuth("custom-account-model");
        var levels = new[] { ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh };
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, levels, ThinkingLevel.High));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("高", vm.ThinkingLevel);
        vm.ThinkingLevel = "超高";
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("超高", vm.ThinkingLevel);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium, ThinkingLevel.High], ThinkingLevel.Medium));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("中", vm.ThinkingLevel);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Low, ThinkingLevel.High], ThinkingLevel.Max));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("低", vm.ThinkingLevel); // Invalid server default falls back through the shared normalizer.
    }

    [Fact]
    public async Task Saving_During_Reload_Keeps_The_Current_Valid_Account_Choice_For_The_Controller_Codec()
    {
        var vm = await OAuth("custom-account-model");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium, ThinkingLevel.XHigh]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        vm.ThinkingLevel = "超高";
        var completion = new TaskCompletionSource<TranslationModelList>();
        vm.ModelListLoader = (_, _) => completion.Task;
        var loading = vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("超高", vm.ThinkingLevel);
        vm.SetSaving(true);
        Assert.Equal(ThinkingLevel.XHigh, ThinkingLevelNames.FromLabel(vm.ThinkingLevel));
        Assert.Equal(ThinkingLevel.XHigh, vm.CollectProviderSettings(new()).ThinkingLevel);
        completion.SetResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium]));
        await loading;
        Assert.Equal("超高", vm.ThinkingLevel);
        vm.SetSaving(false);
    }

    [Fact]
    public async Task Selecting_Another_Slug_Refreshes_Its_Capabilities_And_Description()
    {
        var vm = await OAuth("first");
        vm.ModelListLoader = (_, _) => Task.FromResult(new TranslationModelList(["first", "second"])
        { Choices = [new("first", "First") { SupportedReasoningLevels = [ThinkingLevel.Medium] },
            new("second", "Second") { SupportedReasoningLevels = [ThinkingLevel.XHigh] }] });
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal(["中"], vm.ThinkingLevels);
        vm.SelectedModel = "second";
        Assert.Equal(["超高"], vm.ThinkingLevels);
        Assert.Contains("second", vm.ThinkingDescription);
        Assert.Contains("xhigh", vm.ThinkingDescription);
    }

    [Fact]
    public async Task Account_Switch_Discards_Late_Capabilities_And_Uses_Only_The_New_Account()
    {
        var vm = await OAuth("shared-slug");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog("shared-slug", [ThinkingLevel.Medium]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        var oldCompletion = new TaskCompletionSource<TranslationModelList>();
        var newCompletion = new TaskCompletionSource<TranslationModelList>();
        CancellationToken oldToken = default;
        vm.ModelListLoader = (request, token) =>
        {
            if (request.ChatGptAccountId == AccountB.Id) return newCompletion.Task;
            oldToken = token; return oldCompletion.Task;
        };
        var loading = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.SelectedChatGptAccount = AccountB;
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(["服务端默认"], vm.ThinkingLevels);
        oldCompletion.SetResult(Catalog("shared-slug", [ThinkingLevel.Max]));
        // Completing the current queued request also lets the old ExecuteAsync finish deterministically.
        newCompletion.SetResult(Catalog("shared-slug", [ThinkingLevel.XHigh]));
        await loading;
        Assert.Equal(["超高"], vm.ThinkingLevels);
        Assert.Equal("超高", vm.ThinkingLevel);
        Assert.Equal(AccountB.Id, vm.ChatGptAccountId);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("authentication")]
    [InlineData("preset")]
    public async Task Context_Changes_Clear_Capabilities_And_Reject_The_Previous_Context(string change)
    {
        var vm = await OAuth("custom-account-model");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        var completion = new TaskCompletionSource<TranslationModelList>();
        CancellationToken oldToken = default;
        vm.ModelListLoader = (_, token) => { oldToken = token; return completion.Task; };
        var loading = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.ModelListLoader = null;
        switch (change)
        {
            case "provider": vm.SelectedProvider = "DeepSeek"; break;
            case "authentication": vm.SelectedAuthentication = "API Key"; break;
            case "preset": vm.CopyPresetCommand.Execute(null); break;
        }
        Assert.True(oldToken.IsCancellationRequested);
        completion.SetResult(Catalog("stale-context-model", [ThinkingLevel.Max]));
        await loading;
        Assert.Empty(vm.ChatGptModels);
        Assert.DoesNotContain("stale-context-model", vm.Models);
        if (change == "provider") Assert.True(vm.ShowDeepSeekThinking);
        else Assert.Equal(["服务端默认"], vm.ThinkingLevels);
    }

    [Fact]
    public async Task Changing_Model_During_Load_Uses_The_New_Selected_Slugs_Capabilities()
    {
        var vm = await OAuth("first");
        var completion = new TaskCompletionSource<TranslationModelList>();
        vm.ModelListLoader = (_, _) => completion.Task;
        var loading = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.SelectedModel = "second";
        completion.SetResult(new(["first", "second"])
        { Choices = [new("first", "First") { SupportedReasoningLevels = [ThinkingLevel.Medium] },
            new("second", "Second") { SupportedReasoningLevels = [ThinkingLevel.XHigh] }] });
        await loading;
        Assert.Equal("second", vm.SelectedModel);
        Assert.Equal(["超高"], vm.ThinkingLevels);
    }

    [Fact]
    public async Task Late_Account_Snapshot_Cannot_Restore_Revoked_Account_Capabilities()
    {
        var vm = await OAuth("custom-account-model");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.XHigh]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        var completion = new TaskCompletionSource<IReadOnlyList<ChatGptAccount>>();
        vm.ChatGptAccountsLoader = _ => completion.Task;
        var oldRefresh = vm.RefreshChatGptAccountsAsync();
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA with { IsConnected = false }]);
        await vm.RefreshChatGptAccountsAsync();
        completion.SetResult([AccountA]);
        await oldRefresh;
        Assert.False(vm.CanUseChatGptPlan);
        Assert.Empty(vm.ChatGptModels);
        Assert.Equal("服务端默认", vm.ThinkingLevel);
    }

    [Fact]
    public async Task Cancellation_And_Account_Revocation_Clear_Capabilities_And_Reject_Late_Responses()
    {
        var vm = await OAuth("custom-account-model");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.XHigh]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        var completion = new TaskCompletionSource<TranslationModelList>();
        vm.ModelListLoader = (_, _) => completion.Task;
        var loading = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.CancelLoadModelsCommand.Execute(null);
        completion.SetResult(Catalog(vm.SelectedModel, [ThinkingLevel.Max]));
        await loading;
        Assert.Empty(vm.ChatGptModels);
        Assert.Equal(["服务端默认"], vm.ThinkingLevels);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.XHigh]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("超高", vm.ThinkingLevel);
        completion = new();
        vm.ModelListLoader = (_, _) => completion.Task;
        loading = vm.LoadModelsCommand.ExecuteAsync(null);
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([AccountA with { IsConnected = false }]);
        await vm.RefreshChatGptAccountsAsync();
        completion.SetResult(Catalog(vm.SelectedModel, [ThinkingLevel.Max]));
        await loading;
        Assert.Empty(vm.ChatGptModels);
        Assert.Equal("服务端默认", vm.ThinkingLevel);
        Assert.False(vm.CanUseChatGptPlan);
    }

    [Theory]
    [InlineData(ThinkingLevel.Medium, "中", "medium")]
    [InlineData(ThinkingLevel.XHigh, "超高", "xhigh")]
    public async Task Default_And_Preset_Load_Copy_Save_And_Benchmark_Preserve_The_Codec(ThinkingLevel level, string label, string effort)
    {
        var settings = new AppSettings { TranslationProvider = TranslationProviderKind.OpenAI, OpenAiModel = "gpt-6.1-sol", ThinkingLevel = level };
        var vm = new TranslationSettingsViewModel();
        vm.ApplyProviderSettings(settings);
        Assert.False(vm.IsSwitchingProvider);
        Assert.Equal(label, vm.ThinkingLevel);
        Assert.Equal(level, vm.CollectProviderSettings(settings).ThinkingLevel);
        vm.ApplySavedApiKey("synthetic-thinking-test-key");
        TranslationConnectionTestRequest? request = null;
        vm.ConnectionTester = (observed, _) => { request = observed; return Task.FromResult(new ConnectionTestResult(true, "ok")); };
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(level, request!.ThinkingLevel);
        Assert.Contains(effort, vm.ThinkingDescription);
        vm.CopyPresetCommand.Execute(null);
        var first = vm.SelectedPreset!;
        Assert.Equal(level, first.Config.ThinkingLevel);
        vm.CopyPresetCommand.Execute(null);
        var copy = vm.SelectedPreset!;
        Assert.Equal(level, copy.Config.ThinkingLevel);
        vm.ApplySavedApiKey("synthetic-preset-thinking-key");
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(level, request!.ThinkingLevel);
        vm.ThinkingLevel = "高";
        vm.SelectedPreset = first;
        Assert.Equal(label, vm.ThinkingLevel);
        var saved = vm.CollectProviderSettings(settings);
        var restored = new TranslationSettingsViewModel();
        restored.ApplyProviderSettings(saved);
        Assert.Equal(label, restored.ThinkingLevel);
        Assert.Equal(level, restored.SelectedPreset!.Config.ThinkingLevel);
        restored.SelectedPreset = restored.Presets.Single(p => p.Id == copy.Id);
        Assert.Equal("高", restored.ThinkingLevel);
        vm.DefaultPresetCommand.Execute(null);
        Assert.Equal(label, vm.ThinkingLevel);
    }

    [Fact]
    public async Task OAuth_Custom_Presets_Keep_Their_Choice_Until_Their_Account_Metadata_Arrives()
    {
        var vm = await OAuth("custom-account-model");
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium, ThinkingLevel.XHigh]));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        vm.ThinkingLevel = "超高";
        vm.ModelListLoader = null;
        vm.CopyPresetCommand.Execute(null);
        var first = vm.SelectedPreset!;
        vm.CopyPresetCommand.Execute(null);
        vm.SelectedPreset = first;
        Assert.Equal(ThinkingLevel.XHigh, first.Config.ThinkingLevel);
        Assert.Equal(ThinkingLevel.XHigh, vm.CollectProviderSettings(new()).TranslationProviderPresets.Single(p => p.Id == first.Id).ThinkingLevel);
        vm.ModelListLoader = (_, _) => Task.FromResult(Catalog(vm.SelectedModel, [ThinkingLevel.Medium, ThinkingLevel.XHigh], ThinkingLevel.Medium));
        await vm.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal("超高", vm.ThinkingLevel);
    }

    [Fact]
    public void Capability_Changes_Notify_All_Dynamic_Thinking_Properties()
    {
        var vm = new TranslationSettingsViewModel();
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SelectedProvider = "OpenAI";
        vm.SelectedModel = "gpt-6.1-sol";
        foreach (var name in new[] { nameof(vm.ThinkingLevel), nameof(vm.ThinkingLevels), nameof(vm.ThinkingDescription),
            nameof(vm.ThinkingRecommendation), nameof(vm.ShowDeepSeekThinking), nameof(vm.CanBoundLowThinkingWait) })
            Assert.Contains(name, changed);
    }
}
