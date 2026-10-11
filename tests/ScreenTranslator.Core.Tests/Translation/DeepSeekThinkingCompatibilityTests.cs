using System.Net;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class DeepSeekThinkingCompatibilityTests
{
    [Theory]
    [InlineData(ThinkingLevel.Medium, null, "low")]
    [InlineData(ThinkingLevel.XHigh, null, "high")]
    [InlineData(ThinkingLevel.High, ThinkingLevel.Medium, "low")]
    [InlineData(ThinkingLevel.Low, ThinkingLevel.XHigh, "high")]
    [InlineData(ThinkingLevel.High, ThinkingLevel.Minimal, "none")]
    public async Task Direct_Construction_And_Request_Overrides_Use_Legal_DeepSeek_Efforts(
        ThinkingLevel configured, ThinkingLevel? requestOverride, string expected)
    {
        var handler = new Capture();
        var provider = new DeepSeekTranslationProvider(new HttpClient(handler), new Secret(), new() { ThinkingLevel = configured });
        var request = new TranslationRequest("en", "zh-CN", TranslationStyle.Natural, "", [new("b1", "Hello", 1, new(0, 0, 10, 10), 0)])
            { ThinkingOverride = requestOverride };
        await provider.TranslateRawAsync(request, default);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, payload.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(expected == "none" ? "disabled" : "enabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"{\"blocks\":[{\"id\":\"b1\",\"translation\":\"你好\"}]}"},"finish_reason":"stop"}]}""") };
        }
    }
    private sealed class Secret : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<string?>("test-key");
        public Task SetAsync(string key, string value, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
