using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserAutoImageProbe(bool ActiveDocumentFound, int TabId, int WindowId,
    string? DocumentToken, BrowserImageReply? Image);

/// <summary>Visibility admits a new job. An admitted job follows image/document identity until completion.</summary>
public static class BrowserAutoImageLifetimePolicy
{
    public static bool ShouldInvalidateAutomaticDocument(BrowserInvalidated changed) =>
        changed.FrameId == 0 && changed.Reason is not ("devicePixelRatioChanged" or "activeTabChanged" or "browserWindowFocusChanged");

    public static bool CanStart(AutoImageCandidate candidate, BrowserAutoImageProbe probe, bool requirePageGate) =>
        CanContinue(candidate, probe, requirePageGate)
        && probe.Image!.AutoImageAllowed != false
        && IsLargeRenderedImage(probe.Image);

    // Keep this admission rule aligned with image-auto-policy.js. Once a job is
    // admitted, scrolling/reflow does not invalidate its identity or completed cache.
    public static bool IsLargeRenderedImage(BrowserImageReply image)
    {
        var width = image.Display.Width; var height = image.Display.Height;
        var viewport = image.ViewportSize; var clip = image.Clip; var natural = image.Natural;
        return new[] { width, height, viewport.Width, viewport.Height, clip.Width, clip.Height, natural.Width, natural.Height }.All(double.IsFinite)
            && Math.Min(natural.Width, natural.Height) >= 120 && Math.Max(natural.Width, natural.Height) >= 240
            && natural.Width * natural.Height <= 24_000_000 && viewport.Width > 0 && viewport.Height > 0
            && Math.Min(width, height) >= 240 && Math.Max(width, height) >= 360 && width * height >= 120_000
            && (Math.Min(width, viewport.Width) * Math.Min(height, viewport.Height) / (viewport.Width * viewport.Height) >= .18
                || Math.Min(width, height) >= 400 && Math.Max(width, height) >= 600)
            && clip.Width >= 64 && clip.Height >= 64
            && clip.Width * clip.Height >= Math.Max(120000, viewport.Width * viewport.Height * .02);
    }

    public static bool CanContinue(AutoImageCandidate candidate, BrowserAutoImageProbe probe, bool requirePageGate) =>
        probe.ActiveDocumentFound && probe.TabId == candidate.TabId && probe.WindowId == candidate.WindowId
        && string.Equals(probe.DocumentToken, candidate.DocumentToken, StringComparison.Ordinal)
        && probe.Image is { Found: true } image
        && string.Equals(image.ImageId, candidate.ImageId, StringComparison.Ordinal)
        && string.Equals(image.DocumentToken, candidate.DocumentToken, StringComparison.Ordinal)
        && (image.AutoImageAdmitted ?? image.AutoImageAllowed) != false
        && (!requirePageGate || (image.AutoImageAdmitted ?? image.AutoImageAllowed) == true);

    /// <summary>Runs once; identity invalidation cancels. Offscreen status is deliberately absent from this guard.</summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<bool>> isCurrent,
        Func<CancellationToken, Task<T>> operation, CancellationToken token, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(operation);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!await isCurrent(active.Token).ConfigureAwait(false)) active.Cancel();
        active.Token.ThrowIfCancellationRequested();
        var watching = WatchAsync();
        try
        {
            var result = await operation(active.Token).ConfigureAwait(false);
            active.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            active.Cancel(); await watching.ConfigureAwait(false);
        }
        async Task WatchAsync()
        {
            try
            {
                while (!active.IsCancellationRequested)
                {
                    await Task.Delay(interval, active.Token).ConfigureAwait(false);
                    if (!await isCurrent(active.Token).ConfigureAwait(false)) { active.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) { if (!active.IsCancellationRequested) active.Cancel(); }
            catch (Exception) { active.Cancel(); }
        }
    }
}
