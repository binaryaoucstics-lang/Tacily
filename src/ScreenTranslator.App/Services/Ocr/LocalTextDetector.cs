using System.Runtime.InteropServices;
using RapidOcrNet;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Ocr;

public sealed class LocalTextDetector(string? modelPath = null) : ITextDetector, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TextDetector? _detector;
    private volatile bool _disposed;
    public static string DefaultModelPath => System.IO.Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");

    public async Task<IReadOnlyList<TextRegion>> DetectAsync(CapturedBitmap bitmap, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Detect(bitmap, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_disposed) { _detector?.Dispose(); _detector = null; }
            _gate.Release();
        }
    }

    private IReadOnlyList<TextRegion> Detect(CapturedBitmap bitmap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_detector is null)
        {
            var detector = new TextDetector();
            try { detector.InitModel(modelPath ?? DefaultModelPath, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)); _detector = detector; }
            catch { detector.Dispose(); throw; }
        }
        var regions = new List<TextRegion>();
        using var source = ToSkia(bitmap);
        DetectTile(source, new PixelPoint(0, 0), 1024);
        // Scan overlapping tiles at native resolution, so small text is not lost when a large image is resized.
        if (bitmap.Width > 1280 || bitmap.Height > 1280)
        {
            const int tileSize = 1024, step = 896;
            for (var y = 0; y < bitmap.Height; y += step)
                for (var x = 0; x < bitmap.Width; x += step)
                {
                    token.ThrowIfCancellationRequested();
                    var width = Math.Min(tileSize, bitmap.Width - x);
                    var height = Math.Min(tileSize, bitmap.Height - y);
                    if (width < 32 || height < 32) continue;
                    using var tile = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using (var canvas = new SKCanvas(tile)) canvas.DrawBitmap(source,
                        new SKRect(x, y, x + width, y + height), new SKRect(0, 0, width, height));
                    DetectTile(tile, new PixelPoint(x, y), 1024);
                }
        }
        return TextRegionGrouper.Deduplicate(regions);

        void DetectTile(SKBitmap tile, PixelPoint offset, int resolution)
        {
            token.ThrowIfCancellationRequested();
            var boxes = _detector.GetTextBoxes(tile, ScaleParam.GetScaleParam(tile, resolution), 0.45f, 0.25f, 1.7f, token)
                ?? throw new InvalidOperationException("本地文字检测运行失败，可切换检测 API 或重新选择。");
            foreach (var box in boxes)
            {
                var left = box.BoxPoints.Min(point => point.X);
                var top = box.BoxPoints.Min(point => point.Y);
                var right = box.BoxPoints.Max(point => point.X);
                var bottom = box.BoxPoints.Max(point => point.Y);
                var bounds = new PixelRect(left + offset.X, top + offset.Y, right - left, bottom - top)
                    .Intersect(new PixelRect(0, 0, bitmap.Width, bitmap.Height));
                if (bounds.IsUsable) regions.Add(new TextRegion(bounds, box.Score));
            }
        }
    }

    public static SKBitmap ToSkia(CapturedBitmap bitmap,bool transparent=false)
    {
        var source = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, transparent?SKAlphaType.Premul:SKAlphaType.Opaque);
        var bytes = bitmap.Pixels.ToArray();
        for (var y = 0; y < bitmap.Height; y++)
            Marshal.Copy(bytes, y * bitmap.Stride, source.GetPixels() + y * source.RowBytes, bitmap.Width * 4);
        return source;
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_gate.Wait(0)) return;
        try { _detector?.Dispose(); _detector = null; }
        finally { _gate.Release(); }
    }
}
