using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using System.Text.Json;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class TranslationPresetSettingsTests
{
    [Fact]
    public void Custom_Presets_Do_Not_Overwrite_Default_Provider_Drafts()
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = "default-model", BaseUrl = "https://default.example/v1" };
        vm.AddPresetCommand.Execute(null);
        vm.SelectedProtocol = "OpenAI Responses";
        vm.SelectedModel = "preset-model";
        vm.BaseUrl = "https://preset.example/v1";
        var settings = vm.CollectProviderSettings(new AppSettings());
        Assert.Equal("default-model", settings.OpenAiModel);
        Assert.Equal("https://default.example/v1", settings.OpenAiBaseUrl);
        Assert.Equal("preset-model", TranslationProviderConfiguration.Model(settings));
        vm.DefaultPresetCommand.Execute(null);
        Assert.Equal("OpenAI", vm.SelectedProvider);
        Assert.Equal("default-model", vm.SelectedModel);
        Assert.Equal("https://default.example/v1", vm.BaseUrl);
    }

    [Fact]
    public void Two_Custom_Services_Retain_Independent_Protocol_Model_Url_Thinking_And_Key_Masks()
    {
        var vm = new TranslationSettingsViewModel();
        vm.AddPresetCommand.Execute(null);
        var first = vm.SelectedPreset!;
        vm.PresetName = "服务 A";
        vm.SelectedModel = "model-a";
        vm.BaseUrl = "https://a.example/v1";
        vm.ThinkingLevel = "高";
        vm.ApplySavedApiKey("fake-a-key-1111");
        vm.AddPresetCommand.Execute(null);
        var second = vm.SelectedPreset!;
        vm.PresetName = "服务 B";
        vm.SelectedProvider = "Claude";
        vm.SelectedModel = "model-b";
        vm.BaseUrl = "https://b.example/messages";
        vm.ThinkingLevel = "关闭";
        vm.ApplySavedApiKey("fake-b-key-2222");
        vm.SelectedPreset = first;
        Assert.Equal("服务 A", vm.PresetName);
        Assert.Equal("OpenAI 兼容", vm.SelectedProvider);
        Assert.Equal("model-a", vm.SelectedModel);
        Assert.Equal("https://a.example/v1", vm.BaseUrl);
        Assert.Equal("高", vm.ThinkingLevel);
        Assert.EndsWith("1111", vm.SavedApiKeyMask);
        Assert.Equal("", vm.ApiKey);
        vm.SelectedPreset = second;
        Assert.Equal("Claude", vm.SelectedProvider);
        Assert.Equal("model-b", vm.SelectedModel);
        Assert.Equal("关闭", vm.ThinkingLevel);
        Assert.EndsWith("2222", vm.SavedApiKeyMask);
        var settings = vm.CollectProviderSettings(new AppSettings());
        var restored = new TranslationSettingsViewModel();
        restored.ApplyProviderSettings(settings.Migrate(out _));
        Assert.Equal(second.Id, restored.SelectedPreset!.Id);
        Assert.Equal("model-b", restored.SelectedModel);
        Assert.Equal(2, restored.Presets.Count);
        Assert.False(restored.HasSavedApiKey);
    }

    [Fact]
    public async Task Delayed_Connection_Result_And_Key_Preview_Cannot_Contaminate_Another_Preset()
    {
        var vm = new TranslationSettingsViewModel();
        vm.AddPresetCommand.Execute(null);
        vm.SelectedModel = "a";
        vm.ApplySavedApiKey("fake-key-a");
        var secretA = vm.ActiveSecretName;
        var completion = new TaskCompletionSource<ConnectionTestResult>();
        TranslationConnectionTestRequest? observed = null;
        vm.ConnectionTester = (request, _) => { observed = request; return completion.Task; };
        var task = vm.TestConnectionCommand.ExecuteAsync(null);
        vm.AddPresetCommand.Execute(null);
        vm.SelectedModel = "b";
        vm.ApplySavedApiKey(secretA, "fake-key-a");
        Assert.False(vm.HasSavedApiKey);
        Assert.Equal(secretA, observed!.SecretName);
        Assert.Equal("a", observed.Model);
        completion.SetResult(new(true, "wrong-profile-success"));
        await task;
        Assert.False(vm.ConnectionSucceeded);
        Assert.DoesNotContain("wrong-profile-success", vm.ConnectionStatus);
    }

    [Fact]
    public void Copy_Creates_A_New_Key_Identity_And_Captures_Explicit_Copy_Source()
    {
        var vm = new TranslationSettingsViewModel { SelectedProvider = "OpenAI", SelectedModel = "my-model" };
        vm.ApplySavedApiKey("fake-test-key");
        var source = vm.ActiveSecretName;
        string? copiedFrom = null;
        vm.SaveRequested += (_, _) => copiedFrom = vm.PendingCopySecretName;
        vm.CopyPresetCommand.Execute(null);
        Assert.Equal(source, copiedFrom);
        Assert.NotEqual(source, vm.ActiveSecretName);
        Assert.Equal("my-model", vm.SelectedModel);
        vm.CompletePresetKeyCopy();
        Assert.Null(vm.PendingCopySecretName);
        var created = vm.SelectedPreset;
        vm.SetSaving(true);
        vm.AddPresetCommand.Execute(null);
        vm.DeletePresetCommand.Execute(null);
        vm.DefaultPresetCommand.Execute(null);
        Assert.Same(created, vm.SelectedPreset);
        vm.SetSaving(false);
        vm.DeletePresetCommand.Execute(null);
        Assert.Empty(vm.Presets);
        Assert.Null(vm.SelectedPreset);
        Assert.Equal("openai-api-key", vm.ActiveSecretName);
    }

    [Fact]
    public async Task Metadata_And_Dpapi_Keys_RoundTrip_Without_Plaintext_In_Settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTranslator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preset = new TranslationProviderPreset { Name = "测试服务", Model = "model", ThinkingLevel = ThinkingLevel.Low };
            var settings = new AppSettings { TranslationProviderPresets = [preset], SelectedTranslationPresetId = preset.Id };
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var secrets = new DpapiSecretStore(Path.Combine(directory, "secrets"));
            var name = TranslationProviderConfiguration.SecretName(settings);
            await secrets.SetAsync(name, "synthetic-private-key-9876");
            await store.SaveAsync(settings);
            var restored = (await store.LoadAsync()).Migrate(out _);
            Assert.Equal(preset, Assert.Single(restored.TranslationProviderPresets));
            Assert.Equal(preset.Id, restored.SelectedTranslationPresetId);
            Assert.Equal("synthetic-private-key-9876", await secrets.GetAsync(name));
            Assert.DoesNotContain("synthetic-private-key", await File.ReadAllTextAsync(path));
            Assert.DoesNotContain("synthetic-private-key", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory, "secrets", name + ".bin"))));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(json.RootElement.GetProperty("translationProviderPresets")[0].TryGetProperty("apiKey", out _));
        }
        finally { Directory.Delete(directory, true); }
    }
}
