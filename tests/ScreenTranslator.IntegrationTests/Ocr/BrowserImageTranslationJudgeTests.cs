using ScreenTranslator.App.Services.Ocr;
using ScreenTranslator.Core.Models;
using SkiaSharp;

namespace ScreenTranslator.IntegrationTests.Ocr;

public sealed class BrowserImageTranslationJudgeTests
{
    [Fact]
    public void HugeImageIsBoundedBeforeDetectorAndPreservesAspectRatio()
    {
        var image = Image(4000, 600, SKColors.White);
        var small = BrowserImageTranslationJudge.Downscale(image, default);
        Assert.Equal(1280, small.Width);
        Assert.Equal(192, small.Height);
        Assert.True(small.Pixels.Length <= 1280 * 1280 * 4);
    }

    [Fact]
    public void SmallImageReusesCallerPixelsWithoutCopy()
    {
        var image = Image(640, 480, SKColors.White);
        Assert.Same(image, BrowserImageTranslationJudge.Downscale(image, default));
    }

    [Fact]
    public async Task CancellationIsNotMistakenForNoText()
    {
        using var judge = new BrowserImageTranslationJudge();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.AssessAsync(Image(64, 64, SKColors.White), "简体中文", cancelled.Token));
    }

    [Fact]
    public async Task DisposedJudgeRejectsNewWork()
    {
        var judge = new BrowserImageTranslationJudge();
        judge.Dispose();
        judge.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => judge.AssessAsync(Image(64, 64, SKColors.White), "简体中文", default));
    }

    [Fact]
    public void LocalModelsAndPixelLimitsAreWellBelowFiveGiB()
    {
        Assert.Equal(0, BrowserImageTranslationJudge.GpuAllocationBytes);
        Assert.InRange(BrowserImageTranslationJudge.InstalledModelBytes, 1, 64L * 1024 * 1024);
        var maximumOwnedPixels = 3L * BrowserImageTranslationJudge.MaximumImageEdge * BrowserImageTranslationJudge.MaximumImageEdge * 4 +
            BrowserImageTranslationJudge.MaximumTextRegions * BrowserImageTranslationJudge.MaximumCropWidth * BrowserImageTranslationJudge.MaximumCropHeight * 4L;
        Assert.True(maximumOwnedPixels < 64L * 1024 * 1024);
    }

    [Fact]
    [Trait("Category", "OcrEnvironment")]
    public async Task ResidentInferenceRejectsBlankAndReadsEnglishWithoutNetwork()
    {
        using var judge = new BrowserImageTranslationJudge(FindDetectorModel());
        var blank = await judge.AssessAsync(Image(640, 200, SKColors.White), "简体中文", default);
        Assert.False(blank.NeedsTranslation);
        Assert.Equal("no-text", blank.ReasonCode);
        using var bitmap = new SKBitmap(960, 300, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 52);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("PLEASE HELP ME ESCAPE", 35, 95, font, paint);
            canvas.DrawText("Do not open that door!", 35, 200, font, paint);
        }
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        var input = new CapturedBitmap(bitmap.Width, bitmap.Height, bitmap.RowBytes, bytes);
        var first = await judge.AssessAsync(input, "简体中文", default);
        var repeat = await judge.AssessAsync(input, "简体中文", default);
        Assert.True(first.NeedsTranslation, $"{first.ReasonCode}: {string.Join("|", first.RecognizedText.Select(block => block.Text))}");
        Assert.True(repeat.NeedsTranslation);
        Assert.True(repeat.ElapsedMilliseconds < BrowserImageTranslationJudge.InferenceTimeoutMilliseconds);
        Assert.All(first.TextRegions, region => Assert.Equal(region.Bounds, region.Bounds.Intersect(new PixelRect(0, 0, input.Width, input.Height))));
    }

    private static string FindDetectorModel()
    {
        var path = LocalTextDetector.DefaultModelPath;
        Assert.True(System.IO.File.Exists(path));
        return path;
    }

    [Fact]
    [Trait("Category", "OcrEnvironment")]
    public async Task VerticalJapaneseGlyphsWithRealLineSpacingTriggerForeignText()
    {
        using var judge = new BrowserImageTranslationJudge(FindDetectorModel());
        using var bitmap = new SKBitmap(420, 960, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            // Use the shipped font so a clean Windows runner renders Japanese
            // glyphs instead of fallback squares when Meiryo is not installed.
            var fontPath = System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansCJKsc-Regular.otf");
            Assert.True(System.IO.File.Exists(fontPath));
            using var face = SKTypeface.FromFile(fontPath);
            Assert.NotNull(face);
            using var font = new SKFont(face, 55);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            var row = 0;
            foreach (var character in "また明日会おう") canvas.DrawText(character.ToString(), 180, 80 + 85 * row++, font, paint);
        }
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        var assessment = await judge.AssessAsync(new CapturedBitmap(bitmap.Width, bitmap.Height, bitmap.RowBytes, bytes), "zh-CN", default);
        Assert.True(assessment.NeedsTranslation, $"{assessment.ReasonCode}: {string.Join("|", assessment.AllRecognizedText.Select(block => $"{block.Text} ({block.Confidence:F2})"))}");
        Assert.Equal("ja", assessment.DetectedLanguage);
    }

    [Fact]
    [Trait("Category", "OcrEnvironment")]
    public async Task NewsPhotoCreditBesideChineseCaptionDoesNotStartAutomaticTranslation()
    {
        using var judge = new BrowserImageTranslationJudge(FindDetectorModel());
        var assessment = await judge.AssessAsync(CaptionImage("欢迎回来，我们现在可以出发了。"), "zh-Hans", default);
        // Actual local recognizer output, not injected text. Ensure this fixture
        // contains the triggering photographer line rather than passing as blank.
        Assert.Contains(assessment.AllRecognizedText, line => line.Text.Contains("Reuters", StringComparison.OrdinalIgnoreCase));
        Assert.False(assessment.NeedsTranslation,
            $"{assessment.ReasonCode}: {string.Join("|", assessment.AllRecognizedText.Select(line => $"{line.Text} ({line.Confidence:F2})"))}");
        Assert.DoesNotContain(assessment.RecognizedText, line => line.Text.Contains("Reuters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "OcrEnvironment")]
    public async Task RealEnglishCaptionBesidePhotoCreditStillStartsTranslation()
    {
        using var judge = new BrowserImageTranslationJudge(FindDetectorModel());
        var assessment = await judge.AssessAsync(CaptionImage("PLEASE HELP ME ESCAPE"), "zh-Hans", default);
        Assert.Contains(assessment.AllRecognizedText, line => line.Text.Contains("Reuters", StringComparison.OrdinalIgnoreCase));
        Assert.True(assessment.NeedsTranslation,
            $"{assessment.ReasonCode}: {string.Join("|", assessment.AllRecognizedText.Select(line => $"{line.Text} ({line.Confidence:F2})"))}");
        Assert.DoesNotContain(assessment.RecognizedText, line => line.Text.Contains("Reuters", StringComparison.OrdinalIgnoreCase));
    }

    private static CapturedBitmap CaptionImage(string caption)
    {
        using var bitmap = new SKBitmap(1200, 300, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"), 42);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText(caption, 24, 90, font, paint);
            using var creditFont = new SKFont(SKTypeface.FromFamilyName("Arial"), 30);
            canvas.DrawText("Sarah Meyssonnier/Reuters", 24, 220, creditFont, paint);
        }
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bitmap.RowBytes, bytes);
    }

    private static CapturedBitmap Image(int width, int height, SKColor color)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        { bytes[i] = color.Blue; bytes[i + 1] = color.Green; bytes[i + 2] = color.Red; bytes[i + 3] = 255; }
        return new CapturedBitmap(width, height, width * 4, bytes);
    }
}
