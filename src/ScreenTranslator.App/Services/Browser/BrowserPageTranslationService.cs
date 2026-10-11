using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using ScreenTranslator.Core.Abstractions;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserPageTranslationOptions
{
    public string SourceLanguage { get; init; } = "auto";
    public string TargetLanguage { get; init; } = "简体中文";
    public TranslationStyle Style { get; init; } = TranslationStyle.Natural;
    public string Context { get; init; } = string.Empty;
    // Provider/model/endpoint/prompt-policy identity, hashed into the cache key.
    // API secrets must never be supplied here.
    public string CacheScope { get; init; } = string.Empty;
    public bool ExplicitSelection { get; init; }
    public int Parallelism { get; init; } = 10;
    public int MaxBatchCharacters { get; init; } = 6_000;
    public int MaxBatchBlocks { get; init; } = 16;
    public int MaxPartCharacters { get; init; } = 1_800;
    public int MaxRequests { get; init; } = 24;
}

/// <summary>
/// Translates a bounded snapshot of plain DOM text. Concurrent batches retain
/// original IDs, and independently generated pieces of a long node are revealed
/// only when all previous pieces have completed. Inline paragraphs are staged until
/// their facts pass the existing guard, with unsafe nodes retried within the request
/// budget. An optional shared cache persists only complete, validated translations.
/// </summary>
public sealed class BrowserPageTranslationService
{
    public const int MaximumBlocks = 128;
    public const int MaximumCharacters = 32_768;
    private readonly Func<BrowserPageTranslationOptions, ITranslationProvider> _providerFactory;
    private readonly BrowserPageTranslationCache _cache;
    private readonly Action<string>? _diagnostic;
    // Shared by simultaneous documents, including background tabs and retries.
    private readonly SemaphoreSlim _providerSlots = new(10, 10);
    // Only hashes of source keys and answers proven unsafe again on a source-only
    // retry. This service-local FIFO is never persisted as translation cache data.
    private const int MaximumRejectionFingerprints = 256;
    private readonly object _rejectionGate = new();
    private readonly HashSet<string> _rejectionFingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> _rejectionOrder = new();

    public BrowserPageTranslationService(
        Func<BrowserPageTranslationOptions, ITranslationProvider> providerFactory,
        int maximumCacheEntries = BrowserPageTranslationCache.DefaultMaximumMemoryEntries,
        int maximumCacheCharacters = BrowserPageTranslationCache.DefaultMaximumMemoryCharacters,
        BrowserPageTranslationCache? cache = null, Action<string>? diagnostic = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _cache = cache ?? new BrowserPageTranslationCache(maximumCacheEntries, maximumCacheCharacters);
        _diagnostic = diagnostic;
    }

    public int CacheCount => _cache.Count;

