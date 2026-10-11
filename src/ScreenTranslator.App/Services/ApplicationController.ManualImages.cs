using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private readonly ConcurrentDictionary<(Guid Connection, string Request), byte> _manualImageRequests = new();
    private bool HandleManualImageCandidate(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type != "manualImageCandidate") return false;
        if (!_disposed && _browserConnections.TryGetValue(args.ConnectionId, out var kind) && kind == BrowserKind.Chrome
            && BrowserManualImageRequest.TryRead(root, out var request) && _manualImageRequests.Count < 4
            && _manualImageRequests.TryAdd((args.ConnectionId, request!.RequestId), 0))
            _ = TranslateManualBrowserImageAsync(args.ConnectionId, request);
        return true;
    }
    private async Task TranslateManualBrowserImageAsync(Guid connection, BrowserManualImageRequest request)
    {
        var acquired = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = cancellation.Token;
        try
        {
            acquired = await _captureGate.WaitAsync(0, token).ConfigureAwait(false);
            if (!acquired) { await ReplyManualImageAsync(connection, request, "failed", "desktop_busy"); return; }
            var settings = Volatile.Read(ref _autoImageConfiguration).Settings;
            var document = await QueryAutoDocumentAsync(connection, request.TabId, request.WindowId, request.DocumentToken, token).ConfigureAwait(false);
            if (document is not { Active: true } || !ManualImageGenerationCurrent(connection, request)) throw new OperationCanceledException(token);
            _autoImageQueue?.SetManualBusy(true);
            var source = await QueryBrowserImageAsync(connection, new { type = "queryImage", tabId = request.TabId,
                browserWindowId = request.WindowId, documentToken = request.DocumentToken, imageId = request.ImageId }, true, token).ConfigureAwait(false);
            if (source is not { Bytes: not null } original || !request.Matches(original.Reply) || !ManualImageGenerationCurrent(connection, request))
            { await ReplyManualImageAsync(connection, request, "failed", "image_unavailable"); return; }
            var image = await Task.Run(() => CapturedBitmapEncoder.DecodeOriginal(original.Bytes), token).ConfigureAwait(false);
            await using var transport = new BrowserOverlayTransport(_browserBridgeServer!, new(connection, request.TabId,
                request.ImageId, request.DocumentToken, image.Size));
            await ReplyManualImageAsync(connection, request, "translating", "");
            await transport.PublishAsync([], true, token, phase: "preparing").ConfigureAwait(false);
            await BrowserAutoImageLifetimePolicy.RunAsync(async current =>
            {
                if (!ManualImageGenerationCurrent(connection, request)) return false;
                var page = await QueryAutoDocumentAsync(connection, request.TabId, request.WindowId, request.DocumentToken, current).ConfigureAwait(false);
                if (page is null) return false;
                var geometry = await QueryBrowserImageAsync(connection, new { type = "queryImageGeometry", tabId = request.TabId,
                    browserWindowId = request.WindowId, documentToken = request.DocumentToken, imageId = request.ImageId,
                    allowInactive = true }, false, current).ConfigureAwait(false);
                return request.Matches(geometry?.Reply) && geometry?.Reply.Natural == original.Reply.Natural;
            }, current => _autoImageTranslator!.TranslateAsync(image, settings, transport, current, manual: true), token).ConfigureAwait(false);
            await ReplyManualImageAsync(connection, request, "completed", "");
        }
        catch (OperationCanceledException) { await ReplyManualImageAsync(connection, request, "cancelled", "image_changed"); }
        catch (Exception error) { AppDiagnostics.Write($"browser_manual_image_failed type={error.GetType().Name}"); await ReplyManualImageAsync(connection, request, "failed", "translation_failed"); }
        finally
        {
            if (acquired) { _autoImageQueue?.SetManualBusy(false); _captureGate.Release(); }
            _manualImageRequests.TryRemove((connection, request.RequestId), out _);
        }
    }
    private bool ManualImageGenerationCurrent(Guid connection, BrowserManualImageRequest request) => !_disposed
        && request.Generation == Volatile.Read(ref _autoImageConfiguration).Generation
        && _browserConnections.TryGetValue(connection, out var kind) && kind == BrowserKind.Chrome;
    private async Task ReplyManualImageAsync(Guid connection, BrowserManualImageRequest request, string state, string reason)
    {
        try
        {
            if (_disposed || _browserBridgeServer is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _browserBridgeServer.SendAsync(connection, JsonSerializer.Serialize(new { type = "manualImageStatus", requestId = request.RequestId,
                imageId = request.ImageId, documentToken = request.DocumentToken, tabId = request.TabId,
                generation = request.Generation, state, reason }), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
    }
}
