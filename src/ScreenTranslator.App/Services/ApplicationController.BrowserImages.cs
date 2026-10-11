using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private readonly ConcurrentDictionary<string, PendingImageQuery> _pendingImageQueries = new();

    private async Task<OriginalBrowserImage?> ReadBrowserImageAsync(PixelPoint point, nint handle, CancellationToken token)
    {
        OriginalBrowserImage? Fallback(string reason)
        { AppDiagnostics.Write($"browser_image_fallback reason={reason}"); return null; }
        if (_browserBridgeServer is null || _browserConnections.IsEmpty) return Fallback("extension_disconnected");
        var browser = BrowserWindowEventMonitor.CaptureBrowserWindow(handle);
        if (browser is null) return null;
        var match = await QueryMatchingActiveTabAsync(browser.Browser, browser.Snapshot, token);
        if (match is null) return Fallback("active_tab_not_matched");
        var viewport = await BrowserViewportResolver.ResolveAsync(browser.Snapshot, point,
            match.Value.Reply.ViewportSize!.Value, match.Value.Reply.DevicePixelRatio, token);
        if (viewport is null) return Fallback("viewport_not_resolved");
        var reply = await QueryBrowserImageAsync(match.Value.ConnectionId, new
        {
            type = "queryImage", tabId = match.Value.Reply.TabId, browserWindowId = match.Value.Reply.BrowserWindowId,
            x = point.X - viewport.Value.X, y = point.Y - viewport.Value.Y,
        }, true, token);
        if (reply is null || !reply.Value.Reply.Found || reply.Value.Bytes is null) return Fallback("image_query_unavailable");
        try
        {
            var bitmap = await Task.Run(() => CapturedBitmapEncoder.DecodeOriginal(reply.Value.Bytes), token);
            var mapping = reply.Value.Reply.CreateMapping(bitmap.Size, viewport.Value);
            var now = AccessibleTextResolver.GetWindowBounds(handle);
            if (mapping is null || now != browser.Snapshot.Bounds || mapping.ToImage(point) is null) return Fallback("image_geometry_not_matched");
            var binding = new OriginalBrowserImage(bitmap, mapping, browser, match.Value.ConnectionId,
                match.Value.Reply.TabId, match.Value.Reply.BrowserWindowId, reply.Value.Reply.ImageId!, reply.Value.Reply.DocumentToken!,
                point, viewport.Value);
            AppDiagnostics.Write($"browser_original_image width={bitmap.Width} height={bitmap.Height}");
            return binding;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or NotSupportedException or ArgumentException)
        { AppDiagnostics.Write($"browser_image_fallback type={exception.GetType().Name}"); return null; }
    }

    private async Task<SourceImageMapping?> RefreshBrowserImageAsync(OriginalBrowserImage image)
    {
        if (_disposed || _browserBridgeServer is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var current = AccessibleTextResolver.GetWindowBounds(image.Browser.Snapshot.Handle);
            if (current is null || current.Value.Width != image.Browser.Snapshot.Bounds.Width || current.Value.Height != image.Browser.Snapshot.Bounds.Height) return null;
            var reply = await QueryBrowserImageAsync(image.ConnectionId, new
            {
                type = "queryImageGeometry", tabId = image.TabId, browserWindowId = image.WindowId,
                imageId = image.ImageId, documentToken = image.DocumentToken,
            }, false, timeout.Token);
            if (reply is null || reply.Value.Reply.ImageId != image.ImageId || reply.Value.Reply.DocumentToken != image.DocumentToken) return null;
            // Mapping stays relative to the capture's window origin; the highlight handles window movement separately.
            return reply.Value.Reply.CreateMapping(image.Bitmap.Size, image.Viewport);
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task<(BrowserImageReply Reply, byte[]? Bytes)?> QueryBrowserImageAsync(Guid connectionId, object request, bool withImage, CancellationToken token)
    {
        if (_browserBridgeServer is null) return null;
        var id = Guid.NewGuid().ToString("N");
        var pending = new PendingImageQuery(connectionId);
        _pendingImageQueries[id] = pending;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(withImage ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(800));
        try
        {
            var properties = JsonSerializer.SerializeToElement(request, BrowserJsonOptions).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value);
            properties["requestId"] = id;
            await _browserBridgeServer.SendAsync(connectionId, JsonSerializer.Serialize(properties), timeout.Token);
            var result = await pending.Completion.Task.WaitAsync(timeout.Token);
            return (result, withImage && result.Found ? pending.Transfer.Complete(result.Base64Length) : null);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException or FormatException)
        { if (token.IsCancellationRequested) token.ThrowIfCancellationRequested(); return null; }
        finally { _pendingImageQueries.TryRemove(id, out _); }
    }

    private bool HandleBrowserImageMessage(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type is not ("browserImageChunk" or "browserImageReply")) return false;
        if (!root.TryGetProperty("requestId", out var property) || property.GetString() is not { } id
            || !_pendingImageQueries.TryGetValue(id, out var pending) || pending.ConnectionId != args.ConnectionId) return true;
        try
        {
            if (type == "browserImageChunk") pending.Transfer.Append(root.GetProperty("index").GetInt32(), root.GetProperty("data").GetString()!);
            else pending.Completion.TrySetResult(JsonSerializer.Deserialize<BrowserImageReply>(args.Json, BrowserJsonOptions)!);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException or ArgumentException)
        { pending.Completion.TrySetException(exception); }
        return true;
    }

    private sealed record OriginalBrowserImage(CapturedBitmap Bitmap, SourceImageMapping Mapping, CapturedBrowserWindow Browser,
        Guid ConnectionId, int TabId, int WindowId, string ImageId, string DocumentToken, PixelPoint Point, PixelRect Viewport);
    private sealed class PendingImageQuery(Guid connectionId)
    {
        public Guid ConnectionId { get; } = connectionId;
        public BrowserImageTransfer Transfer { get; } = new();
        public TaskCompletionSource<BrowserImageReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