    public async Task<BrowserPageTranslationResult> TranslateAsync(
        IReadOnlyList<BrowserPageTextBlock> blocks,
        BrowserPageTranslationOptions options,
        Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        Validate(blocks, options, onUpdate);
        cancellationToken.ThrowIfCancellationRequested();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cacheEpoch = _cache.ClearEpoch;
        var pageHasJapanese = blocks.Any(block => PageTextEligibility.HasJapaneseScript(block.Text)
            || PageTextEligibility.HasJapaneseScript(block.Context));
        var nodes = blocks.Select(block => new NodeState(block,
            CacheKey(block, blocks, options, pageHasJapanese || block.ForeignHanContext), GroupContext(block, blocks), cacheEpoch,
            LocalCalendar(block, blocks, options) is null
                && (PageCalendarText.TranslateLabel(block.Text, options.TargetLanguage) is not null
                    || PageTextEligibility.NeedsTranslation(block.Text, options.TargetLanguage, pageHasJapanese || block.ForeignHanContext,
                        explicitSelection: options.ExplicitSelection)),
            BrowserParagraphTranslationArtifacts.Key(block, options, IdentityContext(block, blocks)
                + "\nforeignHanContext=" + (pageHasJapanese || block.ForeignHanContext),
                SelectionOverridesPolicy(block, options, pageHasJapanese || block.ForeignHanContext)),
            LegacyCacheKeys(block, blocks, options, pageHasJapanese || block.ForeignHanContext),
            pageHasJapanese || block.ForeignHanContext)).ToArray();
        var semanticGate = new object();
        var semanticGroups = nodes.GroupBy(node => node.Block.GroupId is { Length: > 0 } group
            ? "group:" + group : "node:" + node.Block.Id).Select(group => group.ToArray()).ToList();
        foreach (var group in semanticGroups)
        {
            // Legacy lists can share a group ID while containing independent sentences.
            // Source ranges/context identify inline fragments even in a one-node selection.
            var stageInline = group.Any(node => BrowserParagraphTranslationArtifacts.HasIdentity(node.Block)
                    && node.Block.SourceLength != node.Block.SourceGroupLength
                || node.Block.ContextKind is "source" or "selection" && node.Block.Context is { Length: > 0 } context
                    && NormalizeHtmlText(context) != NormalizeHtmlText(node.Block.Text))
                || group.Length > 1 && !group.All(node => node.Block.Text.TrimEnd().EndsWith('.')
                    || node.Block.Text.TrimEnd().EndsWith('?') || node.Block.Text.TrimEnd().EndsWith('!')
                    || node.Block.Text.TrimEnd().EndsWith('。') || node.Block.Text.TrimEnd().EndsWith('？')
                    || node.Block.Text.TrimEnd().EndsWith('！'));
            foreach (var node in group) node.StageInline = stageInline;
        }
        var parts = new List<TextPart>();
        var cacheHits = 0;
        var cached = await _cache.GetManyAsync(nodes.Where(node => node.Eligible).Select(node => node.CacheKey)
            .Concat(nodes.Where(node => node.Eligible).SelectMany(node => node.LegacyKeys))
            .Concat(nodes.Where(node => node.Eligible && node.ArtifactKey is not null).Select(node => node.ArtifactKey!)), cancellationToken).ConfigureAwait(false);
        var artifacts = nodes.Where(node => node.ArtifactKey is not null).GroupBy(node => node.ArtifactKey!)
            .ToDictionary(group => group.Key, group => cached.TryGetValue(group.Key, out var serialized)
                ? BrowserParagraphTranslationArtifacts.Read(serialized, group.First().Block) : null, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!node.Eligible) { node.FinalTranslation = LocalCalendar(node.Block, blocks, options) ?? node.Block.Text; continue; }
            string? translation = cached.TryGetValue(node.CacheKey, out var exact) ? exact : null;
            if (translation is null)
                foreach (var key in node.LegacyKeys)
                    if (cached.TryGetValue(key, out var legacy)) { translation = legacy; node.LegacyHit = true; break; }
            if (translation is null && node.ArtifactKey is { } artifactKey && artifacts[artifactKey] is { } artifact)
            {
                translation = BrowserParagraphTranslationArtifacts.Replay(artifact, node.Block, options.TargetLanguage, node.ForeignHanContext, options.ExplicitSelection);
                node.ArtifactHit = translation is not null;
            }
            if (translation is not null)
            {
                node.CacheHit = true;
                node.FinalTranslation = translation;
            }
        }
        // Restored strings must still satisfy the current node/group facts.
        // This happens before any prefix is sent or any model request starts.
        foreach (var group in semanticGroups.Where(group => group.All(node => node.FinalTranslation is not null)))
        {
            if (PageTextSemanticGuard.FindUnsafeIds(group.Select(node => node.Block).ToArray(),
                group.ToDictionary(node => node.Block.Id, node => node.FinalTranslation!, StringComparer.Ordinal)).Count != 0)
            {
                foreach (var node in group.Where(node => node.Eligible))
                {
                    node.FinalTranslation = null; node.CacheHit = false; node.ArtifactHit = false; node.LegacyHit = false;
                    _cache.Remove(node.CacheKey, cacheEpoch);
                    foreach (var legacy in node.LegacyKeys) _cache.Remove(legacy, cacheEpoch);
                }
            }
            else foreach (var node in group) node.SemanticVerified = true;
        }
        foreach (var node in nodes)
        {
            if (node.CacheHit) { cacheHits++; continue; }
            if (node.Eligible)
            {
                var pieces = Split(node.Block.Text, Math.Min(options.MaxPartCharacters, options.MaxBatchCharacters));
                node.Parts = pieces.Select((piece, index) => new TextPart(
                    "n" + parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + index,
                    node, index, piece.Text, piece.Joiner)).ToArray();
                parts.AddRange(node.Parts);
            }
        }
        var batches = Batch(parts, options);
        // The entire request budget is validated before any provider/key/network operation.
        if (batches.Count > options.MaxRequests)
            throw new TranslationConfigurationException("网页段落超过本轮请求预算，已保留原文。请缩小自动翻译范围。");

