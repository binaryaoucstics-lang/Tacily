using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace BrowserPageQa;

internal sealed class JsonlHost : IAsyncDisposable
{
    internal const int MaximumActiveRequests = 4;
    internal const int MaximumLineCharacters = 1_048_576;
    private readonly bool _live;
    private readonly Lazy<Task<AppSettings>> _settings;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Work> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Work?> _current = new();
    private readonly BrowserPageTranslationCache _cache = new();
    private readonly BrowserPageTranslationService _service;
    private bool _stopping;
    private int _transportFailed;

    internal JsonlHost(bool live, Func<Task<AppSettings>> settings,
        Func<AppSettings, ITranslationProvider> providerFactory, TextWriter output)
    {
        _live = live; _settings = new(settings); _output = output;
        // The shared production service retains its ten provider slots and bounded memory-only cache.
        _service = new(_ => new CountedProvider(providerFactory(_current.Value!.Settings!), _current.Value!), cache: _cache);
    }

    internal async Task RunAsync(TextReader input)
    {
        // Bound input before parsing, including a malicious line without a newline.
        var buffer = new char[4096]; var line = new StringBuilder(); var oversized = false;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), _shutdown.Token).AsTask().WaitAsync(_shutdown.Token)) != 0)
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (oversized) await ErrorAsync("", "", "line_too_large");
                        else await HandleAsync(line.ToString().TrimEnd('\r'));
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length == MaximumLineCharacters) { line.Clear(); oversized = true; }
                        else line.Append(buffer[i]);
                    }
                }
            if (oversized) await ErrorAsync("", "", "line_too_large");
            else if (line.Length != 0) await HandleAsync(line.ToString().TrimEnd('\r'));
            await DrainAsync(); // EOF drains admitted requests; it is not evidence of successful translation.
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { await DrainAsync(); }
        catch { CancelAll(); await DrainAsync(); throw; }
    }

    internal async Task HandleAsync(string line)
    {
        string id = "", caseId = "";
        try
        {
            if (line.Length > MaximumLineCharacters) { await ErrorAsync(id, caseId, "line_too_large"); return; }
            using var json = JsonDocument.Parse(line, new() { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            id = Identity(root, "id"); caseId = Identity(root, "caseId");
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || HasDuplicates(root)) throw new JsonException();
            var action = root.GetProperty("action").GetString();
            if (action == "cancel")
            {
                bool found;
                lock (_gate)
                {
                    found = _active.TryGetValue(id, out var work) && work.CaseId == caseId;
                    if (found) work!.Cancellation.Cancel();
                }
                if (!found) await ErrorAsync(id, caseId, "unknown_request");
                return;
            }
            if (action is not ("capabilities" or "assess" or "message")) throw new JsonException();
            lock (_gate)
            {
                if (_usedIds.Count >= 100_000) throw new HostException("request_limit");
                if (!_usedIds.Add(id)) throw new HostException("duplicate_id");
                if (_stopping) throw new HostException("host_stopping");
            }
            AutoPageCandidate? candidate = null;
            if (action == "message")
            {
                var message = root.GetProperty("message");
                if (message.GetProperty("type").GetString() != "pageAutoCandidate"
                    || !BrowserPageAutoProtocol.TryReadCandidate(Guid.NewGuid(), message, out candidate))
                    throw new HostException("invalid_candidate");
                var work = new Work(id, caseId, candidate!);
                lock (_gate)
                {
                    if (_active.Count >= MaximumActiveRequests) { work.Cancellation.Dispose(); throw new HostException("busy"); }
                    _active.Add(id, work);
                    work.Task = Task.Run(() => ExecuteSafelyAsync(work));
                }
                return;
            }
            if (action == "assess") candidate = ReadAssessment(root);
            var settings = await SettingsAsync();
            if (action == "capabilities")
                await EmitAsync(new { schemaVersion = 1, id, caseId, type = "capabilities", live = _live,
                    provider = TranslationProviderConfiguration.Provider(settings).ToString(),
                    targetLanguage = settings.TargetLanguage, policyVersion = PageTranslationDecision.PolicyVersion });
            else
            {
                var assessment = Assess(candidate!, settings);
                await EmitAsync(new { schemaVersion = 1, id, caseId, type = "assessment", live = _live,
                    needsTranslation = assessment.NeedsTranslation, reason = assessment.ReasonCode,
                    confidence = assessment.Confidence, detectedLanguage = assessment.DetectedLanguage,
                    isMixedTargetLanguage = assessment.IsMixedTargetLanguage });
            }
        }
        catch (HostException error) { await ErrorAsync(id, caseId, error.Code); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException)
        { await ErrorAsync(id, caseId, "invalid_request"); }
    }

    private static AutoPageCandidate ReadAssessment(JsonElement root)
    {
        // Reuse native validation for both sample/statistic bounds; no new business rules.
        var wire = JsonSerializer.Serialize(new { requestId = "assessment", documentToken = "assessment",
            tabId = 0, browserWindowId = 0, frameId = 0, generation = 0, revision = 0,
            blocks = Array.Empty<object>(), samples = root.GetProperty("samples"),
            pageSummary = root.TryGetProperty("statistics", out var stats) ? stats : JsonSerializer.SerializeToElement(new { }),
            siteHost = BrowserPageAutoProtocol.OptionalString(root, "siteHost") });
        using var document = JsonDocument.Parse(wire);
        if (!BrowserPageAutoProtocol.TryReadCandidate(Guid.NewGuid(), document.RootElement, out var candidate))
            throw new HostException("invalid_assessment");
        return candidate!;
    }

    private static PageTranslationAssessment Assess(AutoPageCandidate candidate, AppSettings settings)
    {
        if (candidate.ManualMode is { } mode)
            return new(true, "manual " + mode, 1, candidate.DocumentLanguage ?? "unknown", false)
                { ReasonCode = "manual-" + mode };
        var rule = BrowserSitePolicy.Resolve(candidate.SiteHost, settings);
        if (rule.Mode is "allow" or "block")
            return new(rule.Mode == "allow", rule.Mode, 1, "", false)
                { ReasonCode = rule.Mode == "allow" ? "site-whitelist" : "site-blacklist" };
        return PageTranslationDecision.Inspect(candidate.Samples ?? [], settings.TargetLanguage, candidate.Statistics).RuleDecision;
    }

    private async Task ExecuteSafelyAsync(Work work)
    {
        try { await ExecuteAsync(work); }
        catch
        {
            // Observe task faults even if the task ended before EOF took its active snapshot.
            Interlocked.Exchange(ref _transportFailed, 1);
            CancelAll();
        }
        finally
        {
            lock (_gate) { _active.Remove(work.Id); work.Cancellation.Dispose(); }
        }
    }

    private async Task ExecuteAsync(Work work)
    {
        var watch = Stopwatch.StartNew(); var success = false; string? failure = null;
        _current.Value = work;
        try
        {
            work.Cancellation.Token.ThrowIfCancellationRequested();
            work.Settings = await SettingsAsync();
            work.Cancellation.Token.ThrowIfCancellationRequested();
            // Admission is an empty valid production translations frame. The transport metadata
            // lets the bridge resolve session.send with { accepted: true }; no invented frame type.
            await PacketAsync(work, new("pageAutoTranslations"), accepted: true);
            var assessment = Assess(work.Candidate, work.Settings);
            await PacketAsync(work, new("pageAutoAssessment", assessment));
            if (assessment.NeedsTranslation && work.Candidate.Blocks.Count != 0)
            {
                var settings = work.Settings;
                var result = await _service.TranslateAsync(work.Candidate.Blocks, new()
                {
                    SourceLanguage = settings.SourceLanguage, TargetLanguage = settings.TargetLanguage,
                    Style = settings.TranslationStyle,
                    Context = settings.TranslationContext[..Math.Min(2048, settings.TranslationContext.Length)],
                    Parallelism = Math.Clamp(settings.BrowserPageTranslationConcurrency, 1, 10),
                    // Memory cache belongs to this immutable settings snapshot and this host only.
                    CacheScope = "browser-page-qa-session", ExplicitSelection = work.Candidate.ManualMode == "selection",
                }, async (update, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    work.Updates[update.Id] = update;
                    await PacketAsync(work, new("pageAutoTranslations", Updates: [update]));
                }, work.Cancellation.Token);
                work.RequestCount = result.RequestCount; work.CacheHits = result.CacheHits; work.FailedCount = result.FailedCount;
                success = result.FailedCount == 0 && result.CompletedCount == work.Candidate.Blocks.Count;
                if (!success) failure = "translation_incomplete";
            }
            else success = true;
            work.Cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            success = false;
            failure = error is OperationCanceledException && work.Cancellation.IsCancellationRequested ? "cancelled"
                : error is HostException host ? host.Code : PageAutoFailure.Code(error);
            work.CacheHits = work.Updates.Values.Count(update => update.FromCache && update.IsComplete && !update.Failed);
            // Explicitly restore every unfinished source; streamed prefixes never count as complete.
            foreach (var block in work.Candidate.Blocks)
                if (!work.Updates.TryGetValue(block.Id, out var update) || !update.IsComplete || update.Failed)
                {
                    var failed = new BrowserPageTranslationUpdate(block.Id, block.Text, false, Failed: true);
                    work.Updates[block.Id] = failed;
                    await PacketAsync(work, new("pageAutoTranslations", Updates: [failed]));
                }
            work.FailedCount = work.Updates.Values.Count(update => update.Failed);
            await ErrorAsync(work.Id, work.CaseId, failure);
        }
        finally
        {
            try
            {
                // Finished closes frontend bookkeeping, with error on every unsuccessful operation.
                // Terminal is separate audit evidence emitted only after production tasks have drained.
                await PacketAsync(work, new("pageAutoTranslations", Finished: true, Error: failure));
                await EmitAsync(new { schemaVersion = 1, id = work.Id, caseId = work.CaseId, type = "terminal", live = _live,
                    success, requestCount = work.RequestCount, cacheHits = work.CacheHits, failedCount = work.FailedCount,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            }
            finally
            {
                _current.Value = null;
            }
        }
    }

    private async Task<AppSettings> SettingsAsync()
    {
        try { return await _settings.Value.ConfigureAwait(false); }
        catch { throw new HostException("settings_unavailable"); }
    }

    private Task PacketAsync(Work work, AutoPagePacket packet, bool accepted = false)
    {
        using var document = JsonDocument.Parse(BrowserPageAutoProtocol.Serialize(work.Candidate, packet));
        return EmitAsync(new { schemaVersion = 1, id = work.Id, caseId = work.CaseId, type = "packet",
            live = _live, accepted, packet = document.RootElement.Clone() });
    }

    private Task ErrorAsync(string id, string caseId, string code) => EmitAsync(new
        { schemaVersion = 1, id, caseId, type = "error", live = _live, code, message = "Request could not be completed (" + code + ")." });

    private async Task EmitAsync(object value)
    {
        var line = JsonSerializer.Serialize(value);
        await _writeGate.WaitAsync();
        try { await _output.WriteLineAsync(line); await _output.FlushAsync(); }
        finally { _writeGate.Release(); }
    }

    private static string Identity(JsonElement root, string name)
    {
        var value = root.GetProperty(name).GetString();
        if (value is not { Length: > 0 and <= 128 } || value.Any(char.IsControl)) throw new JsonException();
        return value;
    }

    private static bool HasDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) if (!names.Add(field.Name) || HasDuplicates(field.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicates(item)) return true;
        return false;
    }

    internal void CancelAll()
    {
        lock (_gate) { _stopping = true; foreach (var work in _active.Values) work.Cancellation.Cancel(); }
        _shutdown.Cancel();
    }

    private Task ActiveTasksAsync()
    {
        lock (_gate) return Task.WhenAll(_active.Values.Select(work => work.Task).ToArray());
    }

    internal async Task DrainAsync()
    {
        await ActiveTasksAsync();
        if (Volatile.Read(ref _transportFailed) != 0) throw new IOException("transport_failed");
    }

    public async ValueTask DisposeAsync()
    {
        CancelAll(); await ActiveTasksAsync(); await _cache.DisposeAsync(); _writeGate.Dispose(); _shutdown.Dispose();
    }

    private sealed class HostException(string code) : Exception { public string Code { get; } = code; }
    private sealed class Work(string id, string caseId, AutoPageCandidate candidate)
    {
        public string Id { get; } = id;
        public string CaseId { get; } = caseId;
        public AutoPageCandidate Candidate { get; } = candidate;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = System.Threading.Tasks.Task.CompletedTask;
        public AppSettings? Settings { get; set; }
        public ConcurrentDictionary<string, BrowserPageTranslationUpdate> Updates { get; } = new(StringComparer.Ordinal);
        public int RequestCount;
        public int CacheHits;
        public int FailedCount;
    }

    private sealed class CountedProvider(ITranslationProvider inner, Work work) : ITranslationProvider, IStreamingTranslationProvider
    {
        public async Task<string> TranslateRawAsync(TranslationRequest request, CancellationToken token)
        { Interlocked.Increment(ref work.RequestCount); return await inner.TranslateRawAsync(request, token); }
        public async Task<string> TranslateRawStreamingAsync(TranslationRequest request,
            IProgress<TranslationStreamUpdate> progress, CancellationToken token)
        {
            Interlocked.Increment(ref work.RequestCount);
            return inner is IStreamingTranslationProvider streaming
                ? await streaming.TranslateRawStreamingAsync(request, progress, token)
                : await inner.TranslateRawAsync(request, token);
        }
    }
}
