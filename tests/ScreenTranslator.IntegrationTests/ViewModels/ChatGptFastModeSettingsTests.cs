using System.Text.Json;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class ChatGptFastModeSettingsTests
{
    private static TranslationSettingsViewModel OAuth() => new()
    { SelectedProvider = "OpenAI", SelectedAuthentication = "ChatGPT 登录（使用订阅额度）", SelectedModel = "offline-model" };

    private static async Task<TranslationSettingsViewModel> ReadyAsync()
    {
        var vm = OAuth();
        var account = new ChatGptAccount("synthetic-account", "离线测试账户", true, true, false);
        vm.ChatGptAccountsLoader = _ => Task.FromResult<IReadOnlyList<ChatGptAccount>>([account]);
        await vm.RefreshChatGptAccountsAsync();
        vm.SelectedChatGptAccount = account;
        return vm;
    }

    private static TranslationBenchmarkResult Benchmark(string? requested, string? effective) =>
        new(TranslationBenchmark.SourceSentence, "合成译文", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200),
            40, 32, 0, BenchmarkRateKind.ContentGeneration)
        { RequestedServiceTier = requested, EffectiveServiceTier = effective };

    private static ConnectionTestResult Result(string? requested, string? effective) =>
        new(true, "合成测速完成") { Benchmark = Benchmark(requested, effective) };

    [Fact]
    public void Default_Is_Off_And_Editing_Requires_OAuth_And_Idle_Settings()
    {
        var vm = new TranslationSettingsViewModel();
        Assert.False(vm.ChatGptFastModeEnabled);
        Assert.False(vm.CanEditChatGptFastMode);
        vm.ChatGptFastModeEnabled = true;
        Assert.False(vm.ChatGptFastModeEnabled);
        vm.SelectedProvider = "OpenAI";
        Assert.False(vm.CanEditChatGptFastMode);
        vm.SelectedAuthentication = "ChatGPT 登录（使用订阅额度）";
        Assert.False(vm.ChatGptFastModeEnabled);
        Assert.True(vm.CanEditChatGptFastMode);
        Assert.Contains("额度", vm.ChatGptFastModeDescription);
        Assert.Contains("账户", vm.ChatGptFastModeDescription);
        vm.SetSaving(true);
        Assert.False(vm.CanEditChatGptFastMode);
        vm.ChatGptFastModeEnabled = true;
        Assert.False(vm.ChatGptFastModeEnabled);
        vm.SetSaving(false);
        vm.ChatGptFastModeEnabled = true;
        Assert.True(vm.ChatGptFastModeEnabled);
    }

    [Fact]
    public async Task Sign_In_Disables_Fast_Mode_Editing_Until_Finished()
    {
        var vm = OAuth();
        var account = new ChatGptAccount("synthetic-account", "离线测试账户", true, true, false);
        var pending = new TaskCompletionSource<ChatGptSignInResult>();
        vm.ChatGptSigner = (_, _) => pending.Task;
        var signIn = vm.SignInChatGptCommand.ExecuteAsync(null);
        Assert.False(vm.CanEditChatGptFastMode);
        vm.ChatGptFastModeEnabled = true;
        Assert.False(vm.ChatGptFastModeEnabled);
        pending.SetResult(new(account, false));
        await signIn;
        Assert.True(vm.CanEditChatGptFastMode);
    }

    [Fact]
    public void Save_Copy_Switch_And_Json_Restore_Keep_Independent_Preset_Choices()
    {
        var vm = OAuth();
        vm.ChatGptFastModeEnabled = true;
        AppSettings? saved = null;
        vm.SaveRequested += (_, _) => saved = vm.CollectProviderSettings(new());
        vm.SaveCommand.Execute(null);
        Assert.True(saved!.ChatGptFastModeEnabled);
        vm.CopyPresetCommand.Execute(null);
        var first = vm.SelectedPreset!;
        Assert.True(vm.ChatGptFastModeEnabled);
        Assert.True(Assert.Single(saved!.TranslationProviderPresets).ChatGptFastModeEnabled);
        Assert.Null(vm.PendingCopySecretName);
        vm.ChatGptFastModeEnabled = false;
        vm.SaveCommand.Execute(null);
        Assert.True(saved!.ChatGptFastModeEnabled);
        Assert.False(Assert.Single(saved.TranslationProviderPresets).ChatGptFastModeEnabled);
        vm.CopyPresetCommand.Execute(null);
        var second = vm.SelectedPreset!;
        Assert.False(vm.ChatGptFastModeEnabled);
        vm.ChatGptFastModeEnabled = true;
        vm.SelectedPreset = first;
        Assert.False(vm.ChatGptFastModeEnabled);
        vm.SelectedPreset = second;
        Assert.True(vm.ChatGptFastModeEnabled);
        vm.SelectedPreset = first;
        vm.SaveCommand.Execute(null);
        var serialized = JsonSerializer.Serialize(saved);
        var restored = new TranslationSettingsViewModel();
        restored.ApplyProviderSettings(JsonSerializer.Deserialize<AppSettings>(serialized)!.Migrate(out _));
        Assert.Equal(first.Id, restored.SelectedPreset!.Id);
        Assert.False(restored.ChatGptFastModeEnabled);
        restored.SelectedPreset = restored.Presets.Single(p => p.Id == second.Id);
        Assert.True(restored.ChatGptFastModeEnabled);
        restored.DefaultPresetCommand.Execute(null);
        Assert.True(restored.ChatGptFastModeEnabled);
        Assert.True(restored.UsesChatGptPlan);
        restored.AddPresetCommand.Execute(null);
        Assert.False(restored.ChatGptFastModeEnabled);
        Assert.False(restored.UsesChatGptPlan);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Claude")]
    [InlineData("OpenAI 兼容")]
    [InlineData("DeepSeek")]
    public async Task Api_Key_Benchmarks_Never_Request_Fast_Mode_Even_With_A_Saved_Choice(string provider)
    {
        var vm = OAuth();
        vm.ChatGptFastModeEnabled = true;
        vm.SelectedAuthentication = "API Key";
        vm.SelectedProvider = provider;
        vm.ApplySavedApiKey("synthetic-key");
        TranslationConnectionTestRequest? observed = null;
        vm.ConnectionTester = (request, _) => { observed = request; return Task.FromResult(Result(null, null)); };
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.NotNull(observed);
        Assert.False(observed.UseChatGptPlan);
        Assert.False(observed.ChatGptFastModeEnabled);
        Assert.False(vm.CanEditChatGptFastMode);
        Assert.False(TranslationProviderConfiguration.ChatGptFastModeEnabled(vm.CollectProviderSettings(new())));
        Assert.False(vm.ShowServiceTierNote);
        Assert.Empty(vm.ServiceTierNote);
    }

    [Fact]
    public async Task OAuth_Benchmark_Command_Captures_The_Current_Switch_And_Clears_Completed_Results()
    {
        var vm = await ReadyAsync();
        var observed = new List<TranslationConnectionTestRequest>();
        vm.ConnectionTester = (request, _) =>
        {
            observed.Add(request);
            return Task.FromResult(Result(request.ChatGptFastModeEnabled ? "fast" : null, "default"));
        };
        await vm.TestConnectionCommand.ExecuteAsync(null);
        vm.ChatGptFastModeEnabled = true;
        Assert.Null(vm.ConnectionBenchmark);
        Assert.Empty(vm.ConnectionTranslation);
        Assert.False(vm.ShowServiceTierNote);
        await vm.TestConnectionCommand.ExecuteAsync(null);
        vm.ChatGptFastModeEnabled = false;
        Assert.Null(vm.ConnectionBenchmark);
        Assert.False(vm.ConnectionSucceeded);
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(new[] { false, true, false }, observed.Select(r => r.ChatGptFastModeEnabled));
        Assert.All(observed, request =>
        {
            Assert.True(request.UseChatGptPlan);
            Assert.Equal("synthetic-account", request.ChatGptAccountId);
            Assert.Equal("offline-model", request.Model);
            Assert.Empty(request.ApiKey);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_The_Switch_Cancels_And_Discards_A_Result_From_The_Previous_Mode(bool initial)
    {
        var vm = await ReadyAsync();
        vm.ChatGptFastModeEnabled = initial;
        var pending = new TaskCompletionSource<ConnectionTestResult>();
        CancellationToken previous = default;
        vm.ConnectionTester = (request, token) =>
        {
            Assert.Equal(initial, request.ChatGptFastModeEnabled);
            previous = token;
            return pending.Task;
        };
        var test = vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.IsTesting);
        Assert.True(vm.CanEditChatGptFastMode);
        vm.ChatGptFastModeEnabled = !initial;
        Assert.True(previous.IsCancellationRequested);
        pending.SetResult(Result(initial ? "fast" : null, "fast") with { Message = "过期测速" });
        await test;
        Assert.Null(vm.ConnectionBenchmark);
        Assert.Empty(vm.ConnectionTranslation);
        Assert.False(vm.ConnectionSucceeded);
        Assert.DoesNotContain("过期测速", vm.ConnectionStatus);
        Assert.False(vm.ShowServiceTierNote);
        Assert.Empty(vm.ServiceTierNote);
        vm.ConnectionTester = (request, _) => Task.FromResult(Result(request.ChatGptFastModeEnabled ? "fast" : null, "default"));
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.ConnectionSucceeded);
        Assert.Equal(!initial ? "fast" : null, vm.ConnectionBenchmark!.RequestedServiceTier);
        Assert.Equal("default", vm.ConnectionBenchmark.EffectiveServiceTier);
    }

    [Theory]
    [InlineData("fast", "fast", "请求：快速模式；实际：快速模式。")]
    [InlineData("fast", "priority", "请求：快速模式；实际：快速模式。")]
    [InlineData("priority", "fast", "请求：快速模式；实际：快速模式。")]
    [InlineData(" FAST ", " Priority ", "请求：快速模式；实际：快速模式。")]
    [InlineData("fast", "default", "请求：快速模式；实际：普通模式（快速模式未生效）。")]
    [InlineData("priority", "default", "请求：快速模式；实际：普通模式（快速模式未生效）。")]
    [InlineData("fast", null, "请求：快速模式；实际：未确认（服务端未返回处理模式）。")]
    [InlineData("fast", " ", "请求：快速模式；实际：未确认（服务端未返回处理模式）。")]
    [InlineData("fast", "future-tier", "请求：快速模式；实际：未确认（服务端返回未识别的模式：future-tier）。")]
    [InlineData(null, "default", "请求：未指定快速模式；实际：普通模式。")]
    [InlineData(null, "priority", "请求：未指定快速模式；实际：快速模式。")]
    [InlineData(null, null, "请求：未指定快速模式；实际：未确认（服务端未返回处理模式）。")]
    [InlineData("default", "default", "请求：普通模式；实际：普通模式。")]
    [InlineData("future-tier", "default", "请求：未识别的模式（future-tier）；实际：普通模式。")]
    public async Task Tier_Note_Separates_Requested_Mode_From_Actual_Server_Mode(string? requested, string? effective, string expected)
    {
        var vm = await ReadyAsync();
        // Deliberately leave the current switch off: the result's metadata is the evidence.
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        vm.ConnectionTester = (_, _) => Task.FromResult(Result(requested, effective));
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.ShowServiceTierNote);
        Assert.Equal(expected, vm.ServiceTierNote);
        Assert.Contains(nameof(vm.ServiceTierNote), changes);
        Assert.Contains(nameof(vm.ShowServiceTierNote), changes);
        vm.ChatGptFastModeEnabled = true;
        Assert.False(vm.ShowServiceTierNote);
        Assert.Empty(vm.ServiceTierNote);
    }
}
