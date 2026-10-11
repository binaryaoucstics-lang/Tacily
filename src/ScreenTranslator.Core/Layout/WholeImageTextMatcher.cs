using System.Text;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class WholeImageTextMatcher
{
    // Local recognition provides positions only. Translation source text and its order remain authoritative.
    public static IReadOnlyList<ImageAtlasRegion> Match(IReadOnlyList<OcrBlock> paragraphs, IReadOnlyList<OcrBlock> lines, bool tolerant = false, double minimumCoverage = .85)
    {
        if (tolerant) return MatchTolerant(paragraphs, lines, minimumCoverage);
        var normalized = paragraphs.Select(block => Normalize(block.Text)).ToArray();
        var result = new List<ImageAtlasRegion>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            var matches = new List<PixelRect>();
            var occupied = new List<(int Start, int End)>();
            var text = normalized[index];
            foreach (var line in lines.OrderByDescending(line => Normalize(line.Text).Length))
            {
                var query = Normalize(line.Text);
                if (query.Length < 2 || !line.BoundsInCapturePixels.IsUsable
                    || normalized.Count(value => value.Contains(query, StringComparison.Ordinal)) != 1) continue;
                // Identical words at distinct image positions cannot be assigned by encounter order,
                // especially while later VLM paragraphs have not arrived yet.
                if (lines.Any(other => Normalize(other.Text) == query && other.BoundsInCapturePixels.IsUsable
                    && !SamePosition(line.BoundsInCapturePixels, other.BoundsInCapturePixels))) continue;
                var start = text.IndexOf(query, StringComparison.Ordinal);
                if (start < 0 || text.IndexOf(query, start + 1, StringComparison.Ordinal) >= 0
                    || occupied.Any(span => start < span.End && start + query.Length > span.Start)) continue;
                occupied.Add((start, start + query.Length));
                matches.Add(line.BoundsInCapturePixels);
            }
            // Partial coincidence must never attach a whole paragraph to an unrelated text position.
            if (text.Length == 0 || occupied.Sum(span => span.End - span.Start) < text.Length * .85) matches.Clear();
            var bounds = matches.Count == 0 ? default : matches.Aggregate((a, b) => a.Union(b));
            result.Add(new ImageAtlasRegion(paragraphs[index].Id, bounds, default)
                { TextBounds = matches, IsVertical = matches.Count > 0 && matches.Count(rect => rect.Height >= rect.Width * 1.7) * 2 >= matches.Count });
        }
        return result;
    }

    private static IReadOnlyList<ImageAtlasRegion> MatchTolerant(IReadOnlyList<OcrBlock> paragraphs, IReadOnlyList<OcrBlock> lines, double minimumCoverage)
    {
        var sources = paragraphs.Select(block => Normalize(block.Text)).ToArray();
        var grouped = new List<List<OcrBlock>>();
        foreach (var line in lines.Where(line => line.BoundsInCapturePixels.IsUsable && Normalize(line.Text).Length >= 2))
        {
            var group = grouped.FirstOrDefault(group => SamePosition(group[0].BoundsInCapturePixels, line.BoundsInCapturePixels));
            if (group is null) grouped.Add([line]); else group.Add(line);
        }
        var assignments = new List<(int Paragraph, int Start, int Length, double Score, PixelRect Bounds)>();
        foreach (var group in grouped)
        {
            var candidates = new List<(int Paragraph, int Start, int Length, double Score, PixelRect Bounds)>();
            foreach (var line in group)
            {
                var query = Normalize(line.Text);
                if (lines.Any(other => Normalize(other.Text) == query && other.BoundsInCapturePixels.IsUsable
                    && !SamePosition(line.BoundsInCapturePixels, other.BoundsInCapturePixels))) continue;
                for (var index = 0; index < sources.Length; index++)
                {
                    var match = BestSpan(query, sources[index], line.Confidence >= .7);
                    if (match.Length == 0) continue;
                    var shortKana = sources[index].Length is >= 3 and <= 4 && query.Length == sources[index].Length
                        && line.Confidence >= .95 && query.Any(character => character is >= '\u3041' and <= '\u3096');
                    candidates.Add((index, match.Start, match.Length,
                        match.Score + (sources[index] == query ? .05 : shortKana ? .12 : 0), line.BoundsInCapturePixels));
                }
            }
            var best = candidates.OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.Length).FirstOrDefault();
            if (best.Length == 0) continue;
            var alternative = candidates.Where(candidate => candidate.Paragraph != best.Paragraph)
                .Select(candidate => candidate.Score).DefaultIfEmpty(0).Max();
            if (best.Score - alternative < .04) continue;
            assignments.Add(best);
        }
        var result = new List<ImageAtlasRegion>();
        for (var index = 0; index < sources.Length; index++)
        {
            var spans = new List<(int Start, int End)>();
            var bounds = new List<PixelRect>();
            var agreement = 0.0;
            foreach (var candidate in assignments.Where(candidate => candidate.Paragraph == index)
                .OrderByDescending(candidate => candidate.Score).ThenByDescending(candidate => candidate.Length))
            {
                if (spans.Any(span => candidate.Start < span.End && candidate.Start + candidate.Length > span.Start)) continue;
                spans.Add((candidate.Start, candidate.Start + candidate.Length));
                bounds.Add(candidate.Bounds);
                agreement += candidate.Length * Math.Min(1, candidate.Score);
            }
            var covered = spans.Sum(span => span.End - span.Start);
            if (sources[index].Length == 0 || covered < sources[index].Length * minimumCoverage || agreement < covered * .78) bounds.Clear();
            var distinct = bounds.Distinct().ToArray();
            result.Add(new(paragraphs[index].Id, distinct.Length == 0 ? default : distinct.Aggregate((a, b) => a.Union(b)), default)
                { TextBounds = distinct, IsVertical = distinct.Length > 0 && distinct.Count(rect => rect.Height >= rect.Width * 1.7) * 2 >= distinct.Length });
        }
        return result;
    }

    private static (int Start, int Length, double Score) BestSpan(string query, string source, bool allowErrors)
    {
        var exact = source.IndexOf(query, StringComparison.Ordinal);
        if (exact >= 0)
            return source.IndexOf(query, exact + 1, StringComparison.Ordinal) < 0 ? (exact, query.Length, 1) : default;
        if (!allowErrors || query.Length < 3) return default;
        var maxErrors = Math.Max(1, query.Length / 5);
        var best = (Start: 0, Length: 0, Score: 0.0);
        var tied = false;
        for (var start = 0; start < source.Length; start++)
            for (var length = Math.Max(2, query.Length - maxErrors); length <= query.Length + maxErrors && start + length <= source.Length; length++)
            {
                var distance = EditDistance(query.AsSpan(), source.AsSpan(start, length));
                if (distance > maxErrors) continue;
                var score = 1 - distance / (double)Math.Max(query.Length, length);
                if (score > best.Score + .001 || Math.Abs(score - best.Score) < .001 && start == best.Start && length > best.Length)
                { best = (start, length, score); tied = false; }
                else if (Math.Abs(score - best.Score) < .001 && Math.Abs(start - best.Start) >= query.Length) tied = true;
            }
        return tied ? default : best;
    }

    private static int EditDistance(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 0; i < left.Length; i++)
        {
            current[0] = i + 1;
            for (var j = 0; j < right.Length; j++) current[j + 1] = Math.Min(Math.Min(previous[j + 1] + 1, current[j] + 1),
                previous[j] + (left[i] == right[j] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(character => character is >= '\u30a1' and <= '\u30f6' or '\u30fd' or '\u30fe'
            ? (char)(character - 0x60) : char.ToUpperInvariant(character)));

    private static bool SamePosition(PixelRect left, PixelRect right)
    {
        var overlap = left.Intersect(right);
        return (long)overlap.Width * overlap.Height >= .8 * Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height);
    }
}
