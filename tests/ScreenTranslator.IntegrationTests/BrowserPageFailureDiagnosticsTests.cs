using System.Collections.Concurrent;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.IntegrationTests;

public sealed class BrowserPageFailureDiagnosticsTests
{
    private const string PrivateText = "SOURCE_SECRET The entrance is blue.";
    private const string UnsafeText = "MODEL_SECRET 演示于1955年举行。";

    [Fact]
    public async Task MalformedFramesRetryInSmallerBatchesAndKeepSuccessfulGroups()
    {
        var log = new ConcurrentQueue<string>(); var sizes = new ConcurrentQueue<int>();
        var blocks = Enumerable.Range(0, 6).Select(i => new BrowserPageTextBlock("node" + i, PrivateText + (char)('a' + i))).ToArray();
        var service = new BrowserPageTranslationService(_ => new Provider(request =>
        {
            sizes.Enqueue(request.Blocks.Count);
            return request.Blocks.Count > 4 || request.Blocks[0].Text.EndsWith('e')
                ? "MALFORMED_SECRET https://secret.invalid sk-credential" : Json(request, _ => "入口是蓝色。");
        }), diagnostic: log.Enqueue);
        var result = await Run(service, blocks, new() { MaxRequests = 3 });
        Assert.Equal(new[] { 6, 4, 2 }, sizes.ToArray());
        Assert.Equal(3, result.RequestCount); Assert.Equal(4, result.CompletedCount); Assert.Equal(2, result.FailedCount);
        Assert.Equal(4, service.CacheCount);
        Has(log, "phase=initial reason=format_failure"); Has(log, "phase=format_retry reason=retry_started");
        Has(log, "phase=format_retry reason=format_failure"); Has(log, "reason=successful_group"); Has(log, "reason=result_mismatch count=2");
        Privacy(log);
    }

    [Fact]
    public async Task GuardAndNumericTransferAreObservedBeforeCorrectedGroupsComplete()
    {
        var log = new ConcurrentQueue<string>(); var calls = 0;
        BrowserPageTextBlock[] blocks = [new("receiver", "The demonstration", "inline"), new("owner", "took place in 1954.", "inline")];
        var service = new BrowserPageTranslationService(_ => new Provider(request =>
        {
            var retry = Interlocked.Increment(ref calls) > 1;
            return Json(request, block => block.Text == blocks[0].Text
                ? retry ? "演示" : "1954年的演示" : retry ? "于1954年举行。" : "举行。");
        }), diagnostic: log.Enqueue);
        var updates = new ConcurrentQueue<BrowserPageTranslationUpdate>();
        var result = await service.TranslateAsync(blocks, new() { MaxRequests = 3 }, (update, _) =>
        { updates.Enqueue(update); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, result.FailedCount); Assert.Equal(3, result.RequestCount);
        Assert.All(updates, update => Assert.True(update.IsComplete && !update.Failed));
        Has(log, "reason=numeric_transfer"); Has(log, "reason=guard_rejection");
        Has(log, "phase=semantic_retry reason=retry_started"); Has(log, "reason=service_complete count=0");
        Privacy(log);
    }

    [Theory]
    [InlineData(1, "retry_budget_exhausted", 1)]
    [InlineData(3, "repeated_rejected_answer", 2)]
    public async Task UnsafeAnswersRemainFailedAndDoNotCache(int budget, string reason, int requests)
    {
        var log = new ConcurrentQueue<string>();
        var service = new BrowserPageTranslationService(_ => new Provider(request => Json(request, _ => UnsafeText)), diagnostic: log.Enqueue);
        var result = await Run(service, [new("fact", "The demonstration took place in 1954.")], new() { MaxRequests = budget });
        Assert.Equal(requests, result.RequestCount); Assert.Equal(1, result.FailedCount); Assert.Equal(0, service.CacheCount);
        Has(log, "reason=guard_rejection"); Has(log, "reason=" + reason); Has(log, "reason=result_mismatch count=1");
        if (budget > 1) Assert.DoesNotContain(log, line => line.Contains("retry_budget_exhausted"));
        Privacy(log);
    }

    [Fact]
    public async Task FormatRetryCannotExceedBudgetAndPreflightDoesNotCreateProvider()
    {
        var log = new ConcurrentQueue<string>(); var created = 0;
        var service = new BrowserPageTranslationService(_ => { created++; return new Provider(_ => "SECRET malformed"); }, diagnostic: log.Enqueue);
        var result = await Run(service, [new("node", PrivateText)], new() { MaxRequests = 1 });
        Assert.Equal(1, created); Assert.Equal(1, result.FailedCount); Assert.Equal(1, result.RequestCount);
        Has(log, "reason=retry_budget_exhausted");
        created = 0; log.Clear();
        await Assert.ThrowsAsync<TranslationConfigurationException>(() => Run(service,
            [new("one", PrivateText), new("two", PrivateText)], new() { MaxBatchBlocks = 1, MaxRequests = 1 }));
        Assert.Equal(0, created); Has(log, "reason=request_budget_exceeded count=2"); Privacy(log);
    }

    [Fact]
    public async Task DefaultAndThrowingDiagnosticCallbacksPreserveProviderRequestsAndResults()
    {
        async Task<BrowserPageTranslationResult> Translate(Action<string>? callback)
        {
            var service = new BrowserPageTranslationService(_ => new Provider(request => Json(request, _ => "入口是蓝色。")), diagnostic: callback);
            return await Run(service, [new("node", PrivateText)], new());
        }
        var plain = await Translate(null);
        var broken = await Translate(_ => throw new IOException("EXCEPTION_SECRET https://secret.invalid"));
        Assert.Equal(plain.RequestCount, broken.RequestCount); Assert.Equal(plain.CompletedCount, broken.CompletedCount);
        Assert.Equal(plain.Blocks[0], broken.Blocks[0]);
    }

    [Fact]
    public async Task ProviderExceptionsKeepTheirOriginalFailureWithoutLoggingTheirPayload()
    {
        var log = new ConcurrentQueue<string>();
        var service = new BrowserPageTranslationService(_ => new Provider(_ =>
            throw new InvalidOperationException("EXCEPTION_SECRET https://secret.invalid sk-credential")), diagnostic: log.Enqueue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service, [new("node", PrivateText)], new()));
        Has(log, "reason=service_failure"); Assert.Equal(0, service.CacheCount); Privacy(log);
    }

