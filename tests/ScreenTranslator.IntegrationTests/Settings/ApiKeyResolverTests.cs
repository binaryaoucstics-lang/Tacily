using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests.Settings;

public sealed class ApiKeyResolverTests
{
    [Fact]
    public async Task ResolveAsync_Prefers_Replacement_Without_Reading_Storage()
    {
        var store = new RecordingSecretStore("stored-key");

        var resolved = await ApiKeyResolver.ResolveAsync(
            "  replacement-key  ",
            store);

        Assert.Equal("replacement-key", resolved);
        Assert.Equal(0, store.GetCount);
    }

    [Fact]
    public async Task ResolveAsync_Uses_Secure_Storage_When_Input_Is_Blank()
    {
        var store = new RecordingSecretStore("stored-key");

        var resolved = await ApiKeyResolver.ResolveAsync(" ", store);

        Assert.Equal("stored-key", resolved);
        Assert.Equal(1, store.GetCount);
        Assert.Equal(
            DeepSeekTranslationProvider.ApiKeyName,
            store.LastRequestedKey);
    }

    private sealed class RecordingSecretStore(string? value) : ISecretStore
    {
        public int GetCount { get; private set; }

        public string? LastRequestedKey { get; private set; }

        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            LastRequestedKey = key;
            return Task.FromResult(value);
        }

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
