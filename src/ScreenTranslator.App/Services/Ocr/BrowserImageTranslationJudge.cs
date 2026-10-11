using System.Diagnostics;
using System.IO;
using RapidOcrNet;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;
using SkiaSharp;

namespace ScreenTranslator.App.Services.Ocr;

/// <summary>
/// Resident CPU PP-OCRv5 judgement; no VLM, Python worker, GPU provider or network.
/// Work is deliberately bounded because this runs automatically on arriving images.
/// </summary>
public sealed class BrowserImageTranslationJudge : IDisposable
{
    public const int MaximumImageEdge = 1280;
    public const int MaximumTextRegions = 32;
    public const int MaximumCropWidth = 2048;
    public const int MaximumCropHeight = 96;
    public const int InferenceTimeoutMilliseconds = 6000;
    public const long GpuAllocationBytes = 0;
    public const long MaximumModelBudgetBytes = 5L * 1024 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalTextDetector _detector;
    private TextRecognizer? _recognizer;
    private volatile bool _disposed;

    public BrowserImageTranslationJudge(string? detectorModelPath = null) => _detector = new LocalTextDetector(detectorModelPath);

    public static long InstalledModelBytes => new[] { LocalTextDetector.DefaultModelPath,
        Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-OCRv5_rec_mobile.onnx"),
        Path.Combine(AppContext.BaseDirectory, "models", "v5", "ppocrv5_dict.txt") }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);

    public async Task<ImageTranslationAssessment> AssessAsync(CapturedBitmap image, string targetLanguage, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(InferenceTimeoutMilliseconds);
        var entered = false;
        try
        {
            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(async () =>
            {
                var small = Downscale(image, budget.Token);
                var downscaled = small.Width != image.Width || small.Height != image.Height;
                var regions = await _detector.DetectAsync(small, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                if (regions.Count == 0) return Finish(ImageTranslationDecision.Assess([], targetLanguage), regions.Count, downscaled);
                EnsureRecognizer();
                using var bitmap = LocalTextDetector.ToSkia(small);
                var crops = new List<SKBitmap>();
                var bounds = new List<PixelRect>();
                try
                {
                    foreach (var region in regions.Where(region => region.Confidence >= .45)
                        .OrderByDescending(region => region.Confidence).ThenByDescending(region => (long)region.Bounds.Width * region.Bounds.Height)
                        .Take(MaximumTextRegions))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        var clipped = region.Bounds.Intersect(new PixelRect(0, 0, small.Width, small.Height));
                        if (!clipped.IsUsable) continue;
                        using var crop = new SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                        using (var canvas = new SKCanvas(crop)) canvas.DrawBitmap(bitmap,
                            new SKRect(clipped.X, clipped.Y, clipped.Right, clipped.Bottom), new SKRect(0, 0, crop.Width, crop.Height));
                        if (clipped.Height >= clipped.Width * 1.7)
                        {
                            // One pass is sufficient to identify a script. Seven pitch retries
                            // are useful for transcription but unsuitable for an automatic gate.
                            using var strip = BuildVerticalStrip(crop);
                            crops.Add(BoundCrop(strip));
                        }
                        else crops.Add(BoundCrop(crop));
                        bounds.Add(MapToOriginal(clipped, small, image));
                    }
                    if (crops.Count == 0) return Finish(ImageTranslationDecision.Assess([], targetLanguage), regions.Count, downscaled);
                    var recognized = _recognizer!.GetTextLines(crops.ToArray(), 2, cancellationToken: budget.Token);
                    budget.Token.ThrowIfCancellationRequested();
                    var blocks = recognized.Select((line, index) => new OcrBlock($"auto-{index + 1}",
                        string.Concat(line.Chars ?? []), line.CharScores is { Length: > 0 } scores ? scores.Average() : 0,
                        bounds[index], index)).Where(block => !string.IsNullOrWhiteSpace(block.Text)).ToArray();
                    return Finish(ImageTranslationDecision.Assess(blocks, targetLanguage) with
                    { AllRecognizedText = blocks, DetectedTextRegions = regions.Select(region => new TextRegion(MapToOriginal(region.Bounds, small, image), region.Confidence)).ToArray() }, regions.Count, downscaled);
                }
                finally { foreach (var crop in crops) crop.Dispose(); }
            }, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(false, "本地判断超过时间预算，跳过自动翻译；仍可点击翻译。", 0, "und", [], watch.ElapsedMilliseconds)
            { ReasonCode = "local-timeout" };
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not ObjectDisposedException)
        {
            // Missing/corrupt local weights must not silently upload an image to decide.
            return new(false, "本地判断暂不可用，跳过自动翻译；仍可点击翻译。", 0, "und", [], watch.ElapsedMilliseconds)
            { ReasonCode = "local-unavailable", ErrorType = exception.GetType().Name };
        }
        finally
        {
            if (entered)
            {
                if (_disposed) ReleaseModels();
                _gate.Release();
            }
        }

        ImageTranslationAssessment Finish(ImageTranslationAssessment assessment, int count, bool downscaled) =>
            assessment with { ElapsedMilliseconds = watch.ElapsedMilliseconds, DetectorRegionCount = count, WasDownscaled = downscaled };
    }

    private void EnsureRecognizer()
    {
        if (_recognizer is not null) return;
        if (InstalledModelBytes > MaximumModelBudgetBytes) throw new InvalidOperationException("Local judgement model exceeds its memory budget.");
        var candidate = new TextRecognizer();
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "models", "v5");
            candidate.InitModel(Path.Combine(directory, "ch_PP-OCRv5_rec_mobile.onnx"), Path.Combine(directory, "ppocrv5_dict.txt"), 2);
            _recognizer = candidate;
        }
        catch { candidate.Dispose(); throw; }
    }

    // Resample straight from caller-owned BGRA memory: never allocate a full copy of a
    // huge original, including panoramic images that arrive from a browser.
    internal static CapturedBitmap Downscale(CapturedBitmap image, CancellationToken token)
    {
        var scale = Math.Min(1, MaximumImageEdge / (double)Math.Max(image.Width, image.Height));
        if (scale >= 1) return image;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var stride = width * 4;
        var bytes = new byte[stride * height];
        var original = image.Pixels.Span;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var sourceY = Math.Min(image.Height - 1, (int)((y + .5) * image.Height / height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(image.Width - 1, (int)((x + .5) * image.Width / width));
                var source = sourceY * image.Stride + sourceX * 4;
                var destination = y * stride + x * 4;
                original.Slice(source, 4).CopyTo(bytes.AsSpan(destination, 4));
            }
        }
        return new CapturedBitmap(width, height, stride, bytes);
    }

    private static SKBitmap BoundCrop(SKBitmap source)
    {
        var scale = Math.Min(1, Math.Min(MaximumCropWidth / (double)source.Width, MaximumCropHeight / (double)source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height));
        return result;
    }

    // DB boxes contain expansion padding. A pitch inferred from that padded width
    // splits characters through their middles. Find actual ink and real blank row
    // gaps instead; merge small gaps inside a glyph (二/三/一) before making a strip.
    internal static SKBitmap BuildVerticalStrip(SKBitmap crop)
    {
        static int Luma(SKColor color) => (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
        var sample = new List<int>();
        for (var y = 0; y < crop.Height; y += Math.Max(1, crop.Height / 48))
            for (var x = 0; x < crop.Width; x += Math.Max(1, crop.Width / 32)) sample.Add(Luma(crop.GetPixel(x, y)));
        sample.Sort();
        var dark = sample[sample.Count / 2] < 128;
        var side = Math.Max(1, crop.Width * 15 / 100);
        var counts = new int[crop.Height];
        var left = crop.Width; var right = 0;
        for (var y = 0; y < crop.Height; y++)
            for (var x = side; x < crop.Width - side; x++)
                if (dark ? Luma(crop.GetPixel(x, y)) > 170 : Luma(crop.GetPixel(x, y)) < 140)
                { counts[y]++; left = Math.Min(left, x); right = Math.Max(right, x + 1); }
        if (right <= left) return crop.Copy();
        var glyphWidth = right - left;
        var threshold = Math.Max(1, glyphWidth / 30);
        var top = Array.FindIndex(counts, count => count >= threshold);
        var bottom = Array.FindLastIndex(counts, count => count >= threshold);
        if (top < 0 || bottom <= top) return crop.Copy();
        var bands = new List<(int Top, int Bottom)>();
        var mergeGap = Math.Max(2, (int)Math.Round(glyphWidth * .14));
        var start = top; var lastInk = top;
        for (var y = top; y <= bottom; y++)
        {
            if (counts[y] >= threshold) lastInk = y;
            else if (y - lastInk > mergeGap && start >= 0)
            { bands.Add((start, lastInk + 1)); start = -1; }
            if (start < 0 && counts[y] >= threshold) { start = y; lastInk = y; }
        }
        if (start >= 0) bands.Add((start, lastInk + 1));
        if (bands.Count < 2 || bands.Count > 48 || bands.Any(band => band.Bottom - band.Top > glyphWidth * 2.1))
        {
            // Touching text has no safe gap evidence: one bounded pitch estimate
            // uses the measured glyph width instead of the padded detector box.
            var count = Math.Clamp((int)Math.Round((bottom - top + 1) / (glyphWidth * 1.06)), 1, 48);
            bands.Clear();
            for (var index = 0; index < count; index++) bands.Add((top + (bottom - top + 1) * index / count,
                top + (bottom - top + 1) * (index + 1) / count));
        }
        const int cell = 56, margin = 4;
        var result = new SKBitmap(cell * bands.Count + margin * 2, cell + margin * 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(result);
        canvas.Clear(dark ? SKColors.Black : SKColors.White);
        for (var index = 0; index < bands.Count; index++)
        {
            var band = bands[index]; var height = band.Bottom - band.Top;
            var factor = Math.Min(48d / glyphWidth, 48d / height);
            var widthOut = (float)(glyphWidth * factor); var heightOut = (float)(height * factor);
            var xOut = margin + index * cell + (cell - widthOut) / 2;
            var yOut = margin + (cell - heightOut) / 2;
            canvas.DrawBitmap(crop, new SKRect(left, band.Top, right, band.Bottom), new SKRect(xOut, yOut, xOut + widthOut, yOut + heightOut));
        }
        return result;
    }

    private static PixelRect MapToOriginal(PixelRect bounds, CapturedBitmap small, CapturedBitmap original)
    {
        var left = (int)Math.Floor(bounds.X * original.Width / (double)small.Width);
        var top = (int)Math.Floor(bounds.Y * original.Height / (double)small.Height);
        var right = (int)Math.Ceiling(bounds.Right * original.Width / (double)small.Width);
        var bottom = (int)Math.Ceiling(bounds.Bottom * original.Height / (double)small.Height);
        return new PixelRect(left, top, right - left, bottom - top).Intersect(new PixelRect(0, 0, original.Width, original.Height));
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_gate.Wait(0)) return;
        try { ReleaseModels(); }
        finally { _gate.Release(); }
    }

    private void ReleaseModels() { _recognizer?.Dispose(); _recognizer = null; _detector.Dispose(); }
}
