using System.IO;
using System.Text.Json;
using System.Reflection;
using ScreenTranslator.App.Services;
using ScreenTranslator.App.Services.Browser;
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

    public static async Task<AppSettings> LoadSettingsAsync(bool useRunningPreset = false)
    {
        // JsonSettingsStore.LoadAsync backs up corrupt settings. This read-only host must not use it.
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlobalAITranslator", "settings.json");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await ReadSettingsAsync(stream).ConfigureAwait(false);
        if (!useRunningPreset) return settings;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var bridge = new BrowserBridgeClient();
        await bridge.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await bridge.WriteAsync("{\"type\":\"bridgeReady\",\"browser\":\"chrome\"}", timeout.Token).ConfigureAwait(false);
        while (true)
        {
            var line = await bridge.ReadAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("running_configuration_unavailable");
            using var frame = JsonDocument.Parse(line);
            if (frame.RootElement.GetProperty("type").GetString() != "pageAutoConfig") continue;
            var scope = frame.RootElement.GetProperty("cacheScope").GetString();
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlobalAITranslator", "secrets");
            // Enumerate names only. Credential contents remain lazy in CreateProvider.
            var ids = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "translation-preset-*.bin")
                .Select(path => Path.GetFileNameWithoutExtension(path)["translation-preset-".Length..]).ToArray() : [];
            return ResolveRunningPreset(settings, scope, ids);
        }
    }

    internal static string CacheScope(AppSettings settings) => (string)(typeof(ApplicationController)
        .GetMethod("PageCacheScope", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidDataException("running_configuration_version_unsupported"))
        .Invoke(null, [settings])!;

    internal static AppSettings ResolveRunningPreset(AppSettings settings, string? scope, IEnumerable<string> ids)
    {
        if (scope is not { Length: 64 } || scope.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("running_configuration_invalid");
        if (scope == CacheScope(settings)) return settings;
        // This recovery is restricted to the user's confirmed official DeepSeek
        // configuration. Never try another provider, model, endpoint or key as fallback.
        if (TranslationProviderConfiguration.Provider(settings) != TranslationProviderKind.DeepSeek)
            throw new InvalidDataException("running_configuration_mismatch");
        var matches = new List<AppSettings>();
        foreach (var id in ids.Where(id => Guid.TryParseExact(id, "N", out _)).Distinct())
        {
            var candidate = settings with { SelectedTranslationPresetId = id,
                TranslationProviderPresets = [new() { Id = id, Provider = TranslationProviderKind.DeepSeek,
                    Model = TranslationProviderConfiguration.Model(settings), BaseUrl = TranslationProviderConfiguration.BaseUrl(settings),
                    Network = TranslationProviderConfiguration.Network(settings), ThinkingLevel = settings.ThinkingLevel }] };
            if (scope == CacheScope(candidate)) matches.Add(candidate);
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException("running_configuration_mismatch");
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
