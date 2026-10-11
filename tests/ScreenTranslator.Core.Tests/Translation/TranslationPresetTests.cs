using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationPresetTests
{
    private const string Answer = """{"blocks":[{"id":"b1","translation":"你好"}]}""";
    private static TranslationRequest Request() => new("en", "zh-CN", TranslationStyle.Natural, "",
        [new("b1", "Hello", 1, new(0, 0, 40, 20), 0)]) { IsPageText = true };

    [Theory]
    [InlineData(TranslationProviderKind.ChatCompletions, "/proxy/v1/chat/completions")]
    [InlineData(TranslationProviderKind.OpenAI, "/proxy/v1/responses")]
    [InlineData(TranslationProviderKind.Claude, "/proxy/v1/messages")]
    public async Task Custom_Preset_Selects_Protocol_Endpoint_Model_And_Own_Key(TranslationProviderKind kind, string endpoint)
    {
        var preset = new TranslationProviderPreset { Provider = kind, Model = "custom-model", BaseUrl = "https://service.example/proxy/v1" };
        var settings = new AppSettings { TranslationProviderPresets = [preset], SelectedTranslationPresetId = preset.Id };
        var secrets = new Secrets(TranslationProviderConfiguration.PresetSecretName(preset.Id));
        using var handler = new Handler(Envelope(kind));
        using var http = new HttpClient(handler);
        var provider = TranslationProviderFactory.Create(http, secrets, settings);
        Assert.Equal(Answer, await provider.TranslateRawAsync(Request(), default));
        Assert.Equal(endpoint, handler.Path);
        Assert.Equal(secrets.Expected, secrets.Read);
        Assert.Equal(kind == TranslationProviderKind.Claude ? "private-test-key" : null, handler.ClaudeKey);
        Assert.Equal(kind == TranslationProviderKind.Claude ? null : "Bearer private-test-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("custom-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("https://service.example", "https://service.example/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/chat/completions")]
    [InlineData("https://service.example/api/chat/completions/", "https://service.example/api/chat/completions")]
    public void Chat_Endpoint_Is_Normalized_Once(string source, string expected) => Assert.Equal(expected,
        TranslationProviderConfiguration.Endpoint(TranslationProviderKind.ChatCompletions, source).AbsoluteUri);

    [Theory]
    [InlineData("https://service.example/v1/responses")]
    [InlineData("https://key@service.example/v1")]
    [InlineData("https://service.example/v1?api_key=private")]
    public void Wrong_Protocol_And_Credential_Urls_Are_Rejected(string source) => Assert.Throws<TranslationConfigurationException>(
        () => TranslationProviderConfiguration.Endpoint(TranslationProviderKind.ChatCompletions, source));

    [Fact]
    public async Task Generic_Chat_Streams_And_Uses_Full_Image_Without_DeepSeek_Parameters()
    {
        const string imageAnswer = """{"blocks":[{"id":"p1","box":[100,100,700,700],"area":[100,100,700,700],"flow":"h","translation":"你好"}]}""";
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = imageAnswer }, finish_reason = "stop" } } });
        using var handler = new Handler("data: " + payload + "\n\ndata: [DONE]\n\n", true);
        using var http = new HttpClient(handler);
        var preset = new TranslationProviderPreset { Model = "vision-model", BaseUrl = "http://localhost:1234/v1/chat/completions" };
        var settings = new AppSettings { TranslationProviderPresets = [preset], SelectedTranslationPresetId = preset.Id };
        var secrets = new Secrets(TranslationProviderConfiguration.PresetSecretName(preset.Id));
        var provider = (IStreamingTranslationProvider)TranslationProviderFactory.Create(http, secrets, settings);
        var updates = new ProgressCollector();
        Assert.Equal(imageAnswer, await provider.TranslateRawStreamingAsync(Request() with
            { IsPageText = false, ImagePng = [1, 2, 3], ImageSize = new(40, 40), IsDirectVision = true }, updates, default));
        Assert.NotEmpty(updates.Updates);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        var image = root.GetProperty("messages")[1].GetProperty("content").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "image_url");
        Assert.Equal("data:image/png;base64,AQID", image.GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("/v1/chat/completions", handler.Path);
    }

    [Fact]
    public void Presets_Isolate_Cache_And_Key_Identity_While_Renames_Preserve_Cache()
    {
        var first = new TranslationProviderPreset { Model = "model-a" };
        var second = first with { Id = Guid.NewGuid().ToString("N"), Name = "second" };
        var settings = new AppSettings { TranslationProviderPresets = [first, second], SelectedTranslationPresetId = first.Id };
        var alternate = settings with { SelectedTranslationPresetId = second.Id };
        Assert.NotEqual(TranslationProviderConfiguration.TransportIdentity(settings), TranslationProviderConfiguration.TransportIdentity(alternate));
        Assert.NotEqual(TranslationProviderConfiguration.SecretName(settings), TranslationProviderConfiguration.SecretName(alternate));
        Assert.Equal(TranslationProviderConfiguration.TransportIdentity(settings), TranslationProviderConfiguration.TransportIdentity(settings with
            { TranslationProviderPresets = [first with { Name = "renamed" }, second] }));
    }

    [Fact]
    public void Migration_Preserves_Legacy_Selection_And_Rejects_Untrusted_Preset_Ids()
    {
        var legacy = new AppSettings { Version = 11, TranslationProvider = TranslationProviderKind.Claude, ClaudeModel = "existing-model" };
        var migrated = legacy.Migrate(out _);
        Assert.Equal("claude-api-key", TranslationProviderConfiguration.SecretName(migrated));
        Assert.Equal("existing-model", TranslationProviderConfiguration.Model(migrated));
        Assert.Equal("Claude\nexisting-model\nhttps://api.anthropic.com/v1", TranslationProviderConfiguration.TransportIdentity(migrated));
        var good = new TranslationProviderPreset { Model = "custom" };
        var invalid = good with { Id = "../private" };
        var repaired = (legacy with { TranslationProviderPresets = [good, invalid, good], SelectedTranslationPresetId = invalid.Id }).Migrate(out _);
        Assert.Single(repaired.TranslationProviderPresets);
        Assert.Equal("", repaired.SelectedTranslationPresetId);
        Assert.Throws<TranslationConfigurationException>(() => TranslationProviderConfiguration.PresetSecretName(invalid.Id));
    }

    [Fact]
    public async Task Missing_Preset_Key_Does_Not_Fall_Back_To_Another_Provider()
    {
        var preset = new TranslationProviderPreset { Model = "custom" };
        using var http = new HttpClient(new Handler(Envelope(preset.Provider)));
        var secrets = new Secrets("deepseek-api-key");
        var provider = TranslationProviderFactory.Create(http, secrets, new AppSettings
            { TranslationProviderPresets = [preset], SelectedTranslationPresetId = preset.Id });
        await Assert.ThrowsAsync<TranslationAuthenticationException>(() => provider.TranslateRawAsync(Request(), default));
        Assert.Equal(TranslationProviderConfiguration.PresetSecretName(preset.Id), secrets.Read);
    }

    private static string Envelope(TranslationProviderKind kind) => kind switch
    {
        TranslationProviderKind.OpenAI => JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = Answer } } } } }),
        TranslationProviderKind.Claude => JsonSerializer.Serialize(new { type = "message", role = "assistant", content = new[] { new { type = "text", text = Answer } }, stop_reason = "end_turn" }),
        _ => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Answer }, finish_reason = "stop" } } }),
    };
    private sealed class Secrets(string expected) : ISecretStore
    {
        public string Expected { get; } = expected;
        public string? Read;
        public Task<string?> GetAsync(string key, CancellationToken token = default)
        { Read = key; return Task.FromResult<string?>(key == Expected ? "private-test-key" : null); }
        public Task SetAsync(string key, string value, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Handler(string response, bool stream = false) : HttpMessageHandler
    {
        public string? Body, Path, Authorization, ClaudeKey;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.Authorization?.ToString();
            ClaudeKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
    private sealed class ProgressCollector : IProgress<TranslationStreamUpdate>
    { public List<TranslationStreamUpdate> Updates { get; } = []; public void Report(TranslationStreamUpdate update) => Updates.Add(update); }
}
