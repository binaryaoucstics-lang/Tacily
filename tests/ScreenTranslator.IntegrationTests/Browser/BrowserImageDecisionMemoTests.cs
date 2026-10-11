using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserImageDecisionMemoTests
{
    private static CapturedBitmap Preview(byte mark = 1) => new(4, 3, 16,
        Enumerable.Repeat(mark, 48).ToArray());
    private static ImageTranslationAssessment Positive => new(true, "original OCR text must not persist", .94,
        "latin", [new(new(1, 2, 3, 4), .94)], 200)
    {
        ReasonCode = "foreign-readable-text", DetectorRegionCount = 7, WasDownscaled = true,
        RecognizedText = [new("text", "private OCR sentence", .94, new(1, 2, 3, 4), 0)],
        AllRecognizedText = [new("all", "private OCR full sentence", .94, new(1, 2, 3, 4), 0)],
        DetectedTextRegions = [new(new(2, 3, 4, 5), .94)],
    };

    [Fact]
    public async Task SameAndRestartedMemoReuseExactPreviewWithoutRunningJudge()
    {
        using var directory = new TestDirectory(); var calls = 0;
        Task<ImageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var memo = new BrowserImageDecisionMemo(persistentCache: cache);
            Assert.False((await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None)).CacheHit);
            var warm = await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
            Assert.True(warm.CacheHit); AssertMetadataOnly(warm.Assessment);
            Assert.Equal(1, memo.JudgementCalls); Assert.Equal(1, calls); await cache.FlushAsync();
        }
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        {
            var memo = new BrowserImageDecisionMemo(persistentCache: cache);
            var cold = await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
            Assert.True(cold.CacheHit); AssertMetadataOnly(cold.Assessment);
            Assert.Equal(0, memo.JudgementCalls); Assert.Equal(1, memo.CacheHits); Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task StoredDtoHasNoOcrPixelsCoordinatesReasonOrImageMetadata()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserImageDecisionMemo(persistentCache: cache);
        await memo.AssessAsync(Preview(), "zh", _ => Task.FromResult(Positive), CancellationToken.None); await cache.FlushAsync();
        var key = System.IO.Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories)));
        var stored = (await cache.GetManyAsync([key]))[key];
        Assert.DoesNotContain("private OCR", stored); Assert.DoesNotContain("original OCR", stored);
        using var json = JsonDocument.Parse(stored);
        Assert.Equal(new[] { "confidence", "detectedLanguage", "needsTranslation", "reasonCode", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task EveryPixelDimensionsStrideTargetModelAndPolicyIsolateDecisions()
    {
        await using var cache = new BrowserPageTranslationCache(); var calls = 0;
        Task<ImageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        var memo = new BrowserImageDecisionMemo(persistentCache: cache);
        await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
        Assert.True((await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None)).CacheHit);
        var changed = Preview().Pixels.ToArray(); changed[^1] = 100;
        await memo.AssessAsync(new(4, 3, 16, changed), "zh", Judge, CancellationToken.None);
        await memo.AssessAsync(new(3, 4, 12, Preview().Pixels), "zh", Judge, CancellationToken.None);
        await memo.AssessAsync(new(3, 3, 16, Preview().Pixels), "zh", Judge, CancellationToken.None);
        await memo.AssessAsync(Preview(), "ja", Judge, CancellationToken.None);
        await new BrowserImageDecisionMemo(persistentCache: cache, policyVersion: "changed")
            .AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
        await new BrowserImageDecisionMemo(persistentCache: cache, modelIdentity: "changed")
            .AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
        Assert.Equal(7, calls);
    }

    [Theory]
    [InlineData("no-text", 0, "und")]
    [InlineData("unreadable-or-noise", 0, "und")]
    [InlineData("target-script", .92, "ja")]
    public async Task CertainNegativeDecisionsArePersistedAndReused(string code, double confidence, string language)
    {
        using var directory = new TestDirectory(); var calls = 0;
        Task<ImageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(
            new ImageTranslationAssessment(false, "negative", confidence, language, [], 10) { ReasonCode = code }); }
        await using (var cache = new BrowserPageTranslationCache(directory: directory.Path))
        { await new BrowserImageDecisionMemo(persistentCache: cache).AssessAsync(Preview(), "ja", Judge, CancellationToken.None); await cache.FlushAsync(); }
        await using var restarted = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserImageDecisionMemo(persistentCache: restarted);
        var result = await memo.AssessAsync(Preview(), "ja", Judge, CancellationToken.None);
        Assert.True(result.CacheHit); Assert.False(result.Assessment.NeedsTranslation); Assert.Equal(code, result.Assessment.ReasonCode);
        Assert.Equal(1, calls); Assert.Equal(0, memo.JudgementCalls);
    }

    [Theory]
    [InlineData("local-timeout")]
    [InlineData("local-unavailable")]
    [InlineData("insufficient-evidence")]
    [InlineData("same-script-ambiguous")]
    [InlineData("target-or-han-ambiguous")]
    [InlineData("unsupported-target")]
    [InlineData("unknown")]
    public async Task UncertaintyAndFailuresDoNotBecomeCachedDecisions(string code)
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserImageDecisionMemo(persistentCache: cache); var calls = 0;
        Task<ImageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(
            Positive with { NeedsTranslation = false, ReasonCode = code }); }
        await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None);
        Assert.False((await memo.AssessAsync(Preview(), "zh", Judge, CancellationToken.None)).CacheHit);
        await cache.FlushAsync(); Assert.Equal(2, calls); Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ErrorOrCanceledLateResultIsNotPersisted()
    {
        await using var cache = new BrowserPageTranslationCache();
        var memo = new BrowserImageDecisionMemo(persistentCache: cache);
        await Assert.ThrowsAsync<IOException>(() => memo.AssessAsync(Preview(), "zh",
            _ => Task.FromException<ImageTranslationAssessment>(new IOException("local failed")), CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); var started = Signal(); var release = Signal();
        var active = memo.AssessAsync(Preview(), "zh", async _ =>
        { started.TrySetResult(); await release.Task; return Positive; }, cancellation.Token);
        await started.Task; cancellation.Cancel(); release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active); Assert.Equal(0, cache.Count);
        var result = await memo.AssessAsync(Preview(), "zh", _ => Task.FromResult(Positive), CancellationToken.None);
        Assert.False(result.CacheHit); Assert.Equal(3, memo.JudgementCalls);
    }

    [Fact]
    public async Task SharedClearPreventsLateWriteAndInvalidatesWarmImageDecision()
    {
        using var directory = new TestDirectory();
        await using var cache = new BrowserPageTranslationCache(directory: directory.Path);
        var memo = new BrowserImageDecisionMemo(persistentCache: cache); var started = Signal(); var release = Signal();
        var active = memo.AssessAsync(Preview(), "zh", async _ =>
        { started.TrySetResult(); await release.Task; return Positive; }, CancellationToken.None);
        await started.Task; await cache.ClearAsync(); release.TrySetResult(); await active; await cache.FlushAsync();
        Assert.Equal(0, cache.Count); Assert.Empty(Directory.GetFiles(directory.Path, "*.cache", SearchOption.AllDirectories));
        Assert.False((await memo.AssessAsync(Preview(), "zh", _ => Task.FromResult(Positive), CancellationToken.None)).CacheHit);
        await cache.ClearAsync();
        Assert.False((await memo.AssessAsync(Preview(), "zh", _ => Task.FromResult(Positive), CancellationToken.None)).CacheHit);
    }

    [Fact]
    public async Task MemoryOnlyMemoIsBoundedAndClearRejudges()
    {
        var memo = new BrowserImageDecisionMemo(maximumEntries: 2); var calls = 0;
        Task<ImageTranslationAssessment> Judge(CancellationToken _) { calls++; return Task.FromResult(Positive); }
        async Task<bool> Assess(byte mark) => (await memo.AssessAsync(Preview(mark), "zh", Judge, CancellationToken.None)).CacheHit;
        Assert.False(await Assess(1)); Assert.False(await Assess(2)); Assert.True(await Assess(1));
        Assert.False(await Assess(3)); Assert.False(await Assess(2)); Assert.Equal(4, calls);
        memo.Clear(); Assert.False(await Assess(2)); Assert.Equal(5, calls);
    }

    private static void AssertMetadataOnly(ImageTranslationAssessment value)
    {
        Assert.True(value.NeedsTranslation); Assert.Empty(value.TextRegions); Assert.Empty(value.RecognizedText);
        Assert.Empty(value.AllRecognizedText); Assert.Empty(value.DetectedTextRegions); Assert.Equal(0, value.ElapsedMilliseconds);
        Assert.Equal(0, value.DetectorRegionCount); Assert.Null(value.ErrorType);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.CurrentDirectory, "work", "image-decision-memo-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
