using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserAutoImageTranslationResult(
    TranslationResult Result,
    bool CacheHit,
    int TileCount);

/// <summary>
/// Independent automatic browser jobs share resident local layout models and HTTP,
/// while streaming repair and typography run off the UI thread. Takes ownership
/// of each job's overlay transport, retaining only a successfully completed layer.
/// </summary>
public sealed class BrowserAutoImageTranslator : IAsyncDisposable
{
    private readonly ImageTranslationCache _cache;
    private readonly ISecretStore _secrets;
    private readonly Func<AppSettings, ITranslationProvider>? _providerFactory;
    private readonly Func<CapturedBitmap, CancellationToken, Task<GroundedVisionDocument>>? _prepareAsync;
    private readonly Func<CapturedBitmap, CancellationToken, Task<IReadOnlyList<OcrBlock>>>? _locateAsync;
    private readonly Func<CapturedBitmap, string, CancellationToken, Task<ImageTranslationAssessment>>? _assessAsync;
    private readonly BrowserImageTranslationJudge _localJudge;
    private readonly bool _ownsLocalJudge;
    private readonly ApiHttpClientRouter _apiHttp;
    private readonly bool _ownsApiHttp;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    // Constructors do not load weights or start a Python/GPU worker.
    private readonly LocalTextDetector _detector = new();
    private int _disposed;

    public BrowserAutoImageTranslator(
        ImageTranslationCache cache,
        ISecretStore secrets,
        Func<AppSettings, ITranslationProvider>? providerFactory = null,
        Func<CapturedBitmap, CancellationToken, Task<GroundedVisionDocument>>? prepareAsync = null,
        Func<CapturedBitmap, CancellationToken, Task<IReadOnlyList<OcrBlock>>>? locateAsync = null,
        BrowserImageTranslationJudge? localJudge = null,
        Func<CapturedBitmap, string, CancellationToken, Task<ImageTranslationAssessment>>? assessAsync = null,
        ApiHttpClientRouter? apiHttp = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _providerFactory = providerFactory;
        _prepareAsync = prepareAsync;
        _locateAsync = locateAsync;
        _localJudge = localJudge ?? new();
        _ownsLocalJudge = localJudge is null;
        _assessAsync = assessAsync;
        _apiHttp = apiHttp ?? new(); _ownsApiHttp = apiHttp is null;
    }

    public async Task<BrowserAutoImageTranslationResult> TranslateAsync(
        CapturedBitmap image,
        AppSettings settings,
        BrowserOverlayTransport transport,
        CancellationToken token, bool manual = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transport);
        var completed = false;
        var acquired = false;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token, transport.DismissalToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _executionGate.WaitAsync(active.Token).ConfigureAwait(false);
            acquired = true;
            active.Token.ThrowIfCancellationRequested();

            var key = await Task.Run(() => ImageTranslationCache.Key(image, settings), active.Token).ConfigureAwait(false);
            if (_cache.Get(key) is { } cached && IsComplete(cached.Result, cached.Tiles))
            {
                await transport.PublishAsync(cached.Tiles, true, active.Token, phase: "cached").ConfigureAwait(false);
                completed = true;
                return new(cached.Result, true, cached.Tiles.Count);
            }

            // Failed credentials never spend time loading local layout models.
            if (_providerFactory is null && string.IsNullOrWhiteSpace(
                    await _secrets.GetAsync(TranslationProviderConfiguration.SecretName(settings), active.Token).ConfigureAwait(false)))
                throw new TranslationAuthenticationException($"请在翻译设置中填写并保存 {TranslationProviderConfiguration.Label(settings.TranslationProvider)} API Key。");

            // Recheck the actual original, not the bounded browser preview. A
            // preview can misread a credit as prose. Preparation runs alongside
            // this gate; a definite local negative never dispatches a VLM.
            // Injected providers remain isolated from installed OCR weights;
            // tests can opt into the same gate through assessAsync.
            var assessmentTask = Task.Run(async () => _assessAsync is not null
                ? await _assessAsync(image, settings.TargetLanguage, active.Token).ConfigureAwait(false)
                : _providerFactory is null
                    ? await _localJudge.AssessAsync(image, settings.TargetLanguage, active.Token).ConfigureAwait(false)
                    : null, active.Token);
            var pngTask = Task.Run(() => CapturedBitmapEncoder.ToPng(image), active.Token);
            var groundingTask = _prepareAsync is not null
                ? Task.Run(() => _prepareAsync(image, active.Token), active.Token)
                : Task.FromResult(new GroundedVisionDocument([], null));
            await Task.WhenAll(pngTask, groundingTask, assessmentTask).ConfigureAwait(false);
            var assessment = await assessmentTask.ConfigureAwait(false);
            if (!manual && assessment is { NeedsTranslation: false })
            {
                AppDiagnostics.Write($"browser_auto_original_negative code={assessment.ReasonCode}");
                return new(new([]), false, 0);
            }
            var localEvidence = assessment is null ? [] : DualVisionLocalEvidence.FromAssessment(assessment);
            var grounding = await groundingTask.ConfigureAwait(false);
            // A real positive admission now has a visible, unobtrusive waiting
            // signal before the provider starts, rather than after the first tile.
            await transport.PublishAsync([], true, active.Token, phase: "translating").ConfigureAwait(false);
            var provider = _providerFactory is not null
                ? _providerFactory(settings) ?? throw new InvalidOperationException("翻译服务未提供可用的模型连接。")
                : TranslationProviderFactory.Create(_apiHttp.GetClient(settings), _secrets, settings, allowLowWaitRetry: true);
            DeepSeekRequestDiagnostics.Observe(provider, "auto_translation");
            var responsive = new ResponsiveThinkingTranslationProvider(provider, settings.ThinkingLevel,
                settings.TranslationProvider == TranslationProviderKind.DeepSeek && settings.BoundedLowThinkingEnabled);
            responsive.FastRetryStarted += () => AppDiagnostics.Write("deepseek_fast_retry role=auto_translation from=Low to=None reason=no_content_deadline");
            provider = responsive;
            var request = new TranslationRequest(
                settings.SourceLanguage,
                settings.TargetLanguage,
                settings.TranslationStyle,
                settings.TranslationContext,
                [new OcrBlock("image", string.Empty, 1, default, 0)])
            {
                ImagePng = await pngTask.ConfigureAwait(false),
                ImageSize = image.Size,
                IsDirectVision = true,
                VisionRegions = grounding.Regions,
                VisionReadingDetailPng = grounding.DetailPng,
                VisionReadingSheets = grounding.ReadingSheets,
                VisionReadingHints = grounding.ReadingHints,
            };
            IReadOnlyList<TranslatedBlock> FilterSource(IReadOnlyList<TranslatedBlock> blocks) =>
                VisionSemantics.Filter(VisionSemantics.Filter(blocks, request), settings.TargetLanguage,
                    localEvidence, sourceIsComplete: false);

