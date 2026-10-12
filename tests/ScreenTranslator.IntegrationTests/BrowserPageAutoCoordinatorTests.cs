using System.Collections.Concurrent;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.IntegrationTests;

public sealed class BrowserPageAutoCoordinatorTests
{
    private static readonly Guid Connection = Guid.Parse("c2fc5587-7eca-4af5-aa5e-d9f2da88b27a");
    private static AppSettings Settings => new() { BrowserAutoPageTranslationEnabled = true };
    private static AutoPageCandidate Candidate(string request = "request-1", long revision = 1,
        long generation = 10, int tab = 1, string document = "doc-1", bool samples = true) => new(
        Connection, request, document, tab, 5, generation, revision, "en",
        [new("node-1", "This is a public paragraph with enough words to translate into Chinese.")],
        samples ? [new("This is a public paragraph with enough words to translate into Chinese.")] : null, null);
    private static PageTranslationAssessment Assessment => new(true, "foreign", .99, "en", false)
        { ReasonCode = "foreign-script" };
    private static BrowserPageTranslationResult Result => new([new("node-1", "已经翻译的文字", true)], 1, 0);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task signal) => signal.WaitAsync(TimeSpan.FromSeconds(3));

    [Fact]
    public void ThresholdOnlyChangesPreserveManualGenerationButTranslationInputsDoNot()
    {
        var next = Settings with { BrowserChineseProseShareThresholdPercent = 70 };
        Assert.True(BrowserAutoConfigurationPolicy.PreserveManualGeneration(Settings, next));
        Assert.False(BrowserAutoConfigurationPolicy.OnlyAnimationPreferenceChanged(Settings, next));
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(Settings, next with { TargetLanguage = "fr" }));
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(Settings, next with { TranslationContext = "changed" }));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public async Task ThresholdChangePreservesAnActiveManualStreamAndAutomaticPause(string mode)
    {
        var started = Signal(); var release = Signal(); var finished = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, update, token) =>
            {
                started.TrySetResult(); await release.Task.WaitAsync(token);
                await update(new("node-1", "手动翻译完成", true), token); return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        var manual = Candidate(samples: false) with { ManualMode = mode };
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        coordinator.PauseThroughRevision(Connection, 1, "doc-1", 10, 1);
        coordinator.Configure(Settings with { BrowserChineseProseShareThresholdPercent = 70 }, 10);
        Assert.True(coordinator.IsCurrent(manual));
        release.TrySetResult(); await Wait(finished.Task);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-still-paused")));
    }

    [Fact]
    public async Task ThresholdChangeRecalculatesTheSameRevisionAndRejectsPreparedOldJudgement()
    {
        var completed = 0;
        var thresholds = new ConcurrentQueue<int>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, settings, _) =>
        {
            thresholds.Enqueue(settings.BrowserChineseProseShareThresholdPercent);
            return Task.FromResult(Assessment);
        }, (_, _, _, _) => Task.FromResult(Result), (_, _) => Task.FromResult(true), (_, packet, _) =>
        { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate())); await EventuallyAsync(() => completed == 1);
        coordinator.Configure(Settings with { BrowserChineseProseShareThresholdPercent = 70 }, 10);
        Assert.False(coordinator.TryEnqueue(Candidate("old-prepared"), Assessment, 50));
        Assert.False(coordinator.TryEnqueue(Candidate("old-prepared-default"), Assessment));
        Assert.False(coordinator.TryEnqueue(Candidate("no-new-samples", samples: false)));
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("new-threshold")));
        await EventuallyAsync(() => completed == 2);
        Assert.Equal(new[] { 50, 70 }, thresholds.ToArray());
    }

    [Fact]
    public async Task ThresholdChangeCancelsAnInFlightOldJudgementBeforeItCanPopulateTheSession()
    {
        var started = Signal(); var canceled = Signal(); var finished = Signal(); var judges = 0;
        await using var coordinator = new BrowserPageAutoCoordinator(async (_, settings, token) =>
        {
            Interlocked.Increment(ref judges);
            if (settings.BrowserChineseProseShareThresholdPercent == 50)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Assessment with { NeedsTranslation = false };
            }
            return Assessment;
        }, (_, _, _, _) => Task.FromResult(Result), (_, _) => Task.FromResult(true), (candidate, packet, _) =>
        { if (candidate.RequestId == "new" && packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        var old = Candidate(); Assert.True(coordinator.TryEnqueue(old)); await Wait(started.Task);
        coordinator.Configure(Settings with { BrowserChineseProseShareThresholdPercent = 70 }, 10);
        Assert.False(coordinator.IsCurrent(old)); await Wait(canceled.Task);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("new")));
        await Wait(finished.Task); Assert.Equal(2, judges);
    }

    [Fact]
    public async Task ThresholdChangeKeepsExistingAutomaticConfigurationFailure()
    {
        var finished = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => Task.FromException<BrowserPageTranslationResult>(
                new ScreenTranslator.Core.Translation.TranslationAuthenticationException("existing failure")),
            (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate())); await Wait(finished.Task);
        coordinator.Configure(Settings with { BrowserChineseProseShareThresholdPercent = 70 }, 10);
        Assert.False(coordinator.TryEnqueue(Candidate("still-failed")));
        Assert.Equal("api_key_error", coordinator.RejectionReason(Candidate("still-failed")));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    public void NonChineseProseCountsStayWithinTheBoundedWholePageTotal(int foreign, bool expected)
    {
        var candidate = Candidate() with { Statistics = new(TotalCharacters: 100, HanProseCharacters: 40,
            NonChineseProseCharacters: foreign) };
        Assert.Equal(expected, BrowserPageAutoCoordinator.IsBounded(candidate));
    }

    [Fact]
    public async Task Blacklist_Prevents_Automatic_Work_And_Still_Allows_An_Explicit_Selection()
    {
        var sent = Signal(); var translations = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => { Interlocked.Increment(ref translations); return Task.FromResult(Result); },
            (_, _) => Task.FromResult(true), (_, packet, _) => { if (packet.Finished) sent.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings with { BrowserTranslationBlacklist = ["example.com"] }, 10);
        var candidate = Candidate() with { SiteHost = "news.example.com" };
        Assert.False(coordinator.TryEnqueue(candidate)); Assert.Equal("site_blocked", coordinator.RejectionReason(candidate));
        Assert.True(coordinator.TryEnqueue(candidate with { ManualMode = "selection", Samples = null }));
        await Wait(sent.Task); Assert.Equal(1, translations);
    }

    [Fact]
    public async Task Changing_An_Unrelated_Site_Or_Concurrency_Preserves_An_Active_Manual_Stream()
    {
        var started = Signal(); var release = Signal(); var complete = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); return Result; },
            (_, _) => Task.FromResult(true), (_, packet, _) => { if (packet.Finished) complete.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        var candidate = Candidate() with { ManualMode = "selection", SiteHost = "reader.example.com", Samples = null };
        Assert.True(coordinator.TryEnqueue(candidate)); await Wait(started.Task);
        coordinator.Configure(Settings with { BrowserTranslationBlacklist = ["other.org"], BrowserPageTranslationConcurrency = 3 }, 10);
        Assert.True(coordinator.IsCurrent(candidate)); release.TrySetResult(); await Wait(complete.Task);
    }

    [Fact]
    public async Task Blacklisting_The_Current_Host_Cancels_Only_Its_Automatic_Stream()
    {
        var started = Signal(); var cancelled = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) => {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, _, _) => Task.CompletedTask);
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate() with { SiteHost = "reader.example.com" })); await Wait(started.Task);
        coordinator.Configure(Settings with { BrowserTranslationBlacklist = ["example.com"] }, 10); await Wait(cancelled.Task);
    }

    [Fact]
    public async Task NextBatchCanBeAdmittedWhileTheFinishedPacketIsBeingDelivered()
    {
        var finished = Signal(); var admitted = false;
        BrowserPageAutoCoordinator? instance = null;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => Task.FromResult(Result), (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                if (packet.Finished && candidate.RequestId == "first")
                {
                    admitted = instance!.TryEnqueue(Candidate("second", samples: false));
                    finished.TrySetResult();
                }
                return Task.CompletedTask;
            });
        instance = coordinator; coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate("first"))); await Wait(finished.Task);
        Assert.True(admitted);
    }

    [Fact]
    public async Task PreparedPageIsRecognizedWhileAllTranslationWorkersAreOccupied()
    {
        var occupied = Signal(); var release = Signal(); var recognized = Signal(); var started = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            { if (Interlocked.Increment(ref started) == BrowserPageAutoCoordinator.MaximumActiveCandidates) occupied.TrySetResult();
                await release.Task.WaitAsync(token); return Result; }, (_, _) => Task.FromResult(true),
            (candidate, packet, _) => { if (candidate.TabId == 20 && packet.Assessment is not null) recognized.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        for (var tab = 1; tab <= BrowserPageAutoCoordinator.MaximumActiveCandidates; tab++)
            Assert.True(coordinator.TryEnqueue(Candidate("busy-" + tab, tab: tab, document: "doc-" + tab)));
        await Wait(occupied.Task);
        var page = Candidate("new-page", tab: 20, document: "new-document");
        Assert.True(coordinator.TryEnqueue(page, Assessment));
        await coordinator.AnnounceAssessmentAsync(page, Assessment, default); await Wait(recognized.Task);
        Assert.False(release.Task.IsCompleted); release.TrySetResult();
    }

    [Fact]
    public async Task ATransientFailureDoesNotPermanentlyDisableAutomaticRequests()
    {
        var finished = Signal(); var failed = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => Task.FromException<BrowserPageTranslationResult>(new ScreenTranslator.Core.Translation.TranslationUnavailableException("temporary network outage")),
            (_, _) => Task.FromResult(true), (_, packet, _) => { if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; },
            _ => failed.TrySetResult());
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(finished.Task); await Wait(failed.Task);
        Assert.True(coordinator.TryEnqueue(Candidate("recovered", samples: false)));
    }

    [Fact]
    public async Task NewPageIsAssessedWhileAnEarlierTabStillTranslates()
    {
        var oldStarted = Signal(); var release = Signal(); var newAssessed = Signal();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (candidate, _, _, token) =>
            {
                if (candidate.TabId == 1) { oldStarted.TrySetResult(); await release.Task.WaitAsync(token); }
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                if (candidate.TabId == 2 && packet.Assessment is not null) newAssessed.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate())); await Wait(oldStarted.Task);
        Assert.True(coordinator.TryEnqueue(Candidate("new-page", tab: 2, document: "new-document")));
        await Wait(newAssessed.Task);
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public async Task ExplicitManualRequestRunsWithAutomaticTranslationDisabledAndNoPageSamples(string mode)
    {
        var started = Signal(); var release = Signal(); var finished = Signal();
        var judges = 0; var checks = 0;
        var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(Assessment); }, async (_, _, update, token) =>
        {
            started.TrySetResult(); await release.Task.WaitAsync(token);
            await update(new("node-1", "手动译文", true), token); return Result;
        }, (_, _) => { Interlocked.Increment(ref checks); return Task.FromResult(true); }, (_, packet, _) =>
        { packets.Enqueue(packet); if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(new(), 10);
        var manual = Candidate(samples: false) with { ManualMode = mode };
        Assert.False(coordinator.TryEnqueue(Candidate("automatic")));
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        Assert.True(coordinator.IsCurrent(manual));
        Assert.False(coordinator.IsCurrent(manual with { ManualMode = mode == "page" ? "selection" : "page" }));
        Assert.False(coordinator.IsCurrent(manual with { WindowId = 6 }));
        Assert.False(coordinator.IsCurrent(manual with { Revision = 2 }));
        Assert.False(coordinator.IsCurrent(manual with { Generation = 11 }));
        Assert.False(coordinator.IsCurrent(manual with { TabId = 2 }));
        Assert.False(coordinator.IsCurrent(manual with { ConnectionId = Guid.NewGuid() }));
        Assert.False(coordinator.IsCurrent(manual with { RequestId = "other" }));
        Assert.False(coordinator.IsCurrent(manual with { DocumentToken = "other" }));
        release.TrySetResult(); await Wait(finished.Task);
        Assert.Equal(0, judges); Assert.Equal(1, checks);
        var assessment = Assert.Single(packets, packet => packet.Assessment is not null).Assessment!;
        Assert.True(assessment.NeedsTranslation); Assert.Equal("manual-" + mode, assessment.ReasonCode);
        Assert.Contains(packets, packet => packet.Updates?.Single().Translation == "手动译文");
        Assert.Contains(packets, packet => packet.Finished && packet.Error is null);
    }

    [Theory]
    [InlineData("mixed-target-body", "selection")]
    [InlineData("too-little-text", "page")]
    [InlineData("model-veto", "selection")]
    public async Task ManualRequestBypassesPageVetoWithoutReplacingTheCachedAutomaticAssessment(string reason, string mode)
    {
        var judges = 0; var translations = 0; var completed = 0;
        var veto = new PageTranslationAssessment(false, "keep", .99, "zh", reason == "mixed-target-body")
            { ReasonCode = reason };
        var packets = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(veto); }, (_, _, _, _) =>
        { Interlocked.Increment(ref translations); return Task.FromResult(Result); }, (_, _) => Task.FromResult(true),
            (candidate, packet, _) =>
            {
                packets.Enqueue((candidate, packet));
                if (packet.Finished) Interlocked.Increment(ref completed);
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await EventuallyAsync(() => completed == 1);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("manual") with
        {
            ManualMode = mode, Samples = [new("The manual selection has different samples.")],
            Statistics = new(TotalCharacters: 50, LatinCharacters: 50)
        }));
        await EventuallyAsync(() => completed == 2);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("automatic-after", samples: false)));
        await EventuallyAsync(() => completed == 3);
        Assert.Equal(1, judges); Assert.Equal(1, translations);
        Assert.Contains(packets, item => item.Candidate.RequestId == "manual" && item.Packet.Assessment?.NeedsTranslation == true);
        Assert.Contains(packets, item => item.Candidate.RequestId == "automatic-after" &&
            item.Packet.Assessment is { NeedsTranslation: false } assessment && assessment.ReasonCode == reason);
    }

    [Fact]
    public async Task FirstManualSelectionCannotSupplyAnAutomaticAssessmentOrSkipAutomaticSamples()
    {
        var judges = 0; var translations = 0; var completed = 0;
        var veto = new PageTranslationAssessment(false, "keep", 1, "zh", true) { ReasonCode = "mixed-target-body" };
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(veto); }, (_, _, _, _) =>
        { Interlocked.Increment(ref translations); return Task.FromResult(Result); }, (_, _) => Task.FromResult(true),
            (_, packet, _) => { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate(samples: false) with { ManualMode = "selection" }));
        await EventuallyAsync(() => completed == 1);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-without-samples", samples: false)));
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("automatic-with-samples")));
        await EventuallyAsync(() => completed == 2);
        Assert.Equal(1, judges); Assert.Equal(1, translations);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public async Task AutomaticPausePreservesAnActiveManualRequestAndStaysPausedAfterItsNewRevision(string mode)
    {
        var started = Signal(); var release = Signal(); var completed = 0; var judges = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(Assessment); }, async (candidate, _, _, token) =>
        {
            if (candidate.ManualMode is not null) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Result;
        }, (_, _) => Task.FromResult(true), (_, packet, _) =>
        { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await EventuallyAsync(() => completed == 1);
        coordinator.PauseThroughRevision(Connection, 1, "doc-1", 10, 1);
        var manual = Candidate("manual", revision: 2, samples: false) with { ManualMode = mode };
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        coordinator.Cancel(Connection, 1, "doc-1", 10, 2, "manual", pause: true);
        coordinator.Cancel(Connection, 1, "doc-1", 10, 2);
        coordinator.PauseThroughRevision(Connection, 1, "doc-1", 10, 3);
        Assert.True(coordinator.IsCurrent(manual));
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-revision", revision: 3)));
        Assert.True(coordinator.IsCurrent(manual));
        release.TrySetResult(); await EventuallyAsync(() => completed == 2);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-paused", revision: 2, samples: false)));
        coordinator.Resume(Connection, 1, "doc-1", 10, 2);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("automatic-resumed", revision: 2, samples: false)));
        await EventuallyAsync(() => completed == 3); Assert.Equal(1, judges);
    }

    [Theory]
    [InlineData("selection", false)]
    [InlineData("page", true)]
    public async Task MatchingExplicitCancelAndRealNavigationCancelManualProviders(string mode, bool navigation)
    {
        var started = Signal(); var canceled = Signal(); var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) => { packets.Enqueue(packet); return Task.CompletedTask; });
        coordinator.Configure(new(), 10);
        var manual = Candidate(samples: false) with { ManualMode = mode };
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        coordinator.Cancel(Connection, 1, "doc-1", 10, 1, "wrong-request");
        Assert.True(coordinator.IsCurrent(manual));
        if (navigation) coordinator.DocumentInvalidated(Connection, 1, "doc-1");
        else coordinator.Cancel(Connection, 1, "doc-1", 10, 1, manual.RequestId);
        await Wait(canceled.Task); Assert.False(coordinator.IsCurrent(manual));
        Assert.DoesNotContain(packets, packet => packet.Finished || packet.Updates is not null);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public async Task InactiveManualRequestCannotTranslateOrSendAssessment(string mode)
    {
        var verified = Signal(); var judges = 0; var translations = 0; var sends = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(Assessment); }, (_, _, _, _) =>
        { Interlocked.Increment(ref translations); return Task.FromResult(Result); },
            (_, _) => { verified.TrySetResult(); return Task.FromResult(false); },
            (_, packet, _) => {
                Assert.True(packet.Finished); Assert.Equal("page-unavailable", packet.Error);
                Assert.Null(packet.Assessment); Assert.Null(packet.Updates);
                Interlocked.Increment(ref sends); return Task.CompletedTask;
            });
        coordinator.Configure(new(), 10);
        Assert.True(coordinator.TryEnqueue(Candidate(samples: false) with { ManualMode = mode }));
        await Wait(verified.Task);
        await EventuallyAsync(() => sends == 1);
        Assert.Equal(0, judges); Assert.Equal(0, translations);
    }

    [Fact]
    public async Task ManualFailureDoesNotBlockAutomaticRetriesOrFurtherManualRequests()
    {
        var completed = 0; var failures = 0; var judges = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judges); return Task.FromResult(Assessment); }, (candidate, _, _, _) =>
            candidate.ManualMode == "selection" ? Task.FromException<BrowserPageTranslationResult>(new InvalidOperationException("manual failure"))
                : Task.FromResult(Result), (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; },
            _ => Interlocked.Increment(ref failures));
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await EventuallyAsync(() => completed == 1);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("manual-failed", samples: false) with { ManualMode = "selection" }));
        await EventuallyAsync(() => completed == 2 && failures == 1);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("automatic-after", samples: false)));
        await EventuallyAsync(() => completed == 3);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("manual-page", samples: false) with { ManualMode = "page" }));
        await EventuallyAsync(() => completed == 4); Assert.Equal(1, judges);
    }

    [Fact]
    public async Task ExplicitManualRequestCanRunAfterAutomaticFailureWithoutClearingThatFailure()
    {
        var completed = 0; var failures = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (candidate, _, _, _) => candidate.ManualMode is null
                ? Task.FromException<BrowserPageTranslationResult>(new ScreenTranslator.Core.Translation.TranslationAuthenticationException("automatic failure"))
                : Task.FromResult(Result), (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; },
            _ => Interlocked.Increment(ref failures));
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await EventuallyAsync(() => completed == 1 && failures == 1);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("manual", revision: 2, samples: false) with { ManualMode = "page" }));
        await EventuallyAsync(() => completed == 2);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-after", revision: 2, samples: false)));
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("page")]
    public async Task AutomaticSettingSwitchesPreserveAnActiveManualStreamAndItsPauseState(string mode)
    {
        var started = Signal(); var release = Signal(); var finished = Signal();
        var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, update, token) =>
            {
                started.TrySetResult(); await release.Task.WaitAsync(token);
                await update(new("node-1", "完成手动翻译", true), token); return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) =>
            { packets.Enqueue(packet); if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        var enabled = Settings with { BrowserAutoImageTranslationEnabled = true };
        coordinator.Configure(enabled, 10);
        var manual = Candidate(samples: false) with { ManualMode = mode };
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        coordinator.PauseThroughRevision(Connection, 1, "doc-1", 10, 1);
        coordinator.Configure(enabled with { BrowserAutoPageTranslationEnabled = false, BrowserAutoImageTranslationEnabled = false }, 10);
        Assert.True(coordinator.IsCurrent(manual));
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-disabled", tab: 2)));
        coordinator.Configure(enabled, 10); Assert.True(coordinator.IsCurrent(manual));
        release.TrySetResult(); await Wait(finished.Task);
        Assert.Contains(packets, packet => packet.Updates?.Any(update => update.IsComplete) == true);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-still-paused")));
    }

    [Fact]
    public async Task DisablingAutomaticTranslationCancelsAutomaticWorkAndKeepsQueuedManualWorkOnLatestSettings()
    {
        var started = Signal(); var canceled = Signal(); var manualStarted = Signal(); var releaseManual = Signal(); var manualFinished = Signal();
        var automaticStarted = 0;
        AppSettings? manualSettings = null;
        var packets = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (candidate, settings, _, token) =>
            {
                if (candidate.ManualMode is null)
                {
                    if (Interlocked.Increment(ref automaticStarted) == BrowserPageAutoCoordinator.MaximumActiveCandidates) started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); }
                }
                else
                {
                    manualSettings = settings; manualStarted.TrySetResult();
                    await releaseManual.Task.WaitAsync(token);
                }
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                packets.Enqueue((candidate, packet));
                if (candidate.ManualMode is not null && packet.Finished) manualFinished.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10);
        for (var tab = 1; tab <= BrowserPageAutoCoordinator.MaximumActiveCandidates; tab++)
            Assert.True(coordinator.TryEnqueue(Candidate("automatic-" + tab, tab: tab)));
        await Wait(started.Task);
        var manual = Candidate("manual-queued", tab: 5, samples: false) with { ManualMode = "page" };
        Assert.True(coordinator.TryEnqueue(manual));
        var disabled = Settings with { BrowserAutoPageTranslationEnabled = false };
        coordinator.Configure(disabled, 10);
        Assert.True(coordinator.IsCurrent(manual));
        await Wait(canceled.Task); await Wait(manualStarted.Task);
        Assert.Same(disabled, manualSettings);
        Assert.True(coordinator.IsCurrent(manual));
        releaseManual.TrySetResult(); await Wait(manualFinished.Task);
        Assert.DoesNotContain(packets, item => item.Candidate.ManualMode is null &&
            (item.Packet.Finished || item.Packet.Updates is not null));
    }

    [Fact]
    public async Task GenuineTranslationSettingChangeCancelsManualWorkAndRejectsItsOldGeneration()
    {
        var started = Signal(); var canceled = Signal(); var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) => { packets.Enqueue(packet); return Task.CompletedTask; });
        coordinator.Configure(new(), 10);
        var manual = Candidate(samples: false) with { ManualMode = "page" };
        Assert.True(coordinator.TryEnqueue(manual)); await Wait(started.Task);
        coordinator.Configure(new() { TargetLanguage = "fr" }, 11); await Wait(canceled.Task);
        Assert.False(coordinator.IsCurrent(manual)); Assert.False(coordinator.TryEnqueue(manual with { RequestId = "old-generation" }));
        Assert.DoesNotContain(packets, packet => packet.Finished || packet.Updates is not null);
    }

    [Fact]
    public async Task ExplicitCancelAllRequestsCancelsManualAndQueuedWorkWithoutChangingTheConfiguration()
    {
        var started = Signal(); var canceled = Signal(); var newFinished = Signal(); var calls = 0;
        var packets = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (candidate, _, _, token) =>
            {
                Interlocked.Increment(ref calls);
                if (candidate.RequestId != "new-request")
                {
                    if (Volatile.Read(ref calls) == BrowserPageAutoCoordinator.MaximumActiveCandidates) started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); }
                }
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                packets.Enqueue((candidate, packet));
                if (candidate.RequestId == "new-request" && packet.Finished) newFinished.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(new(), 10);
        var manual = Candidate(samples: false) with { ManualMode = "page" };
        Assert.True(coordinator.TryEnqueue(manual));
        for (var tab = 2; tab <= BrowserPageAutoCoordinator.MaximumActiveCandidates; tab++)
            Assert.True(coordinator.TryEnqueue(Candidate("blocking-" + tab, tab: tab, samples: false) with { ManualMode = "page" }));
        await Wait(started.Task);
        Assert.True(coordinator.TryEnqueue(Candidate("queued", tab: 5, samples: false) with { ManualMode = "selection" }));
        coordinator.CancelAllRequests();
        Assert.False(coordinator.IsCurrent(manual)); await Wait(canceled.Task);
        Assert.True(coordinator.TryEnqueue(manual with { RequestId = "new-request" })); await Wait(newFinished.Task);
        Assert.Equal(BrowserPageAutoCoordinator.MaximumActiveCandidates + 1, calls);
        Assert.DoesNotContain(packets, item => item.Candidate.RequestId != "new-request" &&
            (item.Packet.Finished || item.Packet.Updates is not null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("selection")]
    [InlineData("page")]
    public void NativeCandidatesCannotBypassSourceGroupMetadataBounds(string? mode)
    {
        var block = new BrowserPageTextBlock("node-1", "Hi😀", SourceGroupHash: new string('a', 64),
            SourceGroupLength: 6, SourceStart: 1, SourceLength: 4);
        var candidate = Candidate() with { ManualMode = mode, Blocks = [block] };
        Assert.True(BrowserPageAutoCoordinator.IsBounded(candidate));
        foreach (var invalid in new[]
        {
            block with { SourceGroupHash = null },
            block with { SourceGroupLength = null },
            block with { SourceStart = null },
            block with { SourceLength = null },
            block with { SourceGroupHash = new string('x', 64) },
            block with { SourceGroupHash = new string('a', 63) },
            block with { SourceGroupLength = 1_000_001 },
            block with { SourceGroupLength = -1 },
            block with { SourceStart = -1 },
            block with { SourceStart = 3 },
            block with { SourceLength = 3 },
            block with { SourceLength = -1 },
            block with { SourceStart = int.MaxValue }
        }) Assert.False(BrowserPageAutoCoordinator.IsBounded(candidate with { Blocks = [invalid] }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("source", true)]
    [InlineData("selection", true)]
    [InlineData("", false)]
    [InlineData("Source", false)]
    [InlineData("page", false)]
    [InlineData(" source", false)]
    public void NativeCandidatesRequireAnAllowedContextKind(string? contextKind, bool expected)
    {
        foreach (var mode in new string?[] { null, "selection", "page" })
        {
            var block = Candidate().Blocks[0] with { ContextKind = contextKind };
            Assert.Equal(expected, BrowserPageAutoCoordinator.IsBounded(Candidate() with { ManualMode = mode, Blocks = [block] }));
        }
    }

    [Fact]
    public async Task SwitchingAwayAfterAdmissionKeepsTheStreamAndDoesNotQueryForegroundForEachFrame()
    {
        var started = Signal(); var release = Signal(); var finished = Signal();
        var active = true; var checks = 0; var judgeCalls = 0;
        var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { judgeCalls++; return Task.FromResult(Assessment); }, async (_, _, update, token) =>
        {
            await update(new("node-1", "已译", false), token);
            started.TrySetResult(); await release.Task.WaitAsync(token);
            await update(new("node-1", "已经翻译的文字", true), token);
            return Result;
        }, (_, _) => { checks++; return Task.FromResult(active); }, (_, packet, _) =>
        { packets.Enqueue(packet); if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(started.Task); active = false; release.TrySetResult(); await Wait(finished.Task);
        Assert.Equal(1, checks); Assert.Equal(1, judgeCalls);
        Assert.Contains(packets, packet => packet.Updates?.Any(update => update.IsComplete) == true);
        Assert.Contains(packets, packet => packet.Finished && packet.Error is null);
    }

    [Fact]
    public async Task MoreThanFourTabsRetainTheirAssessmentWhenReturningToSameDocument()
    {
        var judges = 0; var completed = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { judges++; return Task.FromResult(Assessment); }, (_, _, _, _) => Task.FromResult(Result),
            (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        for (var tab = 1; tab <= 12; tab++)
        {
            Assert.True(coordinator.TryEnqueue(Candidate("first-" + tab, tab: tab)));
            await EventuallyAsync(() => completed == tab);
        }
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("return", tab: 1, samples: false)));
        await EventuallyAsync(() => completed == 13);
        Assert.Equal(12, judges);
    }

    [Fact]
    public async Task NodeRebuildRevisionReusesDecisionOnlyWhenSampleLanguageAndStatisticsAreUnchanged()
    {
        var judges = 0; var completed = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { judges++; return Task.FromResult(Assessment); }, (_, _, _, _) => Task.FromResult(Result),
            (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished) Interlocked.Increment(ref completed); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate())); await EventuallyAsync(() => completed == 1);
        Assert.True(coordinator.TryEnqueue(Candidate("rebuild", 2) with { Blocks = [new("new-node", Candidate().Blocks[0].Text)] }));
        await EventuallyAsync(() => completed == 2); Assert.Equal(1, judges);
        Assert.True(coordinator.TryEnqueue(Candidate("source-change", 3) with { Samples = [new("A genuinely different source paragraph.")] }));
        await EventuallyAsync(() => completed == 3); Assert.Equal(2, judges);
        Assert.True(coordinator.TryEnqueue(Candidate("statistics-change", 4) with { Statistics = new(TotalCharacters: 100, LatinCharacters: 100) }));
        await EventuallyAsync(() => completed == 4); Assert.Equal(3, judges);
        Assert.True(coordinator.TryEnqueue(Candidate("language-change", 5) with { DocumentLanguage = "ja" }));
        await EventuallyAsync(() => completed == 5); Assert.Equal(4, judges);
    }

    [Fact]
    public async Task ReusesSameRevisionDecisionAndStreamsOnlyCoalescedLatestNodes()
    {
        var judgeCalls = 0;
        var finished = Signal();
        var packets = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator(
            (_, _, _) => { Interlocked.Increment(ref judgeCalls); return Task.FromResult(Assessment); },
            async (_, _, update, token) =>
            {
                for (var i = 1; i <= 300; i++) await update(new("node-1", new string('中', i), i == 300), token);
                return Result;
            }, (_, _) => Task.FromResult(true),
            (candidate, packet, _) =>
            {
                packets.Enqueue((candidate, packet));
                if (packet.Finished) finished.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(finished.Task);
        var replacements = packets.Where(item => item.Packet.Updates is not null).ToArray();
        Assert.Single(replacements);
        Assert.Equal(300, Assert.Single(replacements[0].Packet.Updates!).Translation.Length);
        Assert.True(replacements[0].Packet.Updates![0].IsComplete);
        finished = Signal();
        // The finished send occurs just before releasing the session's request slot.
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("request-2", samples: false)));
        await Wait(finished.Task);
        Assert.Equal(1, judgeCalls);
        Assert.Equal(2, packets.Count(item => item.Packet.Finished));
    }

    [Fact]
    public async Task NewRevisionCancelsOldProviderAndSuppressesAllLateOutput()
    {
        var started = Signal(); var canceled = Signal(); var finished = Signal();
        var output = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (candidate, _, update, token) =>
            {
                if (candidate.Revision == 1)
                {
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); }
                    // A misbehaving late provider callback must not reach the bridge.
                    try { await update(new("node-1", "stale", true), token); }
                    catch (OperationCanceledException) { }
                    return Result;
                }
                await update(new("node-1", "当前版本", true), token);
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                output.Enqueue((candidate, packet));
                if (candidate.Revision == 2 && packet.Finished) finished.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(started.Task);
        Assert.True(coordinator.TryEnqueue(Candidate("new", 2)));
        await Wait(canceled.Task); await Wait(finished.Task);
        Assert.DoesNotContain(output, item => item.Candidate.Revision == 1 &&
            (item.Packet.Updates is not null || item.Packet.Finished));
        Assert.Contains(output, item => item.Candidate.Revision == 2 && item.Packet.Updates?[0].Translation == "当前版本");
    }

    [Fact]
    public async Task ReconfigurationCancelsOldSnapshotBeforeNewGenerationRuns()
    {
        var started = Signal(); var canceled = Signal(); var finished = Signal();
        var output = new ConcurrentQueue<(AutoPageCandidate Candidate, AutoPagePacket Packet)>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (candidate, _, _, token) =>
            {
                if (candidate.Generation == 10)
                {
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); }
                }
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            {
                output.Enqueue((candidate, packet));
                if (candidate.Generation == 11 && packet.Finished) finished.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(started.Task); coordinator.Configure(Settings, 11);
        Assert.False(coordinator.TryEnqueue(Candidate("stale")));
        Assert.True(coordinator.TryEnqueue(Candidate("fresh", generation: 11)));
        await Wait(canceled.Task); await Wait(finished.Task);
        Assert.DoesNotContain(output, item => item.Candidate.Generation == 10 && item.Packet.Finished);
    }

    [Fact]
    public async Task WrongRequestCancelCannotInterruptCurrentBatchAndPauseCanResume()
    {
        var started = Signal(); var canceled = Signal(); var resumed = Signal();
        var judgeCalls = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { Interlocked.Increment(ref judgeCalls); return Task.FromResult(Assessment); },
            async (candidate, _, _, token) =>
            {
                if (candidate.RequestId == "request-1")
                {
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); }
                }
                return Result;
            }, (_, _) => Task.FromResult(true), (candidate, packet, _) =>
            { if (candidate.RequestId == "request-2" && packet.Finished) resumed.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(started.Task);
        coordinator.Cancel(Connection, 1, "doc-1", 10, 1, "different-request");
        Assert.True(coordinator.IsCurrent(Candidate()));
        coordinator.PauseThroughRevision(Connection, 1, "doc-1", 10, 2);
        await Wait(canceled.Task);
        Assert.False(coordinator.TryEnqueue(Candidate("request-2", samples: false)));
        coordinator.Resume(Connection, 1, "doc-1", 10, 1);
        await EventuallyAsync(() => coordinator.TryEnqueue(Candidate("request-2", samples: false)));
        await Wait(resumed.Task); Assert.Equal(1, judgeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NavigationAndConnectionLossCancelProviderWithoutLateFrames(bool navigation)
    {
        var started = Signal(); var canceled = Signal(); var output = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) => { output.Enqueue(packet); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(started.Task);
        if (navigation) coordinator.DocumentInvalidated(Connection, 1, "doc-1"); else coordinator.ConnectionClosed(Connection);
        await Wait(canceled.Task);
        Assert.False(coordinator.IsCurrent(Candidate()));
        Assert.DoesNotContain(output, packet => packet.Finished || packet.Updates is not null);
    }

    [Fact]
    public async Task InactiveTabCannotRunLocalJudgementOrModelUpload()
    {
        var verified = Signal(); var judges = 0; var translations = 0; var sends = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) =>
        { judges++; return Task.FromResult(Assessment); }, (_, _, _, _) => { translations++; return Task.FromResult(Result); },
            (_, _) => { verified.TrySetResult(); return Task.FromResult(false); },
            (_, packet, _) => { Assert.True(packet.Finished); Assert.Null(packet.Assessment); Assert.Null(packet.Updates); sends++; return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(verified.Task);
        await EventuallyAsync(() => sends == 1);
        Assert.Equal(0, judges); Assert.Equal(0, translations);
    }

    [Fact]
    public async Task FailureFinishesBatchAndBlocksAutomaticRetryUntilExplicitResume()
    {
        var finished = Signal(); var failures = 0;
        var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, update, token) =>
            {
                await update(new("node-1", "prefix", false), token);
                await update(new("node-1", string.Empty, false, Failed: true), token);
                throw new ScreenTranslator.Core.Translation.TranslationAuthenticationException("simulated authentication failure");
            }, (_, _) => Task.FromResult(true), (_, packet, _) =>
            { packets.Enqueue(packet); if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; },
            _ => Interlocked.Increment(ref failures));
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(finished.Task);
        await EventuallyAsync(() => failures == 1);
        Assert.False(coordinator.TryEnqueue(Candidate("automatic-retry", samples: false)));
        Assert.Contains(packets, packet => packet.Finished && packet.Error == "api_key_error");
        Assert.Contains(packets, packet => packet.Updates?.Any(update => update.Failed && update.Translation == "") == true);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    [InlineData(System.Net.HttpStatusCode.OK)]
    public async Task Unsupported_Fast_Mode_Pauses_Automatic_Requests_And_Changing_The_Setting_Resumes_Them(System.Net.HttpStatusCode status)
    {
        var rejected = Signal(); var completed = Signal(); var calls = 0;
        var settings = Settings with { TranslationProvider = ScreenTranslator.Core.Translation.TranslationProviderKind.OpenAI,
            OpenAiAuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth, ChatGptFastModeEnabled = true };
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, active, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return active.ChatGptFastModeEnabled
                    ? Task.FromException<BrowserPageTranslationResult>(new ScreenTranslator.Core.Authentication.ChatGptException(
                        "Fast tier is not supported", "subscription_sharing_unsupported_capability", status, parameter: "service_tier"))
                    : Task.FromResult(Result);
            }, (_, _) => Task.FromResult(true), (_, packet, _) =>
            {
                if (packet.Finished && packet.Error == "api_configuration") rejected.TrySetResult();
                else if (packet.Finished && packet.Error is null) completed.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate())); await Wait(rejected.Task);
        Assert.False(coordinator.TryEnqueue(Candidate("same-fast-mode", samples: false)));
        Assert.Equal(1, calls);
        coordinator.Configure(settings with { ChatGptFastModeEnabled = false }, 11);
        Assert.True(coordinator.TryEnqueue(Candidate("fast-mode-disabled", generation: 11))); await Wait(completed.Task);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Temporary_Fast_Mode_Unavailability_Does_Not_Permanently_Pause_The_Page()
    {
        var rejected = Signal(); var completed = Signal(); var calls = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<BrowserPageTranslationResult>(new ScreenTranslator.Core.Authentication.ChatGptException(
                    "Temporarily unavailable", "subscription_sharing_usage_unavailable", System.Net.HttpStatusCode.ServiceUnavailable, parameter: "service_tier"))
                : Task.FromResult(Result), (_, _) => Task.FromResult(true), (_, packet, _) =>
            {
                if (packet.Finished && packet.Error is not null) rejected.TrySetResult();
                else if (packet.Finished) completed.TrySetResult();
                return Task.CompletedTask;
            });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate())); await Wait(rejected.Task);
        Assert.True(coordinator.TryEnqueue(Candidate("retry-after-temporary-failure", samples: false))); await Wait(completed.Task);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PageWorkersAreBoundedAndDuplicateInflightSnapshotsAreRejected()
    {
        var firstStarted = Signal(); var release = Signal(); var allFinished = Signal();
        var active = 0; var peak = 0; var complete = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            {
                var running = Interlocked.Increment(ref active); lock (release) peak = Math.Max(peak, running);
                firstStarted.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref active); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) =>
            { if (packet.Finished && Interlocked.Increment(ref complete) == 4) allFinished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(firstStarted.Task);
        for (var tab = 2; tab <= 4; tab++) Assert.True(coordinator.TryEnqueue(Candidate("request-" + tab, tab: tab)));
        Assert.False(coordinator.TryEnqueue(Candidate("duplicate-inflight", samples: false)));
        await EventuallyAsync(() => Volatile.Read(ref active) == BrowserPageAutoCoordinator.MaximumActiveCandidates);
        release.TrySetResult(); await Wait(allFinished.Task);
        Assert.Equal(BrowserPageAutoCoordinator.MaximumActiveCandidates, peak);
    }

    [Fact]
    public async Task ImageOnlyAndMixedPagesNeverTranslateDomButKeepTheirAssessment()
    {
        var finished = Signal(); var translations = 0;
        var mixed = new PageTranslationAssessment(false, "mixed", .99, "zh", true) { ReasonCode = "mixed-target-body" };
        var packets = new ConcurrentQueue<AutoPagePacket>();
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(mixed),
            (_, _, _, _) => { translations++; return Task.FromResult(Result); }, (_, _) => Task.FromResult(true),
            (_, packet, _) => { packets.Enqueue(packet); if (packet.Finished) finished.TrySetResult(); return Task.CompletedTask; });
        coordinator.Configure(Settings, 10);
        Assert.True(coordinator.TryEnqueue(Candidate() with { Blocks = [], Samples = [] }));
        await Wait(finished.Task); Assert.Equal(0, translations);
        Assert.True(Assert.Single(packets, packet => packet.Assessment is not null).Assessment!.IsMixedTargetLanguage);
    }

    [Fact]
    public async Task DisabledFreshWithoutSamplesAndMalformedCandidatesAreRejected()
    {
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            (_, _, _, _) => Task.FromResult(Result), (_, _) => Task.FromResult(true), (_, _, _) => Task.CompletedTask);
        coordinator.Configure(new(), 10); Assert.False(coordinator.TryEnqueue(Candidate()));
        coordinator.Configure(Settings, 10); Assert.False(coordinator.TryEnqueue(Candidate(samples: false)));
        Assert.False(coordinator.TryEnqueue(Candidate() with { Blocks = [new("same", "a"), new("same", "b")] }));
        Assert.False(coordinator.TryEnqueue(Candidate() with { Samples = [new(new string('x', 12_001))] }));
        Assert.False(coordinator.TryEnqueue(Candidate() with { Statistics = new(10, HanCharacters: 20) }));
        Assert.False(coordinator.TryEnqueue(Candidate() with { ManualMode = "Page" }));
        Assert.False(coordinator.TryEnqueue(Candidate() with { ManualMode = string.Empty }));
        Assert.False(coordinator.TryEnqueue(Candidate(generation: 11) with { ManualMode = "selection" }));
    }

    [Fact]
    public async Task ShutdownCancelsActiveProviderAndDrainsQueuedScopesWithoutStartingModels()
    {
        var started = Signal(); var canceled = Signal(); var calls = 0;
        var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, _, token) =>
            {
                if (Interlocked.Increment(ref calls) == BrowserPageAutoCoordinator.MaximumActiveCandidates) started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, _, _) => Task.CompletedTask);
        coordinator.Configure(Settings, 10);
        for (var tab = 1; tab <= BrowserPageAutoCoordinator.MaximumActiveCandidates; tab++)
            Assert.True(coordinator.TryEnqueue(Candidate("active-" + tab, tab: tab)));
        await Wait(started.Task);
        for (var tab = BrowserPageAutoCoordinator.MaximumActiveCandidates + 1; tab <= BrowserPageAutoCoordinator.MaximumActiveCandidates + 3; tab++)
            Assert.True(coordinator.TryEnqueue(Candidate("queued-" + tab, tab: tab)));
        await coordinator.DisposeAsync(); await Wait(canceled.Task);
        Assert.Equal(BrowserPageAutoCoordinator.MaximumActiveCandidates, calls);
        Assert.False(coordinator.TryEnqueue(Candidate("closed")));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task LostBridgeWhileStreamingCancelsProviderInsteadOfContinuingUploads()
    {
        var canceled = Signal(); var calls = 0;
        await using var coordinator = new BrowserPageAutoCoordinator((_, _, _) => Task.FromResult(Assessment),
            async (_, _, update, token) =>
            {
                Interlocked.Increment(ref calls);
                await update(new("node-1", "正在翻译", false), token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); }
                return Result;
            }, (_, _) => Task.FromResult(true), (_, packet, _) => packet.Updates is null
                ? Task.CompletedTask : Task.FromException(new System.IO.IOException("bridge lost")));
        coordinator.Configure(Settings, 10); Assert.True(coordinator.TryEnqueue(Candidate()));
        await Wait(canceled.Task); Assert.Equal(1, calls);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }
}
