using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class ApiNetworkSettingsTests
{
    [Fact]
    public void SwitchingPresetsAndCopyingKeepIndependentNetworkDrafts()
    {
        var model = new TranslationSettingsViewModel();
        model.NetworkMode = "直连";
        model.AddPresetCommand.Execute(null);
        var a = model.SelectedPreset!;
        model.NetworkMode = "Clash 指定节点"; model.SelectedClashNode = "Node A";
        model.AddPresetCommand.Execute(null);
        var b = model.SelectedPreset!;
        model.NetworkMode = "自定义代理"; model.ProxyUrl = "socks5://127.0.0.1:1080";
        model.SelectedPreset = a;
        Assert.Equal("Clash 指定节点", model.NetworkMode); Assert.Equal("Node A", model.SelectedClashNode);
        model.SelectedPreset = b;
        Assert.Equal("自定义代理", model.NetworkMode); Assert.Equal("socks5://127.0.0.1:1080", model.ProxyUrl);
        model.CopyPresetCommand.Execute(null);
        Assert.Equal(ApiNetworkMode.CustomProxy, model.CollectNetwork().Mode);
        model.DefaultPresetCommand.Execute(null);
        Assert.Equal("直连", model.NetworkMode);
        var saved = model.CollectProviderSettings(new AppSettings());
        Assert.Equal(ApiNetworkMode.Direct, saved.ApiNetwork.Mode);
        Assert.Equal(ApiNetworkMode.ClashNode, saved.TranslationProviderPresets[0].Network.Mode);
        Assert.Equal(ApiNetworkMode.CustomProxy, saved.TranslationProviderPresets[1].Network.Mode);
        Assert.Equal(ApiNetworkMode.CustomProxy, saved.TranslationProviderPresets[2].Network.Mode);
    }

    [Fact]
    public async Task ChangingRouteCancelsStaleModelDiscoveryAndSnapshotsTheChosenRoute()
    {
        var model = new TranslationSettingsViewModel { SelectedProvider = "OpenAI 兼容", BaseUrl = "http://127.0.0.1:1234/v1", NetworkMode = "直连" };
        var result = new TaskCompletionSource<ScreenTranslator.Core.Translation.TranslationModelList>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationModelListRequest? captured = null;
        model.ModelListLoader = (request, _) => { captured = request; return result.Task; };
        var pending = model.LoadModelsCommand.ExecuteAsync(null);
        Assert.Equal(ApiNetworkMode.Direct, captured!.Network!.Mode);
        model.NetworkMode = "Clash 指定节点"; model.SelectedClashNode = "Node B";
        result.SetResult(new(["stale-model"])); await pending;
        Assert.DoesNotContain("stale-model", model.Models);
    }

    [Fact]
    public async Task ConnectionTestUsesItsNetworkSnapshotAndIgnoresChangedRouteResult()
    {
        var model = new TranslationSettingsViewModel { ApiKey = "test-key", NetworkMode = "Clash 指定节点", SelectedClashNode = "Node A" };
        var result = new TaskCompletionSource<ConnectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationConnectionTestRequest? captured = null;
        model.ConnectionTester = (request, _) => { captured = request; return result.Task; };
        var pending = model.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("Node A", captured!.Network!.ClashNode);
        Assert.DoesNotContain("test-key", captured.ToString());
        model.NetworkMode = "直连";
        result.SetResult(new(true, "old connection")); await pending;
        Assert.False(model.ConnectionSucceeded); Assert.DoesNotContain("old connection", model.ConnectionStatus);
    }

    [Fact]
    public async Task MissingInstallationShowsOfficialDownloadOnlyForClashModesAndDoesNotBlockDirect()
    {
        var model = new TranslationSettingsViewModel { ClashInstallationProbe = () => false };
        Assert.False(model.ShowClashDownload);
        model.NetworkMode = "Clash 指定节点"; Assert.True(model.ShowClashDownload);
        model.ClashNodesLoader = _ => throw new ScreenTranslator.Core.Translation.TranslationConfigurationException("未安装 Clash Verge，请从 GitHub 下载。");
        await model.ReadClashNodesCommand.ExecuteAsync(null);
        Assert.Contains("GitHub", model.ClashStatus); Assert.True(model.ShowClashDownload);
        model.NetworkMode = "直连"; Assert.False(model.ShowClashDownload);
        Assert.Equal(ApiNetworkMode.Direct, model.CollectProviderSettings(new()).ApiNetwork.Mode);
    }

    [Fact]
    public async Task StaleNodeReadCannotChangeAnotherPresetsSelection()
    {
        var model = new TranslationSettingsViewModel();
        model.AddPresetCommand.Execute(null); model.NetworkMode = "Clash 指定节点"; model.SelectedClashNode = "Node A";
        var result = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ClashNodesLoader = _ => result.Task;
        var pending = model.ReadClashNodesCommand.ExecuteAsync(null);
        model.AddPresetCommand.Execute(null); model.NetworkMode = "直连";
        result.SetResult(["old-node"]); await pending;
        Assert.Equal("直连", model.NetworkMode); Assert.DoesNotContain("old-node", model.ClashNodes);
    }
}
