using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Windows;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private static readonly TimeSpan BrowserFollowStartupTimeout =
        Timeout.InfiniteTimeSpan;
    private static readonly TimeSpan BrowserFollowRetryInterval =
        TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions BrowserJsonOptions =
        CreateBrowserJsonOptions();

    private readonly ConcurrentDictionary<Guid, BrowserKind> _browserConnections = new();
    private readonly Dictionary<BrowserKind, BrowserInstallation>
        _browserInstallations = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ActiveTabReply>>
        _pendingActiveTabQueries = new();

    private BrowserBridgeServer? _browserBridgeServer;
    private NativeMessagingRegistrationService? _nativeMessagingRegistration;
    private BrowserFollowCoordinator? _browserFollowCoordinator;
    private BrowserWindowEventMonitor? _browserWindowMonitor;
    private BrowserWindowSnapshot? _browserWindowSnapshot;
    private Guid? _activeBrowserConnectionId;
    private int _browserFollowGeneration;
    private bool _browserScrollLogged;

    private async Task InitializeBrowserIntegrationAsync()
    {
        _browserBridgeServer = new BrowserBridgeServer();
        _browserBridgeServer.MessageReceived += OnBrowserBridgeMessageReceived;
        _browserBridgeServer.ConnectionClosed += OnBrowserBridgeConnectionClosed;
        InitializeAutoPages();
        InitializeAutoImages();
        _browserBridgeServer.Start();

        BrowserIntegration.OpenBrowserExtensionsRequested +=
            OnOpenBrowserExtensionsRequested;
        BrowserIntegration.OpenExtensionFolderRequested +=
            OnOpenExtensionFolderRequested;
        BrowserIntegration.InstallExtensionRequested +=
            OnInstallExtensionRequested;
        BrowserIntegration.RepairBridgeRequested +=
            OnRepairBrowserBridgeRequested;

        var detector = new BrowserInstallationDetector();
        foreach (var browser in new[]
                 {
                     BrowserKind.Chrome,
                     BrowserKind.Edge,
                 })
        {
            var installation = detector.Detect(browser);
            _browserInstallations[browser] = installation;
            BrowserIntegration.UpdateDetected(
                browser,
                installation.IsInstalled);
        }

        try
        {
            _nativeMessagingRegistration = new NativeMessagingRegistrationService();
            await _nativeMessagingRegistration.RegisterAsync();
            BrowserFollowDiagnostics.Write("bridge_registration_ready");
            BrowserIntegration.DetailText =
                "图片读取桥接已就绪。加载配套扩展后，点击网页图片会读取完整原图；Chrome 原位译文随页面滚动和缩放。";
        }
        catch (Exception exception)
        {
            BrowserFollowDiagnostics.Write(
                "bridge_registration_failed",
                ("error", exception.Message));
            BrowserIntegration.DetailText =
                $"网页跟随桥接暂不可用：{exception.Message}";
            BrowserIntegration.SetBridgeError(
                $"网页跟随桥接暂不可用：{exception.Message}");
        }
    }

    private async Task TryStartBrowserFollowingAsync(
        LastTranslationWork work,
        IReadOnlyList<TextOverlayWindow> overlays,
        AppSettings settings)
    {
        var capturedBrowser = work.CapturedBrowser;
        var skipReason = !settings.BrowserFollowingEnabled
            ? "setting_disabled"
            : !BrowserIntegration.IsEnabled
                ? "view_model_disabled"
                : capturedBrowser is null
                    ? "source_is_not_browser"
                    : overlays.Count == 0
                        ? "no_overlays"
                        : _browserBridgeServer is null
                            ? "bridge_server_unavailable"
                            : null;
        if (skipReason is not null)
        {
            BrowserFollowDiagnostics.Write(
                "follow_skipped",
                ("reason", skipReason));
            return;
        }

        ArgumentNullException.ThrowIfNull(capturedBrowser);
        BrowserWindowEventMonitor? monitor = null;
        var generation = Volatile.Read(ref _browserFollowGeneration);
        var waitingWasLogged = false;
        BrowserFollowDiagnostics.Write(
            "follow_wait_started",
            ("browser", capturedBrowser.Browser),
            ("generation", generation),
            ("overlays", overlays.Count));
        try
        {
            monitor = new BrowserWindowEventMonitor(
                capturedBrowser.Snapshot.Handle);
            var startupWaiter = new BrowserFollowStartupWaiter(
                BrowserFollowStartupTimeout,
                BrowserFollowRetryInterval);
            var candidate = await startupWaiter.WaitAsync<BrowserFollowCandidate>(
                async cancellationToken =>
                {
                    var snapshot = monitor.GetSnapshot();
                    if (snapshot is null)
                    {
                        return null;
                    }

                    var activeTab = await QueryMatchingActiveTabAsync(
                        capturedBrowser.Browser,
                        snapshot,
                        cancellationToken);
                    return activeTab is null
                        ? null
                        : new BrowserFollowCandidate(activeTab.Value, snapshot);
                },
                () =>
                    !_disposed
                    && generation == Volatile.Read(ref _browserFollowGeneration)
                    && overlays.Any(overlay => overlay.IsLoaded)
                    && monitor.GetSnapshot() is not null,
                () =>
                {
                    BrowserIntegration.DetailText =
                        "译文已显示，正在等待浏览器扩展连接并启用网页跟随…";
                    if (!waitingWasLogged)
                    {
                        waitingWasLogged = true;
                        BrowserFollowDiagnostics.Write(
                            "follow_waiting_for_matching_tab",
                            ("browser", capturedBrowser.Browser));
                    }
                });
            if (candidate is null
                || _disposed
                || generation != Volatile.Read(ref _browserFollowGeneration)
                || overlays.All(overlay => !overlay.IsLoaded))
            {
                monitor.Dispose();
                BrowserFollowDiagnostics.Write(
                    "follow_wait_stopped",
                    ("disposed", _disposed),
                    ("generation_current",
                        generation == Volatile.Read(ref _browserFollowGeneration)),
                    ("loaded_overlays",
                        overlays.Count(overlay => overlay.IsLoaded)));
                if (!_disposed
                    && generation == Volatile.Read(ref _browserFollowGeneration)
                    && overlays.Any(overlay => overlay.IsLoaded))
                {
                    BrowserIntegration.DetailText =
                        "浏览器扩展暂未连接，本次译文保持静态；连接恢复后下次翻译可继续跟随。";
                }

                return;
            }

            var activeOverlays = overlays
                .Where(overlay => overlay.IsLoaded)
                .ToArray();
            var monitorScale = work.Monitor.ScaleX;
            var match = candidate.Value.Match;
            var currentSnapshot = candidate.Value.Snapshot;
            var hello = match.Reply.ToHello();
            var viewportBounds = CalculateViewportBounds(
                currentSnapshot,
                hello,
                monitorScale);
            var session = new BrowserTrackingSession(
                hello,
                monitorScale,
                viewportBounds);
            var existingCoordinator = _browserFollowCoordinator;
            if (existingCoordinator is not null)
            {
                if (existingCoordinator.Browser == hello.Browser
                    && existingCoordinator.BrowserWindowId
                    == hello.BrowserWindowId
                    && existingCoordinator.TabId == hello.TabId
                    && string.Equals(
                        existingCoordinator.DocumentToken,
                        hello.DocumentToken,
                        StringComparison.Ordinal))
                {
                    existingCoordinator.AddGroup(
                        activeOverlays.Cast<ITrackedOverlay>().ToArray(),
                        ToCoreDipRect(
                            work.AbsoluteSelection,
                            work.Monitor));
                    BrowserFollowDiagnostics.Write(
                        "follow_group_attached",
                        ("browser", hello.Browser),
                        ("tab_id", hello.TabId),
                        ("overlays", activeOverlays.Length));
                    BrowserIntegration.DetailText =
                        $"{BrowserLabel(hello.Browser)} 网页跟随已扩展到新的连续译文。";
                }

                monitor.Dispose();
                return;
            }

            var coordinator = new BrowserFollowCoordinator(
                session,
                activeOverlays.Cast<ITrackedOverlay>().ToArray(),
                ToCoreDipRect(work.AbsoluteSelection, work.Monitor));
            coordinator.Invalidated += OnBrowserFollowInvalidated;

            _browserFollowCoordinator = coordinator;
            _browserWindowMonitor = monitor;
            _browserWindowSnapshot = currentSnapshot;
            _activeBrowserConnectionId = match.ConnectionId;
            _browserScrollLogged = false;
            monitor.Changed += OnBrowserWindowChanged;
            BrowserFollowDiagnostics.Write(
                "follow_attached",
                ("browser", hello.Browser),
                ("browser_window_id", hello.BrowserWindowId),
                ("tab_id", hello.TabId),
                ("overlays", activeOverlays.Length));
            BrowserIntegration.DetailText =
                $"{BrowserLabel(hello.Browser)} 网页跟随已启用；滚动不会重复 OCR 或请求翻译。";
        }
        catch (OperationCanceledException)
        {
            monitor?.Dispose();
            BrowserFollowDiagnostics.Write("follow_wait_cancelled");
        }
        catch (Exception exception)
        {
            monitor?.Dispose();
            BrowserFollowDiagnostics.Write(
                "follow_start_failed",
                ("error", exception.Message));
            BrowserIntegration.DetailText =
                $"本次保持静态译文，网页跟随未启动：{exception.Message}";
        }
    }

    private async Task<ActiveTabMatch?> QueryMatchingActiveTabAsync(
        BrowserKind expectedBrowser,
        BrowserWindowSnapshot expectedWindow,
        CancellationToken cancellationToken)
    {
        if (_browserBridgeServer is null)
        {
            return null;
        }

        var preferredConnections = _browserConnections
            .Where(pair => pair.Value == expectedBrowser)
            .Select(pair => pair.Key)
            .ToArray();
        var connectionIds = preferredConnections.Length > 0
            ? preferredConnections
            : _browserBridgeServer.Connections.ToArray();
        if (connectionIds.Length == 0)
        {
            return null;
        }

        var replies = await Task.WhenAll(connectionIds.Select(
            connectionId => QueryActiveTabAsync(connectionId, cancellationToken)));
        return replies
            .Where(reply => reply is not null)
            .Select(reply => reply!.Value)
            .Where(reply =>
                reply.Reply.CanCreateHello
                && reply.Reply.BrowserKind == expectedBrowser
                && WindowBoundsMatch(
                    expectedWindow,
                    reply.Reply.BrowserWindowBounds!.Value))
            .OrderBy(reply => WindowMatchError(
                expectedWindow,
                reply.Reply.BrowserWindowBounds!.Value))
            .Select(reply => (ActiveTabMatch?)reply)
            .FirstOrDefault();
    }

    private async Task<ActiveTabMatch?> QueryActiveTabAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (_browserBridgeServer is null)
        {
            return null;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ActiveTabReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingActiveTabQueries.TryAdd(requestId, completion))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        try
        {
            var request = JsonSerializer.Serialize(
                new ActiveTabQuery("queryActiveTab", requestId),
                BrowserJsonOptions);
            await _browserBridgeServer.SendAsync(
                connectionId,
                request,
                timeout.Token);
            var reply = await completion.Task.WaitAsync(timeout.Token);
            return new ActiveTabMatch(connectionId, reply);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or InvalidOperationException
                or IOException)
        {
            return null;
        }
        finally
        {
            _pendingActiveTabQueries.TryRemove(requestId, out _);
        }
    }

    private void OnBrowserBridgeMessageReceived(
        object? sender,
        BrowserBridgeMessageEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.Json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            if (HandleAutoDocumentReply(e, document.RootElement, type)) return;
            if (HandleBrowserQuickSettings(e, document.RootElement, type)) return;
            if (HandleManualImageCandidate(e, document.RootElement, type)) return;
            if (HandleBrowserImageMessage(e, document.RootElement, type)) return;
            if (HandleAutoImageCandidate(e, document.RootElement, type)) return;
            if (HandleAutoPageMessage(e, document.RootElement, type)) return;
            if (string.Equals(type, "bridgeReady", StringComparison.Ordinal))
            {
                var ready = JsonSerializer.Deserialize<BridgeReady>(
                    e.Json,
                    BrowserJsonOptions);
                if (ready?.BrowserKind is { } readyBrowser)
                {
                    _browserConnections[e.ConnectionId] = readyBrowser;
                    BrowserFollowDiagnostics.Write(
                        "bridge_connected",
                        ("browser", readyBrowser),
                        ("connection_id", e.ConnectionId));
                    UpdateBrowserConnectionStatus(readyBrowser);
                    _ = SendAutoImageConfigurationAsync(e.ConnectionId);
                    _ = SendAutoPageConfigurationAsync(e.ConnectionId);
                }

                return;
            }

            if (string.Equals(type, "activeTab", StringComparison.Ordinal))
            {
                var reply = JsonSerializer.Deserialize<ActiveTabReply>(
                    e.Json,
                    BrowserJsonOptions);
                if (reply?.RequestId is { Length: > 0 } requestId
                    && _pendingActiveTabQueries.TryGetValue(
                        requestId,
                        out var completion))
                {
                    if (reply.Found && !reply.CanCreateHello)
                    {
                        BrowserFollowDiagnostics.Write(
                            "active_tab_state_incomplete",
                            ("browser", reply.Browser),
                            ("browser_window_id", reply.BrowserWindowId),
                            ("tab_id", reply.TabId),
                            ("has_document_token",
                                !string.IsNullOrWhiteSpace(reply.DocumentToken)),
                            ("has_viewport", reply.ViewportSize is not null));
                    }

                    if (reply.BrowserKind is { } browser)
                    {
                        _browserConnections[e.ConnectionId] = browser;
                        UpdateBrowserConnectionStatus(browser);
                    }

                    completion.TrySetResult(reply);
                }

                return;
            }

            var message = BrowserProtocol.Deserialize(e.Json);
            if (message is BrowserInvalidated pageInvalidated
                && BrowserAutoImageLifetimePolicy.ShouldInvalidateAutomaticDocument(pageInvalidated))
            {
                CancelAutoPagesForDocument(e.ConnectionId, pageInvalidated);
                _autoImageQueue?.CancelDocument(e.ConnectionId, pageInvalidated.TabId, pageInvalidated.DocumentToken);
            }
            if (message is BrowserHello hello)
            {
                _browserConnections[e.ConnectionId] = hello.Browser;
                UpdateBrowserConnectionStatus(hello.Browser);
                _ = SendAutoImageConfigurationAsync(e.ConnectionId);
                _ = SendAutoPageConfigurationAsync(e.ConnectionId);
            }

            _application.Dispatcher.BeginInvoke(() =>
            {
                if (_activeBrowserConnectionId == e.ConnectionId)
                {
                    _browserFollowCoordinator?.Handle(message);
                    if (!_browserScrollLogged && message is BrowserScroll scroll)
                    {
                        _browserScrollLogged = true;
                        BrowserFollowDiagnostics.Write(
                            "first_scroll_received",
                            ("browser_window_id", scroll.BrowserWindowId),
                            ("tab_id", scroll.TabId),
                            ("delta_x", scroll.DeltaXCss),
                            ("delta_y", scroll.DeltaYCss));
                    }
                }
            });
        }
        catch (Exception exception) when (
            exception is JsonException
                or BrowserProtocolException
                or InvalidOperationException)
        {
            // Malformed or stale extension messages are ignored.
        }
    }

    private void OnBrowserBridgeConnectionClosed(
        object? sender,
        BrowserBridgeConnectionEventArgs e)
    {
        _autoImageQueue?.ConnectionClosed(e.ConnectionId);
        CancelAutoPagesForConnection(e.ConnectionId);
        if (_browserConnections.TryRemove(e.ConnectionId, out var browser))
        {
            BrowserFollowDiagnostics.Write(
                "bridge_disconnected",
                ("browser", browser),
                ("connection_id", e.ConnectionId));
            UpdateBrowserConnectionStatus(browser);
        }

        _application.Dispatcher.BeginInvoke(() =>
        {
            if (_activeBrowserConnectionId == e.ConnectionId)
            {
                _browserFollowCoordinator?.Invalidate("浏览器扩展连接已断开。");
            }
        });
    }

    private void UpdateBrowserConnectionStatus(BrowserKind browser)
    {
        _application.Dispatcher.BeginInvoke(() =>
            BrowserIntegration.UpdateConnection(
                browser,
                _browserConnections.Values.Any(value => value == browser)));
    }

    private void OnBrowserWindowChanged(
        object? sender,
        BrowserWindowChangedEventArgs e)
    {
        _application.Dispatcher.BeginInvoke(() =>
        {
            var coordinator = _browserFollowCoordinator;
            if (coordinator is null)
            {
                return;
            }

            if (e.Kind is BrowserWindowChangeKind.Minimized
                or BrowserWindowChangeKind.Destroyed
                || e.Snapshot is null
                || _browserWindowSnapshot is null)
            {
                coordinator.Invalidate("浏览器窗口已最小化或关闭。");
                return;
            }

            var previous = _browserWindowSnapshot;
            var current = e.Snapshot;
            if (previous.Dpi != current.Dpi
                || previous.Bounds.Width != current.Bounds.Width
                || previous.Bounds.Height != current.Bounds.Height)
            {
                coordinator.Invalidate("浏览器缩放、DPI 或窗口大小已经变化。");
                return;
            }

            _browserWindowSnapshot = current;
            var scale = previous.Dpi / 96d;
            coordinator.OffsetWithBrowserWindow(
                (current.Bounds.X - previous.Bounds.X) / scale,
                (current.Bounds.Y - previous.Bounds.Y) / scale);
        });
    }

    private void OnBrowserFollowInvalidated(
        object? sender,
        BrowserFollowInvalidatedEventArgs e)
    {
        if (!ReferenceEquals(sender, _browserFollowCoordinator))
        {
            return;
        }

        _browserWindowMonitor?.Dispose();
        _browserWindowMonitor = null;
        _browserWindowSnapshot = null;
        _browserFollowCoordinator = null;
        _activeBrowserConnectionId = null;
        _browserScrollLogged = false;
        BrowserIntegration.DetailText =
            $"网页状态已变化，旧译文已隐藏：{e.Reason}";
    }

    private void StopBrowserFollowing(bool hideOverlays)
    {
        Interlocked.Increment(ref _browserFollowGeneration);
        foreach (var pending in _pendingActiveTabQueries.Values)
        {
            pending.TrySetCanceled();
        }

        var coordinator = _browserFollowCoordinator;
        _browserFollowCoordinator = null;
        _activeBrowserConnectionId = null;
        if (coordinator is not null)
        {
            coordinator.Invalidated -= OnBrowserFollowInvalidated;
            if (hideOverlays && !coordinator.IsInvalidated)
            {
                coordinator.Invalidate("网页跟随已停止。");
            }
        }

        if (_browserWindowMonitor is not null)
        {
            _browserWindowMonitor.Changed -= OnBrowserWindowChanged;
            _browserWindowMonitor.Dispose();
            _browserWindowMonitor = null;
        }

        _browserWindowSnapshot = null;
    }

    private void RemoveBrowserTrackedOverlay(TextOverlayWindow overlay)
    {
        var coordinator = _browserFollowCoordinator;
        if (coordinator is null || !coordinator.RemoveOverlay(overlay))
        {
            return;
        }

        if (coordinator.OverlayCount == 0)
        {
            StopBrowserFollowing(hideOverlays: false);
        }
    }

    private void OnOpenBrowserExtensionsRequested(
        object? sender,
        BrowserKind browser)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = browser == BrowserKind.Chrome
                    ? "chrome.exe"
                    : "msedge.exe",
                Arguments = browser == BrowserKind.Chrome
                    ? "chrome://extensions"
                    : "edge://extensions",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            BrowserIntegration.DetailText =
                $"无法打开浏览器扩展页：{exception.Message}";
        }
    }

    private void OnOpenExtensionFolderRequested(object? sender, EventArgs e)
    {
        var folder = FindExtensionFolder();
        if (folder is null)
        {
            BrowserIntegration.DetailText =
                "未找到 browser-extension 文件夹，请重新解压完整发布包。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            BrowserIntegration.DetailText =
                $"无法打开扩展文件夹：{exception.Message}";
        }
    }

    private void OnInstallExtensionRequested(
        object? sender,
        BrowserKind browser)
    {
        var installation = _browserInstallations.GetValueOrDefault(browser);
        if (installation is null || !installation.IsInstalled)
        {
            BrowserIntegration.DetailText =
                $"{BrowserLabel(browser)} 未安装，无法加载扩展。";
            return;
        }

        var folder = FindExtensionFolder();
        if (folder is null)
        {
            BrowserIntegration.SetBridgeError(
                "未找到浏览器扩展文件，请重新安装完整版本。");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(folder);
            OnOpenBrowserExtensionsRequested(this, browser);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
            BrowserIntegration.SetWaitingForConnection(browser);
            BrowserIntegration.DetailText =
                $"扩展目录已复制并打开。请在 {BrowserLabel(browser)} 中开启开发者模式，选择“加载已解压的扩展”，然后选中该目录。";
        }
        catch (Exception exception)
        {
            BrowserIntegration.SetBridgeError(
                $"无法启动扩展安装引导：{exception.Message}");
        }
    }

    private async Task OnRepairBrowserBridgeRequested(
        object? sender,
        EventArgs e)
    {
        if (_nativeMessagingRegistration is null)
        {
            BrowserIntegration.SetBridgeError(
                "浏览器连接服务尚未初始化。");
            return;
        }

        try
        {
            await _nativeMessagingRegistration.RepairAsync();
            foreach (var installation in _browserInstallations.Values)
            {
                BrowserIntegration.UpdateConnection(
                    installation.Browser,
                    _browserConnections.Values.Any(
                        value => value == installation.Browser));
            }

            BrowserIntegration.DetailText =
                "本机连接已修复。已加载的扩展会自动重连。";
        }
        catch (Exception exception)
        {
            BrowserIntegration.SetBridgeError(
                $"修复连接失败：{exception.Message}");
        }
    }

    private void DisposeBrowserIntegration()
    {
        StopBrowserFollowing(hideOverlays: false);
        BrowserIntegration.OpenBrowserExtensionsRequested -=
            OnOpenBrowserExtensionsRequested;
        BrowserIntegration.OpenExtensionFolderRequested -=
            OnOpenExtensionFolderRequested;
        BrowserIntegration.InstallExtensionRequested -=
            OnInstallExtensionRequested;
        BrowserIntegration.RepairBridgeRequested -=
            OnRepairBrowserBridgeRequested;

        if (_browserBridgeServer is null)
        {
            return;
        }

        var server = _browserBridgeServer;
        server.MessageReceived -= OnBrowserBridgeMessageReceived;
        server.ConnectionClosed -= OnBrowserBridgeConnectionClosed;
        _browserBridgeServer = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The operating system will release remaining pipe handles on exit.
            }
        });
    }

    private static DipRect CalculateViewportBounds(
        BrowserWindowSnapshot snapshot,
        BrowserHello hello,
        double monitorScale)
    {
        var windowBounds = ToDip(snapshot.Bounds, snapshot.Dpi / 96d);
        var viewportWidth =
            hello.ViewportSize.Width * hello.DevicePixelRatio / monitorScale;
        var viewportHeight =
            hello.ViewportSize.Height * hello.DevicePixelRatio / monitorScale;
        return new DipRect(
            windowBounds.X + Math.Max(0, (windowBounds.Width - viewportWidth) / 2),
            windowBounds.Y + Math.Max(0, windowBounds.Height - viewportHeight),
            Math.Min(viewportWidth, windowBounds.Width),
            Math.Min(viewportHeight, windowBounds.Height));
    }

    private static bool WindowBoundsMatch(
        BrowserWindowSnapshot expected,
        CssRect actual) =>
        WindowMatchError(expected, actual) <= 260;

    private static double WindowMatchError(
        BrowserWindowSnapshot expected,
        CssRect actual) => BrowserViewportResolver.WindowMatchError(expected, actual);

    private static DipRect ToDip(PixelRect bounds, double scale) =>
        new(
            bounds.X / scale,
            bounds.Y / scale,
            bounds.Width / scale,
            bounds.Height / scale);

    private static string? FindExtensionFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "browser-extension"),
            Path.Combine(Environment.CurrentDirectory, "browser-extension"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "browser-extension")),
        };
        return candidates.FirstOrDefault(
            candidate => File.Exists(Path.Combine(candidate, "manifest.json")));
    }

    private static string BrowserLabel(BrowserKind browser) =>
        browser == BrowserKind.Chrome ? "Chrome" : "Edge";

    private static JsonSerializerOptions CreateBrowserJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record ActiveTabQuery(string Type, string RequestId);

    private sealed record BridgeReady(string Type, string? Browser)
    {
        public BrowserKind? BrowserKind =>
            ParseBrowserKind(Browser);
    }

    private sealed record ActiveTabReply(
        string Type,
        string? RequestId,
        bool Found,
        string? Browser,
        int BrowserWindowId,
        CssRect? BrowserWindowBounds,
        int TabId,
        int FrameId,
        string? DocumentToken,
        long NavigationGeneration,
        double DevicePixelRatio,
        CssSize? ViewportSize)
    {
        public BrowserKind? BrowserKind =>
            ParseBrowserKind(Browser);

        public bool CanCreateHello =>
            Found
            && BrowserKind is not null
            && BrowserWindowBounds is not null
            && FrameId == 0
            && !string.IsNullOrWhiteSpace(DocumentToken)
            && ViewportSize is
            {
                Width: > 0,
                Height: > 0,
            };

        public BrowserHello ToHello()
        {
            if (!CanCreateHello
                || BrowserKind is not { } browser
                || BrowserWindowBounds is not { } bounds
                || ViewportSize is not { } viewport)
            {
                throw new BrowserProtocolException(
                    "浏览器活动标签页响应不完整。");
            }

            var hello = new BrowserHello(
                browser,
                BrowserWindowId,
                TabId,
                DocumentToken!,
                NavigationGeneration,
                DevicePixelRatio,
                viewport,
                bounds,
                FrameId);
            BrowserProtocol.Validate(hello);
            return hello;
        }
    }

    private readonly record struct ActiveTabMatch(
        Guid ConnectionId,
        ActiveTabReply Reply);

    private readonly record struct BrowserFollowCandidate(
        ActiveTabMatch Match,
        BrowserWindowSnapshot Snapshot);

    private static BrowserKind? ParseBrowserKind(string? browser) =>
        browser?.ToLowerInvariant() switch
        {
            "chrome" => BrowserKind.Chrome,
            "edge" => BrowserKind.Edge,
            _ => null,
        };
}
