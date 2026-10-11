using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class DeepSeekTranslationProviderTests
{
    [Fact]
    public async Task Ovis_Document_Sends_Text_Regions_And_Order_Without_An_Image()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"blocks\":[]}"}}]}""");
        var request = CreateRequest() with { IsOcrDocument = true,
            AtlasRegions = [new ImageAtlasRegion("b1", new(10, 20, 100, 30), default) { IsVertical = true }] };
        await CreateProvider(handler).TranslateRawAsync(request, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(DeepSeekTranslationProvider.OcrGroupingPrompt, messages[0].GetProperty("content").GetString());
        var content = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        using var metadata = JsonDocument.Parse(content.GetString()!);
        var block = metadata.RootElement.GetProperty("blocks")[0];
        Assert.True(block.TryGetProperty("text", out _));
        Assert.True(block.TryGetProperty("order", out _));
        Assert.Equal(4, block.GetProperty("region").GetArrayLength());
        Assert.Equal("vertical-rtl", block.GetProperty("direction").GetString());
        Assert.DoesNotContain("image_url", handler.Body);
    }

    [Fact]
    public async Task Atlas_Request_Sends_One_Crop_Image_And_No_Coordinate_Metadata()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"blocks\":[]}"}}]}""");
        var request = CreateRequest() with
        {
            ImagePng = [1, 2, 3], ImageTextCropPng = [99, 88], ImageTextCropBounds = new PixelRect(500, 900, 300, 100),
            AtlasRegions = [new ImageAtlasRegion("b1", new PixelRect(500, 900, 300, 100), new PixelRect(10, 34, 300, 100))],
            ImageSize = new PixelSize(320, 144),
        };
        await CreateProvider(handler).TranslateRawAsync(request, CancellationToken.None);
        using var payload = JsonDocument.Parse(handler.Body);
        var messages = payload.RootElement.GetProperty("messages");
        var system = messages[0].GetProperty("content").GetString();
        Assert.StartsWith(DeepSeekTranslationProvider.AtlasPrompt, system);
        Assert.Contains("pure numerals/formulas", system);
        var parts = messages[1].GetProperty("content");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal(request.ImagePng, Convert.FromBase64String(parts[1].GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1]));
        var metadata = parts[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("bounds", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("imageSize", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("900", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whole_Image_And_Text_Detail_Keep_Their_Order_And_Explicit_Coordinate_Reference()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"blocks\":[]}"}}]}""");
        var request = CreateRequest() with
        { ImagePng = [1, 2, 3], ImageTextCropPng = [4, 5, 6], ImageTextCropBounds = new PixelRect(0, 0, 50, 10), LocateImageText = true };
        await CreateProvider(handler).TranslateRawAsync(request, CancellationToken.None);
        using var payload = JsonDocument.Parse(handler.Body);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Contains("FIRST", messages[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
        var parts = messages[1].GetProperty("content");
        Assert.Equal(4, parts.GetArrayLength());
        Assert.Equal(request.ImagePng, Convert.FromBase64String(parts[1].GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1]));
        Assert.Equal(request.ImageTextCropPng, Convert.FromBase64String(parts[3].GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1]));
        using var metadata = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
        Assert.Equal(100, metadata.RootElement.GetProperty("imageSize").GetProperty("width").GetInt32());
        Assert.Equal(50, metadata.RootElement.GetProperty("imageTextCropBounds").GetProperty("width").GetInt32());
    }

    [Theory]
    [InlineData(ThinkingLevel.None, "disabled", "none")]
    [InlineData(ThinkingLevel.Low, "enabled", "low")]
    [InlineData(ThinkingLevel.High, "enabled", "high")]
    [InlineData(ThinkingLevel.Max, "enabled", "max")]
    public async Task Image_Request_Preserves_Png_And_Uses_Actual_Thinking_Parameters(ThinkingLevel level, string toggle, string effort)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"blocks\":[]}"}}]}""");
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new FixedSecretStore("test-key"), new DeepSeekOptions { ThinkingLevel = level });
        await provider.TranslateRawAsync(CreateRequest() with { ImagePng = png }, CancellationToken.None);
        using var payload = JsonDocument.Parse(handler.Body);
        var root = payload.RootElement;
        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal(toggle, root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(effort, root.GetProperty("reasoning_effort").GetString());
        var image = root.GetProperty("messages")[1].GetProperty("content")[1];
        Assert.Equal("image_url", image.GetProperty("type").GetString());
        var uri = image.GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", uri);
        Assert.Equal(png, Convert.FromBase64String(uri.Split(',')[1]));
        Assert.DoesNotContain("temperature", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_After_Http_Request_Started_Stops_The_InFlight_Request()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}", TimeSpan.FromSeconds(10));
        var provider = CreateProvider(handler);
        using var source = new CancellationTokenSource();
        var request = provider.TranslateRawAsync(CreateRequest(), source.Token);
        while (handler.Path is null) await Task.Yield();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task Provider_Sends_Current_Flash_NonThinking_Json_Request()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{\"blocks\":[]}"}}]}""");
        var provider = CreateProvider(handler);

        await provider.TranslateRawAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("/chat/completions", handler.Path);
        Assert.Equal("Bearer test-key", handler.Authorization);
        Assert.Contains("\"model\":\"deepseek-flash\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", handler.Body, StringComparison.Ordinal);
        Assert.Contains("JSON", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(TranslationAuthenticationException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(TranslationAuthenticationException))]
    [InlineData(HttpStatusCode.NotFound, typeof(TranslationConfigurationException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(TranslationRateLimitException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(TranslationUnavailableException))]
    public async Task Provider_Maps_Http_Status(
        HttpStatusCode statusCode,
        Type expectedException)
    {
        var provider = CreateProvider(new RecordingHandler(statusCode, "{}"));

        var exception = await Record.ExceptionAsync(
            () => provider.TranslateRawAsync(CreateRequest(), CancellationToken.None));

        Assert.IsType(expectedException, exception);
    }

    [Fact]
    public async Task Provider_Maps_Timeout_But_Preserves_User_Cancellation()
    {
        var timeoutProvider = CreateProvider(
            new RecordingHandler(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(1)),
            TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<TranslationUnavailableException>(
            () => timeoutProvider.TranslateRawAsync(CreateRequest(), CancellationToken.None));

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelledProvider = CreateProvider(
            new RecordingHandler(HttpStatusCode.OK, "{}", delay: TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledProvider.TranslateRawAsync(CreateRequest(), cancellationSource.Token));
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"{}"}}]}""")]
    public async Task Provider_Rejects_Missing_Empty_Or_Truncated_Content(string response)
    {
        var provider = CreateProvider(new RecordingHandler(HttpStatusCode.OK, response));

        await Assert.ThrowsAsync<TranslationFormatException>(
            () => provider.TranslateRawAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Provider_Does_Not_Send_Request_Without_Key()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var provider = new DeepSeekTranslationProvider(
            new HttpClient(handler),
            new FixedSecretStore(null),
            new DeepSeekOptions());

        await Assert.ThrowsAsync<TranslationAuthenticationException>(
            () => provider.TranslateRawAsync(CreateRequest(), CancellationToken.None));

        Assert.Null(handler.Path);
    }

    private static DeepSeekTranslationProvider CreateProvider(
        RecordingHandler handler,
        TimeSpan? timeout = null) =>
        new(
            new HttpClient(handler),
            new FixedSecretStore("test-key"),
            new DeepSeekOptions
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15),
            });

    private static TranslationRequest CreateRequest() =>
        new(
            "auto",
            "zh-CN",
            TranslationStyle.Natural,
            string.Empty,
            [new OcrBlock("b1", "Hello", 1, new PixelRect(0, 0, 100, 20), 0)]);

    private sealed class FixedSecretStore(string? secret) : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret);

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string response,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? Authorization { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (delay is not null)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
