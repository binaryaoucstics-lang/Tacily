using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Networking;

public sealed class ApiHttpClientRouterTests
{
    [Fact]
    public async Task ConcurrentPresetsUseIndependentRoutesWhileDirectBypassesClash()
    {
        using var a = new Server("A"); using var b = new Server("B"); using var direct = new Server("direct");
        var clash = new Routes(a.Address, b.Address); using var router = new ApiHttpClientRouter(clash);
        var clientA = router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode, ClashNode = "A" });
        var clientB = router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode, ClashNode = "B" });
        var values = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
            await (i % 2 == 0 ? clientA : clientB).GetStringAsync(direct.Address)));
        Assert.Equal(5, values.Count(v => v == "A")); Assert.Equal(5, values.Count(v => v == "B"));
        Assert.Empty(direct.Requests);
        Assert.Equal("direct", await router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.Direct }).GetStringAsync(direct.Address));
        Assert.Equal(10, clash.Calls); Assert.Single(direct.Requests);
        Assert.NotSame(clientA, clientB);
    }

    [Fact]
    public async Task CustomProxyAndModelListShareChosenRoute()
    {
        using var proxy = new Server("{\"data\":[{\"id\":\"routed-model\"}]}");
        using var router = new ApiHttpClientRouter(new Routes(proxy.Address, proxy.Address));
        var client = router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.CustomProxy, ProxyUrl = proxy.Address.ToString() });
        var list = await new TranslationModelCatalog(client).ReadAsync(TranslationProviderKind.ChatCompletions,
            "http://127.0.0.1:1/v1", "test-key", CancellationToken.None);
        Assert.Equal(["routed-model"], list.Models);
        Assert.Contains(proxy.Requests, request => request.Contains("/v1/models") && request.Contains("Bearer test-key"));
    }

    [Fact]
    public async Task FirstStreamChunkArrivesBeforeUpstreamCompletes()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Server("", async stream =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n6\r\nfirst\n\r\n"));
            await stream.FlushAsync(); await finish.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("5\r\nlast\n\r\n0\r\n\r\n"));
        });
        using var router = new ApiHttpClientRouter(new Routes(server.Address, server.Address));
        var client = router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode, ClashNode = "A" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync("http://127.0.0.1:1/stream", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("first", await reader.ReadLineAsync(timeout.Token)); Assert.False(finish.Task.IsCompleted);
        finish.SetResult(); Assert.Equal("last", await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task MissingNodeDoesNotFallBackToDirectAndSetupHonorsCancellation()
    {
        using var direct = new Server("must not reach");
        using var router = new ApiHttpClientRouter(new Routes(direct.Address, direct.Address));
        var client = router.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.ClashNode, ClashNode = "missing" });
        await Assert.ThrowsAsync<TranslationConfigurationException>(() => client.GetStringAsync(direct.Address));
        Assert.Empty(direct.Requests);
        using var waiting = new ApiHttpClientRouter(new WaitingRoute());
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.GetClient(new ApiNetworkOptions { Mode = ApiNetworkMode.Clash })
            .GetStringAsync(direct.Address, cancellation.Token));
        Assert.Empty(direct.Requests);
    }

    private sealed class Routes(Uri a, Uri b) : IClashRouteProvider
    {
        private int _calls;
        public int Calls => _calls;
        public Task<IReadOnlyList<string>> ReadNodesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(["A", "B"]);
        public Task<ClashProxyEndpoint> GetEndpointAsync(string? node, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            if (node is "missing") throw new TranslationConfigurationException("node unavailable");
            return Task.FromResult(new ClashProxyEndpoint(node == "B" ? b : a));
        }
        public void Dispose() { }
    }
    private sealed class WaitingRoute : IClashRouteProvider
    {
        public Task<IReadOnlyList<string>> ReadNodesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
        public async Task<ClashProxyEndpoint> GetEndpointAsync(string? node, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
        public void Dispose() { }
    }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public Uri Address { get; }
        public Server(string body, Func<NetworkStream, Task>? streaming = null)
        {
            _listener.Start(); Address = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_shutdown.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                                var headers = new StringBuilder(); string? line;
                                while ((line = await reader.ReadLineAsync(_shutdown.Token)) is { Length: > 0 }) headers.AppendLine(line);
                                Requests.Enqueue(headers.ToString());
                                if (streaming is not null) await streaming(stream);
                                else
                                {
                                    var bytes = Encoding.UTF8.GetBytes(body);
                                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                                    await stream.WriteAsync(bytes);
                                }
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public void Dispose() { _shutdown.Cancel(); _listener.Stop(); _shutdown.Dispose(); }
    }
}
