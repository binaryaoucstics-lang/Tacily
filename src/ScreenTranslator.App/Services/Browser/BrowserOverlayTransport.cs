using System.Collections.Concurrent;
using System.Text.Json;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserImageIdentity(Guid ConnectionId,int TabId,string ImageId,string DocumentToken,PixelSize Size);
public sealed class BrowserOverlayTransport : IAsyncDisposable
{
    private readonly BrowserBridgeServer _server;private readonly BrowserImageIdentity _image;
    private readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> _pending=new();
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly Dictionary<string,byte[]> _sent=[];
    private readonly string _session=Guid.NewGuid().ToString("N");
    private readonly long _generation=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool _begun,_beginAttempted;private long _revision;
    private readonly CancellationTokenSource _stop=new();
    private readonly CancellationTokenSource _dismissed=new();
    private readonly TaskCompletionSource<bool> _closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeStarted;
    public BrowserOverlayTransport(BrowserBridgeServer server,BrowserImageIdentity image)
    {_server=server;_image=image;server.MessageReceived+=Message;}
    public CancellationToken DismissalToken=>_dismissed.Token;
    private void Message(object? sender,BrowserBridgeMessageEventArgs args)
    {
        if(args.ConnectionId!=_image.ConnectionId)return;
        try{using var doc=JsonDocument.Parse(args.Json);var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return;
            if(!root.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String)return;
            if(type.GetString()=="imageOverlayDismissed"&&MatchesIdentity(root))_dismissed.Cancel();
            else if(type.GetString()=="imageOverlayAck"
                && root.TryGetProperty("requestId",out var id)&&id.ValueKind==JsonValueKind.String
                && root.TryGetProperty("found",out var found)&&found.ValueKind is JsonValueKind.True or JsonValueKind.False
                && _pending.TryGetValue(id.GetString()??"",out var promise))
                promise.TrySetResult(found.GetBoolean());}
        catch(JsonException){}
    }
    private bool MatchesIdentity(JsonElement root)
    {
        static bool Matches(JsonElement item,string key,string expected)=>item.TryGetProperty(key,out var value)
            &&value.ValueKind==JsonValueKind.String&&value.GetString()==expected;
        return root.TryGetProperty("tabId",out var tab)&&tab.ValueKind==JsonValueKind.Number&&tab.TryGetInt32(out var tabId)&&tabId==_image.TabId
            &&Matches(root,"imageId",_image.ImageId)&&Matches(root,"documentToken",_image.DocumentToken)&&Matches(root,"sessionId",_session);
    }
    public async Task PublishAsync(IReadOnlyList<RenderedImageTile> tiles,bool enabled,CancellationToken token,string? phase=null)
    {
        using var active=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        token=active.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted)!=0,this);
            if(!_begun){_beginAttempted=true;await SendAsync(new{type="imageOverlayBegin",enabled,phase,generation=_generation,sourceSize=new{width=_image.Size.Width,height=_image.Size.Height}},token);_begun=true;}
            foreach(var tile in tiles)
            {
                if(_sent.TryGetValue(tile.Id,out var old)&&ReferenceEquals(old,tile.Png))continue;
                var encoded=Convert.ToBase64String(tile.Png);var revision=++_revision;
                for(var offset=0;offset<encoded.Length;offset+=262144)
                    await SendAsync(new{type="imageOverlayChunk",id=tile.Id,revision,index=offset/262144,data=encoded.Substring(offset,Math.Min(262144,encoded.Length-offset))},token);
                await SendAsync(new{type="imageOverlayCommit",id=tile.Id,revision,length=encoded.Length,
                    bounds=new{x=tile.Bounds.X,y=tile.Bounds.Y,width=tile.Bounds.Width,height=tile.Bounds.Height}},token);
                _sent[tile.Id]=tile.Png;
            }
            foreach(var id in _sent.Keys.Except(tiles.Select(t=>t.Id)).ToArray())_sent.Remove(id);
            await SendAsync(new{type="imageOverlayState",enabled,phase,ids=tiles.Select(t=>t.Id).ToArray()},token);
        } finally{_gate.Release();}
    }
    public async Task SetEnabledAsync(bool enabled,CancellationToken token=default)
    {
        if(Volatile.Read(ref _disposeStarted)!=0)return;
        using var active=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        await _gate.WaitAsync(active.Token).ConfigureAwait(false);
        try{if(_beginAttempted&&Volatile.Read(ref _disposeStarted)==0)await SendAsync(new{type="imageOverlayState",enabled},active.Token).ConfigureAwait(false);}
        finally{_gate.Release();}
    }
    private async Task SendAsync(object body,CancellationToken token)
    {
        var id=Guid.NewGuid().ToString("N");var promise=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);_pending[id]=promise;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var properties=JsonSerializer.SerializeToElement(body).EnumerateObject().ToDictionary(p=>p.Name,p=>(object)p.Value);
            properties["requestId"]=id;properties["tabId"]=_image.TabId;properties["imageId"]=_image.ImageId;properties["documentToken"]=_image.DocumentToken;properties["sessionId"]=_session;
            await _server.SendAsync(_image.ConnectionId,JsonSerializer.Serialize(properties),deadline.Token).ConfigureAwait(false);
            if(!await promise.Task.WaitAsync(deadline.Token).ConfigureAwait(false))throw new InvalidOperationException("浏览器图片已改变或覆盖层不可用。");
        }finally{_pending.TryRemove(id,out _);}
    }
    public async ValueTask DisposeAsync()
        =>await DisposeAsync(false);
    public async ValueTask DisposeAsync(bool retainCompleted)
    {
        if(Interlocked.Exchange(ref _disposeStarted,1)!=0){await _closed.Task.ConfigureAwait(false);return;}
        _stop.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Chrome can create the layer before its begin ACK arrives. Cleanup
            // is a separate command after all older writes have finished.
            if(!retainCompleted&&_beginAttempted)
                await SendAsync(new{type="imageOverlayState",enabled=false},CancellationToken.None).ConfigureAwait(false);
        }
        catch(Exception error){ScreenTranslator.App.Infrastructure.AppDiagnostics.Write($"browser_overlay_cleanup_failed type={error.GetType().Name}");}
        finally
        {
            _server.MessageReceived-=Message;foreach(var pending in _pending.Values)pending.TrySetCanceled();
            _gate.Release();_closed.TrySetResult(true);
            // Public calls may still be leaving their cancelled wait. The
            // managed semaphore/CTS are reclaimed after the transport is gone.
        }
    }
}
