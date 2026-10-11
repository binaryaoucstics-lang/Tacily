using System.Threading.Channels;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.App.Services.Browser;

public sealed record AutoPageCandidate(Guid ConnectionId, string RequestId, string DocumentToken,
    int TabId, int WindowId, long Generation, long Revision, string? DocumentLanguage,
    IReadOnlyList<BrowserPageTextBlock> Blocks, IReadOnlyList<PageTextSample>? Samples,
    PageTextStatistics? Statistics, string? ManualMode = null, string? SiteHost = null);

public sealed record AutoPagePacket(string Type,
    PageTranslationAssessment? Assessment = null,
    IReadOnlyList<BrowserPageTranslationUpdate>? Updates = null,
    bool Finished = false, string? Error = null);

/// <summary>
/// Bounded page snapshots; their translation service shares the provider budget.
/// paragraph workers. All output is scoped to the document, revision and settings.
/// </summary>
public sealed class BrowserPageAutoCoordinator : IAsyncDisposable
{
    public const int MaximumSessions = 64;
    public const int MaximumQueuedCandidates = 16;
    public const int MaximumActiveCandidates = 4;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(
        new BoundedChannelOptions(MaximumQueuedCandidates) { SingleReader = false, SingleWriter = false });
    private readonly Dictionary<SessionKey, Session> _sessions = [];
    private readonly Func<AutoPageCandidate, AppSettings, CancellationToken, Task<PageTranslationAssessment>> _judge;
    private readonly Func<AutoPageCandidate, AppSettings, Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask>,
        CancellationToken, Task<BrowserPageTranslationResult>> _translate;
    private readonly Func<AutoPageCandidate, CancellationToken, Task<bool>> _isActive;
    private readonly Func<AutoPageCandidate, AutoPagePacket, CancellationToken, Task> _send;
    private readonly Action<Exception> _failure;
    private readonly Task[] _workers;
    private Scope _scope = new(new AppSettings(), 0, new());
    private bool _disposed;

    public BrowserPageAutoCoordinator(
        Func<AutoPageCandidate, AppSettings, CancellationToken, Task<PageTranslationAssessment>> judge,
        Func<AutoPageCandidate, AppSettings, Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask>,
            CancellationToken, Task<BrowserPageTranslationResult>> translate,
        Func<AutoPageCandidate, CancellationToken, Task<bool>> isActive,
        Func<AutoPageCandidate, AutoPagePacket, CancellationToken, Task> send,
        Action<Exception>? failure = null)
    {
        (_judge, _translate, _isActive, _send, _failure) = (judge, translate, isActive, send, failure ?? (_ => { }));
        _workers = Enumerable.Range(0, MaximumActiveCandidates).Select(_ => Task.Run(RunAsync)).ToArray();
    }

