using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Channels;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.ViewModels;

namespace ScreenTranslator.App.Services.Overlays;

public sealed class BrowserInPlaceController : IDisposable
{
    private readonly TranslationResultViewModel _model;private readonly BrowserOverlayTransport _transport;private readonly InPlaceRenderSession _render;
    private readonly CancellationTokenSource _stop=new();
    private readonly Channel<IReadOnlyList<RenderedImageTile>> _outgoing=Channel.CreateBounded<IReadOnlyList<RenderedImageTile>>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private IReadOnlyList<RenderedImageTile> _latest=[];private bool _enabled;
    public BrowserInPlaceController(TranslationResultViewModel model,System.Windows.Threading.Dispatcher dispatcher)
    {
        _model=model;_dispatcher=dispatcher;_transport=model.BrowserOverlay!;_enabled=model.IsInPlaceEnabled;
        _render=new(model.SourceBitmap!,model.SourceInkMask,model.CachedTiles);
        _render.TilesChanged+=tiles=>{_latest=tiles;_outgoing.Writer.TryWrite(tiles);if(!_dispatcher.HasShutdownStarted)_dispatcher.BeginInvoke(()=>{if(_stop.IsCancellationRequested)return;_model.InPlaceRenderedCount=tiles.Count;_model.TilesRendered?.Invoke(tiles);});};
        _render.Failed+=error=>AppDiagnostics.Write($"in_place_worker_failed type={error.GetType().Name}");
        model.Segments.CollectionChanged+=SegmentsChanged;model.PropertyChanged+=Changed;
        _=Task.Run(SendAsync);Update();
    }
    private void SegmentsChanged(object? sender,NotifyCollectionChangedEventArgs args)=>Update();
    private void Update()=>_render.Update(_model.Segments.Select(s=>s.ToBlock()).ToArray(),!_model.IsBusy);
    private void Changed(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName==nameof(TranslationResultViewModel.IsBusy))Update();
        if(args.PropertyName is nameof(TranslationResultViewModel.IsInPlaceEnabled) or nameof(TranslationResultViewModel.ErrorMessage))
        {_enabled=_model.IsInPlaceEnabled&&_model.ErrorMessage is null;_outgoing.Writer.TryWrite(_latest);}
    }
    private async Task SendAsync()
    {
        try{
            var cached = _model.CachedTiles is { Count: > 0 };
            await _transport.PublishAsync([],Volatile.Read(ref _enabled),_stop.Token, cached ? "cached" : "translating").ConfigureAwait(false);
            await foreach(var tiles in _outgoing.Reader.ReadAllAsync(_stop.Token))
                await _transport.PublishAsync(tiles,Volatile.Read(ref _enabled),_stop.Token,
                    cached ? "cached" : _model.IsBusy ? "typesetting" : "complete").ConfigureAwait(false);}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception error){AppDiagnostics.Write($"browser_in_place_failed type={error.GetType().Name}");if(!_dispatcher.HasShutdownStarted)_ = _dispatcher.BeginInvoke(()=>{if(!_stop.IsCancellationRequested){_model.IsExpanded=true;_model.StatusText="页面覆盖层不可用 · 可查看译文";}});}
    }
    public void Dispose(){if(_stop.IsCancellationRequested)return;_model.Segments.CollectionChanged-=SegmentsChanged;_model.PropertyChanged-=Changed;_stop.Cancel();_render.Dispose();_outgoing.Writer.TryComplete();_= _transport.DisposeAsync(!_model.IsBusy&&_model.ErrorMessage is null&&_model.IsInPlaceEnabled);}
}
