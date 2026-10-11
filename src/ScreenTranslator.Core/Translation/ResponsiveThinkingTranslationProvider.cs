using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Translation;

/// <summary>
/// Explicitly enabled responsiveness policy: an image request waiting only for Low
/// reasoning may make one disclosed non-thinking retry. No retry after any answer.
/// </summary>
public sealed class ResponsiveThinkingTranslationProvider : ITranslationProvider, IStreamingTranslationProvider
{
    private readonly ITranslationProvider _inner;
    private readonly ThinkingLevel _defaultThinking;
    private readonly bool _enabled;
    public event Action? FastRetryStarted;

    public ResponsiveThinkingTranslationProvider(ITranslationProvider inner, ThinkingLevel defaultThinking, bool enabled = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (!Enum.IsDefined(defaultThinking)) throw new ArgumentOutOfRangeException(nameof(defaultThinking));
        _defaultThinking = defaultThinking;
        _enabled = enabled;
    }

    // Non-streaming responses cannot establish first-content timing reliably.
    public Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        _inner.TranslateRawAsync(request, cancellationToken);

    public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
        IProgress<TranslationStreamUpdate> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (_inner is not IStreamingTranslationProvider streaming)
            return await _inner.TranslateRawAsync(request, cancellationToken).ConfigureAwait(false);
        var eligible = _enabled && (request.ThinkingOverride ?? _defaultThinking) == ThinkingLevel.Low && request.ImagePng is { Length: > 0 };
        var gate = new object();
        var answerObserved = false;
        var firstClosed = false;
        var initialProgress = new InlineProgress(update =>
        {
            lock (gate)
            {
                if (firstClosed || cancellationToken.IsCancellationRequested) return;
                answerObserved |= !string.IsNullOrWhiteSpace(update.Translation) || !string.IsNullOrWhiteSpace(update.StreamingTail)
                    || update.ImageSegments?.Any(block => !string.IsNullOrWhiteSpace(block.Translation)) == true;
                progress.Report(update);
            }
        });
        try
        {
            return await streaming.TranslateRawStreamingAsync(request, initialProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationFirstContentTimeoutException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                firstClosed = true;
                if (!eligible || answerObserved || error.HasReceivedContent) throw;
            }
            if (FastRetryStarted is { } subscribers)
                foreach (Action subscriber in subscribers.GetInvocationList())
                    try { subscriber(); } catch { /* Notification cannot break the replacement request. */ }
            cancellationToken.ThrowIfCancellationRequested();
            // Direct call, no recursive wrapper: a second timeout/error propagates.
            return await streaming.TranslateRawStreamingAsync(request with { ThinkingOverride = ThinkingLevel.None }, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (gate) firstClosed = true; }
    }

    private sealed class InlineProgress(Action<TranslationStreamUpdate> report) : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) => report(value); }
}
