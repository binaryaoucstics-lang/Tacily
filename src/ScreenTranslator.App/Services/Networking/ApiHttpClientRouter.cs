using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Networking;

public sealed record ClashProxyEndpoint(Uri Address, NetworkCredential? Credentials = null)
{
    public override string ToString() => "Local Clash proxy endpoint";
}

public interface IClashRouteProvider : IDisposable
{
    Task<IReadOnlyList<string>> ReadNodesAsync(CancellationToken token);
    Task<ClashProxyEndpoint> GetEndpointAsync(string? node, CancellationToken token);
}

/// <summary>Each route owns a connection pool. Selecting a preset never mutates an active pool.</summary>
public sealed class ApiHttpClientRouter : IDisposable
{
    private readonly IClashRouteProvider _clash;
    private readonly ClashNodeProber _nodeProber = new();
    private readonly ConcurrentDictionary<string, Lazy<HttpClient>> _clients = new(StringComparer.Ordinal);
    private int _disposed;

    public ApiHttpClientRouter(IClashRouteProvider? clash = null) => _clash = clash ?? new ClashVergeRouteProvider();
    public Task<IReadOnlyList<string>> ReadClashNodesAsync(CancellationToken token) => _clash.ReadNodesAsync(token);
    public Task<ClashNodeProbeResult> TestClashNodeAsync(string node, CancellationToken token) => _nodeProber.TestAsync(node, token);
    public HttpClient GetClient(AppSettings settings) => GetClient(TranslationProviderConfiguration.Network(settings));
    public HttpClient GetClient(ApiNetworkOptions? network)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var options = (network ?? new()).Normalize(); options.Validate();
        return _clients.GetOrAdd(options.Identity, _ => new(() => CreateClient(options), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private HttpClient CreateClient(ApiNetworkOptions options)
    {
        HttpMessageHandler handler = options.Mode switch
        {
            ApiNetworkMode.Direct => Handler(false),
            ApiNetworkMode.CustomProxy => Handler(true, new WebProxy(options.CustomProxyUri())),
            ApiNetworkMode.Clash or ApiNetworkMode.ClashNode => new ClashRoutingHandler(_clash,
                options.Mode == ApiNetworkMode.ClashNode ? options.ClashNode : null),
            _ => Handler(true),
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler Handler(bool proxy, IWebProxy? address = null) => new()
    {
        UseProxy = proxy, Proxy = address, AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(12), MaxConnectionsPerServer = 20,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    };

    private sealed class ClashRoutingHandler(IClashRouteProvider clash, string? node) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _pools = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // Resolving/starting an independent route is asynchronous, including for model discovery.
            // No fallback to the system route if the chosen node fails.
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
            setup.CancelAfter(TimeSpan.FromSeconds(20));
            ClashProxyEndpoint route;
            try { route = await clash.GetEndpointAsync(node, setup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TranslationConfigurationException("Clash 连接准备超时，请确认 Clash Verge 已启动、节点可用后重试。"); }
            var identity = route.Address.AbsoluteUri + "\n" + route.Credentials?.UserName + "\n" + route.Credentials?.Password;
            var pool = _pools.GetOrAdd(identity, _ => new(() => new HttpMessageInvoker(Handler(true,
                new WebProxy(route.Address) { Credentials = route.Credentials })), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            return await pool.SendAsync(request, token).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var pool in _pools.Values) if (pool.IsValueCreated) pool.Value.Dispose();
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var client in _clients.Values) if (client.IsValueCreated) client.Value.Dispose();
        _nodeProber.Dispose();
        _clash.Dispose();
    }
}
