using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>Original-source decisions persist without samples, eligible paragraphs or free-form reasoning.</summary>
public sealed class BrowserPageDecisionMemo
{
    private const int FormatVersion = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
        { "en", "fr", "de", "es", "it", "pt", "nl", "da", "no", "sv", "fi", "pl", "tr", "vi", "id", "ro", "cs",
            "zh", "ja", "ko", "ru", "uk", "bg", "ar", "el", "hi", "th", "latin", "ru-script", "zh-or-ja", "other-script", "und" };
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _maximumEntries;
    private readonly BrowserPageTranslationCache? _persistentCache;
    private readonly string _policyVersion;
    private long _epoch;
    private long _persistentEpoch;
    private long _hits, _judgements;

    public BrowserPageDecisionMemo(int maximumEntries = 2048,
        BrowserPageTranslationCache? persistentCache = null,
        string policyVersion = PageTranslationDecision.PolicyVersion)
    {
        if (maximumEntries is < 0 or > 16_384) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        _maximumEntries = maximumEntries; _persistentCache = persistentCache;
        _persistentEpoch = persistentCache?.ClearEpoch ?? 0;
        _policyVersion = policyVersion;
    }
    public long CacheHits => Interlocked.Read(ref _hits);
    public long JudgementCalls => Interlocked.Read(ref _judgements);

    public async Task<(PageTranslationAssessment Assessment, bool CacheHit)> AssessAsync(
        IReadOnlyList<PageTextSample> samples, string targetLanguage, BrowserPageJudgementMode mode,
        PageTextStatistics? statistics, Func<CancellationToken, Task<PageTranslationAssessment>> judge, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(judge);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        token.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "page-decision-original-sources-v2-html-whitespace", policy = _policyVersion,
            targetLanguage, mode, samples = samples.Select(sample => sample with
            { Text = Regex.Replace(sample.Text, "[ \\t\\r\\n\\f]+", " ").Trim() }).ToArray(), statistics,
        })));
        long epoch;
        long persistentEpoch;
        lock (_gate)
        {
            SynchronizeClearEpoch(); epoch = _epoch; persistentEpoch = _persistentEpoch;
            if (_entries.TryGetValue(key, out var entry))
            {
                token.ThrowIfCancellationRequested();
                _lru.Remove(entry); _lru.AddFirst(entry);
                Interlocked.Increment(ref _hits);
                return (entry.Value.Assessment, true);
            }
        }
        if (_persistentCache is not null && _maximumEntries > 0)
        {
            var cached = await _persistentCache.GetManyAsync([key], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (cached.TryGetValue(key, out var serialized))
            {
                var decision = ReadDecision(serialized);
                if (decision is not null)
                {
                    lock (_gate)
                    {
                        SynchronizeClearEpoch();
                        if (epoch == _epoch && persistentEpoch == _persistentEpoch)
                        {
                            Remember(key, decision); Interlocked.Increment(ref _hits);
                            return (decision, true);
                        }
                    }
                }
                else _persistentCache.Remove(key, persistentEpoch);
            }
        }
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _judgements);
        var assessment = await judge(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (CanRemember(assessment) && _maximumEntries > 0)
        {
            var decision = ToDecision(assessment);
            bool current;
            lock (_gate)
            {
                SynchronizeClearEpoch();
                current = epoch == _epoch && persistentEpoch == _persistentEpoch;
                if (current) Remember(key, decision);
            }
            if (current) _persistentCache?.Set(key, JsonSerializer.Serialize(ToStored(decision), Json), persistentEpoch);
        }
        return (assessment, false);
    }

    /// <summary>Clears local decisions; the application also clears the shared disk cache.</summary>
    public void Clear()
    {
        lock (_gate)
        { _epoch++; _entries.Clear(); _lru.Clear(); _persistentEpoch = _persistentCache?.ClearEpoch ?? 0; }
    }

    private void SynchronizeClearEpoch()
    {
        var current = _persistentCache?.ClearEpoch ?? 0;
        if (current == _persistentEpoch) return;
        _persistentEpoch = current; _epoch++; _entries.Clear(); _lru.Clear();
    }

    private void Remember(string key, PageTranslationAssessment assessment)
    {
        if (_entries.Remove(key, out var old)) _lru.Remove(old);
        _entries.Add(key, _lru.AddFirst(new Entry(key, assessment)));
        while (_entries.Count > _maximumEntries && _lru.Last is { } last)
        { _entries.Remove(last.Value.Key); _lru.RemoveLast(); }
    }

    private static bool CanRemember(PageTranslationAssessment? value)
    {
        if (value is null || !double.IsFinite(value.Confidence) || value.Confidence is < .5 or > 1
            || !Languages.Contains(value.DetectedLanguage)) return false;
        return value.ReasonCode switch
        {
            "foreign-script" => value.NeedsTranslation && !value.IsMixedTargetLanguage,
            "program-foreign-language" => value.NeedsTranslation && !value.IsMixedTargetLanguage,
            "program-target-language" => !value.NeedsTranslation && !value.IsMixedTargetLanguage,
            "mixed-target-body" => !value.NeedsTranslation && value.IsMixedTargetLanguage,
            "target-body" or "target-script" or "insufficient-body" => !value.NeedsTranslation && !value.IsMixedTargetLanguage,
            _ => false,
        };
    }

    private static PageTranslationAssessment ToDecision(PageTranslationAssessment assessment) => assessment with
    { Reason = assessment.ReasonCode, EligibleBlocks = [], ElapsedMilliseconds = 0 };

    private static StoredDecision ToStored(PageTranslationAssessment assessment) => new(FormatVersion,
        assessment.NeedsTranslation, assessment.ReasonCode, assessment.Confidence, assessment.DetectedLanguage,
        assessment.IsMixedTargetLanguage);

    private static PageTranslationAssessment? ReadDecision(string serialized)
    {
        if (serialized.Length > 2_048) return null;
        try
        {
            var value = JsonSerializer.Deserialize<StoredDecision>(serialized, Json);
            if (value is null || value.Version != FormatVersion) return null;
            var assessment = new PageTranslationAssessment(value.NeedsTranslation, value.ReasonCode,
                value.Confidence, value.DetectedLanguage, value.IsMixedTargetLanguage)
            { ReasonCode = value.ReasonCode };
            return CanRemember(assessment) ? assessment : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record Entry(string Key, PageTranslationAssessment Assessment);
    private sealed record StoredDecision(int Version, bool NeedsTranslation, string ReasonCode, double Confidence,
        string DetectedLanguage, bool IsMixedTargetLanguage);
}
