using System.IO;
using System.Text.Json;
using ScreenTranslator.Core.Translation;
using YamlDotNet.RepresentationModel;

namespace ScreenTranslator.App.Services.Networking;

/// <summary>Builds a minimal, private worker config. It is sent through stdin, never written to disk.</summary>
public static class ClashNodeConfiguration
{
    public static string Build(ClashSnapshot snapshot, string node, int port, string pipe, string secret, string password)
    {
        if (!snapshot.Nodes.Contains(node, StringComparer.Ordinal))
            throw new TranslationConfigurationException("预设选择的 Clash 节点已不存在，请重新读取节点列表。");
        var candidates = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        AddCandidates(ClashVergeInstallation.Value(snapshot.Configuration, "proxies"), candidates);
        // Cached subscriptions are read locally, never refreshed by the translation worker.
        if (ClashVergeInstallation.Value(snapshot.Configuration, "proxy-providers") is YamlMappingNode providers)
            foreach (var provider in providers.Children.Values.OfType<YamlMappingNode>())
            {
                if (ClashVergeInstallation.Value(provider, "payload") is { } payload) AddCandidates(payload, candidates);
                var path = ClashVergeInstallation.Scalar(provider, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    if (!Path.IsPathFullyQualified(path)) path = Path.GetFullPath(path,
                        Path.GetDirectoryName(snapshot.Installation.ConfigurationPath)!);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length <= 16 * 1024 * 1024)
                        AddCandidates(ClashVergeInstallation.Value(ClashVergeInstallation.Parse(File.ReadAllText(path)), "proxies"), candidates);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (TranslationConfigurationException) { }
            }
        var proxyNodes = new YamlSequenceNode(); var groups = new YamlSequenceNode();
        var visiting = new HashSet<string>(StringComparer.Ordinal); var included = new HashSet<string>(StringComparer.Ordinal);
        void Include(string name)
        {
            if (name is "DIRECT" or "COMPATIBLE") return;
            if (included.Contains(name)) return;
            if (!visiting.Add(name)) throw new TranslationConfigurationException("所选 Clash 节点存在循环依赖，请选择其他节点。");
            if (candidates.TryGetValue(name, out var proxy))
            {
                var parent = ClashVergeInstallation.Scalar(proxy, "dialer-proxy");
                if (parent.Length > 0) Include(parent);
                proxyNodes.Add(proxy);
            }
            else if (snapshot.Proxies.TryGetProperty(name, out var metadata) && metadata.TryGetProperty("now", out var current)
                && current.GetString() is { Length: > 0 } selected)
            {
                Include(selected);
                groups.Add(new YamlMappingNode { { "name", name }, { "type", "select" }, { "proxies", new YamlSequenceNode(new YamlScalarNode(selected)) } });
            }
            else throw new TranslationConfigurationException("所选节点的本地订阅缓存不可用，请先在 Clash Verge 更新订阅，再读取节点列表。");
            visiting.Remove(name); included.Add(name);
        }
        Include(node);
        var root = new YamlMappingNode
        {
            { "allow-lan", "false" }, { "bind-address", "127.0.0.1" }, { "mode", "rule" },
            { "log-level", "silent" }, { "ipv6", "false" }, { "find-process-mode", "off" },
            { "profile", new YamlMappingNode { { "store-selected", "false" }, { "store-fake-ip", "false" } } },
            { "external-controller-pipe", pipe }, { "secret", secret },
            { "proxies", proxyNodes }, { "proxy-groups", groups }, { "rules", new YamlSequenceNode(new YamlScalarNode("MATCH,DIRECT")) },
            { "listeners", new YamlSequenceNode(new YamlMappingNode
                {
                    { "name", "translator-api" }, { "type", "http" }, { "listen", "127.0.0.1" },
                    { "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "proxy", node },
                    { "users", new YamlSequenceNode(new YamlMappingNode { { "username", "translator" }, { "password", password } }) },
                }) },
        };
        // Use existing DNS choices without importing TUN, fake-IP, listeners, rules or update jobs.
        var dns = new YamlMappingNode { { "enable", "true" }, { "enhanced-mode", "redir-host" }, { "ipv6", "false" } };
        if (ClashVergeInstallation.Value(snapshot.Configuration, "dns") is YamlMappingNode sourceDns)
            foreach (var key in new[] { "nameserver", "default-nameserver", "proxy-server-nameserver" })
                if (ClashVergeInstallation.Value(sourceDns, key) is { } value) dns.Add(key, value);
        if (ClashVergeInstallation.Value(dns, "nameserver") is null)
            dns.Add("nameserver", new YamlSequenceNode(new YamlScalarNode("system")));
        root.Add("dns", dns);
        if (ClashVergeInstallation.Value(snapshot.Configuration, "hosts") is { } hosts) root.Add("hosts", hosts);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }

    private static void AddCandidates(YamlNode? source, Dictionary<string, YamlMappingNode> result)
    {
        if (source is not YamlSequenceNode sequence) return;
        foreach (var proxy in sequence.Children.OfType<YamlMappingNode>())
            if (ClashVergeInstallation.Scalar(proxy, "name") is { Length: > 0 } name) result.TryAdd(name, proxy);
    }
}
