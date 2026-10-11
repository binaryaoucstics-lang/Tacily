using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.ViewModels;

public sealed class NodeBenchmarkSettingsTests
{
    [Fact]
    public async Task SortingKeepsSelectionAndMovesUnavailableNodesToEnd()
    {
        var model = new TranslationSettingsViewModel { NetworkMode = "Clash 指定节点" };
        model.ClashNodesLoader = _ => Task.FromResult<IReadOnlyList<string>>(["A", "B", "C"]);
        await model.ReadClashNodesCommand.ExecuteAsync(null); model.SelectedClashNode = "A";
        model.ClashNodeTester = (name, _) => Task.FromResult(name == "C" ? new(name, ClashNodeHealth.Unavailable)
            : new ClashNodeProbeResult(name, ClashNodeHealth.Available, name == "A" ? 100 : 20));
        await model.TestAllClashNodesCommand.ExecuteAsync(null);
        Assert.Equal(["B", "A", "C"], model.ClashNodeChoices.Select(c => c.Name)); Assert.Equal("A", model.SelectedClashNode);
        model.SortClashNodesByDelay = false; Assert.Equal(["A", "B", "C"], model.ClashNodeChoices.Select(c => c.Name));
        Assert.Contains("3/3", model.ClashProbeStatus);
    }

    [Fact]
    public async Task BenchmarkPassesThinkingAndDisplaysSourceTranslationAndHonestMetrics()
    {
        var model = new TranslationSettingsViewModel { ApiKey = "test-key", ThinkingLevel = "高" };
        TranslationConnectionTestRequest? captured = null;
        model.ConnectionTester = (request, _) =>
        {
            captured = request;
            return Task.FromResult(new ConnectionTestResult(true, "翻译测速完成", TimeSpan.FromSeconds(3))
                { Benchmark = new(TranslationBenchmark.SourceSentence, "真实测试译文", TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1100), 40, 90, 10, BenchmarkRateKind.ContentGeneration) });
        };
        await model.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ThinkingLevel.High, captured!.ThinkingLevel); Assert.NotNull(captured.Progress);
        Assert.Equal("真实测试译文", model.ConnectionTranslation); Assert.Contains("1100", model.FirstCharacterMetric);
        Assert.Contains("40", model.ThroughputMetric); Assert.True(model.ShowConnectionDetails);
        model.NetworkMode = "直连";
        Assert.Null(model.ConnectionBenchmark); Assert.False(model.ShowConnectionDetails);
    }
}
