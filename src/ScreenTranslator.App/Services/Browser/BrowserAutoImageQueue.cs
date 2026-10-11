using System.Threading.Channels;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.Services.Browser;

public sealed record AutoImageCandidate(Guid ConnectionId, string RequestId, string ImageId,
    string DocumentToken, int TabId, int WindowId, long Generation, string PreviewBase64, string? SiteHost = null);

/// <summary>Two bounded workers: local decisions never wait for cloud translation.</summary>
public sealed class BrowserAutoImageQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Work> _assessment = Channel.CreateBounded<Work>(4);
    private readonly Channel<Work> _translation = Channel.CreateBounded<Work>(32);
    private readonly HashSet<(Guid, string, long)> _pending = [];
    private readonly Func<AutoImageCandidate, AppSettings, CancellationToken, Task<ImageTranslationAssessment>> _judge;
    private readonly Func<AutoImageCandidate, bool, string, CancellationToken, Task> _reply;
    private readonly Func<AutoImageCandidate, AppSettings, CancellationToken, Task> _translate;
    private readonly Action<Exception> _failure;
    private readonly Task _judging, _translating;
    private Scope _scope = new(new AppSettings(), 0, new());
    private readonly List<CancellationTokenSource> _retired = [];
    private CancellationTokenSource? _activeTranslation;
    private AutoImageCandidate? _activeCandidate;
    private Guid _activeConnection;
    private bool _activeWasPaused;
    private bool _manualBusy, _disposed;
    private TaskCompletionSource _manualIdle = CompletedSignal();

    public BrowserAutoImageQueue(
        Func<AutoImageCandidate, AppSettings, CancellationToken, Task<ImageTranslationAssessment>> judge,
        Func<AutoImageCandidate, bool, string, CancellationToken, Task> reply,
        Func<AutoImageCandidate, AppSettings, CancellationToken, Task> translate,
        Action<Exception>? failure = null)
    {
        (_judge, _reply, _translate, _failure) = (judge, reply, translate, failure ?? (_ => { }));
        _judging = Task.Run(JudgeAsync); _translating = Task.Run(TranslateAsync);
    }

    public void Configure(AppSettings settings, long generation)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_scope.Generation == generation && _scope.Settings.BrowserAutoImageTranslationEnabled == settings.BrowserAutoImageTranslationEnabled
                && ScreenTranslator.Core.Browser.BrowserAutoConfigurationPolicy.PreserveManualGeneration(_scope.Settings, settings)) {
                _scope.Settings = settings;
                if (_activeCandidate is not null && ScreenTranslator.Core.Browser.BrowserSitePolicy.Resolve(_activeCandidate.SiteHost, settings).Mode == "block")
                    _activeTranslation?.Cancel();
                return;
            }
            _scope.Cancellation.Cancel(); _retired.Add(_scope.Cancellation);
            _scope = new(settings, generation, new());
            _pending.Clear();
        }
    }

    public bool TryEnqueue(AutoImageCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed || !_scope.Settings.BrowserAutoImageTranslationEnabled || candidate.Generation != _scope.Generation
                || ScreenTranslator.Core.Browser.BrowserSitePolicy.Resolve(candidate.SiteHost, _scope.Settings).Mode == "block") return false;
            var key = (candidate.ConnectionId, candidate.ImageId, candidate.Generation);
            if (!_pending.Add(key)) return false;
            if (_assessment.Writer.TryWrite(new(candidate, _scope))) return true;
            _pending.Remove(key); return false;
        }
    }

    public void SetManualBusy(bool busy)
    {
        lock (_gate)
        {
            if (_disposed || busy == _manualBusy) return;
            _manualBusy = busy;
            if (busy) { _manualIdle = new(TaskCreationOptions.RunContinuationsAsynchronously); _activeWasPaused = true; _activeTranslation?.Cancel(); }
            else _manualIdle.TrySetResult();
        }
    }

    public void ConnectionClosed(Guid connection)
    {
        lock (_gate) { if (_activeConnection == connection) _activeTranslation?.Cancel(); }
    }

    public void CancelDocument(Guid connection, int tabId, string documentToken)
    {
        lock (_gate)
        {
            if (_activeCandidate is not { } candidate || candidate.ConnectionId != connection || candidate.TabId != tabId
                || !string.Equals(candidate.DocumentToken, documentToken, StringComparison.Ordinal)) return;
            // This document ended/paused. It is not a manual operation's temporary yield.
            _activeWasPaused = false;
            _activeTranslation?.Cancel();
        }
    }

    private async Task JudgeAsync()
    {
        try
        {
            await foreach (var work in _assessment.Reader.ReadAllAsync(_lifetime.Token))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, work.Scope.Cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var assessment = await _judge(work.Candidate, work.Scope.Settings, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (ScreenTranslator.Core.Browser.BrowserSitePolicy.Resolve(work.Candidate.SiteHost, work.Scope.Settings).Mode == "block") {
                        await _reply(work.Candidate, false, "site_blocked", cancellation.Token).ConfigureAwait(false); Remove(work); continue;
                    }
                    // Release Chrome's assessment slot before waiting for a translation slot.
                    await _reply(work.Candidate, assessment.NeedsTranslation, assessment.ReasonCode, cancellation.Token).ConfigureAwait(false);
                    if (assessment.NeedsTranslation)
                        await _translation.Writer.WriteAsync(work, cancellation.Token).ConfigureAwait(false);
                    else Remove(work);
                }
                catch (OperationCanceledException) { Remove(work); }
                catch (Exception error)
                {
                    Remove(work); _failure(error);
                    try { await _reply(work.Candidate, false, "local-unavailable", _lifetime.Token).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TranslateAsync()
    {
        try
        {
            await foreach (var work in _translation.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    // A manual hotkey selection has priority. A cancelled automatic task
                    // resumes once the manual operation ends, without cancelling it.
                    while (true)
                    {
                        Task idle;
                        lock (_gate) idle = _manualIdle.Task;
                        await idle.WaitAsync(work.Scope.Cancellation.Token).ConfigureAwait(false);
                        if (ScreenTranslator.Core.Browser.BrowserSitePolicy.Resolve(work.Candidate.SiteHost, work.Scope.Settings).Mode == "block") break;
                        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, work.Scope.Cancellation.Token);
                        lock (_gate)
                        {
                            if (_manualBusy) continue;
                            _activeConnection = work.Candidate.ConnectionId; _activeCandidate = work.Candidate;
                            _activeTranslation = cancellation; _activeWasPaused = false;
                        }
                        var retry = false;
                        try { await _translate(work.Candidate, work.Scope.Settings, cancellation.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!work.Scope.Cancellation.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                        { lock (_gate) retry = _activeWasPaused; }
                        finally { lock (_gate) { _activeTranslation = null; _activeCandidate = null; _activeConnection = default; } }
                        if (!retry) break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { _failure(error); }
                finally { Remove(work); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Remove(Work work)
    { lock (_gate) _pending.Remove((work.Candidate.ConnectionId, work.Candidate.ImageId, work.Candidate.Generation)); }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _scope.Cancellation.Cancel(); _lifetime.Cancel();
            _manualIdle.TrySetCanceled(); _assessment.Writer.TryComplete(); _translation.Writer.TryComplete();
        }
        await Task.WhenAll(_judging, _translating).ConfigureAwait(false);
        _scope.Cancellation.Dispose(); foreach (var retired in _retired) retired.Dispose(); _lifetime.Dispose();
    }

    private static TaskCompletionSource CompletedSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
    private sealed class Scope(AppSettings settings, long generation, CancellationTokenSource cancellation)
    {
        public AppSettings Settings { get; set; } = settings;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }
    private sealed record Work(AutoImageCandidate Candidate, Scope Scope);
}