            using var render = new InPlaceRenderSession(image, grounding.InkMask);
            var tiles = Channel.CreateBounded<IReadOnlyList<RenderedImageTile>>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            Exception? sendingError = null;
            render.TilesChanged += PublishTiles;
            void PublishTiles(IReadOnlyList<RenderedImageTile> latest) => tiles.Writer.TryWrite(latest);
            async Task SendTilesAsync()
            {
                try
                {
                    var published = false;
                    await foreach (var latest in tiles.Reader.ReadAllAsync(active.Token).ConfigureAwait(false))
                    {
                        if (!published && latest.Count == 0) continue;
                        await transport.PublishAsync(latest, true, active.Token, phase: "typesetting").ConfigureAwait(false);
                        published = true;
                    }
                }
                catch (Exception error)
                {
                    Volatile.Write(ref sendingError, error);
                    active.Cancel();
                    throw;
                }
            }
            var sender = SendTilesAsync();
            Task? locating = null;
            try
            {
                // Inline progress deliberately avoids capturing a WPF dispatcher.
                var progress = new InlineProgress(update =>
                {
                    if (update.ImageSegments is { } segments)
                        render.Update(FilterSource(segments));
                });
                var placement = new VisionPlacementRefiner(image, grounding.InkMask);
                var layout = new ParallelVisionLayout(progress, active.Token,
                    placementRefiner: (blocks, lines) => placement.Refine(FilterSource(blocks), lines));
                locating = layout.LocateAsync(cancellation => assessment is not null ? Task.FromResult(localEvidence)
                    : _locateAsync is not null ? _locateAsync(image, cancellation)
                    : new VisionRegionLocator(_detector).LocateAsync(image, cancellation));
                var orchestrator = new TranslationOrchestrator(provider, new TranslationResponseValidator());
                var result = await orchestrator.TranslateAsync(request, active.Token, layout).ConfigureAwait(false);
                await locating.ConfigureAwait(false);
                result = layout.Complete(result);
                result = new(FilterSource(result.Blocks));
                render.Update(result.Blocks, final: true);
                var finalTiles = await render.FinishAsync(active.Token).ConfigureAwait(false);
                render.TilesChanged -= PublishTiles;
                tiles.Writer.TryComplete();
                await sender.ConfigureAwait(false);
                active.Token.ThrowIfCancellationRequested();
                if (finalTiles.Count == 0) return new(result, false, 0);
                // The final batch must be acknowledged even if intermediate channel
                // snapshots were dropped while Chrome was committing an earlier tile.
                await transport.PublishAsync(finalTiles, true, active.Token, phase: "complete").ConfigureAwait(false);
                if (IsComplete(result, finalTiles)) _cache.Set(key, new(grounding, result, finalTiles));
                completed = true;
                return new(result, false, finalTiles.Count);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref sendingError) is { } error)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
                throw;
            }
            finally
            {
                render.TilesChanged -= PublishTiles;
                tiles.Writer.TryComplete();
                active.Cancel();
                // Observe the sender and optional position task on every path.
                try { await sender.ConfigureAwait(false); } catch (Exception) { }
                if (locating is not null)
                    try { await locating.ConfigureAwait(false); } catch (Exception) { }
            }
        }
        finally
        {
            await transport.DisposeAsync(retainCompleted: completed).ConfigureAwait(false);
            if (acquired) _executionGate.Release();
        }
    }

    private static bool IsComplete(TranslationResult result, IReadOnlyList<RenderedImageTile> tiles) =>
        result.Blocks.Count > 0
        && result.Blocks.All(block => block.IsComplete && block.Bounds.IsUsable)
        && tiles.Count == result.Blocks.Count
        && tiles.Select(tile => tile.Id).Distinct(StringComparer.Ordinal).Count() == tiles.Count
        && tiles.All(tile => tile.Optimized && result.Blocks.Any(block =>
            block.Id == tile.Id && block.Translation == tile.Translation));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        await _executionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ownsLocalJudge) _localJudge.Dispose();
            _detector.Dispose();
        if (_ownsApiHttp) _apiHttp.Dispose();
        }
        finally { _executionGate.Release(); }
    }

    private sealed class InlineProgress(Action<TranslationStreamUpdate> report) : IProgress<TranslationStreamUpdate>
    {
        public void Report(TranslationStreamUpdate value) => report(value);
    }
}
