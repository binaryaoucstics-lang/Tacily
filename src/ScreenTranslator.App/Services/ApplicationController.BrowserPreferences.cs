using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private bool HandleBrowserQuickSettings(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type != "browserQuickSettings") return false;
        try {
            if (!_browserConnections.TryGetValue(args.ConnectionId, out var kind) || kind != BrowserKind.Chrome
                || root.GetProperty("frameId").GetInt32() != 0) return true;
            var id = root.GetProperty("requestId").GetString(); var document = root.GetProperty("documentToken").GetString();
            var tab = root.GetProperty("tabId").GetInt32(); var window = root.GetProperty("browserWindowId").GetInt32();
            var generation = root.GetProperty("generation").GetInt64(); var host = OptionalPageString(root, "siteHost");
            if (id is not { Length: > 0 and <= 64 } || document is not { Length: > 0 and <= 256 } || tab < 0 || window < 0
                || host is not null && (host.Length > 253 || BrowserSitePolicy.NormalizeDomain(host) != host)
                || !BrowserQuickSettingsPatch.TryRead(root.GetProperty("changes"), out var patch)) return true;
            _ = ApplyBrowserQuickSettingsAsync(args.ConnectionId, id, document, tab, window, generation, host, patch!);
        } catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
        return true;
    }

    private async Task ApplyBrowserQuickSettingsAsync(Guid connection, string id, string document, int tab, int window,
        long generation, string? host, BrowserQuickSettingsPatch patch)
    {
        var ok = false; var reason = "settings_unavailable";
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var current = await QueryAutoDocumentAsync(connection, tab, window, document, timeout.Token).ConfigureAwait(false);
            if (current is not { Active: true } || current.SiteHost != host || generation != Volatile.Read(ref _autoPageConfiguration).Generation)
                reason = "document_changed";
            else {
                var operation = await _application.Dispatcher.InvokeAsync(async () => {
                    if (_disposed || generation != Volatile.Read(ref _autoPageConfiguration).Generation) return false;
                    var next = patch.Apply(CollectSettings(), host ?? "");
                    await SaveSettingsAsync(next);
                    _applyingSettings = true;
                    try {
                        if (patch.AutoPage.HasValue) GeneralSettings.BrowserAutoPageTranslationEnabled = next.BrowserAutoPageTranslationEnabled;
                        if (patch.AutoImages.HasValue) GeneralSettings.BrowserAutoImageTranslationEnabled = next.BrowserAutoImageTranslationEnabled;
                        if (patch.Concurrency.HasValue) GeneralSettings.BrowserPageTranslationConcurrency = next.BrowserPageTranslationConcurrency;
                        if (patch.TargetLanguage is not null) {
                            GeneralSettings.TargetLanguage = LanguageLabel(next.TargetLanguage); TranslationSettings.TargetLanguage = LanguageLabel(next.TargetLanguage);
                        }
                        if (patch.SiteMode is not null) {
                            GeneralSettings.BrowserTranslationWhitelist = next.BrowserTranslationWhitelist;
                            GeneralSettings.BrowserTranslationBlacklist = next.BrowserTranslationBlacklist;
                        }
                    } finally { _applyingSettings = false; }
                    SyncAutoImageConfiguration(); return true;
                });
                ok = await operation.ConfigureAwait(false);
                await SendAutoImageConfigurationAsync(connection).ConfigureAwait(false);
                await SendAutoPageConfigurationAsync(connection).ConfigureAwait(false);
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or InvalidOperationException) { }
        try {
            if (_disposed || _browserBridgeServer is null) return;
            using var replyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(connection, JsonSerializer.Serialize(new { type = "browserQuickSettingsResult",
                requestId = id, documentToken = document, tabId = tab, ok, reason = ok ? null : reason }), replyTimeout.Token).ConfigureAwait(false);
        } catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }
}
