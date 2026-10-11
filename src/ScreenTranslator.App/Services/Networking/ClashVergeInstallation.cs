using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ScreenTranslator.Core.Translation;
using YamlDotNet.RepresentationModel;

namespace ScreenTranslator.App.Services.Networking;

public sealed class ClashVergeInstallation
{
    public string ConfigurationPath { get; }
    public string CorePath { get; }
    public ClashVergeInstallation(string configurationPath, string corePath)
    { ConfigurationPath = Path.GetFullPath(configurationPath); CorePath = Path.GetFullPath(corePath); }

    public static ClashVergeInstallation Discover()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "io.github.clash-verge-rev.clash-verge-rev");
        var config = Path.Combine(folder, "clash-verge.yaml");
        var core = FindCorePath() ?? throw new TranslationConfigurationException("未安装 Clash Verge。如需指定节点功能，请前往官方 GitHub 下载。");
        if (!File.Exists(config)) throw new TranslationConfigurationException("未找到 Clash Verge 当前配置，请先启动 Clash Verge；也可填写自定义代理地址。");
        return new(config, core);
    }

    public static bool IsInstalled() => FindCorePath() is not null;
    private static string? FindCorePath()
    {
        foreach (var process in Process.GetProcessesByName("verge-mihomo"))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path && File.Exists(path)) return path; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
            foreach (var name in new[] { "Clash Verge", "Clash Verge Rev" })
            {
                var core = Path.Combine(root, name, "verge-mihomo.exe");
                if (File.Exists(core)) return core;
            }
        return null;
    }

    public static YamlMappingNode Parse(string text)
    {
        if (text.Length > 16 * 1024 * 1024) throw new TranslationConfigurationException("Clash 配置过大，无法读取。");
        try
        {
            var yaml = new YamlStream(); yaml.Load(new StringReader(text));
            if (yaml.Documents.Count == 1 && yaml.Documents[0].RootNode is YamlMappingNode map) return map;
        }
        catch (YamlDotNet.Core.YamlException) { }
        throw new TranslationConfigurationException("无法读取 Clash 当前配置，请在 Clash Verge 中确认配置已生效。");
    }
    public static YamlNode? Value(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    public static string Scalar(YamlMappingNode map, string key) => (Value(map, key) as YamlScalarNode)?.Value ?? "";

    public async Task<ClashSnapshot> ReadAsync(CancellationToken token)
    {
        try
        {
            var info = new FileInfo(ConfigurationPath);
            if (!info.Exists || info.Length > 16 * 1024 * 1024) throw new TranslationConfigurationException("Clash 当前配置不可读。");
            var root = Parse(await File.ReadAllTextAsync(ConfigurationPath, token).ConfigureAwait(false));
            var pipe = Scalar(root, "external-controller-pipe");
            var address = Scalar(root, "external-controller");
            var secret = Scalar(root, "secret");
            using var client = Controller(pipe, address, secret);
            using var response = await client.GetAsync("proxies", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TranslationConfigurationException("Clash 控制接口不可用，请确认 Clash Verge 已启动。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false));
            var proxies = json.RootElement.GetProperty("proxies").Clone();
            return new(this, root, proxies, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch (TranslationConfigurationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or HttpRequestException or JsonException or KeyNotFoundException)
        { throw new TranslationConfigurationException("无法连接 Clash Verge，请确认它已启动且配置已生效。"); }
    }

    public static HttpClient Controller(string pipe, string address, string secret)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) };
        Uri endpoint;
        if (!string.IsNullOrWhiteSpace(pipe))
        {
            const string prefix = @"\\.\pipe\";
            if (!pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pipe[prefix.Length..].Contains('\\') || pipe.Length > 256)
            { handler.Dispose(); throw new TranslationConfigurationException("Clash 本地控制管道格式无效。"); }
            var name = pipe[prefix.Length..];
            handler.ConnectCallback = async (_, token) =>
            {
                var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await stream.ConnectAsync(3000, token).ConfigureAwait(false); return stream; }
                catch { stream.Dispose(); throw; }
            };
            endpoint = new("http://localhost/");
        }
        else
        {
            if (address.StartsWith("0.0.0.0:", StringComparison.Ordinal)) address = "127.0.0.1:" + address[8..];
            if (!Uri.TryCreate("http://" + address + "/", UriKind.Absolute, out endpoint!) || !endpoint.IsLoopback || endpoint.Port < 1
                || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/")
            { handler.Dispose(); throw new TranslationConfigurationException("Clash 未提供可用的本地控制接口。"); }
        }
        var client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(6) };
        if (!string.IsNullOrEmpty(secret)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
}

public sealed record ClashSnapshot(ClashVergeInstallation Installation, YamlMappingNode Configuration, JsonElement Proxies, long Stamp, long Length)
{
    public IReadOnlyList<string> Nodes => Proxies.EnumerateObject().Where(p => p.Name is not ("DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "GLOBAL")
        && !p.Value.TryGetProperty("all", out _)).Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public ClashProxyEndpoint DefaultEndpoint()
    {
        if (!int.TryParse(ClashVergeInstallation.Scalar(Configuration, "mixed-port"), out var port) || port is < 1 or > 65535)
            if (!int.TryParse(ClashVergeInstallation.Scalar(Configuration, "port"), out port) || port is < 1 or > 65535)
                throw new TranslationConfigurationException("Clash 未启用 HTTP / Mixed 代理端口。");
        NetworkCredential? credential = null;
        if (ClashVergeInstallation.Value(Configuration, "authentication") is YamlSequenceNode auth
            && auth.Children.OfType<YamlScalarNode>().FirstOrDefault()?.Value is { } pair && pair.IndexOf(':') is var split && split > 0)
            credential = new(pair[..split], pair[(split + 1)..]);
        return new(new Uri($"http://127.0.0.1:{port}"), credential);
    }
    public override string ToString() => "Local Clash installation snapshot";
}
