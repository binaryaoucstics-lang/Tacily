using System.Text.Json;
using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.Core.Translation;
using YamlDotNet.RepresentationModel;

namespace ScreenTranslator.IntegrationTests.Networking;

public sealed class ClashNodeConfigurationTests
{
    [Fact]
    public void WorkerImportsOnlySelectedNodeAndDependencyChainWithoutGlobalPortsTunOrSecrets()
    {
        var root = ClashVergeInstallation.Parse("""
            mixed-port: 7897
            secret: original-controller-secret
            external-controller: 0.0.0.0:9090
            tun: {enable: true}
            listeners: [{name: user-listener, type: http, port: 2345}]
            proxies:
              - {name: '节点 A', type: socks5, server: 127.0.0.1, port: 1234, password: node-password, dialer-proxy: upstream}
              - {name: relay, type: http, server: 127.0.0.1, port: 1235}
              - {name: unused, type: http, server: 127.0.0.1, port: 1236, password: unrelated-secret}
            proxy-groups: [{name: upstream, type: select, proxies: [relay, unused]}]
            rules: [MATCH,upstream]
            """);
        using var json = JsonDocument.Parse("""{"节点 A":{"type":"Socks5"},"relay":{"type":"Http"},"upstream":{"type":"Selector","now":"relay","all":["relay","unused"]}}""");
        var snapshot = new ClashSnapshot(new("C:/test/config.yaml", "C:/test/mihomo.exe"), root, json.RootElement.Clone(), 1, 1);
        var text = ClashNodeConfiguration.Build(snapshot, "节点 A", 20000, @"\\.\pipe\test", "worker-secret", "worker-password");
        var worker = ClashVergeInstallation.Parse(text);
        Assert.Null(ClashVergeInstallation.Value(worker, "mixed-port")); Assert.Null(ClashVergeInstallation.Value(worker, "tun"));
        Assert.Null(ClashVergeInstallation.Value(worker, "external-controller"));
        Assert.DoesNotContain("original-controller-secret", text); Assert.DoesNotContain("unrelated-secret", text);
        Assert.Contains("node-password", text); Assert.Contains("worker-password", text);
        Assert.Equal(2, ((YamlSequenceNode)ClashVergeInstallation.Value(worker, "proxies")!).Children.Count);
        Assert.Equal("original-controller-secret", ClashVergeInstallation.Scalar(root, "secret"));
        Assert.DoesNotContain("original", snapshot.ToString());
    }

    [Fact]
    public void MissingCachedNodeOrCyclicDialerDependencyFailsWithoutSubstitutingAnotherNode()
    {
        using var json = JsonDocument.Parse("""{"A":{"type":"Http"}}""");
        var snapshot = new ClashSnapshot(new("C:/test/config.yaml", "C:/test/mihomo.exe"), ClashVergeInstallation.Parse("proxies: []"), json.RootElement.Clone(), 1, 1);
        Assert.Throws<TranslationConfigurationException>(() => ClashNodeConfiguration.Build(snapshot, "A", 20000, @"\\.\pipe\test", "secret", "password"));
        var cyclic = snapshot with { Configuration = ClashVergeInstallation.Parse("proxies: [{name: A, type: http, server: example.com, port: 1, dialer-proxy: A}]") };
        Assert.Throws<TranslationConfigurationException>(() => ClashNodeConfiguration.Build(cyclic, "A", 20000, @"\\.\pipe\test", "secret", "password"));
    }
}
