using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using ScreenTranslator.Core.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private readonly PageLanguageJudge _autoPageJudge = new();
    private BrowserPageDecisionMemo _pageDecisionMemo = new();
    private BrowserPageAutoCoordinator? _autoPageCoordinator;
    private BrowserPageTranslationCache? _pageTextCache;
    private long _pageCacheEpoch;
    private AutoPageConfiguration _autoPageConfiguration = new(new AppSettings(), 0, null);

    private void InitializeAutoPages()
    {
        _pageTextCache = new(directory: BrowserPageTranslationCache.DefaultDirectory,
            maximumDiskBytes: _persistedSettings.BrowserPageCacheEnabled ? _persistedSettings.BrowserPageCacheMegabytes * 1024L * 1024 : 0);
        _pageDecisionMemo = new(persistentCache: _pageTextCache);
        _autoPageCoordinator = new(JudgeAutoPageAsync, TranslateAutoPageAsync, IsAutoPageActiveAsync,
            SendAutoPagePacketAsync, error =>
            {
                AppDiagnostics.Write($"browser_page_auto_failed type={error.GetType().Name} code={PageAutoFailure.Code(error)}");
                if (!_disposed) _application.Dispatcher.BeginInvoke(() =>
                    MainWindow.StatusText = "网页翻译未完成，可检查 API 设置后恢复翻译");
            });
    }

    private void SyncAutoPageConfiguration(AppSettings settings, long generation)
    {
        if (_disposed || _autoPageCoordinator is null) return;
        _pageTextCache?.ConfigureDiskLimit(settings.BrowserPageCacheEnabled ? settings.BrowserPageCacheMegabytes * 1024L * 1024 : 0);
        // Capture this exact settings object in each factory. A reconfiguration cancels
        // the old scope before any later paragraph can create an old model request.
        var previous = Volatile.Read(ref _autoPageConfiguration);
        var sameTransport = TranslationProviderConfiguration.ConnectionIdentity(previous.Settings) == TranslationProviderConfiguration.ConnectionIdentity(settings)
            && previous.Settings.BriefThinkingGuidanceEnabled == settings.BriefThinkingGuidanceEnabled;
        var service = sameTransport && previous.Service is not null ? previous.Service : new BrowserPageTranslationService(_ =>
        {
            var provider = TranslationProviderFactory.Create(_apiHttp.GetClient(settings), _secretStore, settings,
                thinkingOverride: ThinkingLevel.None, timeout: TimeSpan.FromSeconds(settings.TranslationProvider == TranslationProviderKind.DeepSeek ? 45 : 60), chatGptTokens: _chatGpt);
            DeepSeekRequestDiagnostics.Observe(provider, "page-auto");
            return provider;
        }, cache: _pageTextCache, diagnostic: AppDiagnostics.Write);
        Volatile.Write(ref _autoPageConfiguration, new(settings, generation, service));
        _autoPageCoordinator.Configure(settings, generation);
        foreach (var connection in _browserConnections.Keys) _ = SendAutoPageConfigurationAsync(connection);
    }

    private async Task SendAutoPageConfigurationAsync(Guid connection)
    {
        try
        {
            var config = Volatile.Read(ref _autoPageConfiguration);
            if (_disposed || _browserBridgeServer is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(connection, BrowserPageAutoProtocol.SerializeConfiguration(
                config.Settings, config.Generation, _browserConnections.GetValueOrDefault(connection) == BrowserKind.Chrome,
                PageCacheScope(config.Settings), Volatile.Read(ref _pageCacheEpoch)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private bool HandleAutoPageMessage(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type is not ("pageAutoCandidate" or "pageAutoCancel" or "pageAutoControl")) return false;
        try
        {
            if (!_browserConnections.TryGetValue(args.ConnectionId, out var browser) || browser != BrowserKind.Chrome
                || root.GetProperty("frameId").GetInt32() != 0) return true;
            var tab = root.GetProperty("tabId").GetInt32();
            var window = root.GetProperty("browserWindowId").GetInt32();
            var documentToken = root.GetProperty("documentToken").GetString();
            var generation = root.GetProperty("generation").GetInt64();
            var revision = root.GetProperty("revision").GetInt64();
            if (tab < 0 || window < 0 || documentToken is not { Length: > 0 and <= 256 }
                || generation < 0 || revision is < 0 or > 9_007_199_254_740_991) return true;
            if (type == "pageAutoCancel")
            {
                var requestId = OptionalPageString(root, "requestId");
                if (requestId?.Length is > 64) return true;
                _autoPageCoordinator?.Cancel(args.ConnectionId, tab, documentToken, generation, revision, requestId);
                return true;
            }
            if (type == "pageAutoControl")
            {
                var action = OptionalPageString(root, "action");
                if (action == "pause" && generation == Volatile.Read(ref _autoPageConfiguration).Generation)
                {
                    _autoPageCoordinator?.PauseThroughRevision(args.ConnectionId, tab, documentToken, generation, revision);
                    // Full page pause also stops an in-flight automatic image request.
                    // Manual image translation owns a separate session and is unaffected.
                    _autoImageQueue?.CancelDocument(args.ConnectionId, tab, documentToken);
                }
                else if (action == "resume") _autoPageCoordinator?.Resume(args.ConnectionId, tab,
                    documentToken, generation, revision);
                // Original is a local display toggle; it never interrupts an ongoing stream.
                return true;
            }
            if (BrowserPageAutoProtocol.TryReadCandidate(args.ConnectionId, root, out var candidate))
                _ = PrepareAndQueueAutoPageAsync(candidate!);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
        return true;
    }

    private async Task PrepareAndQueueAutoPageAsync(AutoPageCandidate candidate)
    {
        var configuration = Volatile.Read(ref _autoPageConfiguration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Pure program judgment is independent of API worker occupancy. A new
            // page can be recognized even while four older pages are translating.
            var assessment = candidate.ManualMode is null && candidate.Samples is not null
                ? await JudgeAndRecordAsync(candidate, configuration.Settings, timeout.Token).ConfigureAwait(false) : null;
            if (_autoPageCoordinator?.TryEnqueue(candidate, assessment,
                    configuration.Settings.BrowserChineseProseShareThresholdPercent) == true)
            {
                if (assessment is not null)
                    await _autoPageCoordinator.AnnounceAssessmentAsync(candidate, assessment, timeout.Token).ConfigureAwait(false);
            }
            else await SendRejectedAutoPageAsync(candidate, _autoPageCoordinator?.RejectionReason(candidate) ?? "desktop_unavailable").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await SendRejectedAutoPageAsync(candidate, "page_unavailable").ConfigureAwait(false); }
        catch (Exception error)
        {
            AppDiagnostics.Write($"browser_page_prepare_failed code={PageAutoFailure.Code(error)} type={error.GetType().Name}");
            await SendRejectedAutoPageAsync(candidate, PageAutoFailure.Code(error)).ConfigureAwait(false);
        }
    }

    private Task<PageTranslationAssessment> JudgeAutoPageAsync(AutoPageCandidate candidate,
        AppSettings settings, CancellationToken token) => JudgeAndRecordAsync(candidate, settings, token);

    private async Task<PageTranslationAssessment> JudgeAndRecordAsync(AutoPageCandidate candidate,
        AppSettings settings, CancellationToken token)
    {
        var rule = BrowserSitePolicy.Resolve(candidate.SiteHost, settings);
        if (candidate.ManualMode is null && rule.Mode is "block" or "allow")
            return new(rule.Mode == "allow", rule.Mode == "allow" ? "网站白名单" : "网站黑名单", 1, "", false)
                { ReasonCode = rule.Mode == "allow" ? "site-whitelist" : "site-blacklist" };
        var (assessment, cached) = await _pageDecisionMemo.AssessAsync(candidate.Samples ?? [], settings.TargetLanguage,
            settings.BrowserPageJudgementMode, candidate.Statistics,
            current => _autoPageJudge.JudgeAsync(candidate.Samples ?? [], settings.TargetLanguage,
                settings.BrowserPageJudgementMode, current, candidate.Statistics,
                settings.BrowserChineseProseShareThresholdPercent), token,
            settings.BrowserChineseProseShareThresholdPercent).ConfigureAwait(false);
        AppDiagnostics.Write($"browser_page_assessment code={assessment.ReasonCode} needs={assessment.NeedsTranslation} rules=true cached={cached} ms={assessment.ElapsedMilliseconds:F0}");
        return assessment;
    }

    private async Task<BrowserPageTranslationResult> TranslateAutoPageAsync(AutoPageCandidate candidate,
        AppSettings settings, Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask> update,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var configuration = Volatile.Read(ref _autoPageConfiguration);
        if (!ReferenceEquals(settings, configuration.Settings)
                && !BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, configuration.Settings)
            || configuration.Generation != candidate.Generation
            || configuration.Service is null) throw new OperationCanceledException(token);
        var result = await configuration.Service.TranslateAsync(candidate.Blocks, new()
        {
            SourceLanguage = settings.SourceLanguage, TargetLanguage = settings.TargetLanguage,
            Style = settings.TranslationStyle, Context = settings.TranslationContext.Length <= 2048
                ? settings.TranslationContext : settings.TranslationContext[..2048],
            Parallelism = Math.Clamp(settings.BrowserPageTranslationConcurrency, 1, 10),
            CacheScope = PageCacheScope(settings),
            ExplicitSelection = candidate.ManualMode == "selection",
        }, update, token).ConfigureAwait(false);
        var mode = candidate.ManualMode is "selection" or "page" ? candidate.ManualMode : "auto";
        AppDiagnostics.Write($"browser_page_translation_complete mode={mode} blocks={candidate.Blocks.Count} cache_hits={result.CacheHits} requests={result.RequestCount} failed={result.FailedCount}");
        return result;
    }

    private async Task<bool> IsAutoPageActiveAsync(AutoPageCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || _autoPageCoordinator?.IsCurrent(candidate) != true ||
            !_browserConnections.TryGetValue(candidate.ConnectionId, out var kind) || kind != BrowserKind.Chrome) return false;
        var active = await QueryAutoDocumentAsync(candidate.ConnectionId, candidate.TabId, candidate.WindowId,
            candidate.DocumentToken, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return active is { Active: true } && (candidate.SiteHost is null || active.SiteHost == candidate.SiteHost)
            && (candidate.ManualMode is not null || BrowserSitePolicy.Resolve(active.SiteHost, Volatile.Read(ref _autoPageConfiguration).Settings).Mode != "block");
    }

    private async Task SendAutoPagePacketAsync(AutoPageCandidate candidate, AutoPagePacket packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || _browserBridgeServer is null || _autoPageCoordinator?.IsCurrent(candidate) != true)
            throw new OperationCanceledException(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await _browserBridgeServer.SendAsync(candidate.ConnectionId, BrowserPageAutoProtocol.Serialize(candidate, packet),
            timeout.Token).ConfigureAwait(false);
    }

    private async Task SendRejectedAutoPageAsync(AutoPageCandidate candidate, string? reason = null)
    {
        try
        {
            var config = Volatile.Read(ref _autoPageConfiguration);
            if (_disposed || _browserBridgeServer is null || config.Generation != candidate.Generation) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Rejections contain no page content. Chrome already admitted this
            // document and validates its token again when routing the packet.
            // A second probe could time out and leave preparation waiting silently.
            await _browserBridgeServer.SendAsync(candidate.ConnectionId, BrowserPageAutoProtocol.Serialize(candidate,
                new("pageAutoTranslations", Finished: true, Error: reason ?? (candidate.ManualMode is null
                    ? "automatic-queue-unavailable" : "manual-queue-unavailable"))), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private void CancelAutoPagesForConnection(Guid connection) => _autoPageCoordinator?.ConnectionClosed(connection);
    private void CancelAutoPagesForDocument(Guid connection, BrowserInvalidated invalidated) =>
        _autoPageCoordinator?.DocumentInvalidated(connection, invalidated.TabId, invalidated.DocumentToken);
    private async Task DisposeAutoPagesAsync()
    {
        if (_autoPageCoordinator is not null) await _autoPageCoordinator.DisposeAsync().ConfigureAwait(false);
        if (_pageTextCache is not null) await _pageTextCache.DisposeAsync().ConfigureAwait(false);
    }

    private static string PageCacheScope(AppSettings settings)
    {
        var identity = JsonSerializer.Serialize(new { transport = TranslationProviderConfiguration.TransportIdentity(settings),
            settings.SourceLanguage, settings.TargetLanguage, settings.TranslationStyle, settings.TranslationContext,
            settings.BriefThinkingGuidanceEnabled, prompt = DeepSeekTranslationProvider.PageTextPrompt, policy = "paragraph-cache-v2-facts-amounts" });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private async void OnClearPageCacheRequested(object? sender, EventArgs args)
    {
        if (_pageTextCache is null) return;
        GeneralSettings.BrowserPageCacheStatus = "正在清理网页译文缓存…";
        try
        {
            _autoPageCoordinator?.CancelAllRequests();
            _pageDecisionMemo.Clear();
            _imageDecisionMemo.Clear();
            Interlocked.Increment(ref _pageCacheEpoch);
            await _pageTextCache.ClearAsync().ConfigureAwait(true);
            GeneralSettings.BrowserPageCacheStatus = "网页译文缓存已清空。";
        }
        catch (Exception) { GeneralSettings.BrowserPageCacheStatus = "缓存清理未完成，请稍后重试。"; }
        finally { if (!_disposed) SyncAutoImageConfiguration(force: true); }
    }

    private static string? OptionalPageString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private sealed record AutoPageConfiguration(AppSettings Settings, long Generation, BrowserPageTranslationService? Service);
}
