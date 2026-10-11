using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;

namespace ScreenTranslator.App.Services;

public sealed partial class ApplicationController
{
    private readonly ConcurrentDictionary<string, PendingDocumentQuery> _pendingDocumentQueries = new();

    /// <summary>Check the nominated tab/document independently of current browser focus.</summary>
    private async Task<BrowserDocumentReply?> QueryAutoDocumentAsync(Guid connection, int tabId, int windowId,
        string documentToken, CancellationToken token)
    {
        if (_browserBridgeServer is null) return null;
        var id = Guid.NewGuid().ToString("N");
        var pending = new PendingDocumentQuery(connection);
        if (!_pendingDocumentQueries.TryAdd(id, pending)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _browserBridgeServer.SendAsync(connection, JsonSerializer.Serialize(new
            { type = "queryBrowserDocument", requestId = id, tabId, browserWindowId = windowId, documentToken }), timeout.Token).ConfigureAwait(false);
            var reply = await pending.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return reply.Found && reply.TabId == tabId && reply.BrowserWindowId == windowId
                && reply.DocumentToken == documentToken && reply.Browser == "chrome" ? reply : null;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { return null; }
        finally { _pendingDocumentQueries.TryRemove(id, out _); }
    }

    private bool HandleAutoDocumentReply(BrowserBridgeMessageEventArgs args, JsonElement root, string? type)
    {
        if (type != "browserDocumentReply") return false;
        var reply = root.Deserialize<BrowserDocumentReply>(BrowserJsonOptions);
        if (reply?.RequestId is { Length: > 0 and <= 64 } id && _pendingDocumentQueries.TryGetValue(id, out var pending)
            && pending.ConnectionId == args.ConnectionId && reply.DocumentToken?.Length is not > 256 && reply.SiteHost?.Length is not > 253)
            pending.Completion.TrySetResult(reply);
        return true;
    }

    private sealed record BrowserDocumentReply(string? RequestId, bool Found, int TabId, int BrowserWindowId,
        string? DocumentToken, string? Browser, bool Active, string? SiteHost = null);
    private sealed class PendingDocumentQuery(Guid connection)
    {
        public Guid ConnectionId { get; } = connection;
        public TaskCompletionSource<BrowserDocumentReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
