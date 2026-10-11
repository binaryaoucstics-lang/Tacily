using System.Threading.Channels;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Overlays;

public sealed record RenderedImageTile(string Id,PixelRect Bounds,byte[] Png,CapturedBitmap Bitmap,bool Optimized)
{public string Translation{get;init;}="";}

/// <summary>Latest stream snapshot wins. Repair, encoding and typography always run off the dispatcher.</summary>
public sealed class InPlaceRenderSession : IDisposable
{
    private readonly CapturedBitmap _source;
    private readonly InPlaceImageRenderer _renderer;
    private readonly Channel<Snapshot> _queue=Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private readonly CancellationTokenSource _stop=new();
    private readonly Dictionary<string,Entry> _entries=[];
    private readonly Task _worker;
    private long _version;
    private int _disposed;
    private TaskCompletionSource<IReadOnlyList<RenderedImageTile>> _completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<IReadOnlyList<RenderedImageTile>>? TilesChanged;
    public event Action<Exception>? Failed;
    public InPlaceRenderSession(CapturedBitmap source,CapturedBitmap? mask=null,IReadOnlyList<RenderedImageTile>? cached=null)
    {_source=source;_renderer=new(source,mask);Cached=cached;_worker=Task.Run(WorkAsync);}
    public IReadOnlyList<RenderedImageTile>? Cached {get;}
    public void Update(IReadOnlyList<TranslatedBlock> blocks,bool final=false)
    {
        if(Volatile.Read(ref _disposed)!=0)return;
        if(Cached is {} cached){TilesChanged?.Invoke(cached);_completion.TrySetResult(cached);return;}
        var version=Interlocked.Increment(ref _version);_queue.Writer.TryWrite(new(version,blocks.ToArray(),final));
    }
    public Task<IReadOnlyList<RenderedImageTile>> FinishAsync(CancellationToken token=default)=>_completion.Task.WaitAsync(token);
    private async Task WorkAsync()
    {
        try
        {
            await foreach(var snapshot in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                if(snapshot.Version!=Volatile.Read(ref _version))continue;
                var visible=snapshot.Blocks.Where(b=>b.Bounds.IsUsable&&!string.IsNullOrWhiteSpace(b.Translation)).ToArray();
                var withdrawn=false;
                foreach(var id in _entries.Keys.Except(visible.Select(b=>b.Id)).ToArray())withdrawn|=_entries.Remove(id);
                if(withdrawn)Publish(snapshot.Version);
                foreach(var block in visible)
                {
                    if(snapshot.Version!=Volatile.Read(ref _version)||_stop.IsCancellationRequested)break;
                    var key=$"{block.Bounds}:{block.LayoutBounds}:{block.IsVertical}:{block.GlyphSizeHint}:{block.VisionInkBounds}:{block.PreciseWords}:{block.TypographyHint}:"
                        +string.Join(';',block.TextBounds)+":"+string.Join(',',block.SourceIds);
                    if(!_entries.TryGetValue(block.Id,out var entry)||entry.Key!=key)
                    {var patch=_renderer.Prepare(block);if(patch is null){if(_entries.Remove(block.Id))Publish(snapshot.Version);continue;}_entries[block.Id]=entry=new(key,patch);}
                    if(entry.Text==block.Translation&&entry.Complete==block.IsComplete)continue;
                    entry.Text=block.Translation;entry.Complete=block.IsComplete;
                    entry.Tile=Render(block,entry.Patch,false);
                    Publish(snapshot.Version);
                }
                if(snapshot.Version!=Volatile.Read(ref _version)||_stop.IsCancellationRequested)continue;
                // First visible translation has already been published. Styling is optional refinement.
                foreach(var block in snapshot.Blocks.Where(b=>b.IsComplete))
                {
                    if(snapshot.Version!=Volatile.Read(ref _version)||_stop.IsCancellationRequested)break;
                    if(!_entries.TryGetValue(block.Id,out var entry)||entry.Tile is null||entry.Tile.Optimized)continue;
                    entry.Patch=entry.Patch with{Style=ComicStyleAnalyzer.Analyze(_source,entry.Patch,block)};
                    entry.Tile=Render(block,entry.Patch,true);Publish(snapshot.Version);
                }
                if(snapshot.Version==Volatile.Read(ref _version)&&snapshot.Final)
                    _completion.TrySetResult(_entries.Values.Where(e=>e.Tile!=null).Select(e=>e.Tile!).ToArray());
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception error){Failed?.Invoke(error);_completion.TrySetException(error);}
        finally{_completion.TrySetCanceled();}
    }
    private RenderedImageTile Render(TranslatedBlock block,InPlacePatch patch,bool optimized)
    {var bitmap=_renderer.Render(patch,block);return new(block.Id,patch.Bounds,CapturedBitmapEncoder.ToPng(bitmap,patch.Transparent),bitmap,optimized){Translation=block.Translation};}
    private void Publish(long version){if(version==Volatile.Read(ref _version)&&!_stop.IsCancellationRequested)TilesChanged?.Invoke(_entries.Values.Where(e=>e.Tile!=null).Select(e=>e.Tile!).ToArray());}
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;_stop.Cancel();_queue.Writer.TryComplete();_ = _worker.ContinueWith(_=>_stop.Dispose(),TaskScheduler.Default);}
    private sealed record Snapshot(long Version,IReadOnlyList<TranslatedBlock> Blocks,bool Final);
    private sealed class Entry(string key,InPlacePatch patch){public string Key{get;}=key;public InPlacePatch Patch{get;set;}=patch;public string Text{get;set;}="";public bool Complete{get;set;}public RenderedImageTile? Tile{get;set;}}
}