    public void Configure(AppSettings settings, long generation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_disposed) return;
            if (_scope.Generation == generation &&
                BrowserAutoConfigurationPolicy.PreserveManualGeneration(_scope.Settings, settings))
            {
                var previous = _scope.Settings; _scope.Settings = settings;
                foreach (var session in _sessions.Values) {
                    var site = BrowserSitePolicy.Resolve(session.SiteHost, settings);
                    if (site.Mode != BrowserSitePolicy.Resolve(session.SiteHost, previous).Mode) {
                        session.Assessment = null; session.AssessmentFingerprint = null;
                        session.AutoFailed = false; session.FailureCode = null;
                    }
                    if (!settings.BrowserAutoPageTranslationEnabled || site.Mode == "block") {
                        if (session.ActiveManualMode is null) session.ActiveCancellation?.Cancel();
                        foreach (var request in session.FinishingRequests.Values) if (request.Mode is null) request.Cancellation.Cancel();
                    }
                }
                return;
            }
            Retire(_scope);
            foreach (var session in _sessions.Values) Retire(session);
            _sessions.Clear();
            _scope = new(settings, generation, new());
        }
    }

    public bool TryEnqueue(AutoPageCandidate candidate, PageTranslationAssessment? preparedAssessment = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_disposed || candidate.ManualMode is null && (!_scope.Settings.BrowserAutoPageTranslationEnabled
                    || BrowserSitePolicy.Resolve(candidate.SiteHost, _scope.Settings).Mode == "block") ||
                candidate.Generation != _scope.Generation || !IsBounded(candidate)) return false;
            var key = new SessionKey(candidate.ConnectionId, candidate.TabId, candidate.DocumentToken);
            foreach (var old in _sessions.Where(pair => pair.Key.ConnectionId == candidate.ConnectionId
                && pair.Key.TabId == candidate.TabId && pair.Key != key).ToArray())
            { Retire(old.Value); _sessions.Remove(old.Key); }
            if (_sessions.TryGetValue(key, out var existing) && candidate.Revision < existing.Revision) return false;
            // Automatic work cannot resume a paused page or replace an explicit manual request.
            if (candidate.ManualMode is null && existing is not null &&
                (existing.AutoPaused || existing.ActiveManualMode is not null)) return false;
            var fresh = existing is null || candidate.Revision > existing.Revision || candidate.WindowId != existing.WindowId;
            if (fresh)
            {
                // Even an image-only page sends an explicitly empty first sample array.
                // Manual selections need no page sample and leave the automatic decision intact.
                if (candidate.ManualMode is null && candidate.Samples is null) return false;
                var fingerprint = candidate.ManualMode is null ? AssessmentFingerprint(candidate) : existing?.AssessmentFingerprint;
                var assessment = existing?.AssessmentFingerprint == fingerprint ? existing?.Assessment : null;
                var autoPaused = existing?.AutoPaused ?? false;
                var autoFailed = candidate.ManualMode is not null && existing?.AutoFailed == true;
                if (existing is not null) Retire(existing);
                if (existing is null && _sessions.Count >= MaximumSessions)
                {
                    var victim = _sessions.OrderBy(pair => pair.Value.ActiveRequest is not null)
                        .ThenBy(pair => pair.Value.LastUse).First();
                    Retire(victim.Value); _sessions.Remove(victim.Key);
                }
                existing = new Session(candidate.Revision, candidate.WindowId, fingerprint)
                    { Assessment = assessment, AutoPaused = autoPaused, AutoFailed = autoFailed };
                _sessions[key] = existing;
            }
            if (existing!.ActiveRequest is not null) return false;
            existing.SiteHost = candidate.SiteHost ?? existing.SiteHost;
            if (candidate.ManualMode is null)
            {
                if (existing!.AutoPaused || existing.AutoFailed) return false;
                if (candidate.Samples is not null)
                {
                    var fingerprint = AssessmentFingerprint(candidate);
                    if (existing.AssessmentFingerprint != fingerprint) existing.Assessment = null;
                    existing.AssessmentFingerprint = fingerprint;
                }
                else if (existing.AssessmentFingerprint is null) return false;
            }
            existing.LastUse = DateTime.UtcNow.Ticks;
            if (candidate.ManualMode is null && preparedAssessment is not null) existing.Assessment = preparedAssessment;
            var requestCancellation = new CancellationTokenSource();
            var work = new Work(candidate, key, existing, _scope, requestCancellation);
            if (!_queue.Writer.TryWrite(work)) { requestCancellation.Dispose(); return false; }
            _scope.ActiveWorks++; existing.ActiveWorks++;
            existing.ActiveRequest = candidate.RequestId;
            existing.ActiveManualMode = candidate.ManualMode;
            existing.ActiveCancellation = requestCancellation;
            return true;
        }
    }

    public Task AnnounceAssessmentAsync(AutoPageCandidate candidate, PageTranslationAssessment assessment, CancellationToken token) =>
        SendCurrentAsync(candidate, new("pageAutoAssessment", assessment), token);

    public string RejectionReason(AutoPageCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed) return "desktop_unavailable";
            if (candidate.Generation != _scope.Generation) return "configuration_changed";
            if (candidate.ManualMode is null && !_scope.Settings.BrowserAutoPageTranslationEnabled) return "automatic_disabled";
            if (candidate.ManualMode is null && BrowserSitePolicy.Resolve(candidate.SiteHost, _scope.Settings).Mode == "block") return "site_blocked";
            if (!IsBounded(candidate)) return "invalid_request";
            if (_sessions.TryGetValue(new(candidate.ConnectionId, candidate.TabId, candidate.DocumentToken), out var session))
            {
                if (candidate.Revision < session.Revision) return "document_changed";
                if (candidate.ManualMode is null && session.AutoFailed) return session.FailureCode ?? "api_configuration";
                if (candidate.ManualMode is null && session.AutoPaused) return "page_paused";
                if (session.ActiveRequest is not null) return "page_busy";
            }
            return "desktop_busy";
        }
    }

    public void CancelAllRequests()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Retire(_scope);
            foreach (var session in _sessions.Values) Retire(session);
            _sessions.Clear();
            _scope = new(_scope.Settings, _scope.Generation, new());
        }
    }

    public bool IsCurrent(AutoPageCandidate candidate)
    {
        lock (_gate)
        {
            var key = new SessionKey(candidate.ConnectionId, candidate.TabId, candidate.DocumentToken);
            return !_disposed && (candidate.ManualMode is not null || _scope.Settings.BrowserAutoPageTranslationEnabled) &&
                _scope.Generation == candidate.Generation && _sessions.TryGetValue(key, out var session) &&
                session.Revision == candidate.Revision && session.WindowId == candidate.WindowId &&
                (candidate.ManualMode is not null || !session.AutoPaused) && !session.Cancellation.IsCancellationRequested &&
                (session.ActiveManualMode == candidate.ManualMode &&
                    session.ActiveRequest == candidate.RequestId && session.ActiveCancellation?.IsCancellationRequested == false
                 || session.FinishingRequests.TryGetValue(candidate.RequestId, out var finishing)
                    && finishing.Mode == candidate.ManualMode && !finishing.Cancellation.IsCancellationRequested);
        }
    }

    public void Cancel(Guid connection, int tabId, string documentToken, long generation, long revision,
        string? requestId = null, bool pause = false)
    {
        lock (_gate)
        {
            if (_scope.Generation != generation || !_sessions.TryGetValue(new(connection, tabId, documentToken), out var session)
                || session.Revision != revision || requestId is not null && session.ActiveRequest != requestId) return;
            if (pause || requestId is null)
            {
                session.AutoPaused = true;
                if (session.ActiveManualMode is null) session.ActiveCancellation?.Cancel();
            }
            else session.ActiveCancellation?.Cancel();
        }
    }

    public void Resume(Guid connection, int tabId, string documentToken, long generation, long revision)
    {
        lock (_gate)
        {
            if (_scope.Generation != generation || !_sessions.TryGetValue(new(connection, tabId, documentToken), out var session)
                || session.Revision != revision) return;
            session.AutoPaused = false; session.AutoFailed = false;
        }
    }

    public void PauseThroughRevision(Guid connection, int tabId, string documentToken, long generation, long revision)
    {
        lock (_gate)
        {
            if (_scope.Generation != generation || !_sessions.TryGetValue(new(connection, tabId, documentToken), out var session)
                || session.Revision > revision) return;
            session.AutoPaused = true;
            if (session.ActiveManualMode is null) session.ActiveCancellation?.Cancel();
        }
    }

    public void ConnectionClosed(Guid connection)
    {
        lock (_gate)
            foreach (var pair in _sessions.Where(pair => pair.Key.ConnectionId == connection).ToArray())
            { Retire(pair.Value); _sessions.Remove(pair.Key); }
    }

    public void DocumentInvalidated(Guid connection, int tabId, string documentToken)
    {
        lock (_gate)
            if (_sessions.Remove(new(connection, tabId, documentToken), out var session)) Retire(session);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var requestToken = work.RequestCancellation.Token;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,
                    work.Scope.Cancellation.Token, work.Session.Cancellation.Token, requestToken);
                var token = cancellation.Token;
                try
                {
                    await EnsureActiveAsync(work.Candidate, token).ConfigureAwait(false);
                    var assessment = work.Candidate.ManualMode is { } manualMode
                        ? new PageTranslationAssessment(true, "manual " + manualMode, 1, work.Candidate.DocumentLanguage ?? "unknown", false)
                            { ReasonCode = "manual-" + manualMode }
                        : work.Session.Assessment;
                    if (assessment is null)
                    {
                        assessment = await _judge(work.Candidate, work.Scope.Settings, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            if (!IsCurrent(work.Candidate)) throw new OperationCanceledException(token);
                            work.Session.Assessment = assessment;
                        }
                    }
                    await SendCurrentAsync(work.Candidate, new("pageAutoAssessment", assessment), token).ConfigureAwait(false);
                    if (!assessment.NeedsTranslation || work.Candidate.Blocks.Count == 0)
                    {
                        await SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Finished: true), token).ConfigureAwait(false);
                        continue;
                    }
                    await using var updates = new CoalescedUpdates(
                        values => SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Updates: values), token), token,
                        cancellation.Cancel);
                    try
                    {
                        await _translate(work.Candidate, work.Scope.Settings, updates.PublishAsync, token).ConfigureAwait(false);
                        await updates.CompleteAsync().ConfigureAwait(false);
                        await SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Finished: true), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        await updates.CompleteAsync().ConfigureAwait(false);
                        if (work.Candidate.ManualMode is null) lock (_gate)
                        {
                            work.Session.AutoFailed = PageAutoFailure.IsConfiguration(error);
                            work.Session.FailureCode = PageAutoFailure.Code(error);
                        }
                        await SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Finished: true,
                            Error: PageAutoFailure.Code(error)), token).ConfigureAwait(false);
                        _failure(error);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(work.Candidate)) { }
                catch (OperationCanceledException)
                {
                    // A failed initial active-document probe is not a canceled
                    // request. Close its frontend preparation state explicitly.
                    try { await SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Finished: true,
                        Error: "page-unavailable"), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                catch (Exception error)
                {
                    _failure(error);
                    try
                    {
                        await SendCurrentAsync(work.Candidate, new("pageAutoTranslations", Finished: true,
                            Error: PageAutoFailure.Code(error)), token).ConfigureAwait(false);
                        if (work.Candidate.ManualMode is null) lock (_gate)
                        { work.Session.AutoFailed = PageAutoFailure.IsConfiguration(error); work.Session.FailureCode = PageAutoFailure.Code(error); }
                    }
                    catch (Exception sendingError) when (sendingError is OperationCanceledException or System.IO.IOException or InvalidOperationException) { }
                }
                finally
                {
                    lock (_gate) Release(work);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task EnsureActiveAsync(AutoPageCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(candidate) || !await _isActive(candidate, token).ConfigureAwait(false) || !IsCurrent(candidate))
            throw new OperationCanceledException(token);
        token.ThrowIfCancellationRequested();
    }

    private async Task SendCurrentAsync(AutoPageCandidate candidate, AutoPagePacket packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Admission verifies the exact document once. Thereafter native and Chrome
        // routing validate document/revision identity, including inactive tabs.
        // Requiring foreground focus on every stream frame used to discard a
        // completed translation after a tab switch and caused needless retries.
        lock (_gate)
        {
            if (!IsCurrent(candidate)) throw new OperationCanceledException(token);
            if (packet.Finished && _sessions.TryGetValue(new(candidate.ConnectionId, candidate.TabId, candidate.DocumentToken), out var session)
                && session.ActiveRequest == candidate.RequestId)
            {
                // Release admission BEFORE the completed packet reaches Chrome. Keep
                // its identity/token valid until delivery finishes so the next batch
                // cannot cancel or steal this request's terminal message.
                session.FinishingRequests[candidate.RequestId] = (session.ActiveManualMode, session.ActiveCancellation!);
                session.ActiveRequest = null; session.ActiveManualMode = null; session.ActiveCancellation = null;
            }
        }
        await _send(candidate, packet, token).ConfigureAwait(false);
    }

    private static string AssessmentFingerprint(AutoPageCandidate candidate) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidate.DocumentLanguage, candidate.Samples, candidate.Statistics,
        })));

    private static void Retire(Scope scope)
    {
        scope.Retired = true; scope.Cancellation.Cancel();
        if (scope.ActiveWorks == 0) scope.Cancellation.Dispose();
    }

    private static void Retire(Session session)
    {
        session.Retired = true; session.Cancellation.Cancel();
        if (session.ActiveWorks == 0) session.Cancellation.Dispose();
    }

    private static void Release(Work work)
    {
        if (work.Session.ActiveRequest == work.Candidate.RequestId)
        {
            work.Session.ActiveRequest = null;
            work.Session.ActiveManualMode = null;
            work.Session.ActiveCancellation = null;
        }
        work.Session.FinishingRequests.Remove(work.Candidate.RequestId);
        work.RequestCancellation.Dispose();
        work.Scope.ActiveWorks--; work.Session.ActiveWorks--;
        if (work.Scope.Retired && work.Scope.ActiveWorks == 0) work.Scope.Cancellation.Dispose();
        if (work.Session.Retired && work.Session.ActiveWorks == 0) work.Session.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); Retire(_scope);
            foreach (var session in _sessions.Values) Retire(session);
            _queue.Writer.TryComplete();
        }
        await Task.WhenAll(_workers).ConfigureAwait(false);
        lock (_gate)
        {
            while (_queue.Reader.TryRead(out var queued)) Release(queued);
            _sessions.Clear();
            _lifetime.Dispose();
        }
    }

    public static bool IsBounded(AutoPageCandidate candidate) =>
        candidate.TabId >= 0 && candidate.WindowId >= 0 && candidate.ConnectionId != Guid.Empty &&
        candidate.Generation is >= 0 and <= 9_007_199_254_740_991 && candidate.Revision >= 0 && candidate.Revision <= 9_007_199_254_740_991 &&
        candidate.RequestId is { Length: > 0 and <= 64 } && candidate.DocumentToken is { Length: > 0 and <= 256 } &&
        candidate.ManualMode is null or "selection" or "page" &&
        (candidate.SiteHost is null || candidate.SiteHost.Length <= 253 && BrowserSitePolicy.NormalizeDomain(candidate.SiteHost) == candidate.SiteHost) &&
        candidate.DocumentLanguage?.Length is not > 64 && candidate.Blocks.Count <= 48 &&
        candidate.Blocks.All(block => block is not null && block.Id is { Length: > 0 and <= 128 }
            && block.Text is { Length: > 0 } && !string.IsNullOrWhiteSpace(block.Text)
            && block.GroupId?.Length is not > 128 && block.Context?.Length is not > 512 &&
            block.ContextKind is null or "source" or "selection" && ValidSourceGroup(block)) &&
        candidate.Blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() == candidate.Blocks.Count &&
        candidate.Blocks.Sum(block => (long)block.Text.Length + (block.Context?.Length ?? 0)) <= 32_768 &&
        (candidate.Samples is null || candidate.Samples.Count <= 64 &&
            candidate.Samples.All(sample => sample is not null && sample.Kind is "body" or "heading" or "title" or "navigation" or "code" or "form"
                && sample.Text is { Length: <= 12_000 } && sample.LanguageHint?.Length is not > 64) &&
            candidate.Samples.Sum(sample => (long)sample.Text.Length) <= 12_000) && ValidStatistics(candidate.Statistics);

    private static bool ValidSourceGroup(BrowserPageTextBlock block)
    {
        if (block.SourceGroupHash is null && block.SourceGroupLength is null &&
            block.SourceStart is null && block.SourceLength is null) return true;
        return block.SourceGroupHash is { Length: 64 } hash &&
            hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') &&
            block.SourceGroupLength is int groupLength && groupLength is > 0 and <= 1_000_000 &&
            block.SourceStart is int start && start >= 0 && block.SourceLength is int length &&
            length == block.Text.Length && (long)start + length <= groupLength;
    }

    private static bool ValidStatistics(PageTextStatistics? value) => value is null ||
        value.TotalCharacters is >= 0 and <= 10_000_000 && value.HanCharacters >= 0 && value.KanaCharacters >= 0 &&
        value.LatinCharacters >= 0 && (long)value.HanCharacters + value.KanaCharacters + value.LatinCharacters <= value.TotalCharacters &&
        value.ProseBlocks is >= 0 and <= 100_000 && value.HanProseCharacters >= 0 && value.LatinProseCharacters >= 0 &&
        value.KanaProseCharacters >= 0 && (long)value.HanProseCharacters + value.LatinProseCharacters + value.KanaProseCharacters <= value.TotalCharacters;

    private sealed class Scope(AppSettings settings, long generation, CancellationTokenSource cancellation)
    {
        public AppSettings Settings { get; set; } = settings;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int ActiveWorks { get; set; }
        public bool Retired { get; set; }
    }
    private sealed record SessionKey(Guid ConnectionId, int TabId, string DocumentToken);
    private sealed record Work(AutoPageCandidate Candidate, SessionKey Key, Session Session, Scope Scope, CancellationTokenSource RequestCancellation);
    private sealed class Session(long revision, int windowId, string? assessmentFingerprint)
    {
        public long Revision { get; } = revision;
        public string? SiteHost { get; set; }
        public int WindowId { get; } = windowId;
        public string? AssessmentFingerprint { get; set; } = assessmentFingerprint;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationTokenSource? ActiveCancellation { get; set; }
        public string? ActiveRequest { get; set; }
        public string? ActiveManualMode { get; set; }
        public Dictionary<string, (string? Mode, CancellationTokenSource Cancellation)> FinishingRequests { get; } = new(StringComparer.Ordinal);
        public PageTranslationAssessment? Assessment { get; set; }
        public bool AutoPaused { get; set; }
        public bool AutoFailed { get; set; }
        public string? FailureCode { get; set; }
        public long LastUse { get; set; }
        public int ActiveWorks { get; set; }
        public bool Retired { get; set; }
    }

    /// <summary>35 ms frames carry only the newest cumulative string per node.</summary>
    private sealed class CoalescedUpdates : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, BrowserPageTranslationUpdate> _pending = new(StringComparer.Ordinal);
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(1);
        private readonly CancellationToken _token;
        private readonly Task _sender;
        public CoalescedUpdates(Func<IReadOnlyList<BrowserPageTranslationUpdate>, Task> send, CancellationToken token,
            Action cancel)
        {
            _token = token;
            _sender = Task.Run(async () =>
            {
                try
                {
                    await foreach (var unused in _signal.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        await Task.Delay(35, token).ConfigureAwait(false);
                        BrowserPageTranslationUpdate[] updates;
                        lock (_gate) { updates = _pending.Values.ToArray(); _pending.Clear(); }
                        if (updates.Length > 0) await send(updates).ConfigureAwait(false);
                    }
                }
                catch { cancel(); throw; }
            });
        }
        public ValueTask PublishAsync(BrowserPageTranslationUpdate update, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (_gate) _pending[update.Id] = update;
            _signal.Writer.TryWrite(true);
            return ValueTask.CompletedTask;
        }
        public async Task CompleteAsync()
        {
            _signal.Writer.TryComplete();
            await _sender.ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            _signal.Writer.TryComplete();
            try { await _sender.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        }
    }
}
