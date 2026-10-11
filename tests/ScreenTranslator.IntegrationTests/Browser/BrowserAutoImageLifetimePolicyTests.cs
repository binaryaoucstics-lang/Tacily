using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserAutoImageLifetimePolicyTests
{
    private static readonly AutoImageCandidate Candidate = new(Guid.NewGuid(), "request", "image", "document", 1, 7, 10, "preview");
    private static readonly BrowserImageReply Visible = new("reply", true, "image", "document", new(850, 1200),
        new(0, 0, 425, 600), new(0, 0, 425, 600), 1.25, new(1280, 720), 0) { AutoImageAllowed = true };
    private static BrowserAutoImageProbe Probe(BrowserImageReply? image = null) => new(true, 1, 7, "document", image ?? Visible);

    [Theory]
    [InlineData(160, 220, 1280, 720, false)]
    [InlineData(320, 240, 1280, 720, false)]
    [InlineData(400, 300, 800, 600, true)]
    [InlineData(400, 300, 1920, 1080, false)]
    [InlineData(425, 600, 3840, 2160, true)]
    [InlineData(600, 1800, 1280, 720, true)]
    [InlineData(200, 1000, 800, 600, false)]
    [InlineData(600, 800, 0, 720, false)]
    public void Initial_Admission_Uses_Rendered_Size_And_Viewport_Area(double width, double height, double viewportWidth, double viewportHeight, bool expected)
    {
        var image = Visible with { Display = new(0, 0, width, height), Clip = new(0, 0, Math.Min(width, viewportWidth), Math.Min(height, viewportHeight)),
            ViewportSize = new(viewportWidth, viewportHeight) };
        Assert.Equal(expected, BrowserAutoImageLifetimePolicy.CanStart(Candidate, Probe(image), false));
    }

    [Fact]
    public void Thumbnail_Collapse_Does_Not_Retire_An_Already_Admitted_Stream()
    {
        var thumbnail = Visible with { Display = new(0, 0, 160, 220), Clip = new(0, 0, 160, 220), AutoImageAllowed = false, AutoImageAdmitted = true };
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, Probe(thumbnail), false));
        Assert.True(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, Probe(thumbnail), false));
    }

    [Fact]
    public void OffscreenClipCannotAdmitNewUploadButDoesNotInvalidateAdmittedImage()
    {
        var offscreen = Probe(Visible with { Clip = new(0, 0, 0, 0), Display = new(0, -2000, 425, 600) });
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, offscreen, true));
        Assert.True(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, offscreen, true));
        Assert.True(BrowserAutoImageLifetimePolicy.CanStart(Candidate, Probe(), true));
    }

    [Fact]
    public void VisibilityOrPausedGateDoesNotAuthorizeInitialUpload()
    {
        var blocked = Probe(Visible with { AutoImageAllowed = false });
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, blocked, false));
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, blocked, true));
        Assert.False(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, blocked, true));
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, Probe(Visible with { Clip = new(0, 0, double.NaN, 40) }), false));
    }

    [Theory]
    [InlineData("active-document-not-found")]
    [InlineData("tab-changed")]
    [InlineData("window-changed")]
    [InlineData("navigation")]
    [InlineData("image-detached")]
    [InlineData("image-replaced")]
    [InlineData("image-document-changed")]
    [InlineData("full-page-paused")]
    public void TrueIdentityChangesCancelEvenWhenTheOldClipWasOffscreen(string change)
    {
        var probe = Probe(Visible with { Clip = new(0, 0, 0, 0) });
        probe = change switch
        {
            "active-document-not-found" => probe with { ActiveDocumentFound = false },
            "tab-changed" => probe with { TabId = 2 },
            "window-changed" => probe with { WindowId = 8 },
            "navigation" => probe with { DocumentToken = "new-document" },
            "image-detached" => probe with { Image = Visible with { Found = false } },
            "image-replaced" => probe with { Image = Visible with { ImageId = "new-image" } },
            "image-document-changed" => probe with { Image = Visible with { DocumentToken = "new-document" } },
            "full-page-paused" => probe with { Image = Visible with { AutoImageAllowed = false } },
            _ => throw new InvalidOperationException(),
        };
        Assert.False(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, probe, true));
    }

    [Theory]
    [InlineData("devicePixelRatioChanged", 0, false)]
    [InlineData("navigationStarted", 0, true)]
    [InlineData("activeTabChanged", 0, false)]
    [InlineData("browserWindowFocusChanged", 0, false)]
    [InlineData("documentUnloaded", 0, true)]
    [InlineData("documentUnloaded", 1, false)]
    [InlineData("unknownDocumentInvalidation", 0, true)]
    public void OnlyTopDocumentInvalidationsEndAutomaticJobsWhileDpiReflowRemainsLocal(string reason, int frame, bool cancel)
    {
        var changed = new BrowserInvalidated(7, 1, "document", 10, reason, frame);
        Assert.Equal(cancel, BrowserAutoImageLifetimePolicy.ShouldInvalidateAutomaticDocument(changed));
    }

    [Fact]
    public void InactiveIdentityRetainsAdmittedJobButDoesNotAdmitNewUpload()
    {
        var inactive = Probe(Visible with { AutoImageAllowed = false, AutoImageAdmitted = true });
        Assert.False(BrowserAutoImageLifetimePolicy.CanStart(Candidate, inactive, true));
        Assert.True(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, inactive, true));
        Assert.False(BrowserAutoImageLifetimePolicy.CanContinue(Candidate,
            Probe(Visible with { AutoImageAllowed = false, AutoImageAdmitted = false }), true));
    }

    [Fact]
    public async Task GuardCancelsPausedOperationOnRealIdentityInvalidationAndRunsOnlyOnce()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Probe(); var calls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var job = BrowserAutoImageLifetimePolicy.RunAsync(
            _ => Task.FromResult(BrowserAutoImageLifetimePolicy.CanContinue(Candidate, Volatile.Read(ref current), true)),
            async token =>
            {
                Interlocked.Increment(ref calls); started.TrySetResult(token);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return 1;
            }, deadline.Token, TimeSpan.FromMilliseconds(10));
        var jobToken = await started.Task.WaitAsync(deadline.Token);
        Volatile.Write(ref current, Probe(Visible with { Found = false }));
        await cancelled.Task.WaitAsync(deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.True(jobToken.IsCancellationRequested); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingCurrentIdentityNeverStartsOperationAndExternalScopeCancelStopsExistingJob()
    {
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrowserAutoImageLifetimePolicy.RunAsync(
            _ => Task.FromResult(false), _ => { calls++; return Task.FromResult(1); }, CancellationToken.None));
        Assert.Equal(0, calls);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new CancellationTokenSource();
        var job = BrowserAutoImageLifetimePolicy.RunAsync(_ => Task.FromResult(true), async token =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 1; }, scope.Token, TimeSpan.FromMilliseconds(10));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); scope.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
    }
}
