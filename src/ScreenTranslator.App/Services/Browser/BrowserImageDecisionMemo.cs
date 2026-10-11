using System.Security.Cryptography;
using System.Text.Json;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>
/// Exact-preview decision memo. Cached metadata is not OCR evidence: consumers
/// needing text or coordinates must reuse a completed translation or recognize
/// the original image. Pixels, OCR text and geometry never enter the stored DTO.
/// </summary>
public sealed class BrowserImageDecisionMemo
{
    public const string DefaultModelIdentity = "PP-OCRv5-mobile-det-rec-RapidOcrNet-4.2.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
        { "und", "latin", "zh-or-ja", "ja", "ko", "ar-script", "cyrillic", "el-script" };
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _maximumEntries;
    private readonly BrowserPageTranslationCache? _persistentCache;
    private readonly string _policyVersion;
    private readonly string _modelIdentity;
    private long _epoch, _persistentEpoch, _hits, _judgements;

    public BrowserImageDecisionMemo(int maximumEntries = 2048, BrowserPageTranslationCache? persistentCache = null,
        string policyVersion = ImageTranslationDecision.PolicyVersion, string modelIdentity = DefaultModelIdentity)
    {
        if (maximumEntries is < 0 or > 16_384) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion); ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        _maximumEntries = maximumEntries; _persistentCache = persistentCache;
        _policyVersion = policyVersion; _modelIdentity = modelIdentity;
        _persistentEpoch = persistentCache?.ClearEpoch ?? 0;
    }

    public long CacheHits => Interlocked.Read(ref _hits);
    public long JudgementCalls => Interlocked.Read(ref _judgements);

    public async Task<(ImageTranslationAssessment Assessment, bool CacheHit)> AssessAsync(CapturedBitmap preview,
        string targetLanguage, Func<CancellationToken, Task<ImageTranslationAssessment>> judge, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(judge);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage); token.ThrowIfCancellationRequested();
        // Hash every supplied preview byte, including stride padding, and dimensions.
        // Only the resulting digest is used as metadata; pixels are not retained.
        var pixels = Convert.ToHexString(SHA256.HashData(preview.Pixels.Span));
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "image-preview-decision-v1", policy = _policyVersion, model = _modelIdentity,
            pixels, preview.Width, preview.Height, preview.Stride, targetLanguage,
        })));
        long epoch, persistentEpoch;
        lock (_gate)
        {
            SynchronizeClearEpoch(); epoch = _epoch; persistentEpoch = _persistentEpoch;
            if (_entries.TryGetValue(key, out var entry))
            {
                token.ThrowIfCancellationRequested(); _lru.Remove(entry); _lru.AddFirst(entry);
                Interlocked.Increment(ref _hits); return (entry.Value.Assessment, true);
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
                        { Remember(key, decision); Interlocked.Increment(ref _hits); return (decision, true); }
                    }
                }
                else _persistentCache.Remove(key, persistentEpoch);
            }
        }
        token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _judgements);
        var assessment = await judge(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (CanRemember(assessment) && _maximumEntries > 0)
        {
            var decision = MetadataOnly(assessment);
            bool current;
            lock (_gate)
            {
                SynchronizeClearEpoch(); current = epoch == _epoch && persistentEpoch == _persistentEpoch;
                if (current) Remember(key, decision);
            }
            if (current) _persistentCache?.Set(key, JsonSerializer.Serialize(new StoredDecision(1, decision.NeedsTranslation,
                decision.ReasonCode, decision.Confidence, decision.DetectedLanguage), Json), persistentEpoch);
        }
        return (assessment, false);
    }

    /// <summary>The application also clears the shared persistent cache.</summary>
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

    private void Remember(string key, ImageTranslationAssessment decision)
    {
        if (_entries.Remove(key, out var old)) _lru.Remove(old);
        _entries.Add(key, _lru.AddFirst(new Entry(key, decision)));
        while (_entries.Count > _maximumEntries && _lru.Last is { } last)
        { _entries.Remove(last.Value.Key); _lru.RemoveLast(); }
    }

    private static bool CanRemember(ImageTranslationAssessment? value)
    {
        if (value is null || value.ErrorType is not null || !double.IsFinite(value.Confidence)
            || value.Confidence is < 0 or > 1 || !Languages.Contains(value.DetectedLanguage)) return false;
        return value.ReasonCode switch
        {
            "foreign-readable-text" => value.NeedsTranslation && value.Confidence >= .72 && value.DetectedLanguage != "und",
            "target-script" => !value.NeedsTranslation && value.Confidence >= .72 && value.DetectedLanguage != "und",
            "no-text" or "unreadable-or-noise" => !value.NeedsTranslation,
            _ => false, // Script ambiguity, uncertainty and local failures remain retryable.
        };
    }

    private static ImageTranslationAssessment MetadataOnly(ImageTranslationAssessment value) => new(
        value.NeedsTranslation, value.ReasonCode, value.Confidence, value.DetectedLanguage, [], 0) { ReasonCode = value.ReasonCode };

    private static ImageTranslationAssessment? ReadDecision(string serialized)
    {
        if (serialized.Length > 1_024) return null;
        try
        {
            var value = JsonSerializer.Deserialize<StoredDecision>(serialized, Json);
            if (value is null || value.Version != 1) return null;
            var decision = new ImageTranslationAssessment(value.NeedsTranslation, value.ReasonCode,
                value.Confidence, value.DetectedLanguage, [], 0) { ReasonCode = value.ReasonCode };
            return CanRemember(decision) ? decision : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record Entry(string Key, ImageTranslationAssessment Assessment);
    private sealed record StoredDecision(int Version, bool NeedsTranslation, string ReasonCode, double Confidence, string DetectedLanguage);
}
