using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.App.Infrastructure;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;

namespace ScreenTranslator.App.Windows;

/// <summary>Click-through local repair patches and translated glyphs, bound to the captured source window.</summary>
public sealed class InPlaceTranslationWindow : Window
{
    private readonly TranslationResultViewModel _model;
    private readonly InPlaceImageRenderer _renderer;
    private readonly InPlaceRenderSession _renderSession;
    private readonly Func<bool> _controlVisible;
    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly SourceContentInteractionMonitor _interactions;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _dirty = true, _closed, _refreshing, _invalidSnapshot;
    private DateTime _changedAt, _refreshedAt;

    public InPlaceTranslationWindow(TranslationResultViewModel model, Func<bool> controlVisible)
    {
        _model = model; _renderer = new(model.SourceBitmap!); _controlVisible = controlVisible;
        _renderSession=new(model.SourceBitmap!,model.SourceInkMask,model.CachedTiles);
        _renderSession.TilesChanged+=tiles=>
        {
            var ready=tiles.Select(tile=>{var bitmap=BitmapSource.Create(tile.Bitmap.Width,tile.Bitmap.Height,96,96,PixelFormats.Pbgra32,null,tile.Bitmap.Pixels.ToArray(),tile.Bitmap.Stride);bitmap.Freeze();return(tile,bitmap);}).ToArray();
            if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(()=>ApplyTiles(ready));
        };
        _renderSession.Failed+=error=>AppDiagnostics.Write($"in_place_worker_failed type={error.GetType().Name}");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; IsHitTestVisible = false; ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new(GetWindowLongPtr(handle, -20).ToInt64() | 0x20 | 0x80 | 0x08000000));
        };
        _model.Segments.CollectionChanged += SegmentsChanged;
        _model.PropertyChanged += ModelChanged;
        _interactions = new(model.SourceWindowHandle, () => new WindowInteropHelper(this).Handle);
        _interactions.Changed += ContentChanged;
        _timer.Tick += Tick;
        _timer.Start();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TranslationResultViewModel.IsBusy) or nameof(TranslationResultViewModel.IsInPlaceEnabled)
            or nameof(TranslationResultViewModel.ErrorMessage)) _dirty = true;
        if (!_model.IsInPlaceEnabled || _model.ErrorMessage is not null) Hide();
    }
    private void SegmentsChanged(object? sender, NotifyCollectionChangedEventArgs args) => _dirty = true;

    private void ContentChanged()
    {
        _changedAt = DateTime.UtcNow;
        if (_model.RefreshSourceImageMapping is null) _invalidSnapshot = true;
        Hide();
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (_dirty) { _dirty = false; UpdateEntries(); }
        var source = _model.SourceWindowHandle;
        var current = AccessibleTextResolver.GetWindowBounds(source);
        if (source != 0 && GetForegroundWindow() == source && _model.IsInPlaceEnabled && (GetAsyncKeyState(0x1B) & 0x8000) != 0)
        { _model.CloseCommand.Execute(null); return; }
        if (!_controlVisible() || !_model.IsInPlaceEnabled || _model.ErrorMessage is not null || _invalidSnapshot
            || source == 0 || GetForegroundWindow() != source || !IsWindowVisible(source) || IsIconic(source)
            || current is null || _model.SourceWindowBounds is not { } original || current.Value.Width != original.Width || current.Value.Height != original.Height
            || (GetAsyncKeyState(0x12) & 0x8000) != 0)
        { Hide(); UpdateStatus(); return; }
        if (_model.RefreshSourceImageMapping is not null)
        {
            if (!_refreshing && DateTime.UtcNow - _refreshedAt >= TimeSpan.FromMilliseconds(100)) _ = RefreshMappingAsync();
            if (_interactions.PointerDown || DateTime.UtcNow - _changedAt < TimeSpan.FromMilliseconds(150) || _refreshedAt < _changedAt)
            { Hide(); return; }
        }
        var offset = new PixelPoint(current.Value.X - original.X, current.Value.Y - original.Y);
        var mapping = _model.RefreshSourceImageMapping is null
            ? new SourceImageMapping(_model.SourceBitmap!.Size, new DipRect(_model.SourceRegion.X, _model.SourceRegion.Y,
                _model.SourceBitmap.Width, _model.SourceBitmap.Height), _model.SourceRegion)
            : _model.SourceImageMapping;
        if (mapping is null || _entries.Values.All(entry => entry.Patch is null)) { Hide(); UpdateStatus(); return; }
        var viewport = mapping.VisibleBounds.Translate(offset);
        if (!viewport.IsUsable) { Hide(); return; }
        if (!IsVisible) Show();
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new(-1), viewport.X, viewport.Y, viewport.Width, viewport.Height, 0x0010);
        var scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        Width = viewport.Width / scale; Height = viewport.Height / scale;
        foreach (var entry in _entries.Values.Where(entry => entry.Patch is not null && entry.Image is not null))
        {
            var projected = mapping.Project(entry.Patch!.Bounds, offset);
            Canvas.SetLeft(entry.Image!, (projected.X - viewport.X) / scale);
            Canvas.SetTop(entry.Image!, (projected.Y - viewport.Y) / scale);
            entry.Image!.Width = projected.Width / scale; entry.Image.Height = projected.Height / scale;
        }
        UpdateStatus();
    }

    private async Task RefreshMappingAsync()
    {
        _refreshing = true;
        try
        {
            var mapping = await _model.RefreshSourceImageMapping!();
            if (_closed) return;
            _model.SourceImageMapping = mapping;
            _refreshedAt = DateTime.UtcNow;
            if (mapping is null) Hide();
        }
        catch (Exception) { if (!_closed) { _model.SourceImageMapping = null; Hide(); } }
        finally { _refreshing = false; }
    }

    private void UpdateEntries()
    {
        _renderSession.Update(_model.Segments.Select(s=>s.ToBlock()).ToArray(),!_model.IsBusy);
    }

    private void ApplyTiles((RenderedImageTile tile,BitmapSource bitmap)[] ready)
    {
        if(_closed)return;
        ready=ready.Where(item=>_model.Segments.Any(s=>s.Id==item.tile.Id&&s.Translation.StartsWith(item.tile.Translation,StringComparison.Ordinal))).ToArray();
        foreach(var key in _entries.Keys.Except(ready.Select(t=>t.tile.Id)).ToArray()){if(_entries[key].Image is{} old)_canvas.Children.Remove(old);_entries.Remove(key);}
        foreach(var (tile,bitmap) in ready)
        {
            if(!_entries.TryGetValue(tile.Id,out var entry))
            {entry=new(){Current=_model.Segments.First(s=>s.Id==tile.Id).ToBlock(),GeometryKey=tile.Id,Image=new Image{Stretch=Stretch.Fill,IsHitTestVisible=false}};
                _entries[tile.Id]=entry;_canvas.Children.Add(entry.Image);
                if (_model.CachedTiles is not { Count: > 0 }) Behaviors.TranslationMotion.Reveal(entry.Image, .35, 150);}
            entry.Patch=new(tile.Bounds,tile.Bitmap,SkiaSharp.SKColors.Black,12,false,"worker",0);
            entry.Image!.Source=bitmap;
        }
        _model.TilesRendered?.Invoke(ready.Select(t=>t.tile).ToArray());
        UpdateStatus();
    }

    private void LegacyUpdateEntries()
    {
        var blocks = _model.Segments.Select(segment => segment.ToBlock()).ToArray();
        foreach (var key in _entries.Keys.Except(blocks.Where(block => block.Bounds.IsUsable).Select(block => block.Id)).ToArray())
        { if (_entries[key].Image is { } image) _canvas.Children.Remove(image); _entries.Remove(key); }
        foreach (var block in blocks.Where(block => block.Bounds.IsUsable && block.Translation.Length > 0))
        {
            var key = block.SourceText + block.Bounds + block.LayoutBounds + block.HasVisionPlacement + block.IsVertical + string.Join(";", block.TextBounds);
            if (!_entries.TryGetValue(block.Id, out var entry) || entry.GeometryKey != key)
            {
                if (entry?.Image is { } old) _canvas.Children.Remove(old);
                entry = new() { Current = block, GeometryKey = key };
                _entries[block.Id] = entry;
                _ = PrepareAsync(block.Id, entry);
            }
            else
            {
                var changed = entry.Current.Translation != block.Translation || entry.Current.IsComplete != block.IsComplete;
                entry.Current = block;
                if (changed && entry.Patch is not null) Render(entry);
            }
        }
        if (blocks.Length == 0) Hide();
    }

    private async Task PrepareAsync(string id, Entry entry)
    {
        try
        {
            var patch = await Task.Run(() => _renderer.Prepare(entry.Current), _shutdown.Token);
            if (_closed || !_entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry)) return;
            entry.Patch = patch;
            if (patch is null) return;
            entry.Image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            _canvas.Children.Add(entry.Image);
            Render(entry);
            if (entry.Image is null) return;
            entry.Image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            AppDiagnostics.Write($"in_place_patch method={patch.RepairMethod} width={patch.Bounds.Width} height={patch.Bounds.Height} layout_width={patch.LayoutBounds.Width} layout_height={patch.LayoutBounds.Height} vertical={patch.IsVertical} erased={patch.ErasedPixels}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (!_closed) AppDiagnostics.Write($"in_place_patch_failed type={exception.GetType().Name}"); }
    }

    private void Render(Entry entry)
    {
        try
        {
            var image = _renderer.Render(entry.Patch!, entry.Current);
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels.ToArray(), image.Stride);
            bitmap.Freeze(); entry.Image!.Source = bitmap;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (entry.Image is { } failed) _canvas.Children.Remove(failed);
            entry.Patch = null; entry.Image = null;
            AppDiagnostics.Write($"in_place_render_failed type={exception.GetType().Name}");
        }
    }

    private void UpdateStatus()
    {
        _model.InPlaceRenderedCount = _entries.Values.Count(entry => entry.Patch is not null);
        if (!_model.IsInPlaceEnabled || _model.ErrorMessage is not null) return;
        var count = _model.InPlaceRenderedCount;
        _model.StatusText = _invalidSnapshot ? "内容已移动 · 请重新选择" : _model.IsBusy
            ? count == 0 ? "正在定位 · 可展开查看译文" : $"正在原位翻译 · {count} 段"
            : count == 0 ? "译文已完成 · 点击查看" : $"原位完成 {count}/{_model.Segments.Count} · 点击查看";
    }

    protected override void OnClosed(EventArgs args)
    {
        _closed = true; _shutdown.Cancel(); _timer.Stop(); _interactions.Dispose();
        _renderSession.Dispose();
        _model.Segments.CollectionChanged -= SegmentsChanged; _model.PropertyChanged -= ModelChanged;
        _canvas.Children.Clear(); _entries.Clear(); _shutdown.Dispose(); base.OnClosed(args);
    }

    private sealed class Entry
    {
        public required TranslatedBlock Current { get; set; }
        public required string GeometryKey { get; init; }
        public InPlacePatch? Patch { get; set; }
        public Image? Image { get; set; }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
