using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class TranslationProviderSettingsTests
{
    [Fact]
    public void Switching_Providers_Isolates_Keys_And_Retains_Model_And_Endpoint_Drafts()
    {
        var model = new TranslationSettingsViewModel();
        model.ApplySavedApiKey(TranslationProviderKind.DeepSeek, "ds-test-key-1111");
        model.ApplySavedApiKey(TranslationProviderKind.OpenAI, "oa-test-key-2222");
        model.ApplySavedApiKey(TranslationProviderKind.Claude, null);
        model.SelectedProvider = "OpenAI";
        Assert.EndsWith("2222", model.SavedApiKeyMask);
        Assert.Equal("gpt-6-luna", model.SelectedModel);
        Assert.True(model.CanEditEndpoint);
        model.SelectedModel = "custom-model";
        model.BaseUrl = "https://proxy.example/openai/v1";
        model.ApiKey = "UNSAVED_OPENAI_KEY";
        model.SelectedProvider = "Claude";
        Assert.Equal(string.Empty, model.ApiKey);
        Assert.False(model.HasSavedApiKey);
        Assert.False(model.TestConnectionCommand.CanExecute(null));
        Assert.Equal("claude-sonnet-5-5", model.SelectedModel);
        model.SelectedProvider = "DeepSeek";
        Assert.EndsWith("1111", model.SavedApiKeyMask);
        Assert.False(model.CanEditEndpoint);
        model.SelectedProvider = "OpenAI";
        Assert.Equal("custom-model", model.SelectedModel);
        Assert.Equal("https://proxy.example/openai/v1", model.BaseUrl);
        Assert.Equal(string.Empty, model.ApiKey);
        Assert.DoesNotContain("UNSAVED_OPENAI_KEY", model.SavedApiKeyMask);
    }

    [Fact]
    public void Loaded_Settings_And_All_Provider_Drafts_RoundTrip()
    {
        var settings = new AppSettings { TranslationProvider = TranslationProviderKind.Claude,
            OpenAiModel = "gpt-5.5", OpenAiBaseUrl = "https://proxy.example/openai/v1",
            ClaudeModel = "claude-opus-5-5", ClaudeBaseUrl = "https://proxy.example/claude/v1" };
        var model = new TranslationSettingsViewModel();
        model.ApplyProviderSettings(settings);
        Assert.Equal("Claude", model.SelectedProvider);
        Assert.Equal("claude-opus-5-5", model.SelectedModel);
        Assert.Equal(settings, model.CollectProviderSettings(settings));
        model.SelectedProvider = "OpenAI";
        Assert.Equal("gpt-5.5", model.SelectedModel);
        Assert.Equal("https://proxy.example/openai/v1", model.BaseUrl);
        Assert.Equal(settings with { TranslationProvider = TranslationProviderKind.OpenAI }, model.CollectProviderSettings(settings));
    }

    [Fact]
    public async Task Stale_Connection_Test_Result_Cannot_Apply_To_Another_Provider()
    {
        var model = new TranslationSettingsViewModel();
        model.ApplySavedApiKey("ds-test-key");
        var completion = new TaskCompletionSource<ConnectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationConnectionTestRequest? observed = null;
        model.ConnectionTester = (request, _) => { observed = request; return completion.Task; };
        var task = model.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(TranslationProviderKind.DeepSeek, observed!.Provider);
        model.SelectedProvider = "Claude";
        completion.SetResult(new(true, "old-test-success"));
        await task;
        Assert.False(model.ConnectionSucceeded);
        Assert.DoesNotContain("old-test-success", model.ConnectionStatus);
    }

    [Theory]
    [InlineData("OpenAI", "gpt-6-astra")]
    [InlineData("Claude", "claude-opus-5-5")]
    public void AlwaysThinking_Models_Disclose_Lowest_Mode_And_Do_Not_Enable_DeepSeek_Retry(string provider, string name)
    {
        var model = new TranslationSettingsViewModel { SelectedProvider = provider, SelectedModel = name, ThinkingLevel = "关闭" };
        Assert.Contains("无法关闭", model.ThinkingDescription);
        model.ThinkingLevel = "低";
        Assert.False(model.CanBoundLowThinkingWait);
        model.SelectedProvider = "DeepSeek";
        Assert.True(model.CanBoundLowThinkingWait);
    }

    [Theory]
    [InlineData(TranslationProviderKind.OpenAI, "openai-api-key")]
    [InlineData(TranslationProviderKind.Claude, "claude-api-key")]
    public async Task Key_Resolver_Never_Uses_DeepSeek_Key_For_Another_Provider(TranslationProviderKind provider, string expected)
    {
        var store = new RecordingSecrets();
        Assert.Equal("saved", await ApiKeyResolver.ResolveAsync("", store, provider: provider));
        Assert.Equal(expected, store.Requested);
        store.Requested = null;
        Assert.Equal("new-key", await ApiKeyResolver.ResolveAsync(" new-key ", store, provider: provider));
        Assert.Null(store.Requested);
    }

    [Fact]
    public void Image_Cache_Distinguishes_Provider_Model_And_Endpoint()
    {
        var image = new CapturedBitmap(2, 2, 8, new byte[16]);
        var settings = new AppSettings();
        var original = ImageTranslationCache.Key(image, settings);
        var openai = ImageTranslationCache.Key(image, settings with { TranslationProvider = TranslationProviderKind.OpenAI });
        var claude = ImageTranslationCache.Key(image, settings with { TranslationProvider = TranslationProviderKind.Claude });
        Assert.NotEqual(original, openai);
        Assert.NotEqual(openai, claude);
        var active = settings with { TranslationProvider = TranslationProviderKind.OpenAI };
        Assert.NotEqual(openai, ImageTranslationCache.Key(image, active with { OpenAiModel = "another-model" }));
        Assert.NotEqual(openai, ImageTranslationCache.Key(image, active with { OpenAiBaseUrl = "https://proxy.example/v1" }));
        Assert.Equal(original, ImageTranslationCache.Key(image, settings with { OpenAiModel = "inactive-model" }));
    }

    [Fact]
    public void Save_Disables_Provider_Changes_And_Duplicate_Save_Command()
    {
        var model = new TranslationSettingsViewModel { SelectedProvider = "OpenAI" };
        model.SetSaving(true);
        Assert.False(model.CanEditProvider);
        Assert.False(model.CanEditEndpoint);
        Assert.False(model.SaveCommand.CanExecute(null));
        model.SetSaving(false);
        Assert.True(model.CanEditProvider);
        Assert.True(model.SaveCommand.CanExecute(null));
    }

    private sealed class RecordingSecrets : ISecretStore
    {
        internal string? Requested;
        public Task<string?> GetAsync(string key, CancellationToken token = default) { Requested = key; return Task.FromResult<string?>("saved"); }
        public Task SetAsync(string key, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
