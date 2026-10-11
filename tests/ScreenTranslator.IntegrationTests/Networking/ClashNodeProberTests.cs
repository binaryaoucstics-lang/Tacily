using System.Net;
using System.Net.Http;
using System.Text.Json;
using ScreenTranslator.App.Services.Networking;

namespace ScreenTranslator.IntegrationTests.Networking;

public sealed class ClashNodeProberTests
{
    [Fact]
    public async Task BatchBoundsConcurrencyUsesOnlyGetAndEscapesNodeNames()
    {
        var handler = new Handler(); var connections = 0;
        using var prober = new ClashNodeProber(_ => { connections++; return Task.FromResult(new HttpClient(handler) { BaseAddress = new("http://localhost/") }); });
        var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => prober.TestAsync("节点 / # " + i, CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(ClashNodeHealth.Available, result.Health));
        Assert.True(handler.Maximum <= ClashNodeProber.Concurrency); Assert.Equal(1, connections);
        Assert.All(handler.Paths, path => Assert.Contains("%2F", path));
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(false, true, ClashNodeHealth.Unavailable)]
    [InlineData(true, true, ClashNodeHealth.Available)]
    [InlineData(true, false, ClashNodeHealth.Reachable)]
    public async Task PositiveDelayCannotLabelUnexpectedHttpStatusAsAvailable(bool alive, bool verification, ClashNodeHealth expected)
    {
        using var prober = new ClashNodeProber(_ => Task.FromResult(new HttpClient(new Handler(alive, verification)) { BaseAddress = new("http://localhost/") }));
        var result = await prober.TestAsync("test", CancellationToken.None);
        Assert.Equal(expected, result.Health);
        if (expected == ClashNodeHealth.Unavailable) Assert.Null(result.DelayMilliseconds);
    }

    [Fact]
    public async Task CancelledBatchStopsQueuedChecks()
    {
        var handler = new Handler(delay: 1000);
        using var prober = new ClashNodeProber(_ => Task.FromResult(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        using var token = new CancellationTokenSource(50);
        var tasks = Enumerable.Range(0, 25).Select(i => prober.TestAsync("Node " + i, token.Token)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(tasks));
        Assert.True(handler.Paths.Count <= ClashNodeProber.Concurrency);
    }

    private sealed class Handler(bool alive = true, bool verification = true, int delay = 5) : HttpMessageHandler
    {
        private int _active, _maximum;
        public int Maximum => _maximum;
        public System.Collections.Concurrent.ConcurrentBag<string> Paths { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<HttpMethod> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active); int previous;
            do { previous = _maximum; if (active <= previous) break; } while (Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try
            {
                Paths.Add(request.RequestUri!.OriginalString); Methods.Add(request.Method); await Task.Delay(delay, token);
                var body = request.RequestUri.AbsolutePath.EndsWith("/delay") ? "{\"delay\":42}"
                    : verification ? JsonSerializer.Serialize(new { extra = new Dictionary<string, object> { [ClashNodeProber.ProbeUrl] = new { alive } } }) : "{}";
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
