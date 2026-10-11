using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

/// <summary>Late local positions may enrich a stream, but never hold up its translation.</summary>
public sealed class ParallelVisionLayout(IProgress<TranslationStreamUpdate> progress, CancellationToken token,
    Func<IReadOnlyList<OcrBlock>, IReadOnlyList<OcrBlock>, IReadOnlyList<ImageAtlasRegion>>? matcher = null,
    Func<IReadOnlyList<TranslatedBlock>, IReadOnlyList<OcrBlock>, IReadOnlyList<TranslatedBlock>>? placementRefiner = null) : IProgress<TranslationStreamUpdate>
{
    private readonly object _gate = new();
    private IReadOnlyList<OcrBlock> _lines = [];
    private TranslationStreamUpdate? _latest;
    private (string Id, string Text)[] _mappedSources = [];
    private IReadOnlyList<ImageAtlasRegion> _regions = [];

    public void Report(TranslationStreamUpdate update) => Publish(update);

    public void Publish(TranslationStreamUpdate update)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested) return;
            _latest = update;
            progress.Report(Map(update));
        }
    }

    public async Task LocateAsync(Func<CancellationToken, Task<IReadOnlyList<OcrBlock>>> locate)
    {
        IReadOnlyList<OcrBlock> lines;
        try { lines = await locate(token).ConfigureAwait(false); }
        catch (Exception) { return; } // Optional positions cannot fail a successful cloud translation.
        lock (_gate)
        {
            if (token.IsCancellationRequested) return;
            _lines = lines;
            _mappedSources = [];
            if (_latest is not null) progress.Report(Map(_latest) with { IsLayoutUpdate = true });
        }
    }

    public TranslationResult Complete(TranslationResult result)
    {
        lock (_gate)
        {
            _latest = new(string.Join("\n", result.Blocks.Select(block => block.Translation)))
                { ImageSegments = result.Blocks, StreamingTail = string.Empty };
            return new(Map(_latest).ImageSegments!);
        }
    }

    private TranslationStreamUpdate Map(TranslationStreamUpdate update)
    {
        if (update.ImageSegments is not { } segments || segments.Count == 0) return update;
        if (segments.Any(block => block.HasVisionPlacement))
        {
            if (placementRefiner is null) return update;
            try { return update with { ImageSegments = placementRefiner(segments, _lines) }; }
            catch (Exception) { return update; }
        }
        var sources = segments.Select(block => (block.Id, block.SourceText)).ToArray();
        if (!_mappedSources.SequenceEqual(sources))
        {
            var source = segments.Select((block, index) => new OcrBlock(block.Id, block.SourceText, 1, default, index)).ToArray();
            try { _regions = matcher is null ? WholeImageTextMatcher.Match(source, _lines, tolerant: true) : matcher(source, _lines); }
            catch (Exception) { _regions = WholeImageTextMatcher.Match(source, _lines, tolerant: true); }
            _mappedSources = sources;
        }
        return update with { ImageSegments = segments.Select((block, index) => block with
            { Bounds = _regions[index].SourceBounds, TextBounds = _regions[index].TextBounds, IsVertical = _regions[index].IsVertical }).ToArray() };
    }
}
