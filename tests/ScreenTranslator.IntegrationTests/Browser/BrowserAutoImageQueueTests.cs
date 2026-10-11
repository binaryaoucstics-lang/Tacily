using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserAutoImageQueueTests
{
    private static readonly AppSettings Enabled = new() { BrowserAutoImageTranslationEnabled = true };
    private static AutoImageCandidate Candidate(string image = "image", long generation = 10) =>
        new(Guid.Empty, Guid.NewGuid().ToString("N"), image, "document", 1, 1, generation, "bounded-preview");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ImageTranslationAssessment Assessment(bool yes) => new(yes, "test", 1, "en", [], 1) { ReasonCode = "test" };

    [Fact]
    public async Task Blacklisted_Images_Never_Reach_The_Local_Judge_Or_Cloud_Queue()
    {
        var calls = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => { calls++; return Task.FromResult(Assessment(true)); },
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        queue.Configure(Enabled with { BrowserTranslationBlacklist = ["example.com"] }, 10);
        Assert.False(queue.TryEnqueue(Candidate() with { SiteHost = "news.example.com" })); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisabledOrStaleCandidatesNeverReachLocalJudge()
    {
        var calls = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => { calls++; return Task.FromResult(Assessment(true)); },
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        Assert.False(queue.TryEnqueue(Candidate()));
        queue.Configure(Enabled, 11);
        Assert.False(queue.TryEnqueue(Candidate(generation: 10)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NegativeAssessmentCannotStartTranslation()
    {
        var replied = Signal(); var translations = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(false)),
            (_, yes, _, _) => { Assert.False(yes); replied.SetResult(); return Task.CompletedTask; },
            (_, _, _) => { Interlocked.Increment(ref translations); return Task.CompletedTask; });
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate()));
        await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, translations);
    }

    [Fact]
    public async Task SlowCloudJobDoesNotDelayNextLocalAssessment()
    {
        var firstCloud = Signal(); var nextReply = Signal(); var cloudRelease = Signal(); var replies = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(true)),
            (_, yes, _, _) => { Assert.True(yes); if (Interlocked.Increment(ref replies) == 2) nextReply.SetResult(); return Task.CompletedTask; },
            async (_, _, token) => { firstCloud.TrySetResult(); await cloudRelease.Task.WaitAsync(token); });
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate("first")));
        await firstCloud.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(queue.TryEnqueue(Candidate("second")));
        await nextReply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(cloudRelease.Task.IsCompleted);
        cloudRelease.SetResult();
    }

    [Fact]
    public async Task ChangingTargetCancelsInFlightLocalResultBeforeReplyOrCloud()
    {
        var entered = Signal(); var cancelled = Signal(); var replies = 0;
        await using var queue = new BrowserAutoImageQueue(async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return Assessment(true);
        }, (_, _, _, _) => { replies++; return Task.CompletedTask; }, (_, _, _) => throw new InvalidOperationException("No stale upload"));
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.Configure(Enabled with { TargetLanguage = "en" }, 11);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, replies);
    }

    [Fact]
    public async Task ManualSelectionDefersCloudButAllowsLocalAssessment()
    {
        var replied = Signal(); var translated = Signal();
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(true)),
            (_, _, _, _) => { replied.SetResult(); return Task.CompletedTask; },
            (_, _, _) => { translated.SetResult(); return Task.CompletedTask; });
        queue.Configure(Enabled, 10); queue.SetManualBusy(true);
        Assert.True(queue.TryEnqueue(Candidate())); await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(translated.Task.IsCompleted); queue.SetManualBusy(false);
        await translated.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DuplicatePendingImageIsRejectedAndQueueHasBoundedCapacity()
    {
        var entered = Signal();
        await using var queue = new BrowserAutoImageQueue(async (_, _, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Assessment(true); },
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate("first")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(queue.TryEnqueue(Candidate("first")));
        for (var i = 0; i < 4; i++) Assert.True(queue.TryEnqueue(Candidate($"waiting-{i}")));
        Assert.False(queue.TryEnqueue(Candidate("overflow")));
    }

    [Fact]
    public async Task ManualOperationCancelsAndResumesActiveAutomaticJob()
    {
        var first = Signal(); var cancelled = Signal(); var resumed = Signal(); var runs = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(true)),
            (_, _, _, _) => Task.CompletedTask, async (_, _, token) =>
            {
                if (Interlocked.Increment(ref runs) > 1) { resumed.SetResult(); return; }
                first.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            });
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate()));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3)); queue.SetManualBusy(true);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(resumed.Task.IsCompleted);
        queue.SetManualBusy(false); await resumed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task DisconnectCancelsActiveAutomaticJobWithoutResume()
    {
        var first = Signal(); var cancelled = Signal(); var runs = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(true)),
            (_, _, _, _) => Task.CompletedTask, async (_, _, token) =>
            {
                Interlocked.Increment(ref runs); first.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            });
        queue.Configure(Enabled, 10); Assert.True(queue.TryEnqueue(Candidate()));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3)); queue.ConnectionClosed(Guid.Empty);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(1, runs);
    }

    [Fact]
    public async Task LateOldDocumentCancelDoesNotCancelCurrentDocumentButExactIdentityCancelsImmediately()
    {
        var connection = Guid.NewGuid(); var oldStarted = Signal(); var oldRelease = Signal();
        var currentStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentCancelled = Signal(); var nextStarted = Signal(); var currentRuns = 0;
        await using var queue = new BrowserAutoImageQueue((_, _, _) => Task.FromResult(Assessment(true)),
            (_, _, _, _) => Task.CompletedTask, async (candidate, _, token) =>
            {
                if (candidate.DocumentToken == "old-document")
                {
                    oldStarted.TrySetResult(); await oldRelease.Task.WaitAsync(token); return;
                }
                if (candidate.DocumentToken == "next-document") { nextStarted.TrySetResult(); return; }
                Interlocked.Increment(ref currentRuns); currentStarted.TrySetResult(token);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { currentCancelled.TrySetResult(); throw; }
            });
        queue.Configure(Enabled, 10);
        var old = Candidate("old-image") with { ConnectionId = connection, TabId = 7, DocumentToken = "old-document" };
        var current = Candidate("current-image") with { ConnectionId = connection, TabId = 7, DocumentToken = "current-document" };
        Assert.True(queue.TryEnqueue(old)); await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(queue.TryEnqueue(current)); oldRelease.TrySetResult();
        var currentToken = await currentStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        queue.CancelDocument(connection, 7, "old-document");
        queue.CancelDocument(Guid.NewGuid(), 7, "current-document");
        queue.CancelDocument(connection, 8, "current-document");
        queue.CancelDocument(connection, 7, "CURRENT-DOCUMENT");
        Assert.False(currentToken.IsCancellationRequested);
        Assert.False(currentCancelled.Task.IsCompleted);

        queue.CancelDocument(connection, 7, "current-document");
        Assert.True(currentToken.IsCancellationRequested);
        await currentCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, currentRuns);

        Assert.True(queue.TryEnqueue(Candidate("next-image") with { ConnectionId = connection, TabId = 7, DocumentToken = "next-document" }));
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, currentRuns);
    }
}