        Exception? firstFailure = null;
        void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref firstFailure, error, null);
            active.Cancel();
        }
        var updates = new UpdateDispatcher(onUpdate, cancellationToken, Fail);
        var sender = updates.RunAsync();
        foreach (var node in nodes.Where(node => (node.CacheHit || !node.Eligible)
            && (!node.StageInline || semanticGroups.First(group => group.Contains(node)).All(item => item.FinalTranslation is not null))))
            updates.Publish(new(node.Block.Id, node.FinalTranslation!, true, node.CacheHit));

        var requestCount = 0;
        var requestBudget = new object();
        var remainingRetries = options.MaxRequests - batches.Count;
        async Task<bool> TranslateBatch(IReadOnlyList<TextPart> current, CancellationToken token,
            bool retry = false, bool semanticRetry = false)
        {
            // Reserve retries from the same finite request budget.
            lock (requestBudget)
            {
                if (retry && remainingRetries <= 0)
                    return false;
                if (retry) remainingRetries--;
                requestCount++;
            }
            await _providerSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await TranslateProviderBatch(current, token, semanticRetry).ConfigureAwait(false);
            }
            finally { _providerSlots.Release(); }
            return true;
        }
        async Task TranslateProviderBatch(IReadOnlyList<TextPart> current, CancellationToken token, bool semanticRetry)
        {
            var batch = current;
            token.ThrowIfCancellationRequested();
            var provider = _providerFactory(options)
                ?? throw new InvalidOperationException("网页翻译服务未提供模型连接。");
            var input = batch.Select((part, index) => new OcrBlock(part.Id, part.Text, 1, default, index)).ToArray();
            var sourceLanguage = options.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && batch.All(part => part.Node.Block.ForeignHanContext && IsPureHan(part.Text)) ? "ja" : options.SourceLanguage;
            var request = new TranslationRequest(sourceLanguage, options.TargetLanguage,
                options.Style, semanticRetry ? SemanticRetryContext(batch, options) : RequestContext(batch, options), input)
            {
                ThinkingOverride = ThinkingLevel.None,
                IsPageText = true,
            };
            var stream = new InlineProgress(update =>
            {
                if (token.IsCancellationRequested || update.ImageSegments is null) return;
                foreach (var segment in update.ImageSegments)
                {
                    var part = batch.FirstOrDefault(item => item.Id == segment.Id);
                    if (part is not null) ApplyPart(part, segment.Translation, false, token, updates,
                        semanticGate, semanticGroups);
                }
            });
            var raw = provider is IStreamingTranslationProvider streaming
                ? await streaming.TranslateRawStreamingAsync(request, stream, token).ConfigureAwait(false)
                : await provider.TranslateRawAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var final = PageTextStreaming.ParseComplete(raw, input);
            var byPart = final.ToDictionary(block => block.Id, StringComparer.Ordinal);
            var batchNodes = batch.GroupBy(part => part.Node).ToArray();
            var batchSources = batchNodes.ToDictionary(group => group.Key,
                group => new BrowserPageTextBlock(group.Key.Block.Id, string.Concat(group.Select(part => part.Text))));
            var batchAnswers = batchNodes.ToDictionary(group => group.Key.Block.Id,
                group => string.Concat(group.Select(part => byPart[part.Id].Translation)), StringComparer.Ordinal);
            var assessment = PageTextSemanticGuard.Assess(batchSources.Values.ToArray(), batchAnswers);
            var unsafeParts = assessment.UnsafeIds;
            if (unsafeParts.Count != 0)
            {
                lock (semanticGate)
                {
                    var affected = batch.Select(part => part.Node).Distinct()
                        .Where(node => node.FinalTranslation is null && unsafeParts.Contains(node.Block.Id, StringComparer.Ordinal)).ToHashSet();
                    RecordTransferWitnesses(assessment, affected.ToDictionary(node => node,
                        node => (batchSources[node], batchAnswers[node.Block.Id])), (owner, receiver) =>
                    {
                        var linked = semanticGroups.Where(group => group.Any(node => node == owner || node == receiver)).ToArray();
                        if (linked.Length > 1)
                        {
                            foreach (var group in linked) semanticGroups.Remove(group);
                            semanticGroups.Add(linked.SelectMany(group => group).ToArray());
                        }
                    });
                    foreach (var node in affected)
                    {
                        node.RetryRequired = true;
                        node.CurrentGuardUnsafe = true;
                        node.StageInline = true;
                    }
                }
            }
            foreach (var segment in final)
                ApplyPart(batch.First(part => part.Id == segment.Id), segment.Translation, true, token, updates,
                    semanticGate, semanticGroups);
        }
        void MarkFormatFailure(IReadOnlyList<TextPart> failed)
        {
            lock (semanticGate)
            {
                var affected = failed.Select(part => part.Node).ToHashSet();
                foreach (var group in semanticGroups.Where(group => group.Any(affected.Contains)))
                    MarkSemanticFailure(group, updates);
            }
        }
        try
        {
            try
            {
                await Parallel.ForEachAsync(batches, new ParallelOptions
                { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = active.Token },
                async (batch, token) =>
                {
                    try
                    {
                        try { await TranslateBatch(batch, token).ConfigureAwait(false); }
                        catch (TranslationFormatException)
                        {
                            // One malformed answer must not cancel the rest of the page.
                            // Smaller retries reduce omitted IDs and keep successful groups.
                            foreach (var retry in batch.Chunk(4))
                                try
                                {
                                    if (!await TranslateBatch(retry, token, retry: true).ConfigureAwait(false)) MarkFormatFailure(retry);
                                }
                                catch (TranslationFormatException) { MarkFormatFailure(retry); }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception error) { Fail(error); }
                }).ConfigureAwait(false);
                // Initial batches have drained, including all parts of cross-batch groups.
                // Only provisional unsafe nodes are reset; safe completed groups/cache survive.
                lock (semanticGate)
                    foreach (var group in semanticGroups.Where(group => !group.Any(node => node.SemanticFailed)
                        && group.All(node => node.VerifiedTranslation is not null || node.FinalTranslation is not null)))
                    {
                        var assessment = PageTextSemanticGuard.Assess(group.Select(node => node.Block).ToArray(),
                            group.ToDictionary(node => node.Block.Id, node => node.FinalTranslation ?? node.VerifiedTranslation!, StringComparer.Ordinal));
                        var unsafeIds = assessment.UnsafeIds;
                        RecordTransferWitnesses(assessment, group.Where(node => !node.SemanticVerified
                            && unsafeIds.Contains(node.Block.Id, StringComparer.Ordinal)).ToDictionary(node => node,
                                node => (node.Block, node.FinalTranslation ?? node.VerifiedTranslation!)));
                        foreach (var node in group.Where(node => !node.SemanticVerified
                            && unsafeIds.Contains(node.Block.Id, StringComparer.Ordinal)))
                        {
                            RejectCandidate(node, node.FinalTranslation ?? node.VerifiedTranslation!, true,
                                group, updates);
                        }
                    }
                foreach (var node in nodes.Where(node => node.CacheHit && node.RetryRequired && !node.SemanticVerified))
                {
                    // A partial cache replay has not yet passed this paragraph's guard.
                    // Rejected cached fragments need their own source parts for recovery.
                    node.FinalTranslation = null;
                    node.CacheHit = false; node.ArtifactHit = false; node.LegacyHit = false;
                    cacheHits--;
                    _cache.Remove(node.CacheKey, cacheEpoch);
                    foreach (var key in node.LegacyKeys) _cache.Remove(key, cacheEpoch);
                    node.Parts = Split(node.Block.Text, Math.Min(options.MaxPartCharacters, options.MaxBatchCharacters))
                        .Select((piece, index) => new TextPart("c" + Array.IndexOf(nodes, node) + "-" + index,
                            node, index, piece.Text, piece.Joiner)).ToArray();
                }
                while (!active.IsCancellationRequested && nodes.Any(node => node.RetryRequired && !node.SemanticFailed))
                {
                    var pending = nodes.Where(node => node.RetryRequired && !node.SemanticFailed
                        && node.FinalTranslation is null).ToArray();
                    if (pending.Length == 0 || remainingRetries == 0) break;
                    foreach (var node in pending)
                    {
                        if (node.SemanticFailed) continue;
                        active.Token.ThrowIfCancellationRequested();
                        var retryBatches = Batch(node.Parts, options);
                        if (remainingRetries < retryBatches.Count) continue;
                        lock (semanticGate)
                        {
                            node.RetryRequired = false;
                            node.CurrentGuardUnsafe = false;
                            node.SemanticRetryActive = true;
                            node.VerifiedTranslation = null;
                            foreach (var part in node.Parts) { part.Translation = string.Empty; part.Complete = false; }
                        }
                        foreach (var retry in retryBatches)
                            try { await TranslateBatch(retry, active.Token, retry: true, semanticRetry: true).ConfigureAwait(false); }
                            catch (TranslationFormatException) { node.RetryRequired = true; }
                    }
                    // A split node requires enough budget for every piece; do not spin
                    // when the remaining requests cannot complete any pending node.
                    if (nodes.Where(node => node.RetryRequired && !node.SemanticFailed && node.FinalTranslation is null)
                        .All(node => Batch(node.Parts, options).Count > remainingRetries)) break;
                }
                lock (semanticGate)
                    foreach (var group in semanticGroups.Where(group => group.Any(node => node.RetryRequired)))
                        MarkSemanticFailure(group, updates);
            }
            catch (OperationCanceledException) when (active.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
            cancellationToken.ThrowIfCancellationRequested();
            if (firstFailure is not null)
            {
                // All provider tasks have now drained; queued prefixes are replaced by rollback.
                foreach (var node in nodes.Where(node => node.FinalTranslation is null && !node.SemanticFailed))
                    updates.Publish(new(node.Block.Id, string.Empty, false, Failed: true));
            }
        }
        finally
        {
            updates.Complete();
            await sender.ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in nodes.Where(node => node.ArtifactKey is not null).GroupBy(node => node.ArtifactKey!))
        {
            if (group.Any(node => node.SemanticFailed)) { _cache.Remove(group.Key, cacheEpoch); continue; }
            var complete = group.Where(node => node.Eligible && node.FinalTranslation is not null).ToArray();
            if (complete.Length == 0) continue;
            await _cache.UpdateAsync(group.Key, current => BrowserParagraphTranslationArtifacts.Merge(
                current is null ? artifacts[group.Key] : BrowserParagraphTranslationArtifacts.Read(current, group.First().Block)
                    ?? artifacts[group.Key], group.First().Block,
                complete.Select(node => (node.Block, node.FinalTranslation!))), cacheEpoch, cancellationToken).ConfigureAwait(false);
        }
        foreach (var node in nodes.Where(node => node.Eligible && node.CacheHit && node.FinalTranslation is not null && !node.SemanticFailed))
            _cache.Set(node.CacheKey, node.FinalTranslation!, cacheEpoch);
        _diagnostic?.Invoke($"browser_page_cache nodes={nodes.Length} eligible={nodes.Count(node => node.Eligible)} native={nodes.Count(node => !node.Eligible)} hits={cacheHits} artifact_hits={nodes.Count(node => node.ArtifactHit)} legacy_hits={nodes.Count(node => node.LegacyHit)} requests={requestCount} semantic_failed={nodes.Count(node => node.SemanticFailed)} failed={firstFailure is not null}");
        if (firstFailure is not null) ExceptionDispatchInfo.Capture(firstFailure).Throw();
        return new(nodes.Select(node => new BrowserPageTranslationUpdate(node.Block.Id,
            node.FinalTranslation ?? string.Empty, !node.SemanticFailed, node.CacheHit, node.SemanticFailed)).ToArray(), requestCount, cacheHits);
    }

    private void ApplyPart(TextPart part, string translation, bool complete,
        CancellationToken token, UpdateDispatcher updates, object semanticGate, IReadOnlyList<NodeState[]> semanticGroups)
    {
        if (token.IsCancellationRequested) return;
        var node = part.Node;
        lock (semanticGate)
        {
            if (token.IsCancellationRequested || node.FinalTranslation is not null || node.SemanticFailed || part.Complete) return;
            // Stream replacement is monotonic. A verified final may correct an incomplete prefix.
            if (!complete && !translation.StartsWith(part.Translation, StringComparison.Ordinal)) return;
            part.Translation = translation;
            part.Complete = complete;
            var visible = new StringBuilder();
            foreach (var item in node.Parts)
            {
                visible.Append(item.Translation);
                if (!item.Complete) break;
                visible.Append(item.Joiner);
            }
            var allComplete = node.Parts.All(item => item.Complete);
            var text = visible.ToString();
            if (allComplete)
            {
                node.VerifiedTranslation = text;
                var group = semanticGroups.First(items => items.Contains(node));
                if (node.TransferWitnesses.Values.Any(witness => PageTextSemanticGuard.Assess(
                    [witness.Source, node.Block], new Dictionary<string, string>
                    { [witness.Source.Id] = witness.Answer, [node.Block.Id] = text })
                    .NumericTransfers.Any(edge => edge.OwnerId == witness.Source.Id && edge.ReceivingId == node.Block.Id)))
                {
                    node.RetryRequired = true;
                    node.CurrentGuardUnsafe = true;
                }
                // A fragment already rejected by the guard remains unsafe even if
                // a later neighbour restores its own missing fact. Do not accept
                // an unchanged transferred-fact answer in a single-node fallback.
                if (node.RetryRequired || node.RejectedCandidates.Contains(AnswerFingerprint(text)))
                {
                    // Every fresh answer is checked by the batch guard; when the
                    // paragraph is available, recheck the current full candidates too.
                    var guardUnsafe = node.CurrentGuardUnsafe
                        || group.All(item => item.VerifiedTranslation is not null || item.FinalTranslation is not null)
                            && PageTextSemanticGuard.FindUnsafeIds(group.Select(item => item.Block).ToArray(),
                                group.ToDictionary(item => item.Block.Id,
                                    item => item.FinalTranslation ?? item.VerifiedTranslation!, StringComparer.Ordinal))
                                .Contains(node.Block.Id, StringComparer.Ordinal);
                    RejectCandidate(node, text, guardUnsafe, group, updates);
                    return;
                }
                if (!group.Any(item => item.RetryRequired || item.SemanticFailed)
                    && group.All(item => item.VerifiedTranslation is not null || item.FinalTranslation is not null))
                {
                    var originals = group.Select(item => item.Block).ToArray();
                    var translations = group.ToDictionary(item => item.Block.Id,
                        item => item.FinalTranslation ?? item.VerifiedTranslation!, StringComparer.Ordinal);
                    var assessment = PageTextSemanticGuard.Assess(originals, translations);
                    var unsafeIds = assessment.UnsafeIds;
                    if (unsafeIds.Count != 0)
                    {
                        RecordTransferWitnesses(assessment, group.Where(item => !item.SemanticVerified
                            && unsafeIds.Contains(item.Block.Id, StringComparer.Ordinal)).ToDictionary(item => item,
                                item => (item.Block, translations[item.Block.Id])));
                        foreach (var item in group.Where(item => !item.SemanticVerified
                            && unsafeIds.Contains(item.Block.Id, StringComparer.Ordinal)))
                        {
                            RejectCandidate(item, translations[item.Block.Id], true, group, updates);
                        }
                        return;
                    }
                    foreach (var item in group.Where(item => item.FinalTranslation is null && item.Eligible))
                    {
                        item.FinalTranslation = item.VerifiedTranslation!;
                        _cache.Set(item.CacheKey, item.FinalTranslation, item.CacheEpoch);
                        updates.Publish(new(item.Block.Id, item.FinalTranslation, true));
                    }
                    foreach (var item in group) item.SemanticVerified = true;
                    foreach (var item in group.Where(item => item.StageInline && (item.CacheHit || !item.Eligible)))
                        updates.Publish(new(item.Block.Id, item.FinalTranslation!, true, item.CacheHit));
                    return;
                }
            }
            if (!node.StageInline && !node.RetryRequired && text.Length != 0 && text != node.LastVisible)
            {
                node.LastVisible = text;
                updates.Publish(new(node.Block.Id, text, false));
            }
        }
    }

    private static string AnswerFingerprint(string answer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(answer)));

    private static void RecordTransferWitnesses(
        PageTextSemanticAssessment assessment,
        IReadOnlyDictionary<NodeState, (BrowserPageTextBlock Source, string Answer)> candidates,
        Action<NodeState, NodeState>? link = null)
    {
        // Only real ownership edges from the unchanged guard create dependencies.
        // Receivers may also have unrelated errors (such as a changed weekday);
        // retain the actual owner evidence even while those errors are corrected.
        var byId = candidates.Keys.ToDictionary(node => node.Block.Id, StringComparer.Ordinal);
        foreach (var edge in assessment.NumericTransfers)
        {
            if (!byId.TryGetValue(edge.OwnerId, out var owner)
                || !byId.TryGetValue(edge.ReceivingId, out var receiver))
                continue;
            var witness = candidates[owner];
            // Invocation-local evidence keeps the transferred fact unsafe after
            // the owner recovers, even if the receiver submits a different answer.
            receiver.TransferWitnesses.TryAdd(owner.Block.Id + AnswerFingerprint(witness.Source.Text + "\n" + witness.Answer), witness);
            link?.Invoke(owner, receiver);
        }
    }

    private void RejectCandidate(NodeState node, string answer, bool guardUnsafe,
        NodeState[] group, UpdateDispatcher updates)
    {
        var answerHash = AnswerFingerprint(answer);
        var repeated = !node.RejectedCandidates.Add(answerHash);
        var fingerprint = node.CacheKey + answerHash;
        bool known;
        lock (_rejectionGate)
        {
            known = _rejectionFingerprints.Contains(fingerprint);
            if (node.SemanticRetryActive && repeated && guardUnsafe && !known)
            {
                if (_rejectionFingerprints.Count == MaximumRejectionFingerprints)
                    _rejectionFingerprints.Remove(_rejectionOrder.Dequeue());
                _rejectionFingerprints.Add(fingerprint);
                _rejectionOrder.Enqueue(fingerprint);
            }
        }
        node.RetryRequired = true;
        // History only shortens retries after today's guard also rejected the
        // fresh answer. A new corrected answer can always complete and cache.
        if ((guardUnsafe && known) || (node.SemanticRetryActive && repeated))
            MarkSemanticFailure(group, updates);
    }

    private void MarkSemanticFailure(IEnumerable<NodeState> group, UpdateDispatcher updates)
    {
        foreach (var item in group)
        {
            if (!item.Eligible || item.SemanticVerified || item.SemanticFailed) continue;
            item.SemanticFailed = true;
            item.FinalTranslation = null;
            if (item.Eligible) _cache.Remove(item.CacheKey, item.CacheEpoch);
            updates.Publish(new(item.Block.Id, string.Empty, false, Failed: true));
        }
    }

    private static void Validate(IReadOnlyList<BrowserPageTextBlock> blocks,
        BrowserPageTranslationOptions options,
        Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (blocks.Count > MaximumBlocks || blocks.Any(block => block is null
            || string.IsNullOrWhiteSpace(block.Id) || block.Id.Length > 128 || string.IsNullOrWhiteSpace(block.Text)
            || block.GroupId?.Length > 128 || block.Context?.Length > 512 || !ValidSourceMetadata(block))
            || blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() != blocks.Count
            || blocks.Sum(block => (long)block.Text.Length + (block.Context?.Length ?? 0)) > MaximumCharacters)
            throw new ArgumentException("网页文本快照超出边界或段落 ID 无效。", nameof(blocks));
        if (options.Parallelism is < 1 or > 10 || options.MaxBatchCharacters is < 128 or > 6_000
            || options.MaxBatchBlocks is < 1 or > 16 || options.MaxPartCharacters is < 64 or > 6_000
            || options.MaxRequests is < 1 or > 32 || string.IsNullOrWhiteSpace(options.TargetLanguage)
            || options.TargetLanguage.Length > 64 || string.IsNullOrWhiteSpace(options.SourceLanguage)
            || options.SourceLanguage.Length > 64 || options.Context is null || options.Context.Length > 2_048
            || options.CacheScope is null || options.CacheScope.Length > 2_048)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static List<IReadOnlyList<TextPart>> Batch(IReadOnlyList<TextPart> parts, BrowserPageTranslationOptions options)
    {
        var batches = new List<IReadOnlyList<TextPart>>();
        var batch = new List<TextPart>();
        var characters = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (batch.Count > 0 && (index == 0 || GroupKey(parts[index - 1]) != GroupKey(part)))
            {
                var groupEnd = index;
                var groupCharacters = 0;
                while (groupEnd < parts.Count && GroupKey(parts[groupEnd]) == GroupKey(part))
                { groupCharacters += parts[groupEnd].Text.Length; groupEnd++; }
                var groupCount = groupEnd - index;
                // Keep a coherent supplied group together whenever it fits in one request.
                if (groupCount <= options.MaxBatchBlocks && groupCharacters <= options.MaxBatchCharacters
                    && (batch.Count + groupCount > options.MaxBatchBlocks
                        || characters + groupCharacters > options.MaxBatchCharacters))
                { batches.Add(batch.ToArray()); batch.Clear(); characters = 0; }
            }
            if (batch.Count > 0 && (batch.Count == options.MaxBatchBlocks
                || characters + part.Text.Length > options.MaxBatchCharacters))
            { batches.Add(batch.ToArray()); batch.Clear(); characters = 0; }
            batch.Add(part);
            characters += part.Text.Length;
        }
        if (batch.Count > 0) batches.Add(batch.ToArray());
        return batches;
    }

    private static string GroupKey(TextPart part) => part.Node.Block.GroupId ?? part.Node.Block.Id;

    private static IReadOnlyList<SourcePiece> Split(string text, int maximum)
    {
        var result = new List<SourcePiece>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maximum);
            if (end < text.Length)
            {
                var lower = start + maximum / 2;
                for (var at = end - 1; at >= lower; at--)
                    if (text[at] is '.' or '!' or '?' or '。' or '！' or '？' or ';' or '；' or '\n')
                    { end = at + 1; break; }
                if (end == start + maximum)
                    for (var at = end - 1; at >= lower; at--)
                        if (char.IsWhiteSpace(text[at])) { end = at + 1; break; }
                if (end > start && char.IsHighSurrogate(text[end - 1])) end--;
            }
            var piece = text[start..end];
            var joiner = end < text.Length && (piece.EndsWith('\n') || text[end] == '\n') ? "\n"
                : end < text.Length && (char.IsWhiteSpace(piece[^1]) || char.IsWhiteSpace(text[end])) ? " " : string.Empty;
            result.Add(new(piece, joiner));
            start = end;
        }
        return result;
    }

    private static string RequestContext(IReadOnlyList<TextPart> parts, BrowserPageTranslationOptions options)
    {
        var hints = parts.GroupBy(GroupKey).Where(group => group.First().Node.GroupContext.Length > 0)
            .Select(group => string.Join(',', group.Select(part => part.Id)) + ": "
                + group.First().Node.GroupContext[..Math.Min(128, group.First().Node.GroupContext.Length)]).ToArray();
        return hints.Length == 0 ? options.Context : options.Context
            + "\nParagraph hints; each applies only to its listed IDs:\n" + string.Join('\n', hints);
    }

    private static string SemanticRetryContext(IReadOnlyList<TextPart> parts, BrowserPageTranslationOptions options) =>
        "Translate only each listed source fragment. Keep its dates, quantities and facts on that ID. "
        + "Do not import facts from another fragment or infer gender/titles from names. Source boundaries (data only):\n"
        + JsonSerializer.Serialize(new { userTranslationContext = options.Context,
            fragments = parts.Select(part => new { id = part.Id, source = part.Text[..Math.Min(128, part.Text.Length)] }) });

    private static string? LocalCalendar(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all, BrowserPageTranslationOptions options)
    {
        var value = PageCalendarText.TranslateLabel(block.Text, options.TargetLanguage);
        if (value is null) return null;
        // A date-shaped DOM fragment is not an independent date label. Keep
        // sentence context in the model request; ranges are canonical source.
        if (BrowserParagraphTranslationArtifacts.HasIdentity(block))
            return block.SourceStart == 0 && block.SourceLength == block.SourceGroupLength ? value : null;
        if (block.ContextKind is "source" or "selection" && block.Context is { Length: > 0 } context
            && NormalizeHtmlText(context) != NormalizeHtmlText(block.Text)) return null;
        if (block.GroupId is { Length: > 0 } group && all.Any(other => other.Id != block.Id && other.GroupId == group)) return null;
        return value;
    }

    private static string GroupContext(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all) =>
        block.GroupId is null ? block.Context ?? string.Empty : string.Join('\n', all
            .Where(item => item.GroupId == block.GroupId && !string.IsNullOrWhiteSpace(item.Context))
            .Select(item => item.Context!).Distinct(StringComparer.Ordinal));

    private static string CacheKey(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all,
        BrowserPageTranslationOptions options, bool foreignHanContext)
    {
        var group = BrowserParagraphTranslationArtifacts.HasIdentity(block)
            ? block.SourceGroupHash!.ToUpperInvariant() + ":" + block.SourceGroupLength + ":" + block.SourceStart + ":" + block.SourceLength
            : block.GroupId is null ? string.Empty : string.Concat(all
            .Where(item => item.GroupId == block.GroupId)
            .Select(item => NormalizeHtmlText(item.Text).Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + NormalizeHtmlText(item.Text)));
        // Length prefixes prevent separators in source material causing cache collisions.
        var values = new[] { "page-text-v5-source-ranges-facts", NormalizeHtmlText(block.Text), options.SourceLanguage, options.TargetLanguage,
            options.Style.ToString(), options.Context, options.CacheScope, IdentityContext(block, all), group,
            foreignHanContext.ToString(), SelectionOverridesPolicy(block, options, foreignHanContext).ToString() };
        var key = string.Concat(values.Select(value => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static string IdentityContext(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all)
    {
        BrowserPageTextBlock[] members = block.GroupId is null ? [block] : all.Where(item => item.GroupId == block.GroupId).ToArray();
        return string.Join('\n', members.Select(item => item.Context).Where(context => !string.IsNullOrWhiteSpace(context))
            .Distinct(StringComparer.Ordinal).Where(context => !IsSourceContext(context!, block, members)));
    }

    private static bool IsSourceContext(string context, BrowserPageTextBlock identity, IReadOnlyList<BrowserPageTextBlock> members)
    {
        if (BrowserParagraphTranslationArtifacts.HasIdentity(identity))
        {
            if (identity.ContextKind is not ("source" or "selection")) return false;
            if (identity.ContextKind == "source")
            {
                if (context.Length != Math.Min(512, identity.SourceGroupLength!.Value)) return false;
                foreach (var member in members)
                {
                    if (!BrowserParagraphTranslationArtifacts.HasIdentity(member)
                        || !string.Equals(member.SourceGroupHash, identity.SourceGroupHash, StringComparison.OrdinalIgnoreCase)
                        || member.SourceGroupLength != identity.SourceGroupLength) return false;
                    var start = member.SourceStart!.Value;
                    var end = Math.Min(start + member.Text.Length, context.Length);
                    if (start < end && !context.AsSpan(start, end - start).SequenceEqual(member.Text.AsSpan(0, end - start))) return false;
                }
                return true;
            }
            // A selected source hint is derived only when its exact content is the
            // canonical current selected wire chunks, not arbitrary terminology.
            if (identity.ContextKind != "selection" || members.Any(member => member.ContextKind != "selection"
                || !BrowserParagraphTranslationArtifacts.HasIdentity(member)
                || !string.Equals(member.SourceGroupHash, identity.SourceGroupHash, StringComparison.OrdinalIgnoreCase)
                || member.SourceGroupLength != identity.SourceGroupLength)) return false;
            var selected = SelectedSourceHint(members);
            return context == selected[..Math.Min(512, selected.Length)];
        }
        var source = NormalizeHtmlText(context); var cursor = 0;
        foreach (var member in members)
        {
            var piece = NormalizeHtmlText(member.Text);
            var at = source.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (at < 0 || source[cursor..at].Any(character => !char.IsWhiteSpace(character))) return false;
            cursor = at + piece.Length;
        }
        return cursor == source.Length || source[cursor..].All(char.IsWhiteSpace);
    }

    private static string NormalizeHtmlText(string source) => Regex.Replace(source, "[ \\t\\r\\n\\f]+", " ").Trim();

    private static string SelectedSourceHint(IReadOnlyList<BrowserPageTextBlock> members)
    {
        var sorted = members.OrderBy(member => member.SourceStart).ToArray();
        var text = new StringBuilder(); var end = -1;
        foreach (var member in sorted)
        {
            if (member.SourceStart < end) return string.Empty;
            if (end >= 0 && member.SourceStart > end) text.Append(' ');
            text.Append(member.Text); end = member.SourceStart!.Value + member.Text.Length;
        }
        return text.ToString();
    }

    private static bool SelectionOverridesPolicy(BrowserPageTextBlock block, BrowserPageTranslationOptions options, bool foreignHanContext) =>
        options.ExplicitSelection && !PageTextEligibility.NeedsTranslation(block.Text, options.TargetLanguage, foreignHanContext)
            && PageTextEligibility.NeedsTranslation(block.Text, options.TargetLanguage, foreignHanContext, explicitSelection: true);

    private static bool IsPureHan(string text)
    {
        var letters = text.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(rune => rune.Value is >= 0x3400 and <= 0x9fff
            or >= 0x20000 and <= 0x323af or >= 0xf900 and <= 0xfaff);
    }

    private static bool ValidSourceMetadata(BrowserPageTextBlock block) =>
        block.SourceGroupHash is null && block.SourceGroupLength is null && block.SourceStart is null && block.SourceLength is null
        || BrowserParagraphTranslationArtifacts.HasIdentity(block) && block.SourceGroupHash!.All(Uri.IsHexDigit);

    private static IReadOnlyList<string> LegacyCacheKeys(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all,
        BrowserPageTranslationOptions options, bool foreignHanContext)
    {
        // Old v4 data does not bind the full author's parent. Only migrate when
        // the current complete parent can be proved, never from a lone bank/title
        // selected from a different author paragraph.
        if (foreignHanContext || SelectionOverridesPolicy(block, options, foreignHanContext)
            || BrowserParagraphTranslationArtifacts.HasIdentity(block) && !ProvesCompleteLegacyParent(block, all)) return [];
        var group = block.GroupId is null ? string.Empty : string.Concat(all.Where(item => item.GroupId == block.GroupId)
            .Select(item => item.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + item.Text));
        string Key(string context)
        {
            var values = new[] { "page-text-v4-scoped-facts", block.Text, options.SourceLanguage, options.TargetLanguage,
                options.Style.ToString(), options.Context, options.CacheScope, context, group };
            return BrowserParagraphTranslationArtifacts.Hash(string.Concat(values.Select(value =>
                value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value)));
        }
        var keys = new List<string> { Key(GroupContext(block, all)) };
        // 0.9.3 omitted the duplicated source context for a single whole node.
        // Restore that old shape only when the current context is demonstrably
        // source, never when the user supplied independent terminology.
        var members = block.GroupId is null ? 1 : all.Count(item => item.GroupId == block.GroupId);
        var provesWholeNode = !BrowserParagraphTranslationArtifacts.HasIdentity(block)
            || block.SourceStart == 0 && block.SourceLength == block.SourceGroupLength
                && BrowserParagraphTranslationArtifacts.Hash(block.Text).Equals(block.SourceGroupHash, StringComparison.OrdinalIgnoreCase);
        if (members == 1 && provesWholeNode && IdentityContext(block, all).Length == 0) keys.Add(Key(string.Empty));
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool ProvesCompleteLegacyParent(BrowserPageTextBlock block, IReadOnlyList<BrowserPageTextBlock> all)
    {
        var members = all.Where(member => member.GroupId == block.GroupId).OrderBy(member => member.SourceStart).ToArray();
        if (members.Any(member => member.Context is { } context && context.Length == block.SourceGroupLength
            && BrowserParagraphTranslationArtifacts.Hash(context).Equals(block.SourceGroupHash, StringComparison.OrdinalIgnoreCase))) return true;
        var source = new StringBuilder(); var cursor = 0;
        foreach (var member in members)
        {
            if (!BrowserParagraphTranslationArtifacts.HasIdentity(member) || member.SourceStart < cursor
                || !string.Equals(member.SourceGroupHash, block.SourceGroupHash, StringComparison.OrdinalIgnoreCase)) return false;
            var gap = member.SourceStart!.Value - cursor;
            if (gap > 1) return false;
            if (gap == 1) source.Append(' ');
            source.Append(member.Text); cursor = member.SourceStart.Value + member.Text.Length;
        }
        return cursor == block.SourceGroupLength
            && BrowserParagraphTranslationArtifacts.Hash(source.ToString()).Equals(block.SourceGroupHash, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NodeState(BrowserPageTextBlock block, string key, string groupContext, long cacheEpoch,
        bool eligible, string? artifactKey, IReadOnlyList<string> legacyKeys, bool foreignHanContext)
    {
        public BrowserPageTextBlock Block { get; } = block;
        public string CacheKey { get; } = key;
        public string GroupContext { get; } = groupContext;
        public long CacheEpoch { get; } = cacheEpoch;
        public bool Eligible { get; } = eligible;
        public string? ArtifactKey { get; } = artifactKey;
        public bool ArtifactHit { get; set; }
        public IReadOnlyList<string> LegacyKeys { get; } = legacyKeys;
        public bool LegacyHit { get; set; }
        public bool ForeignHanContext { get; } = foreignHanContext;
        public TextPart[] Parts { get; set; } = [];
        public string LastVisible { get; set; } = string.Empty;
        public string? FinalTranslation { get; set; }
        public string? VerifiedTranslation { get; set; }
        public bool SemanticFailed { get; set; }
        public bool SemanticVerified { get; set; }
        public HashSet<string> RejectedCandidates { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (BrowserPageTextBlock Source, string Answer)> TransferWitnesses { get; } = new(StringComparer.Ordinal);
        public bool CurrentGuardUnsafe { get; set; }
        public bool SemanticRetryActive { get; set; }
        public bool RetryRequired { get; set; }
        public bool StageInline { get; set; }
        public bool CacheHit { get; set; }
    }

    private sealed class TextPart(string id, NodeState node, int index, string text, string joiner)
    {
        public string Id { get; } = id;
        public NodeState Node { get; } = node;
        public int Index { get; } = index;
        public string Text { get; } = text;
        public string Joiner { get; } = joiner;
        public string Translation { get; set; } = string.Empty;
        public bool Complete { get; set; }
    }

    private sealed record SourcePiece(string Text, string Joiner);
    private sealed class InlineProgress(Action<TranslationStreamUpdate> callback) : IProgress<TranslationStreamUpdate>
    { public void Report(TranslationStreamUpdate value) => callback(value); }

    private sealed class UpdateDispatcher(
        Func<BrowserPageTranslationUpdate, CancellationToken, ValueTask> callback,
        CancellationToken token, Action<Exception> fail)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, BrowserPageTranslationUpdate> _latest = new(StringComparer.Ordinal);
        // At most one queued ID per original DOM node. Large cumulative strings are coalesced.
        private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public void Publish(BrowserPageTranslationUpdate update)
        {
            if (token.IsCancellationRequested) return;
            lock (_gate)
            {
                var queued = _latest.ContainsKey(update.Id);
                _latest[update.Id] = update;
                if (!queued) _ready.Writer.TryWrite(update.Id);
            }
        }
        public void Complete() => _ready.Writer.TryComplete();
        public async Task RunAsync()
        {
            try
            {
                await foreach (var id in _ready.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    BrowserPageTranslationUpdate update;
                    lock (_gate) { update = _latest[id]; _latest.Remove(id); }
                    token.ThrowIfCancellationRequested();
                    await callback(update, token).ConfigureAwait(false);
                }
            }
            catch (Exception error) { fail(error); throw; }
        }
    }
}
