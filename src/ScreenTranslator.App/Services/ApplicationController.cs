using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Input;
using Microsoft.Win32;
using ScreenTranslator.App.Services.Appearance;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Hotkeys;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.App.Services.Networking;
using ScreenTranslator.App.Services.Tray;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Windows;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Hotkeys;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Sessions;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using CaptureMode = ScreenTranslator.App.ViewModels.CaptureMode;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Window = System.Windows.Window;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController : IDisposable
{
    private readonly Application _application;
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly ApiHttpClientRouter _apiHttp = new();
    private readonly IScreenCaptureService _screenCapture;
    private readonly IOcrEngine _ocrEngine;
    private readonly LocalTextDetector _localTextDetector = new();
    private readonly ImageTranslationCache _imageCache=new();
    private readonly CancellationTokenSource _ocrWarmupCancellation = new();
    private readonly IGlobalHotkeyService _hotkey;
    private readonly HotkeyRegistrationCoordinator _hotkeyCoordinator;
    private readonly TrayIconService _tray;
    private readonly TranslationSessionCoordinator _sessions = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly List<Window> _resultWindows = [];
    private readonly List<TextOverlayWindow> _activeTextOverlays = [];

    private MainWindow? _mainWindow;
    private ForegroundWindowMonitor? _foregroundWindowMonitor;
    private OverlayFocusCoordinator? _overlayFocusCoordinator;
    private AppSettings _persistedSettings = new();
    private LastTranslationWork? _lastWork;
    private bool _overlaysVisible = true;
    private bool _applyingSettings;
    private bool _exitRequested;
    private bool _disposed;
    private bool _captureSurfacesSuppressed;
    private bool _continuousAcquisitionActive;
    private int _continuousItemFailureCount;
    private CancellationTokenSource? _captureSessionCancellation;
    private SequentialWorkQueue<TranslationCaptureWorkItem>? _continuousCaptureQueue;
    private ContinuousSidePanelWindow? _continuousSidePanel;
    private ContinuousResultsViewModel? _continuousResults;

    public ApplicationController(Application application)
    {
        _application = application;
        _settingsStore = new JsonSettingsStore();
        _secretStore = new DpapiSecretStore();
        _screenCapture = new FallbackScreenCaptureService();
        _ocrEngine = new WindowsOcrEngine();
        _hotkey = new GlobalHotkeyService();
        _hotkeyCoordinator = new HotkeyRegistrationCoordinator(_hotkey);
        _tray = new TrayIconService(application.Dispatcher);

        MainWindow = new MainWindowViewModel();
        BrowserIntegration = new BrowserIntegrationViewModel();
        GeneralSettings = new GeneralSettingsViewModel(BrowserIntegration);
        TranslationSettings = new TranslationSettingsViewModel();
        AppearanceSettings = new AppearanceSettingsViewModel();
        HotkeySettings = new HotkeySettingsViewModel();
        PrivacySettings = new PrivacySettingsViewModel();
    }

    public MainWindowViewModel MainWindow { get; }

    public GeneralSettingsViewModel GeneralSettings { get; }

    public BrowserIntegrationViewModel BrowserIntegration { get; }

    public TranslationSettingsViewModel TranslationSettings { get; }

    public AppearanceSettingsViewModel AppearanceSettings { get; }

    public HotkeySettingsViewModel HotkeySettings { get; }

    public PrivacySettingsViewModel PrivacySettings { get; }

    public async Task InitializeAsync()
    {
        var loadedSettings = await _settingsStore.LoadAsync();
        _persistedSettings = loadedSettings.Migrate(out var hotkeyWasReset);
        if (loadedSettings.Version != AppSettings.CurrentVersion || hotkeyWasReset)
        {
            await _settingsStore.SaveAsync(_persistedSettings);
        }

        ApplySettings(_persistedSettings);
        await InitializeBrowserIntegrationAsync();

        MainWindow.StartCaptureRequested += OnCaptureRequested;
        GeneralSettings.StartCaptureRequested += OnCaptureRequested;
        GeneralSettings.StartContinuousCaptureRequested +=
            OnContinuousCaptureRequested;
        GeneralSettings.PropertyChanged += OnSettingsPropertyChanged;
        GeneralSettings.ClearPageCacheRequested += OnClearPageCacheRequested;
        TranslationSettings.PropertyChanged += OnSettingsPropertyChanged;
        AppearanceSettings.PropertyChanged += OnSettingsPropertyChanged;
        HotkeySettings.PropertyChanged += OnSettingsPropertyChanged;
        BrowserIntegration.PropertyChanged += OnSettingsPropertyChanged;
        PrivacySettings.PropertyChanged += OnSettingsPropertyChanged;
        TranslationSettings.SaveRequested += OnTranslationSettingsSaveRequested;
        TranslationSettings.PresetDeleted += OnTranslationPresetDeleted;
        TranslationSettings.ConnectionTester = TestConnectionAsync;
        TranslationSettings.ModelListLoader = ReadModelListAsync;
        TranslationSettings.ClashNodesLoader = _apiHttp.ReadClashNodesAsync;
        TranslationSettings.ClashNodeTester = _apiHttp.TestClashNodeAsync;
        TranslationSettings.ClashInstallationProbe = ClashVergeInstallation.IsInstalled;
        HotkeySettings.RecordingStarted += OnHotkeyRecordingStarted;
        HotkeySettings.RecordingCancelled += OnHotkeyRecordingCancelled;
        HotkeySettings.GestureSubmitted += OnHotkeyGestureSubmitted;
        PrivacySettings.ClearHistoryRequested += OnClearHistoryRequested;

        _hotkey.CaptureRequested += OnCaptureRequested;
        _tray.CaptureRequested += OnCaptureRequested;
        _tray.ContinuousCaptureRequested += OnContinuousCaptureRequested;
        _tray.ToggleOverlaysRequested += (_, _) => ToggleOverlays();
        _tray.ShowSettingsRequested += (_, _) => ShowSettings();
        _tray.HotkeyPauseChanged += OnHotkeyPauseChanged;
        _tray.ExitRequested += (_, _) => Exit();

        RegisterSavedHotkey();
        ApplyStartupRegistration(
            _persistedSettings.StartWithWindows,
            _persistedSettings.StartSilently);
        MainWindow.StatusText = hotkeyWasReset
            ? $"快捷键设置无效，已恢复为 {HotkeyGesture.Default.ToDisplayString()}"
            : _persistedSettings.HotkeyEnabled && !_hotkeyCoordinator.IsEnabled
                ? "快捷键冲突，可从托盘开始框选"
                : _persistedSettings.HotkeyEnabled
                    ? $"准备就绪 · {HotkeySettings.HotkeyText}"
                    : "准备就绪 · 快捷键已暂停";
        await LoadSavedApiKeyPreviewAsync();
        _ = WarmupVisionAsync();
    }

    public void ShowSettings()
    {
        if (_mainWindow is null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow(MainWindow)
            {
                HideOnClose = GeneralSettings.MinimizeToTray,
            };
            _mainWindow.Closed += OnMainWindowClosed;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
        {
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= OnMainWindowClosed;
        }

        _mainWindow = null;
        if (!_exitRequested && !GeneralSettings.MinimizeToTray)
        {
            Exit();
        }
    }

    private void OnCaptureRequested(object? sender, EventArgs e) =>
        _application.Dispatcher.BeginInvoke(() => _ = RunCaptureSessionAsync(CaptureMode.Single));

    private void OnContinuousCaptureRequested(object? sender, EventArgs e) =>
        _application.Dispatcher.BeginInvoke(() => _ = RunCaptureSessionAsync(CaptureMode.Multiple));

    private async Task ProcessContinuousWorkItemAsync(
        TranslationCaptureWorkItem item,
        CancellationToken cancellationToken) =>
        await _application.Dispatcher.InvokeAsync(
                () => ProcessCaptureWorkItemAsync(
                    item,
                    preserveExistingResults: true,
                    cancellationToken))
            .Task
            .Unwrap();

    private async Task ProcessCaptureWorkItemAsync(
        TranslationCaptureWorkItem item,
        bool preserveExistingResults,
        CancellationToken cancellationToken,
        TranslationResultViewModel? floating = null)
    {
        var session = _sessions.Start();
        try
        {
            _sessions.TryTransition(
                session.Id,
                TranslationSessionState.Ocr);
            MainWindow.StatusText = "正在准备翻译…";
            var ocrElapsed = Stopwatch.StartNew();
            IReadOnlyList<OcrBlock> blocks;
            byte[]? imagePng = null;
            byte[]? sourceImagePng = null;
            IReadOnlyList<ImageAtlasRegion> atlasRegions = [];
            PixelSize? atlasSize = null;
            var isComic = false;
            var inputMode = item.Settings.InputMode;
            GroundedVisionDocument? grounding = null;
            string? cacheKey=null;CachedImageTranslation? cached=null;
            if (inputMode == TranslationInputMode.Automatic && !string.IsNullOrWhiteSpace(item.AccessibleText))
                blocks = [new OcrBlock("block-1", item.AccessibleText, 1, new PixelRect(0, 0, item.Bitmap.Width, item.Bitmap.Height), 0)];
            else
            {
                sourceImagePng = await Task.Run(() => CapturedBitmapEncoder.ToPng(item.Bitmap), cancellationToken);
                imagePng = sourceImagePng;
                atlasSize = item.Bitmap.Size;
                cacheKey = await Task.Run(() => ImageTranslationCache.Key(item.Bitmap, item.Settings), cancellationToken);
                cached = _imageCache.Get(cacheKey);
                grounding = cached?.Grounding;
                blocks = [new OcrBlock("image", string.Empty, 1, default, 0)];
                AppDiagnostics.Write($"vision_image_ready width={item.Bitmap.Width} height={item.Bitmap.Height} bytes={imagePng.Length}");
            }

            AppDiagnostics.Write($"ocr_complete elapsed_ms={ocrElapsed.ElapsedMilliseconds} regions={blocks.Count}");
            var work = new LastTranslationWork(
                item.Monitor,
                item.AbsoluteSelection,
                blocks,
                item.CapturedBrowser,
                item.SourceWindowHandle)
            {
                ImagePng = imagePng, SourceWindowBounds = item.SourceWindowBounds,
                SourceImageMapping = item.SourceImageMapping, RefreshSourceImageMapping = item.RefreshSourceImageMapping,
                ImageBounds = item.ImageBounds, AtlasRegions = atlasRegions, AtlasSize = atlasSize, SourceImagePng = sourceImagePng,
                IsOcrDocument = sourceImagePng is not null && imagePng is null,
                IsDirectVision = imagePng is not null,
                SourceBitmap = item.Bitmap,
                VisionDocument = grounding,
                CacheKey=cacheKey,Cached=cached,BrowserImage=item.BrowserImage,
                IsComic = isComic,
            };
            _lastWork = work;
            if (floating is not null)
            {
                floating.HasTranslationInput = true;
                floating.SourceText = string.Join("\n", blocks.Select(b => b.Text));
                floating.SourceRegion = item.AbsoluteSelection;
                floating.SourceWindowHandle = item.SourceWindowHandle;
                floating.SourceWindowBounds = work.SourceWindowBounds;
                floating.SourceImageMapping = work.SourceImageMapping;
                floating.RefreshSourceImageMapping = work.RefreshSourceImageMapping;
                floating.SourceImage = sourceImagePng is null ? null : CapturedBitmapEncoder.Preview(sourceImagePng);
                floating.SourceInkMask=grounding?.InkMask;floating.CachedTiles=cached?.Tiles;
                if(item.BrowserImage is {} browserImage && _browserBridgeServer is not null)floating.BrowserOverlay=new(_browserBridgeServer,browserImage);
                floating.SourceBitmap = sourceImagePng is null ? null : item.Bitmap;
                floating.IsInPlaceEnabled = sourceImagePng is not null && item.Settings.InPlaceTranslationEnabled;
                floating.SourceLanguageLabel = sourceImagePng is null ? "文字" : imagePng is null
                    ? item.SourceImageMapping is null ? "整图 OCR · 截图" : "整图 OCR · 原图"
                    : item.SourceImageMapping is null ? "整图 VLM · 截图" : "整图 VLM · 原图";
                floating.StatusText = imagePng is null ? "正在等待译文…" : "正在识读并翻译图片…";
                if (sourceImagePng is not null)
                    foreach (var imagePanel in _resultWindows.OfType<SidePanelWindow>().Where(window => ReferenceEquals(window.ViewModel, floating)))
                        imagePanel.PlaceBesideImage(item.ImageBounds ?? item.AbsoluteSelection, item.Monitor);
            }
            using var cancellationRegistration = cancellationToken.Register(() => _sessions.Cancel(session.Id));
            _sessions.TryTransition(
                session.Id,
                TranslationSessionState.Translating);
            await TranslateAndShowAsync(
                work,
                item.Settings,
                session,
                preserveExistingResults,
                floating);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write($"capture_processing_failed type={exception.GetType().Name}");
            _sessions.TryTransition(
                session.Id,
                TranslationSessionState.Failed);
            throw;
        }
    }

    private async Task WarmupVisionAsync()
    {
        try
        {
            await Task.Run(async()=>
            {
                var token=_ocrWarmupCancellation.Token;token.ThrowIfCancellationRequested();ComicFontCatalog.Warmup();
                var bytes=new byte[256*96*4];Array.Fill(bytes,byte.MaxValue);var sample=new CapturedBitmap(256,96,256*4,bytes);
                await _localTextDetector.DetectAsync(sample,token).ConfigureAwait(false);
                AppDiagnostics.Write("native_vision_warmup_ready resident=true");
            },_ocrWarmupCancellation.Token);
        }
        catch(OperationCanceledException)when(_ocrWarmupCancellation.IsCancellationRequested){}
        catch(Exception error){AppDiagnostics.Write($"native_vision_warmup_failed type={error.GetType().Name}");}
    }

    private void OnContinuousPendingCountChanged(
        object? sender,
        int pendingCount)
    {
        _application.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _continuousCaptureQueue))
            {
                return;
            }

            GeneralSettings.ContinuousPendingCount = pendingCount;
            if (_continuousAcquisitionActive)
            {
                MainWindow.StatusText =
                    $"连续框选中 · 待处理 {pendingCount}";
            }
        });
    }

    private void OnContinuousItemFailed(
        object? sender,
        WorkItemFailedEventArgs<TranslationCaptureWorkItem> e)
    {
        Interlocked.Increment(ref _continuousItemFailureCount);
        _application.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _continuousCaptureQueue))
            {
                return;
            }

            MainWindow.StatusText =
                $"一项翻译失败，连续模式继续：{e.Exception.Message}";
        });
    }

    private void SuppressResultWindowsForCapture()
    {
        _captureSurfacesSuppressed = true;
        _mainWindow?.Hide();
        foreach (var window in _resultWindows.ToArray())
        {
            if (window is TextOverlayWindow overlay)
            {
                overlay.SetUserVisibility(false);
            }
            else
            {
                window.Hide();
            }
        }
    }

    private void RestoreResultWindowsAfterCapture()
    {
        _captureSurfacesSuppressed = false;
        foreach (var window in _resultWindows.ToArray())
        {
            if (window is TextOverlayWindow overlay)
            {
                overlay.SetUserVisibility(_overlaysVisible);
            }
            else if (_overlaysVisible)
            {
                window.Show();
            }
            else
            {
                window.Hide();
            }
        }

        _overlayFocusCoordinator?.HandleForegroundChanged(
            ForegroundWindowMonitor.CaptureForegroundRootWindow());
    }

    private async Task TranslateAndShowAsync(
        LastTranslationWork work,
        AppSettings settings,
        TranslationSession session,
        bool preserveExistingResults = false,
        TranslationResultViewModel? floating = null)
    {
        MainWindow.StatusText = $"正在调用 {TranslationProviderConfiguration.Label(settings.TranslationProvider)} 翻译…";
        TranslationResultViewModel? pending = floating;
        var apiProvider = TranslationProviderFactory.Create(_apiHttp.GetClient(settings), _secretStore, settings,
            allowLowWaitRetry: work.ImagePng is not null);
        DeepSeekRequestDiagnostics.Observe(apiProvider, "translation");
        var provider = new ResponsiveThinkingTranslationProvider(apiProvider, settings.ThinkingLevel,
            settings.TranslationProvider == TranslationProviderKind.DeepSeek && settings.BoundedLowThinkingEnabled);
        provider.FastRetryStarted += () =>
        {
            AppDiagnostics.Write("deepseek_fast_retry role=translation from=Low to=None reason=no_content_deadline");
            if (!_disposed) _ = _application.Dispatcher.BeginInvoke(() =>
            {
                if (_sessions.IsCurrent(session.Id) && pending?.IsBusy == true)
                    pending.StatusText = "低思考等待过长，已切换快速翻译…";
            });
        };
        var orchestrator = new TranslationOrchestrator(
            provider,
            new TranslationResponseValidator());
        var request = new TranslationRequest(
            settings.SourceLanguage,
            settings.TargetLanguage,
            settings.TranslationStyle,
            settings.TranslationContext + (work.IsComic ? "\nJapanese manga. Keep uncertain names in source spelling; invent no context." : ""),
            work.Blocks)
        { ImagePng = work.ImagePng, AtlasRegions = work.AtlasRegions, ImageSize = work.AtlasSize,
            IsOcrDocument = work.IsOcrDocument, IsDirectVision = work.IsDirectVision,
            VisionRegions = work.VisionDocument?.Regions ?? [], VisionReadingDetailPng = work.VisionDocument?.DetailPng,
            VisionReadingSheets=work.VisionDocument?.ReadingSheets??[],
            VisionReadingHints = work.VisionDocument?.ReadingHints ?? [] };
        if (pending is null && !preserveExistingResults && settings.DisplayMode == DisplayMode.SidePanel)
        {
            CloseResultWindows();
            pending = new TranslationResultViewModel
            {
                IsBusy = true,
                HasTranslationInput = true,
                IsExpanded = false,
                StatusText = "正在等待译文…",
                SourceText = string.Join("\n", work.Blocks.Select(b => b.Text)),
                TranslatedText = work.ImagePng is null ? "正在翻译选中的文字…" : "正在识读并翻译选区图片…",
                SourceLanguageLabel = work.ImagePng is null ? "文字" : "图片",
                TargetLanguageLabel = LanguageLabel(settings.TargetLanguage),
                IsSourceVisible = false,
                SourceImage = work.SourceImagePng is null ? null : CapturedBitmapEncoder.Preview(work.SourceImagePng),
                SourceRegion = work.AbsoluteSelection,
                SourceWindowHandle = work.SourceWindowHandle,
                SourceWindowBounds = work.SourceWindowBounds,
                SourceImageMapping = work.SourceImageMapping, RefreshSourceImageMapping = work.RefreshSourceImageMapping,
                SourceBitmap = work.SourceImagePng is null ? null : work.SourceBitmap,
                SourceInkMask=work.VisionDocument?.InkMask,CachedTiles=work.Cached?.Tiles,
                BrowserOverlay=work.BrowserImage is {} imageIdentity && _browserBridgeServer is not null?new(_browserBridgeServer,imageIdentity):null,
                IsInPlaceEnabled = work.SourceImagePng is not null && settings.InPlaceTranslationEnabled,
            };
            var panel = ShowSidePanel(work, new TranslationResult([]), settings, pending);
            panel.Closed += (_, _) =>
            {
                if (pending.IsBusy)
                {
                    _sessions.Cancel(session.Id);
                    _captureSessionCancellation?.Cancel();
                }
            };
        }
        TranslationResult result;
        ParallelVisionLayout? parallelLayout = null;
        var translationElapsed = Stopwatch.StartNew();
        var firstTextLogged = false;
        try
        {
            // Full-image local evidence runs beside the VLM. Plain text can stream
            // immediately; in-place ink waits for evidence instead of erasing native captions.
            var evidenceTask = work.Cached is null && work.IsDirectVision && work.SourceBitmap is { } evidenceSource
                ? ReadImageEvidenceAsync(evidenceSource, settings.TargetLanguage, session.CancellationToken)
                : Task.FromResult<IReadOnlyList<OcrBlock>>([]);
            var progress = new Progress<TranslationStreamUpdate>(update =>
            {
                if (work.IsDirectVision) update = ImageTranslationStreamFilter.Apply(update, request,
                    evidenceTask.IsCompletedSuccessfully ? evidenceTask.Result : [], evidenceTask.IsCompletedSuccessfully);
                if (!firstTextLogged && !string.IsNullOrWhiteSpace(update.Translation) && !update.IsLayoutUpdate)
                {
                    firstTextLogged = true;
                    AppDiagnostics.Write($"translation_first_text elapsed_ms={translationElapsed.ElapsedMilliseconds} direct_vision={work.IsDirectVision}");
                }
                if (!_disposed && pending is not null && pending.ErrorMessage is null && _sessions.IsCurrent(session.Id)
                    && !session.CancellationToken.IsCancellationRequested)
                {
                    pending.ApplyStreamUpdate(update);
                }
            });
            if (work.Cached is null && work.IsDirectVision && work.SourceBitmap is { } sourceBitmap)
            {
                var placement = new VisionPlacementRefiner(sourceBitmap,work.VisionDocument?.InkMask);
                parallelLayout = new(progress, session.CancellationToken, placementRefiner: (blocks, lines) =>
                    placement.Refine(VisionSemantics.Filter(VisionSemantics.Filter(blocks, request),
                        settings.TargetLanguage, evidenceTask.IsCompletedSuccessfully ? evidenceTask.Result : [], sourceIsComplete: false), lines));
                _ = parallelLayout.LocateAsync(token => new VisionRegionLocator(_localTextDetector).LocateAsync(sourceBitmap, token));
            }
            AppDiagnostics.Write($"translation_dispatch direct_vision={work.IsDirectVision} thinking={settings.ThinkingLevel}");
            if (work.Cached is not null) result = work.Cached.Result;
            else result = await orchestrator.TranslateAsync(request, session.CancellationToken,
                parallelLayout is null ? progress : parallelLayout);
            if (parallelLayout is not null && work.Cached is null) result = await Task.Run(()=>parallelLayout.Complete(result),session.CancellationToken);
            if (work.Cached is null && work.IsDirectVision)
                result = new(VisionSemantics.Filter(VisionSemantics.Filter(result.Blocks, request),
                    settings.TargetLanguage, await evidenceTask, sourceIsComplete: false));
            if(work.Cached is not null)AppDiagnostics.Write("image_translation_cache_hit network=false render=false");
            AppDiagnostics.Write($"translation_complete elapsed_ms={translationElapsed.ElapsedMilliseconds} blocks={result.Blocks.Count} positioned={result.Blocks.Count(block => block.Bounds.IsUsable)} direct_vision={work.IsDirectVision}");
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            if (pending is not null)
            {
                pending.IsBusy = false;
                pending.TranslatedText = string.Empty;
                pending.ErrorMessage = "翻译已取消";
                pending.StatusText = "翻译已取消";
            }
            MainWindow.StatusText = "已取消翻译";
            return;
        }
        catch (Exception exception) when (pending is not null)
        {
            pending.IsBusy = false;
            pending.TranslatedText = string.Empty;
            pending.ErrorMessage = exception.Message;
            pending.StatusText = exception is TranslationAuthenticationException ? "未设置 API Key · 点击查看" : "翻译失败 · 点击查看";
            _sessions.TryTransition(session.Id, TranslationSessionState.Failed);
            MainWindow.StatusText = "翻译失败，可在浮窗中重试";
            return;
        }

        if (!_sessions.TryPublish(session.Id))
        {
            return;
        }

        if (pending is not null)
        {
            if(work.CacheKey is {} key && work.IsDirectVision)
                pending.TilesRendered=tiles=>{if(tiles.Count==result.Blocks.Count&&tiles.All(t=>t.Optimized&&result.Blocks.Any(b=>b.Id==t.Id&&b.Translation==t.Translation)))_imageCache.Set(key,new(work.VisionDocument,result,tiles));};
            pending.IsBusy = false;
            pending.SourceText = string.Join("\n", result.Blocks.Select(b => b.SourceText));
            pending.TranslatedText = string.Join("\n", result.Blocks.Select(b => b.Translation));
            pending.SetSegments(work.SourceImagePng is null ? [] : result.Blocks);
            pending.ErrorMessage = null;
            if (!pending.IsInPlaceEnabled) pending.IsExpanded = true;
            pending.StatusText = "翻译完成";
        }
        else ShowTranslation(work, result, settings, preserveExistingResults);
        _sessions.TryTransition(session.Id, TranslationSessionState.Displayed);
        MainWindow.StatusText = $"翻译完成 · {result.Blocks.Count} 个文本块";
    }

    private async Task RetryLastAsync()
    {
        if (_lastWork is null || !await _captureGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var session = _sessions.Start();
            MainWindow.IsCaptureAvailable = false;
            _autoImageQueue?.SetManualBusy(true);
            var settings = CollectSettings();
            var retry = _lastWork with { Cached = null };
            if (retry.IsDirectVision && retry.SourceBitmap is { } source)
            {
                var key = await Task.Run(() => ImageTranslationCache.Key(source, settings), session.CancellationToken);
                retry = retry with { CacheKey = key };
                _imageCache.Remove(key);
            }
            _lastWork = retry;
            await TranslateAndShowAsync(retry, settings, session);
        }
        catch (Exception exception)
        {
            MainWindow.StatusText = "重译失败";
            ShowFailure(exception.Message);
        }
        finally
        {
            MainWindow.IsCaptureAvailable = true;
            _captureGate.Release();
            _autoImageQueue?.SetManualBusy(false);
        }
    }

    private void ShowTranslation(
        LastTranslationWork work,
        TranslationResult result,
        AppSettings settings,
        bool preserveExistingResults = false)
    {
        if (!preserveExistingResults)
        {
            CloseResultWindows();
        }

        _overlaysVisible = true;

        if (settings.DisplayMode == DisplayMode.Overlay)
        {
            var overlays = ShowTextOverlays(work, result, settings);
            _ = TryStartBrowserFollowingAsync(work, overlays, settings);
        }
        else
        {
            if (preserveExistingResults)
            {
                ShowContinuousSidePanel(work, result, settings);
            }
            else
            {
                ShowSidePanel(work, result, settings);
            }
        }
    }

    private void ShowContinuousSidePanel(
        LastTranslationWork work,
        TranslationResult result,
        AppSettings settings)
    {
        if (_continuousSidePanel is null
            || !_continuousSidePanel.IsLoaded
            || _continuousResults is null)
        {
            _continuousResults = new ContinuousResultsViewModel();
            _continuousResults.ClearAllRequested +=
                OnContinuousResultsClearAllRequested;
            _continuousSidePanel = new ContinuousSidePanelWindow(
                _continuousResults)
            {
                Opacity = Math.Clamp(settings.OverlayOpacity, 0.72, 1),
            };
            var monitor = work.Monitor;
            var sourceBounds = ToCoreDipRect(
                work.AbsoluteSelection,
                monitor);
            var workArea = ToCoreDipRect(monitor.WorkArea, monitor);
            var panelBounds = SidePanelBoundsService.Place(
                sourceBounds,
                workArea,
                new DipSize(
                    _continuousSidePanel.Width,
                    _continuousSidePanel.Height),
                settings.SidePanelPlacement);
            _continuousSidePanel.Left = panelBounds.X;
            _continuousSidePanel.Top = panelBounds.Y;
            _continuousSidePanel.Width = panelBounds.Width;
            _continuousSidePanel.Height = panelBounds.Height;
            _continuousSidePanel.Closed +=
                OnContinuousSidePanelClosed;
            TrackAndShow(_continuousSidePanel);
        }

        var item = CreateResultViewModel(result, settings);
        item.CopyRequested += OnContinuousResultCopyRequested;
        item.CloseRequested += OnContinuousResultCloseRequested;
        _continuousResults.Results.Add(item);
    }

    private static void OnContinuousResultCopyRequested(
        object? sender,
        string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    private void OnContinuousResultCloseRequested(
        object? sender,
        EventArgs e)
    {
        if (sender is not TranslationResultViewModel item
            || _continuousResults is null)
        {
            return;
        }

        item.CopyRequested -= OnContinuousResultCopyRequested;
        item.CloseRequested -= OnContinuousResultCloseRequested;
        _continuousResults.Results.Remove(item);
    }

    private void OnContinuousResultsClearAllRequested(
        object? sender,
        EventArgs e) =>
        CloseResultWindows();

    private void OnContinuousSidePanelClosed(
        object? sender,
        EventArgs e)
    {
        if (_continuousResults is not null)
        {
            _continuousResults.ClearAllRequested -=
                OnContinuousResultsClearAllRequested;
            foreach (var item in _continuousResults.Results)
            {
                item.CopyRequested -=
                    OnContinuousResultCopyRequested;
                item.CloseRequested -=
                    OnContinuousResultCloseRequested;
            }
        }

        if (_continuousSidePanel is not null)
        {
            _continuousSidePanel.Closed -=
                OnContinuousSidePanelClosed;
        }

        _continuousSidePanel = null;
        _continuousResults = null;
    }

    private SidePanelWindow ShowSidePanel(
        LastTranslationWork work,
        TranslationResult result,
        AppSettings settings,
        TranslationResultViewModel? pending = null)
    {
        var viewModel = pending ?? CreateResultViewModel(result, settings);
        var panel = new SidePanelWindow(viewModel)
        {
            Opacity = Math.Clamp(settings.OverlayOpacity, 0.72, 1),
        };
        viewModel.RetryRequested += (_, _) => _ = RetryLastAsync();
        viewModel.ReselectRequested += (_, _) => _ = RunCaptureSessionAsync(CaptureMode.Single);
        viewModel.SettingsRequested += (_, _) =>
        {
            _captureSessionCancellation?.Cancel();
            _sessions.CancelCurrent();
            ShowSettings();
        };
        viewModel.SwitchModeRequested += (_, _) =>
        {
            CloseResultWindows();
            var overlays = ShowTextOverlays(
                work,
                result,
                settings with { DisplayMode = DisplayMode.Overlay });
            _ = TryStartBrowserFollowingAsync(
                work,
                overlays,
                settings with { DisplayMode = DisplayMode.Overlay });
        };

        var monitor = work.Monitor;
        var sourceBounds = ToCoreDipRect(work.AbsoluteSelection, monitor);
        var workArea = ToCoreDipRect(monitor.WorkArea, monitor);
        var panelBounds = SidePanelBoundsService.Place(
            sourceBounds,
            workArea,
            new DipSize(panel.Width, panel.Height),
            null);
        panel.ApplyPlacement(panelBounds, workArea);
        panel.PlacementChanged += OnSidePanelPlacementChanged;
        TrackAndShow(panel);
        if (work.ImagePng is not null) panel.PlaceBesideImage(work.ImageBounds ?? work.AbsoluteSelection, work.Monitor);
        else panel.PlaceNearPhysicalPoint(work.AbsoluteSelection.Center, work.Monitor);
        return panel;
    }

    private IReadOnlyList<TextOverlayWindow> ShowTextOverlays(
        LastTranslationWork work,
        TranslationResult result,
        AppSettings settings)
    {
        var overlays = new List<TextOverlayWindow>(result.Blocks.Count);
        foreach (var block in result.Blocks)
        {
            var viewModel = new TranslationResultViewModel
            {
                SourceText = block.SourceText,
                TranslatedText = block.Translation,
                SourceLanguageLabel = LanguageLabel(settings.SourceLanguage),
                TargetLanguageLabel = LanguageLabel(settings.TargetLanguage),
            };
            var overlay = new TextOverlayWindow(viewModel)
            {
                Opacity = Math.Clamp(settings.OverlayOpacity, 0.72, 1),
            };
            var absoluteBounds = block.Bounds.Translate(
                new PixelPoint(work.AbsoluteSelection.X, work.AbsoluteSelection.Y));
            overlay.SetBounds(ToDipRect(absoluteBounds, work.Monitor));
            var estimatedFontSize = Math.Clamp(
                absoluteBounds.Height / work.Monitor.ScaleY * 0.48,
                settings.MinimumOverlayFontSize,
                settings.MaximumOverlayFontSize);
            overlay.SetTextStyle(estimatedFontSize);
            overlay.SetInteractive(true);
            viewModel.RetryRequested += (_, _) => _ = RetryLastAsync();
            viewModel.ClearAllRequested += OnOverlayClearAllRequested;
            viewModel.SwitchModeRequested += (_, _) =>
            {
                CloseResultWindows();
                ShowSidePanel(
                    work,
                    result,
                    settings with { DisplayMode = DisplayMode.SidePanel });
            };
            overlay.Closed += OnTextOverlayClosed;
            TrackAndShow(overlay);
            _activeTextOverlays.Add(overlay);
            overlays.Add(overlay);
        }

        StartOverlayFocusTracking(work.SourceWindowHandle, overlays);
        return overlays;
    }

    private static TranslationResultViewModel CreateResultViewModel(
        TranslationResult result,
        AppSettings settings) =>
        new()
        {
            IsExpanded = true,
            HasTranslationInput = true,
            SourceText = string.Join(Environment.NewLine, result.Blocks.Select(block => block.SourceText)),
            TranslatedText = string.Join(Environment.NewLine, result.Blocks.Select(block => block.Translation)),
            SourceLanguageLabel = LanguageLabel(settings.SourceLanguage),
            TargetLanguageLabel = LanguageLabel(settings.TargetLanguage),
        };

    private void TrackAndShow(Window window)
    {
        _resultWindows.Add(window);
        window.Closed += (_, _) => _resultWindows.Remove(window);
        window.Show();
        if (!_captureSurfacesSuppressed)
        {
            return;
        }

        if (window is TextOverlayWindow overlay)
        {
            overlay.SetUserVisibility(false);
        }
        else
        {
            window.Hide();
        }
    }

    private void CloseResultWindows()
    {
        StopOverlayFocusTracking();
        StopBrowserFollowing(hideOverlays: false);
        foreach (var window in _resultWindows.ToArray())
        {
            window.Close();
        }

        _resultWindows.Clear();
        _activeTextOverlays.Clear();
    }

    private void ToggleOverlays()
    {
        _overlaysVisible = !_overlaysVisible;
        foreach (var window in _resultWindows.ToArray())
        {
            if (window is TextOverlayWindow overlay)
            {
                overlay.SetUserVisibility(_overlaysVisible);
            }
            else if (_overlaysVisible)
            {
                window.Show();
            }
            else
            {
                window.Hide();
            }
        }
    }

    private void StartOverlayFocusTracking(
        IntPtr sourceWindowHandle,
        IReadOnlyList<TextOverlayWindow> overlays)
    {
        if (sourceWindowHandle == IntPtr.Zero || overlays.Count == 0)
        {
            return;
        }

        try
        {
            if (_overlayFocusCoordinator is null)
            {
                _overlayFocusCoordinator = new OverlayFocusCoordinator(
                    sourceWindowHandle,
                    overlays);
            }
            else
            {
                _overlayFocusCoordinator.AddGroup(
                    sourceWindowHandle,
                    overlays);
            }

            if (_foregroundWindowMonitor is null)
            {
                _foregroundWindowMonitor = new ForegroundWindowMonitor();
                _foregroundWindowMonitor.Changed +=
                    OnForegroundWindowChanged;
            }

            _overlayFocusCoordinator.HandleForegroundChanged(
                ForegroundWindowMonitor.CaptureForegroundRootWindow());
        }
        catch (InvalidOperationException)
        {
            StopOverlayFocusTracking();
        }
    }

    private void OnForegroundWindowChanged(
        object? sender,
        ForegroundWindowChangedEventArgs e)
    {
        _application.Dispatcher.BeginInvoke(() =>
            _overlayFocusCoordinator?.HandleForegroundChanged(e.WindowHandle));
    }

    private void OnOverlayClearAllRequested(object? sender, EventArgs e) =>
        CloseResultWindows();

    private void OnTextOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is not TextOverlayWindow overlay)
        {
            return;
        }

        overlay.Closed -= OnTextOverlayClosed;
        overlay.ViewModel.ClearAllRequested -= OnOverlayClearAllRequested;
        _activeTextOverlays.Remove(overlay);
        _overlayFocusCoordinator?.Remove(overlay);
        RemoveBrowserTrackedOverlay(overlay);

        if (_activeTextOverlays.Count == 0)
        {
            StopOverlayFocusTracking();
        }
    }

    private void StopOverlayFocusTracking()
    {
        if (_foregroundWindowMonitor is not null)
        {
            _foregroundWindowMonitor.Changed -= OnForegroundWindowChanged;
            _foregroundWindowMonitor.Dispose();
            _foregroundWindowMonitor = null;
        }

        _overlayFocusCoordinator = null;
    }

    private async Task<ConnectionTestResult> TestConnectionAsync(
        TranslationConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        try { _ = TranslationProviderConfiguration.Endpoint(request.Provider, request.BaseUrl); (request.Network ?? new()).Validate(); }
        catch (TranslationConfigurationException error)
        {
            return new ConnectionTestResult(false, error.Message);
        }

        var apiKey = await ApiKeyResolver.ResolveAsync(
            request.ApiKey,
            _secretStore,
            cancellationToken, request.Provider, request.SecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ConnectionTestResult(
                false,
                $"请先配置 {TranslationProviderConfiguration.Label(request.Provider)} API Key");
        }

        var testSettings = new AppSettings
        {
            TranslationProvider = request.Provider, DeepSeekModel = request.Model, DeepSeekBaseUrl = request.BaseUrl,
            OpenAiModel = request.Model, OpenAiBaseUrl = request.BaseUrl,
            ClaudeModel = request.Model, ClaudeBaseUrl = request.BaseUrl,
            ChatCompletionsModel = request.Model, ChatCompletionsBaseUrl = request.BaseUrl,
            ApiNetwork = request.Network ?? new(),
            ThinkingLevel = request.ThinkingLevel,
        };
        try
        {
            var provider = TranslationProviderFactory.Create(_apiHttp.GetClient(testSettings), new InMemorySecretStore(apiKey), testSettings);
            var benchmark = await TranslationBenchmark.RunAsync(provider, request.ThinkingLevel, cancellationToken, request.Progress);
            return new ConnectionTestResult(true, "翻译测速完成", benchmark.Elapsed) { Benchmark = benchmark };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new ConnectionTestResult(false, exception.Message);
        }
    }

    private async void OnTranslationPresetDeleted(string secretName)
    {
        try
        {
            await SaveSettingsAsync();
            await _secretStore.DeleteAsync(secretName);
        }
        catch (Exception exception) { ShowFailure($"删除预设失败：{exception.Message}"); }
    }

    private async void OnTranslationSettingsSaveRequested(object? sender, EventArgs e)
    {
        try
        {
            TranslationSettings.SetSaving(true);
            var snapshot = CollectSettings();
            _ = TranslationProviderConfiguration.Endpoint(TranslationProviderConfiguration.Provider(snapshot), TranslationProviderConfiguration.BaseUrl(snapshot));
            _ = TranslationProviderFactory.Create(_apiHttp.GetClient(snapshot), _secretStore, snapshot);
            var secretName = TranslationProviderConfiguration.SecretName(snapshot);
            var replacementApiKey = TranslationSettings.ApiKey.Trim();
            if (replacementApiKey.Length == 0 && TranslationSettings.PendingCopySecretName is { } copyFrom)
                replacementApiKey = (await _secretStore.GetAsync(copyFrom)) ?? "";
            if (replacementApiKey.Length > 0)
            {
                await _secretStore.SetAsync(
                    secretName,
                    replacementApiKey);
                ApplySavedApiKeyPreview(replacementApiKey);
            }
            else if (TranslationSettings.HasSavedApiKey)
            {
                _applyingSettings = true;
                try
                {
                    TranslationSettings.CancelApiKeyEdit();
                }
                finally
                {
                    _applyingSettings = false;
                }
            }

            await SaveSettingsAsync(snapshot);
            TranslationSettings.CompletePresetKeyCopy();
            TranslationSettings.SetConnectionStatus("设置已安全保存");
            SyncAutoImageConfiguration(force: replacementApiKey.Length > 0);
            MainWindow.StatusText = "翻译设置已保存";
        }
        catch (Exception exception)
        {
            ShowFailure($"保存设置失败：{exception.Message}");
        }
        finally { TranslationSettings.SetSaving(false); }
    }

    private async Task LoadSavedApiKeyPreviewAsync()
    {
        foreach (var provider in Enum.GetValues<TranslationProviderKind>())
        {
            try
            {
                var key = await _secretStore.GetAsync(TranslationProviderConfiguration.SecretName(provider));
                _applyingSettings = true;
                try { TranslationSettings.ApplySavedApiKey(provider, key); }
                finally { _applyingSettings = false; }
            }
            catch (Exception exception)
            {
                _applyingSettings = true;
                try { TranslationSettings.ApplySavedApiKey(provider, null); }
                finally { _applyingSettings = false; }
                if (provider == TranslationSettings.ProviderKind)
                {
                    var message = $"读取已保存的 API Key 失败：{exception.Message}";
                    TranslationSettings.SetConnectionError(message);
                    MainWindow.StatusText = message;
                }
            }
        }
        foreach (var preset in _persistedSettings.TranslationProviderPresets)
        {
            var name = TranslationProviderConfiguration.PresetSecretName(preset.Id);
            try
            {
                var key = await _secretStore.GetAsync(name);
                _applyingSettings = true;
                try { TranslationSettings.ApplySavedApiKey(name, key); }
                finally { _applyingSettings = false; }
            }
            catch (Exception exception)
            {
                if (TranslationSettings.ActiveSecretName == name)
                    TranslationSettings.SetConnectionError($"读取预设密钥失败：{exception.Message}");
            }
        }
    }

    private void ApplySavedApiKeyPreview(string? apiKey)
    {
        _applyingSettings = true;
        try
        {
            TranslationSettings.ApplySavedApiKey(apiKey);
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private async void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingSettings || sender == TranslationSettings && TranslationSettings.IsSwitchingProvider)
        {
            return;
        }

        if (sender == HotkeySettings)
        {
            if (e.PropertyName != nameof(HotkeySettings.IsEnabled))
            {
                return;
            }

            await ApplyHotkeyEnabledStateAsync();
            return;
        }

        if (sender == BrowserIntegration
            && e.PropertyName == nameof(BrowserIntegration.IsEnabled)
            && !BrowserIntegration.IsEnabled)
        {
            StopBrowserFollowing(hideOverlays: false);
            BrowserIntegration.DetailText =
                "网页跟随已关闭；现有译文保持静态显示。";
        }

        if (sender == GeneralSettings && e.PropertyName == nameof(GeneralSettings.TargetLanguage))
        {
            _applyingSettings = true;
            TranslationSettings.TargetLanguage = GeneralSettings.TargetLanguage;
            _applyingSettings = false;
        }
        else if (sender == TranslationSettings && e.PropertyName == nameof(TranslationSettings.TargetLanguage))
        {
            _applyingSettings = true;
            GeneralSettings.TargetLanguage = TranslationSettings.TargetLanguage;
            _applyingSettings = false;
        }

        if (sender == AppearanceSettings && e.PropertyName == nameof(AppearanceSettings.Theme))
        {
            ApplyTheme(ThemeValue(AppearanceSettings.Theme));
        }
        else if (sender == GeneralSettings
                 && (e.PropertyName == nameof(GeneralSettings.StartWithWindows)
                     || e.PropertyName == nameof(GeneralSettings.StartSilently)))
        {
            ApplyStartupRegistration(
                GeneralSettings.StartWithWindows,
                GeneralSettings.StartSilently);
        }
        else if (sender == GeneralSettings
                 && e.PropertyName == nameof(GeneralSettings.MinimizeToTray)
                 && _mainWindow is not null)
        {
            _mainWindow.HideOnClose = GeneralSettings.MinimizeToTray;
        }
        else if (sender == GeneralSettings
                 && e.PropertyName == nameof(GeneralSettings.ShowTrayIcon))
        {
            _tray.SetVisible(GeneralSettings.ShowTrayIcon);
        }

        if (sender == GeneralSettings && e.PropertyName == nameof(GeneralSettings.TranslationAnimationsEnabled))
            Behaviors.TranslationMotion.AppEnabled = GeneralSettings.TranslationAnimationsEnabled;
        if ((sender == GeneralSettings && e.PropertyName is nameof(GeneralSettings.BrowserAutoImageTranslationEnabled) or nameof(GeneralSettings.BrowserAutoPageTranslationEnabled) or nameof(GeneralSettings.TranslationAnimationsEnabled)
                or nameof(GeneralSettings.BrowserPageCacheEnabled) or nameof(GeneralSettings.BrowserPageCacheMegabytes)
                or nameof(GeneralSettings.BrowserTranslationWhitelist) or nameof(GeneralSettings.BrowserTranslationBlacklist)
                or nameof(GeneralSettings.BrowserPageJudgementMode) or nameof(GeneralSettings.BrowserPageTranslationConcurrency) or nameof(GeneralSettings.TargetLanguage))
            || (sender == TranslationSettings && e.PropertyName is nameof(TranslationSettings.TargetLanguage)
                or nameof(TranslationSettings.SourceLanguage) or nameof(TranslationSettings.TranslationStyle)
                or nameof(TranslationSettings.SelectedProvider) or nameof(TranslationSettings.SelectedModel) or nameof(TranslationSettings.BaseUrl)
                or nameof(TranslationSettings.SelectedPreset)
                or nameof(TranslationSettings.CustomContext) or nameof(TranslationSettings.ThinkingLevel) or nameof(TranslationSettings.BoundedLowThinkingEnabled)))
            SyncAutoImageConfiguration();

        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            MainWindow.StatusText = "设置将在退出时重试保存";
        }
    }

    private async Task SaveSettingsAsync(AppSettings? suppliedSnapshot = null)
    {
        var snapshot = suppliedSnapshot ?? CollectSettings();
        await _settingsGate.WaitAsync();
        try
        {
            await _settingsStore.SaveAsync(snapshot);
            _persistedSettings = snapshot;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void OnHotkeyPauseChanged(object? sender, bool paused)
    {
        if (paused)
        {
            _hotkeyCoordinator.Disable();
            HotkeySettings.StatusText = "快捷键已暂停";
        }
        else
        {
            RegisterSavedHotkey();
        }
    }

    private void OnHotkeyRecordingStarted(object? sender, EventArgs e)
    {
        _hotkeyCoordinator.Suspend();
        HotkeySettings.StatusText = "请按新的组合键 · Esc 取消";
    }

    private void OnHotkeyRecordingCancelled(object? sender, EventArgs e)
    {
        if (!_persistedSettings.HotkeyEnabled)
        {
            ApplyHotkeyViewModel(
                _hotkeyCoordinator.CurrentGesture,
                isEnabled: false,
                "快捷键保持暂停");
            return;
        }

        var result = _hotkeyCoordinator.TryRestoreCurrent();
        ApplyHotkeyViewModel(
            result.Gesture,
            result.IsEnabled,
            result.IsEnabled ? "已取消修改，原快捷键已恢复" : result.Message);
    }

    private async void OnHotkeyGestureSubmitted(
        object? sender,
        HotkeyGesture gesture)
    {
        var result = _hotkeyCoordinator.TryReplace(gesture);
        ApplyHotkeyViewModel(
            result.Gesture,
            result.IsEnabled,
            result.Message);

        if (result.Succeeded)
        {
            _persistedSettings = _persistedSettings with
            {
                Hotkey = result.Gesture.ToPersistedString(),
                HotkeyEnabled = true,
            };
            MainWindow.StatusText = $"快捷键已更新 · {result.Gesture.ToDisplayString()}";
            await SaveSettingsAsync();
        }
        else if (!result.IsEnabled)
        {
            _persistedSettings = _persistedSettings with { HotkeyEnabled = false };
            MainWindow.StatusText = "快捷键已禁用，可从托盘开始框选";
            await SaveSettingsAsync();
        }
    }

    private void OnClearHistoryRequested(object? sender, EventArgs e)
    {
        PrivacySettings.SaveTextHistory = false;
        MainWindow.StatusText = "本地翻译历史已清除";
    }

    private void RegisterSavedHotkey()
    {
        var gesture = HotkeyGesture.Parse(_persistedSettings.Hotkey);
        if (!_persistedSettings.HotkeyEnabled)
        {
            _hotkeyCoordinator.Disable();
            ApplyHotkeyViewModel(gesture, isEnabled: false, "快捷键已暂停");
            return;
        }

        var result = _hotkeyCoordinator.TryEnable(gesture);
        ApplyHotkeyViewModel(
            result.Gesture,
            result.IsEnabled,
            result.Succeeded ? "快捷键可用" : result.Message);
        if (!result.Succeeded)
        {
            MainWindow.StatusText = "快捷键冲突，可从托盘开始框选";
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Behaviors.TranslationMotion.AppEnabled = settings.TranslationAnimationsEnabled;
        _applyingSettings = true;
        try
        {
            var targetLabel = LanguageLabel(settings.TargetLanguage);
            GeneralSettings.TargetLanguage = targetLabel;
            GeneralSettings.StartWithWindows = settings.StartWithWindows;
            GeneralSettings.StartSilently = settings.StartSilently;
            GeneralSettings.MinimizeToTray = settings.MinimizeToTray;
            GeneralSettings.ShowTrayIcon = settings.ShowTrayIcon;
            GeneralSettings.TranslationAnimationsEnabled = settings.TranslationAnimationsEnabled;
            GeneralSettings.BrowserAutoImageTranslationEnabled = settings.BrowserAutoImageTranslationEnabled;
            GeneralSettings.BrowserAutoPageTranslationEnabled = settings.BrowserAutoPageTranslationEnabled;
            GeneralSettings.BrowserTranslationWhitelist = settings.BrowserTranslationWhitelist;
            GeneralSettings.BrowserTranslationBlacklist = settings.BrowserTranslationBlacklist;
            GeneralSettings.BrowserPageTranslationConcurrency = Math.Clamp(settings.BrowserPageTranslationConcurrency, 1, 10);
            GeneralSettings.BrowserPageCacheEnabled = settings.BrowserPageCacheEnabled;
            GeneralSettings.BrowserPageCacheMegabytes = settings.BrowserPageCacheMegabytes;
            GeneralSettings.CaptureHotkeyText =
                HotkeyGesture.Parse(settings.Hotkey).ToDisplayString();
            BrowserIntegration.IsEnabled = settings.BrowserFollowingEnabled;
            TranslationSettings.ApplyProviderSettings(settings);
            TranslationSettings.SourceLanguage = LanguageLabel(settings.SourceLanguage);
            TranslationSettings.TargetLanguage = targetLabel;
            TranslationSettings.TranslationStyle = StyleLabel(settings.TranslationStyle);
            TranslationSettings.CustomContext = settings.TranslationContext;
            TranslationSettings.ThinkingLevel = ThinkingLabel(TranslationProviderConfiguration.ActivePreset(settings)?.ThinkingLevel ?? settings.ThinkingLevel);
            TranslationSettings.InputMode = InputLabel(settings.InputMode);
            TranslationSettings.InPlaceTranslationEnabled = settings.InPlaceTranslationEnabled;
            TranslationSettings.BoundedLowThinkingEnabled = TranslationProviderConfiguration.ActivePreset(settings)?.BoundedLowThinkingEnabled ?? settings.BoundedLowThinkingEnabled;
            TranslationSettings.TriggerMode = settings.TriggerMode == TranslationTriggerMode.MousePosition ? "快捷键即译鼠标位置" : "快捷键后点击或拖框";
            AppearanceSettings.Theme = ThemeLabel(settings.Theme);
            ApplyTheme(settings.Theme);
            AppearanceSettings.DisplayMode = "鼠标附近悬浮窗";
            AppearanceSettings.PanelOpacity = settings.OverlayOpacity * 100;
            PrivacySettings.SaveTextHistory = settings.SaveHistory;
            _tray.SetVisible(settings.ShowTrayIcon);
            HotkeySettings.ApplyGesture(
                HotkeyGesture.Parse(settings.Hotkey),
                settings.HotkeyEnabled,
                settings.HotkeyEnabled ? "准备注册快捷键" : "快捷键已暂停");
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private AppSettings CollectSettings() =>
        TranslationSettings.CollectProviderSettings(_persistedSettings) with
        {
            SourceLanguage = LanguageCode(TranslationSettings.SourceLanguage),
            TargetLanguage = LanguageCode(TranslationSettings.TargetLanguage),
            TranslationStyle = StyleValue(TranslationSettings.TranslationStyle),
            TranslationContext = TranslationSettings.CustomContext,
            ThinkingLevel = ThinkingValue(TranslationSettings.ThinkingLevel),
            InputMode = InputValue(TranslationSettings.InputMode),
            InPlaceTranslationEnabled = TranslationSettings.InPlaceTranslationEnabled,
            BoundedLowThinkingEnabled = TranslationSettings.BoundedLowThinkingEnabled,
            TriggerMode = TranslationSettings.TriggerMode == "快捷键即译鼠标位置" ? TranslationTriggerMode.MousePosition : TranslationTriggerMode.ClickOrDrag,
            DisplayMode = DisplayMode.SidePanel,
            Theme = ThemeValue(AppearanceSettings.Theme),
            OverlayOpacity = Math.Clamp(AppearanceSettings.PanelOpacity / 100, 0.72, 1),
            SaveHistory = false,
            StartWithWindows = GeneralSettings.StartWithWindows,
            StartSilently = GeneralSettings.StartSilently,
            MinimizeToTray = GeneralSettings.MinimizeToTray,
            ShowTrayIcon = GeneralSettings.ShowTrayIcon,
            TranslationAnimationsEnabled = GeneralSettings.TranslationAnimationsEnabled,
            Hotkey = HotkeySettings.Gesture.ToPersistedString(),
            HotkeyEnabled = HotkeySettings.IsEnabled,
            BrowserFollowingEnabled = BrowserIntegration.IsEnabled,
            BrowserAutoImageTranslationEnabled = GeneralSettings.BrowserAutoImageTranslationEnabled,
            BrowserAutoPageTranslationEnabled = GeneralSettings.BrowserAutoPageTranslationEnabled,
            BrowserTranslationWhitelist = GeneralSettings.BrowserTranslationWhitelist,
            BrowserTranslationBlacklist = GeneralSettings.BrowserTranslationBlacklist,
            BrowserPageJudgementMode = BrowserPageJudgementMode.Rules,
            BrowserPageTranslationConcurrency = Math.Clamp(GeneralSettings.BrowserPageTranslationConcurrency, 1, 10),
            BrowserPageCacheEnabled = GeneralSettings.BrowserPageCacheEnabled,
            BrowserPageCacheMegabytes = GeneralSettings.BrowserPageCacheMegabytes,
        };

    private async Task ApplyHotkeyEnabledStateAsync()
    {
        if (HotkeySettings.IsEnabled)
        {
            var result = _hotkeyCoordinator.TryEnable(HotkeySettings.Gesture);
            ApplyHotkeyViewModel(
                result.Gesture,
                result.IsEnabled,
                result.Succeeded ? "快捷键可用" : result.Message);
        }
        else
        {
            _hotkeyCoordinator.Disable();
            HotkeySettings.StatusText = "快捷键已暂停";
        }

        await SaveSettingsAsync();
    }

    private void ApplyHotkeyViewModel(
        HotkeyGesture gesture,
        bool isEnabled,
        string statusText)
    {
        _applyingSettings = true;
        try
        {
            HotkeySettings.ApplyGesture(gesture, isEnabled, statusText);
            GeneralSettings.CaptureHotkeyText = gesture.ToDisplayString();
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private async void OnSidePanelPlacementChanged(
        object? sender,
        WindowPlacement placement)
    {
        _persistedSettings = _persistedSettings with
        {
            SidePanelPlacement = placement,
        };
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            MainWindow.StatusText = "侧边窗口位置将在退出时重试保存";
        }
    }

    private static ThinkingLevel ThinkingValue(string label) => label switch
    { "关闭" => ThinkingLevel.None, "高" => ThinkingLevel.High, "最高" => ThinkingLevel.Max, _ => ThinkingLevel.Low };
    private static string ThinkingLabel(ThinkingLevel value) => value switch
    { ThinkingLevel.None => "关闭", ThinkingLevel.High => "高", ThinkingLevel.Max => "最高", _ => "低" };
    private static TranslationInputMode InputValue(string label) => label switch
    { "VLM 直接翻译" => TranslationInputMode.VisionOnly, _ => TranslationInputMode.Automatic };
    private static string InputLabel(TranslationInputMode value) => value switch
    { TranslationInputMode.VisionOnly => "VLM 直接翻译", _ => "自动识别" };

    private static Rect ToDipRect(PixelRect rect, ScreenMonitor monitor) =>
        new(
            rect.X / monitor.ScaleX,
            rect.Y / monitor.ScaleY,
            rect.Width / monitor.ScaleX,
            rect.Height / monitor.ScaleY);

    private static DipRect ToCoreDipRect(PixelRect rect, ScreenMonitor monitor) =>
        new(
            rect.X / monitor.ScaleX,
            rect.Y / monitor.ScaleY,
            rect.Width / monitor.ScaleX,
            rect.Height / monitor.ScaleY);

    private static string LanguageCode(string label) =>
        label switch
        {
            "自动检测" => "auto",
            "简体中文" => "zh-CN",
            "繁体中文" => "zh-TW",
            "英语" => "en",
            "日语" => "ja",
            "韩语" => "ko",
            _ => label,
        };

    private static string LanguageLabel(string code) =>
        code switch
        {
            "auto" => "自动检测",
            "zh-CN" => "简体中文",
            "zh-TW" => "繁体中文",
            "en" => "英语",
            "ja" => "日语",
            "ko" => "韩语",
            _ => code,
        };

    private static TranslationStyle StyleValue(string label) =>
        label switch
        {
            "直译" => TranslationStyle.Literal,
            "学习模式" => TranslationStyle.Learning,
            _ => TranslationStyle.Natural,
        };

    private static string StyleLabel(TranslationStyle style) =>
        style switch
        {
            TranslationStyle.Literal => "直译",
            TranslationStyle.Learning => "学习模式",
            _ => "自然",
        };

    private static ThemePreference ThemeValue(string label) =>
        label switch
        {
            "浅色" => ThemePreference.Light,
            "深色" => ThemePreference.Dark,
            _ => ThemePreference.System,
        };

    private static string ThemeLabel(ThemePreference theme) =>
        theme switch
        {
            ThemePreference.Light => "浅色",
            ThemePreference.Dark => "深色",
            _ => "跟随系统",
        };

    private static void ApplyTheme(ThemePreference preference)
    {
        var theme = preference switch
        {
            ThemePreference.Light => ApplicationTheme.Light,
            ThemePreference.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(theme);
        Application.Current.Resources["TranslationSurfaceBrush"] =
            TranslationSurfacePalette.CreateBrush(theme);
    }

    private static void ApplyStartupRegistration(bool enabled, bool startSilently)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "GlobalAITranslator";
        using var runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(runKeyPath);

        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定屏译程序路径。");
            var silentArgument = startSilently
                ? $" {StartupCommand.SilentArgument}"
                : string.Empty;
            runKey.SetValue(valueName, $"\"{executablePath}\"{silentArgument}");
        }
        else
        {
            runKey.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static void ShowFailure(string message) =>
        MessageBox.Show(
            message,
            "屏译",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);

    public void Exit()
    {
        if (!_disposed && !_exitRequested)
        {
            _exitRequested = true;
            _captureSessionCancellation?.Cancel();
            if (_continuousCaptureQueue is not null)
            {
                _ = _continuousCaptureQueue.DisposeAsync();
            }

            if (_mainWindow is not null)
            {
                _mainWindow.IsApplicationShuttingDown = true;
            }

            _tray.Dispose();
            _application.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Flush completed encrypted cache entries before the process exits.
        try { if (!DisposeAutoPagesAsync().Wait(TimeSpan.FromSeconds(3))) AppDiagnostics.Write("page_cache_shutdown_flush_timeout"); }
        catch (Exception error) { AppDiagnostics.Write($"page_cache_shutdown_failed type={error.GetType().Name}"); }
        _ = DisposeAutoImagesAsync();
        _captureSessionCancellation?.Cancel();
        if (_continuousCaptureQueue is not null)
        {
            try
            {
                var disposeQueue = _continuousCaptureQueue
                    .DisposeAsync()
                    .AsTask();
                _ = disposeQueue.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Shutdown must continue while queued translations are cancelled.
            }
        }

        _tray.Dispose();
        try
        {
            var saveTask = _settingsStore.SaveAsync(CollectSettings());
            _ = saveTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutdown must continue even when settings are unavailable.
        }

        CloseResultWindows();
        DisposeBrowserIntegration();
        _sessions.Dispose();
        _apiHttp.Dispose();
        _localTextDetector.Dispose();
        _ocrWarmupCancellation.Cancel();
        _ocrWarmupCancellation.Dispose();
        _hotkey.Dispose();
        _captureGate.Dispose();
        _settingsGate.Dispose();
    }

    private sealed record CaptureSourceContext(
        IntPtr SourceWindowHandle,
        CapturedBrowserWindow? CapturedBrowser);

    private sealed record AcquiredCapture(
        TranslationCaptureWorkItem Item,
        CaptureMode Mode);

    private sealed record SelectedRegion(
        MonitorCapture Capture,
        PixelRect Bounds,
        CaptureMode Mode)
    {
        public string? AccessibleText { get; init; }
        public nint SourceWindowHandle { get; init; }
    }

    private sealed record LastTranslationWork(
        ScreenMonitor Monitor,
        PixelRect AbsoluteSelection,
        IReadOnlyList<OcrBlock> Blocks,
        CapturedBrowserWindow? CapturedBrowser,
        IntPtr SourceWindowHandle)
    {
        public byte[]? ImagePng { get; init; }
        public PixelRect? SourceWindowBounds { get; init; }
        public SourceImageMapping? SourceImageMapping { get; init; }
        public Func<Task<SourceImageMapping?>>? RefreshSourceImageMapping { get; init; }
        public bool IsOcrDocument { get; init; }
        public bool IsDirectVision { get; init; }
        public GroundedVisionDocument? VisionDocument { get; init; }
        public string? CacheKey{get;init;}
        public CachedImageTranslation? Cached{get;init;}
        public BrowserImageIdentity? BrowserImage{get;init;}
        public CapturedBitmap? SourceBitmap { get; init; }
        public bool IsComic { get; init; }
        public PixelRect? ImageBounds { get; init; }
        public byte[]? SourceImagePng { get; init; }
        public IReadOnlyList<ImageAtlasRegion> AtlasRegions { get; init; } = [];
        public PixelSize? AtlasSize { get; init; }
    }

    private sealed class InMemorySecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(apiKey);

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

    private sealed class SilentTranslationProgress : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) { } }
}
