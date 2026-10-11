using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.App.Windows;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Sessions;
using ScreenTranslator.Core.Settings;
using System.Windows.Threading;
using CaptureMode = ScreenTranslator.App.ViewModels.CaptureMode;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private async Task RunCaptureSessionAsync(CaptureMode initialMode)
    {
        if (_captureSessionCancellation is not null)
        {
            _captureSessionCancellation.Cancel();
            _sessions.CancelCurrent();
            return;
        }
        if (!await _captureGate.WaitAsync(0))
        {
            _sessions.CancelCurrent();
            MainWindow.StatusText = "已取消上一项翻译，请再次按快捷键选择内容";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _autoImageQueue?.SetManualBusy(true);
        _captureSessionCancellation = cancellation;
        MainWindow.IsCaptureAvailable = false;
        _mainWindow?.Hide();
        if (initialMode == CaptureMode.Single) CloseResultWindows();
        _continuousCaptureQueue = null;
        _continuousAcquisitionActive = initialMode == CaptureMode.Multiple;
        GeneralSettings.IsContinuousCaptureActive = _continuousAcquisitionActive;
        SidePanelWindow? panel = null;
        TranslationResultViewModel? model = null;
        try
        {
            do
            {
                var point = MouseSelectionService.CursorPosition;
                var settings = CollectSettings();
                var monitors = _screenCapture.GetMonitors();
                var monitor = MonitorAt(monitors, point);
                model = new TranslationResultViewModel
                {
                    IsBusy = true,
                    IsExpanded = false,
                    StatusText = settings.TriggerMode == TranslationTriggerMode.MousePosition ? "正在定位鼠标下的文字…" : "点击文字或拖框 · Esc 取消",
                    SourceLanguageLabel = "自动识别",
                    TargetLanguageLabel = LanguageLabel(settings.TargetLanguage),
                };
                panel = ShowSidePanel(new LastTranslationWork(monitor, new PixelRect(point.X, point.Y, 1, 1), [], null, nint.Zero),
                    new TranslationResult([]), settings, model);
                var thisModel = model;
                panel.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_captureSessionCancellation, cancellation)) cancellation.Cancel();
                    if (thisModel.IsBusy) _sessions.CancelCurrent();
                };

                var waitForClick = settings.TriggerMode == TranslationTriggerMode.ClickOrDrag;
                TranslationCaptureWorkItem? item = null;
                while (item is null)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    PointerSelection selected;
                    if (waitForClick)
                    {
                        using var mouse = new MouseSelectionService(cancellation.Token);
                        var outline = new SelectionOutlineWindow();
                        PixelRect? pendingBounds = null;
                        var frameTimer = new DispatcherTimer(DispatcherPriority.Render, _application.Dispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(16),
                        };
                        // Keep input callbacks short and render only the latest bounds each frame.
                        mouse.DragChanged += bounds => pendingBounds = bounds;
                        frameTimer.Tick += (_, _) =>
                        {
                            if (pendingBounds is { } bounds && !cancellation.IsCancellationRequested && !mouse.Selection.IsCompleted)
                            {
                                pendingBounds = null;
                                outline.Update(bounds);
                                thisModel.StatusText = "松开鼠标开始翻译";
                            }
                        };
                        frameTimer.Start();
                        try { selected = await mouse.Selection; }
                        finally { frameTimer.Stop(); outline.Close(); }
                    }
                    else selected = new PointerSelection(point, new PixelRect(point.X, point.Y, 1, 1), false);

                    point = selected.Point;
                    monitors = _screenCapture.GetMonitors();
                    monitor = MonitorAt(monitors, point);
                    panel.PlaceNearPhysicalPoint(point, monitor);
                    var sourceHandle = AccessibleTextResolver.FindWindowAt(point);
                    model.StatusText = "正在自动选择文字区域…";
                    MainWindow.StatusText = model.StatusText;
                    MonitorCapture capture;
                    PixelRect? sourceWindowBounds;
                    SmartSelection? region;
                    // DOM image reading does not require hiding the collapsed status popup.
                    if (!selected.IsDrag && await ReadBrowserImageAsync(point, sourceHandle, cancellation.Token) is { } originalImage)
                    {
                        var anchor = originalImage.Mapping.VisibleBounds;
                        model.StatusText = "已读取完整原图，正在识读全部文字…";
                        panel.PlaceBesideImage(anchor, monitor);
                        item = new TranslationCaptureWorkItem(monitor, anchor, originalImage.Bitmap, originalImage.Browser, sourceHandle, settings)
                        {
                            ImageBounds = anchor, SourceWindowBounds = originalImage.Browser.Snapshot.Bounds,
                            SourceImageMapping = originalImage.Mapping,
                            RefreshSourceImageMapping = () => RefreshBrowserImageAsync(originalImage),
                            BrowserImage=originalImage.Browser.Browser==Core.Browser.BrowserKind.Chrome
                                ?new(originalImage.ConnectionId,originalImage.TabId,originalImage.ImageId,originalImage.DocumentToken,originalImage.Bitmap.Size):null,
                        };
                        continue;
                    }
                    // Capture without our own popup or earlier translations covering the selected content.
                    SuppressResultWindowsForCapture();
                    try
                    {
                    capture = await _screenCapture.CaptureAsync(monitor, cancellation.Token);
                    sourceWindowBounds = AccessibleTextResolver.GetWindowBounds(sourceHandle);
                    if (selected.IsDrag)
                        region = new SmartSelection(selected.Bounds.Intersect(monitor.Bounds), null, sourceHandle);
                    else
                    {
                        var imageBounds = await AccessibleTextResolver.ResolveImageBoundsAsync(point, sourceHandle, cancellation.Token);
                        var accessible = imageBounds is null
                            ? await AccessibleTextResolver.ResolveInWindowAsync(point, sourceHandle, cancellation.Token) : null;
                        if (imageBounds is { } image)
                        {
                            model.StatusText = "正在选择图片内的全部文字…";
                            panel.PlaceBesideImage(image, monitor);
                            region = await new SmartSelectionResolver(_ocrEngine).FromWholeImageAsync(capture, image,
                                settings.SourceLanguage, sourceHandle, cancellation.Token);
                        }
                        else if (accessible is not null)
                            region = new SmartSelection(accessible.Bounds.Intersect(monitor.Bounds), accessible.Text, accessible.SourceWindowHandle);
                        else
                        {
                            try
                            {
                                region = await new SmartSelectionResolver(_ocrEngine).FromImageAsync(capture, point,
                                    settings.SourceLanguage, sourceHandle, cancellation.Token);
                            }
                            catch (OcrLanguageUnavailableException) { region = null; }
                        }
                    }
                    }
                    finally { RestoreResultWindowsAfterCapture(); }
                    if (region is null || !region.Bounds.IsUsable)
                    {
                        model.StatusText = "未定位到文字，请拖框选择";
                        waitForClick = true;
                        continue;
                    }
                    var bounds = new PixelRect(region.Bounds.X - monitor.Bounds.X, region.Bounds.Y - monitor.Bounds.Y,
                        region.Bounds.Width, region.Bounds.Height).Intersect(new PixelRect(0, 0, capture.Bitmap.Width, capture.Bitmap.Height));
                    item = new TranslationCaptureWorkItem(monitor, bounds.Translate(new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y)), CapturedBitmapCropper.Crop(capture.Bitmap, bounds),
                        null, region.SourceWindowHandle, settings)
                    { AccessibleText = region.AccessibleText, ImageBounds = region.ImageBounds, IsManualSelection = selected.IsDrag, SourceWindowBounds = sourceWindowBounds };
                    AppDiagnostics.Write($"selected input={(item.AccessibleText is null ? "image" : "text")} width={bounds.Width} height={bounds.Height}");
                }
                await ProcessCaptureWorkItemAsync(item, preserveExistingResults: false, cancellation.Token, model);
                if (initialMode == CaptureMode.Multiple) MainWindow.StatusText = "继续选择下一处 · Esc 或右键结束";
            } while (initialMode == CaptureMode.Multiple && !cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            if (model is not null) { model.IsBusy = false; model.StatusText = "已取消"; }
            if (panel?.IsLoaded == true) panel.Close();
            MainWindow.StatusText = "已取消选区或翻译";
        }
        catch (Exception exception)
        {
            if (model is not null)
            {
                model.IsBusy = false;
                model.StatusText = "操作失败 · 点击查看";
                model.ErrorMessage = exception.Message;
            }
            MainWindow.StatusText = "操作失败，可在浮窗中重新选择";
            AppDiagnostics.Write($"capture_failed type={exception.GetType().Name}");
        }
        finally
        {
            _captureSessionCancellation = null;
            _continuousAcquisitionActive = false;
            GeneralSettings.IsContinuousCaptureActive = false;
            MainWindow.IsCaptureAvailable = true;
            RestoreResultWindowsAfterCapture();
            _captureGate.Release();
            _autoImageQueue?.SetManualBusy(false);
        }
    }

    private static ScreenMonitor MonitorAt(IReadOnlyList<ScreenMonitor> monitors, PixelPoint point) =>
        monitors.FirstOrDefault(m => point.X >= m.Bounds.X && point.X < m.Bounds.Right && point.Y >= m.Bounds.Y && point.Y < m.Bounds.Bottom)
        ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? throw new InvalidOperationException("没有可用的显示器。");
}