    [Fact]
    public async Task ManyActualGuardFailuresProduceBoundedDiagnosticsWithFinalSummary()
    {
        var log = new ConcurrentQueue<string>();
        var service = new BrowserPageTranslationService(_ => new Provider(request => Json(request, _ => UnsafeText)), diagnostic: log.Enqueue);
        var blocks = Enumerable.Range(0, 128).Select(i => new BrowserPageTextBlock("fact" + i, "The demonstration took place in 1954.")).ToArray();
        var result = await Run(service, blocks, new() { MaxRequests = 32 });
        Assert.Equal(128, result.FailedCount); Assert.InRange(result.RequestCount, 8, 32);
        Assert.InRange(log.Count, 1, 66); Has(log, "reason=output_limited"); Has(log, "reason=result_mismatch count=128");
        Privacy(log);
    }

    private static Task<BrowserPageTranslationResult> Run(BrowserPageTranslationService service,
        IReadOnlyList<BrowserPageTextBlock> blocks, BrowserPageTranslationOptions options) =>
        service.TranslateAsync(blocks, options with { Context = "SETTINGS_SECRET", CacheScope = "ENDPOINT_SECRET" },
            (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    private static string Json(TranslationRequest request, Func<OcrBlock, string> translate) =>
        JsonSerializer.Serialize(new { blocks = request.Blocks.Select(block => new { id = block.Id, translation = translate(block) }) });
    private static void Has(IEnumerable<string> log, string code) => Assert.Contains(log, line => line.Contains(code, StringComparison.Ordinal));
    private static void Privacy(IEnumerable<string> log) => Assert.All(log, line =>
    {
        Assert.InRange(line.Length, 1, 256);
        Assert.DoesNotContain("SECRET", line); Assert.DoesNotContain("https", line); Assert.DoesNotContain("credential", line);
        Assert.DoesNotContain("entrance", line); Assert.DoesNotContain("演示", line);
        Assert.Matches(@"\A(browser_page_diagnostic phase=[a-z_]+ reason=[a-z_]+ count=[0-9]+ requests=[0-9]+|browser_page_cache [a-z_0-9= ]+failed=(True|False))\z", line);
    });
    private sealed class Provider(Func<TranslationRequest, string> run) : ITranslationProvider
    {
        public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token) => Task.FromResult(run(request));
    }
}
