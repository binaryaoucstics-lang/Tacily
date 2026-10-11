using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Authentication;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace BrowserPageQa;

/// <summary>Constructed by live mode only; transport and credentials stay lazy until a provider is requested.</summary>
internal sealed class LiveRuntime : IDisposable
{
    private readonly object _gate = new();
    private ApiHttpClientRouter? _router;
    private DpapiSecretStore? _secrets;
    private ChatGptAccountService? _accounts;

    public static async Task<AppSettings> LoadSettingsAsync()
    {
        // JsonSettingsStore.LoadAsync backs up corrupt settings. This read-only host must not use it.
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlobalAITranslator", "settings.json");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadSettingsAsync(stream).ConfigureAwait(false);
    }

    internal static async Task<AppSettings> ReadSettingsAsync(Stream stream)
    {
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .ConfigureAwait(false) ?? throw new JsonException();
        return settings.Migrate(out _);
    }

    public ITranslationProvider CreateProvider(AppSettings settings)
    {
        lock (_gate)
        {
            _router ??= new ApiHttpClientRouter();
            _secrets ??= new DpapiSecretStore();
            // Only token retrieval/normal renewal is used. SignInAsync is never called.
            _accounts ??= new ChatGptAccountService(_secrets,
                _ => throw new TranslationAuthenticationException("Interactive login is unavailable."));
            return TranslationProviderFactory.Create(_router.GetClient(settings), _secrets, settings,
                thinkingOverride: ThinkingLevel.None,
                timeout: TimeSpan.FromSeconds(settings.TranslationProvider == TranslationProviderKind.DeepSeek ? 45 : 60),
                chatGptTokens: _accounts);
        }
    }

    public void Dispose() { lock (_gate) _router?.Dispose(); }
}
