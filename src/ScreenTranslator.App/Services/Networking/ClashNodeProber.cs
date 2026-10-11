using System.Net.Http;
using System.Text.Json;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Networking;

public enum ClashNodeHealth { Untested, Testing, Available, Reachable, Unavailable, Cancelled }
public sealed record ClashNodeProbeResult(string Name, ClashNodeHealth Health, int? DelayMilliseconds = null, string? Reason = null)
{
    public override string ToString() => $"Clash node check ({Health})";
}

/// <summary>Bounded parallel URL tests through the main core, without changing selectors or starting child cores.</summary>
public sealed class ClashNodeProber : IDisposable
{
    public const string ProbeUrl = "https://www.gstatic.com/generate_204";
    public const int Concurrency = 8;
    private readonly Func<CancellationToken, Task<HttpClient>> _connect;
    private readonly SemaphoreSlim _sessionGate = new(1, 1), _requests = new(Concurrency, Concurrency);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<HttpClient> _clients = [];
    private HttpClient? _client;
    private DateTime _connected;
    private int _disposed;

    public ClashNodeProber(Func<CancellationToken, Task<HttpClient>>? connect = null) => _connect = connect ?? ConnectAsync;
    private static async Task<HttpClient> ConnectAsync(CancellationToken token)
    {
        var snapshot = await ClashVergeInstallation.Discover().ReadAsync(token).ConfigureAwait(false);
        return ClashVergeInstallation.Controller(ClashVergeInstallation.Scalar(snapshot.Configuration, "external-controller-pipe"),
            ClashVergeInstallation.Scalar(snapshot.Configuration, "external-controller"), ClashVergeInstallation.Scalar(snapshot.Configuration, "secret"));
    }
    private async Task<HttpClient> ClientAsync(CancellationToken token)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_client is not null && DateTime.UtcNow - _connected < TimeSpan.FromMinutes(1)) return _client;
            var connected = await _connect(token).ConfigureAwait(false);
            lock (_clients)
            {
                if (Volatile.Read(ref _disposed) != 0) { connected.Dispose(); throw new ObjectDisposedException(nameof(ClashNodeProber)); }
                _client = connected; _clients.Add(connected); _connected = DateTime.UtcNow; return connected;
            }
        }
        finally { _sessionGate.Release(); }
    }
    public async Task<ClashNodeProbeResult> TestAsync(string name, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512) throw new TranslationConfigurationException("请选择有效的 Clash 节点。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _requests.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var client = await ClientAsync(linked.Token).ConfigureAwait(false);
            var path = "proxies/" + Uri.EscapeDataString(name);
            using var delayResponse = await client.GetAsync(path + "/delay?timeout=5000&url=" + Uri.EscapeDataString(ProbeUrl) + "&expected=204", linked.Token).ConfigureAwait(false);
            if (!delayResponse.IsSuccessStatusCode) return new(name, ClashNodeHealth.Unavailable, Reason: "超时或连接失败");
            using var delayJson = await ReadJsonAsync(delayResponse, linked.Token).ConfigureAwait(false);
            if (!delayJson.RootElement.TryGetProperty("delay", out var delayValue) || !delayValue.TryGetInt32(out var delay) || delay is < 1 or > 32767)
                return new(name, ClashNodeHealth.Unavailable, Reason: "测速结果无效");
            // URLTest can return a delay even for an unexpected HTTP status. Confirm per-URL health.
            using var healthResponse = await client.GetAsync(path, linked.Token).ConfigureAwait(false);
            if (healthResponse.IsSuccessStatusCode)
            {
                using var health = await ReadJsonAsync(healthResponse, linked.Token).ConfigureAwait(false);
                if (health.RootElement.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object
                    && extra.TryGetProperty(ProbeUrl, out var state) && state.ValueKind == JsonValueKind.Object && state.TryGetProperty("alive", out var alive))
                {
                    if (alive.ValueKind == JsonValueKind.True) return new(name, ClashNodeHealth.Available, delay);
                    if (alive.ValueKind == JsonValueKind.False) return new(name, ClashNodeHealth.Unavailable, Reason: "测试站点响应异常");
                }
            }
            return new(name, ClashNodeHealth.Reachable, delay, "内核未提供 HTTP 状态验证");
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        { return new(name, ClashNodeHealth.Unavailable, Reason: "连接超时"); }
        catch (Exception error) when (error is HttpRequestException or System.IO.IOException or JsonException)
        { return new(name, ClashNodeHealth.Unavailable, Reason: "连接或响应异常"); }
        finally { _requests.Release(); }
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new System.IO.MemoryStream(); var bytes = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 65536) throw new JsonException();
            buffer.Write(bytes, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        lock (_clients) foreach (var client in _clients) client.Dispose();
    }
}
