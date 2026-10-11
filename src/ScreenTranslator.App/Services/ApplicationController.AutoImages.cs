using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using SkiaSharp;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private async Task<IReadOnlyList<OcrBlock>> ReadImageEvidenceAsync(CapturedBitmap image, string target, CancellationToken token)
    {
        try { return DualVisionLocalEvidence.FromAssessment(await _autoImageJudge.AssessAsync(image, target, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            AppDiagnostics.Write($"image_filter_evidence_unavailable type={error.GetType().Name}");
            return [];
        }
    }
    private readonly BrowserImageTranslationJudge _autoImageJudge = new();
    private BrowserImageDecisionMemo _imageDecisionMemo = new();
    private BrowserAutoImageQueue? _autoImageQueue;
    private BrowserAutoImageTranslator? _autoImageTranslator;
    private AutoImageConfiguration _autoImageConfiguration = new(new AppSettings(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private bool _autoImageConfigured;

    private void InitializeAutoImages()
    {
        _imageDecisionMemo = new(persistentCache: _pageTextCache);
        _autoImageTranslator = new(_imageCache, _secretStore, localJudge: _autoImageJudge, apiHttp: _apiHttp, chatGptTokens: _chatGpt);
        _autoImageQueue = new(AssessAutoImageAsync, ReplyAutoImageAsync, TranslateAutoImageAsync,
            error =>
            {
                AppDiagnostics.Write($"browser_auto_failed type={error.GetType().Name}");
                if (!_disposed) _application.Dispatcher.BeginInvoke(() =>
                    MainWindow.StatusText = "自动图片翻译未完成，可检查 API 设置或点击重试");
            });
        SyncAutoImageConfiguration();
    }

    private void SyncAutoImageConfiguration(bool force = false)
    {
        if (_disposed || _autoImageQueue is null) return;
        var settings = CollectSettings();
        var previous = Volatile.Read(ref _autoImageConfiguration);
        if (!force && _autoImageConfigured && BrowserAutoConfigurationPolicy.OnlyAnimationPreferenceChanged(previous.Settings, settings)) {
            Volatile.Write(ref _autoImageConfiguration, previous with { Settings = settings });
            var page = Volatile.Read(ref _autoPageConfiguration);
            Volatile.Write(ref _autoPageConfiguration, page with { Settings = settings });
            foreach (var connection in _browserConnections.Keys) _ = SendTranslationMotionConfigurationAsync(connection);
            return;
        }
        if (!force && _autoImageConfigured && previous.Settings == settings) return;
        var next = new AutoImageConfiguration(settings, !force && BrowserAutoConfigurationPolicy.PreserveManualGeneration(previous.Settings, settings)
            ? previous.Generation : Math.Max(previous.Generation + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        _autoImageConfigured = true;
        Volatile.Write(ref _autoImageConfiguration, next);
        _autoImageQueue.Configure(settings, next.Generation);
        SyncAutoPageConfiguration(settings, next.Generation);
        foreach (var connection in _browserConnections.Keys) _ = SendAutoImageConfigurationAsync(connection);
    }

    private async Task SendAutoImageConfigurationAsync(Guid connection)
    {
        try
        {
            var config = Volatile.Read(ref _autoImageConfiguration);
            if (_disposed || _browserBridgeServer is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(connection, JsonSerializer.Serialize(new
            {
                type = "imageAutoConfig", enabled = config.Settings.BrowserAutoImageTranslationEnabled
                    && _browserConnections.GetValueOrDefault(connection) == BrowserKind.Chrome,
                targetLanguage = config.Settings.TargetLanguage, generation = config.Generation,
                requirePageJudgement = config.Settings.BrowserAutoPageTranslationEnabled,
                siteWhitelist = config.Settings.BrowserTranslationWhitelist, siteBlacklist = config.Settings.BrowserTranslationBlacklist,
            }), timeout.Token).ConfigureAwait(false);
            await SendTranslationMotionConfigurationAsync(connection).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task SendTranslationMotionConfigurationAsync(Guid connection)
    {
        try {
            if (_disposed || _browserBridgeServer is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(connection, JsonSerializer.Serialize(new {
                type = "translationMotionConfig", enabled = Volatile.Read(ref _autoImageConfiguration).Settings.TranslationAnimationsEnabled
            }), timeout.Token).ConfigureAwait(false);
        } catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private bool HandleAutoImageCandidate(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type != "autoImageCandidate") return false;
        try
        {
            var candidate = new AutoImageCandidate(args.ConnectionId, root.GetProperty("requestId").GetString()!,
                root.GetProperty("imageId").GetString()!, root.GetProperty("documentToken").GetString()!,
                root.GetProperty("tabId").GetInt32(), root.GetProperty("browserWindowId").GetInt32(),
                root.GetProperty("generation").GetInt64(), root.GetProperty("previewBase64").GetString()!, OptionalPageString(root, "siteHost"));
            var natural = root.GetProperty("natural");
            if (root.GetProperty("frameId").GetInt32() != 0 || candidate.TabId < 0 || candidate.WindowId < 0
                || candidate.RequestId is not { Length: > 0 and <= 64 } || candidate.ImageId is not { Length: > 0 and <= 64 }
                || candidate.DocumentToken is not { Length: > 0 and <= 256 }
                || candidate.PreviewBase64 is not { Length: > 0 and <= 262144 }
                || natural.GetProperty("width").GetDouble() is not (>= 120 and <= 20000)
                || natural.GetProperty("height").GetDouble() is not (>= 120 and <= 20000)
                || !_browserConnections.TryGetValue(args.ConnectionId, out var browser) || browser != BrowserKind.Chrome) return true;
            if (BrowserSitePolicy.Resolve(candidate.SiteHost, Volatile.Read(ref _autoImageConfiguration).Settings).Mode == "block"
                || _autoImageQueue?.TryEnqueue(candidate) != true)
                _ = ReplyAutoImageAsync(candidate, false, "automatic-queue-unavailable", CancellationToken.None);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        return true;
    }

    private async Task<ImageTranslationAssessment> AssessAutoImageAsync(AutoImageCandidate candidate, AppSettings settings, CancellationToken token)
    {
        EnsureCurrentAutoImage(candidate, token);
        var bytes = Convert.FromBase64String(candidate.PreviewBase64);
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || Math.Max(codec.Info.Width, codec.Info.Height) > BrowserImageTranslationJudge.MaximumImageEdge)
            throw new InvalidDataException("Invalid bounded automatic preview.");
        var preview = CapturedBitmapEncoder.DecodeOriginal(bytes);
        var (assessment, cached) = await _imageDecisionMemo.AssessAsync(preview, settings.TargetLanguage,
            current => _autoImageJudge.AssessAsync(preview, settings.TargetLanguage, current), token).ConfigureAwait(false);
        AppDiagnostics.Write($"browser_auto_assessment code={assessment.ReasonCode} needs={assessment.NeedsTranslation} cached={cached} ms={assessment.ElapsedMilliseconds}");
        return assessment;
    }

    private async Task ReplyAutoImageAsync(AutoImageCandidate candidate, bool needsTranslation, string reason, CancellationToken token)
    {
        try
        {
            if (_disposed || _browserBridgeServer is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(candidate.ConnectionId, JsonSerializer.Serialize(new
            {
                type = "autoImageAssessment", requestId = candidate.RequestId, imageId = candidate.ImageId,
                documentToken = candidate.DocumentToken, tabId = candidate.TabId, generation = candidate.Generation,
                needsTranslation, reason,
            }), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private void EnsureCurrentAutoImage(AutoImageCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = Volatile.Read(ref _autoImageConfiguration);
        if (_disposed || !config.Settings.BrowserAutoImageTranslationEnabled || config.Generation != candidate.Generation
            || BrowserSitePolicy.Resolve(candidate.SiteHost, config.Settings).Mode == "block"
            || !_browserConnections.TryGetValue(candidate.ConnectionId, out var kind) || kind != BrowserKind.Chrome)
            throw new OperationCanceledException(token);
    }

    private async Task<BrowserAutoImageProbe> QueryAutoImageProbeAsync(AutoImageCandidate candidate, CancellationToken token)
    {
        EnsureCurrentAutoImage(candidate, token);
        var active = await QueryAutoDocumentAsync(candidate.ConnectionId, candidate.TabId, candidate.WindowId,
            candidate.DocumentToken, token).ConfigureAwait(false);
        if (active is null)
            return new(false, candidate.TabId, candidate.WindowId, candidate.DocumentToken, null);
        var response = await QueryBrowserImageAsync(candidate.ConnectionId, new
        {
            type = "queryImageGeometry", tabId = candidate.TabId, browserWindowId = candidate.WindowId,
            imageId = candidate.ImageId, documentToken = candidate.DocumentToken, allowInactive = true,
        }, false, token).ConfigureAwait(false);
        EnsureCurrentAutoImage(candidate, token);
        return new(true, active.TabId, active.BrowserWindowId, active.DocumentToken, response?.Reply);
    }

    private async Task<bool> CanStartAutoImageAsync(AutoImageCandidate candidate, CancellationToken token) =>
        BrowserAutoImageLifetimePolicy.CanStart(candidate, await QueryAutoImageProbeAsync(candidate, token).ConfigureAwait(false),
            Volatile.Read(ref _autoImageConfiguration).Settings.BrowserAutoPageTranslationEnabled);

    private async Task<bool> IsAutoImageCurrentAsync(AutoImageCandidate candidate, CancellationToken token) =>
        BrowserAutoImageLifetimePolicy.CanContinue(candidate, await QueryAutoImageProbeAsync(candidate, token).ConfigureAwait(false),
            Volatile.Read(ref _autoImageConfiguration).Settings.BrowserAutoPageTranslationEnabled);

    private async Task RetryAutoImageWhenVisibleAsync(AutoImageCandidate candidate)
    {
        try
        {
            EnsureCurrentAutoImage(candidate, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer!.SendAsync(candidate.ConnectionId, JsonSerializer.Serialize(new
            {
                type = "autoImageRetry", tabId = candidate.TabId, imageId = candidate.ImageId,
                documentToken = candidate.DocumentToken, generation = candidate.Generation,
            }), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task TranslateAutoImageAsync(AutoImageCandidate candidate, AppSettings settings, CancellationToken token)
    {
        EnsureCurrentAutoImage(candidate, token);
        if (!await CanStartAutoImageAsync(candidate, token).ConfigureAwait(false))
        { await RetryAutoImageWhenVisibleAsync(candidate).ConfigureAwait(false); return; }
        var response = await QueryBrowserImageAsync(candidate.ConnectionId, new
        {
            type = "queryImage", tabId = candidate.TabId, browserWindowId = candidate.WindowId,
            imageId = candidate.ImageId, documentToken = candidate.DocumentToken, allowInactive = true,
        }, true, token).ConfigureAwait(false);
        EnsureCurrentAutoImage(candidate, token);
        if (response is not { } original || !original.Reply.Found || original.Bytes is null
            || original.Reply.ImageId != candidate.ImageId || original.Reply.DocumentToken != candidate.DocumentToken)
        { AppDiagnostics.Write("browser_auto_original_unavailable"); return; }
        var image = await Task.Run(() => CapturedBitmapEncoder.DecodeOriginal(original.Bytes), token).ConfigureAwait(false);
        // This read was admitted while visible. Scroll alone must not discard the
        // admitted original or restart its model request; Chrome clips the layer locally.
        await using var transport = new BrowserOverlayTransport(_browserBridgeServer!, new(candidate.ConnectionId, candidate.TabId,
            candidate.ImageId, candidate.DocumentToken, image.Size));
        var result = await BrowserAutoImageLifetimePolicy.RunAsync(
            current => IsAutoImageCurrentAsync(candidate, current),
            current => _autoImageTranslator!.TranslateAsync(image, settings, transport, current), token).ConfigureAwait(false);
        AppDiagnostics.Write($"browser_auto_completed cache={result.CacheHit} tiles={result.TileCount}");
        if (!_disposed) _ = _application.Dispatcher.BeginInvoke(() => MainWindow.StatusText = result.TileCount < result.Result.Blocks.Count
            ? "Chrome 图片已部分翻译，可点击图片重试" : "Chrome 图片自动翻译完成");
    }

    private async Task DisposeAutoImagesAsync()
    {
        if (_autoImageQueue is not null) await _autoImageQueue.DisposeAsync().ConfigureAwait(false);
        _autoImageJudge.Dispose();
        if (_autoImageTranslator is not null) await _autoImageTranslator.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record AutoImageConfiguration(AppSettings Settings, long Generation);
}
