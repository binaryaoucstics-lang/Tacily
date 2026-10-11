using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationModelCatalogTests
{
    [Theory]
    [InlineData(TranslationProviderKind.ChatCompletions, "https://service.example", "https://service.example/v1/models")]
    [InlineData(TranslationProviderKind.ChatCompletions, "https://service.example/api/v1/", "https://service.example/api/v1/models")]
    [InlineData(TranslationProviderKind.ChatCompletions, "http://127.0.0.1:1234/v1/chat/completions", "http://127.0.0.1:1234/v1/models")]
    [InlineData(TranslationProviderKind.OpenAI, "https://service.example/proxy/responses", "https://service.example/proxy/models")]
    [InlineData(TranslationProviderKind.Claude, "https://service.example/v1/messages", "https://service.example/v1/models")]
    [InlineData(TranslationProviderKind.DeepSeek, "https://api.deepseek.com", "https://api.deepseek.com/models")]
    public void ListEndpointPreservesApiPrefixAndHandlesFullGenerationUrls(TranslationProviderKind provider, string url, string expected) =>
        Assert.Equal(expected, TranslationModelCatalog.Endpoint(provider, url).AbsoluteUri);

    [Theory]
    [InlineData("http://service.example/v1")]
    [InlineData("https://user:password@service.example/v1")]
    [InlineData("https://service.example/v1?key=secret")]
    [InlineData("https://service.example/v1#models")]
    public void InvalidAddressesFailBeforeAnyRequest(string url) =>
        Assert.Throws<TranslationConfigurationException>(() => TranslationModelCatalog.Endpoint(TranslationProviderKind.ChatCompletions, url));

    [Fact]
    public async Task CompatibleCatalogUsesGetAndBearerAndReturnsSortedDistinctIds()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.Equal("https://service.example/api/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("synthetic-test-key", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("x-api-key"));
            return Json("""{"data":[{"id":"z-vision"},{"id":"A-chat"},{"id":"z-vision"},{"id":""},{"id":42},{"id":"bad\u0001id"}]}""");
        }));
        var result = await new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.ChatCompletions, "https://service.example/api/v1", "synthetic-test-key");
        Assert.Equal(["A-chat", "z-vision"], result.Models); Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task ClaudePaginationUsesItsOwnAuthAndEncodedCursor()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Null(request.Headers.Authorization); Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            requests++;
            if (requests == 1) { Assert.Equal("?limit=1000", request.RequestUri!.Query); return Json("""{"data":[{"id":"claude-a"}],"has_more":true,"last_id":"provider/model+one"}"""); }
            Assert.Contains("after_id=provider%2Fmodel%2Bone", request.RequestUri!.Query);
            return Json("""{"data":[{"id":"claude-b"}],"has_more":false}""");
        }));
        var result = await new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.Claude, "https://service.example/v1", "test-key");
        Assert.Equal(["claude-a", "claude-b"], result.Models); Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("[\"local-a\",\"local-b\"]")]
    [InlineData("{\"models\":[{\"name\":\"local-a\"},{\"id\":\"local-b\"}]}")]
    public async Task LocalServicesAllowNoKeyAndCommonCompatibleCatalogShapes(string response)
    {
        using var http = new HttpClient(new Handler(request => { Assert.Null(request.Headers.Authorization); return Json(response); }));
        Assert.Equal(["local-a", "local-b"], (await new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.ChatCompletions, "http://localhost:1234/v1", null)).Models);
    }

    [Theory]
    [InlineData(401, "API Key")]
    [InlineData(403, "权限")]
    [InlineData(404, "不支持")]
    [InlineData(405, "不支持")]
    [InlineData(429, "频繁")]
    [InlineData(503, "暂不可用")]
    [InlineData(302, "重定向")]
    public async Task FailureStatusesAreHelpfulAndNeverEchoTheServerBody(int status, string expected)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("sensitive-test-key from upstream") }));
        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key"));
        Assert.Contains(expected, error.Message); Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Theory]
    [InlineData("<html>not an API</html>")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"message\":\"echo-key\"}")]
    public async Task InvalidResponsesDoNotLeakContentsOrDestroyManualModelEntry(string response)
    {
        using var http = new HttpClient(new Handler(_ => Json(response)));
        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key"));
        Assert.Contains("模型", error.Message); Assert.DoesNotContain(response, error.ToString());
    }

    [Fact]
    public async Task CatalogBoundsLargeListsAndRejectsOversizeResponses()
    {
        using var http = new HttpClient(new Handler(_ => Json(JsonSerializer.Serialize(new { data = Enumerable.Range(0, 2050).Select(i => new { id = "model-" + i }) }))));
        var result = await new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key");
        Assert.Equal(TranslationModelCatalog.MaximumModels, result.Models.Count); Assert.True(result.IsTruncated);
        using var oversized = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]) }));
        Assert.Contains("过大", (await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(oversized).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key"))).Message);
    }

    [Fact]
    public async Task MissingAndInvalidKeysNeverReachTheNetwork()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("must not request")));
        foreach (var key in new[] { "", "test\nkey" })
            await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", key));
    }

    [Fact]
    public async Task RepeatedPaginationCursorStopsInsteadOfLooping()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return Json("""{"data":[{"id":"same-model"}],"has_more":true,"last_id":"same-model"}"""); }));
        await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.Claude, "https://service.example", "test-key"));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndTransportFailureIsSanitized()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var http = new HttpClient(new Handler(_ => Json("{\"data\":[]}")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranslationModelCatalog(http).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key", cancellation.Token));
        using var failed = new HttpClient(new Handler(_ => throw new HttpRequestException("do-not-echo-key")));
        var error = await Assert.ThrowsAsync<ModelCatalogException>(() => new TranslationModelCatalog(failed).ReadAsync(TranslationProviderKind.OpenAI, "https://service.example", "test-key"));
        Assert.DoesNotContain("do-not-echo", error.ToString());
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    }
}
