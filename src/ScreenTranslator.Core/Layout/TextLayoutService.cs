using System.Text.RegularExpressions;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public sealed partial class TextLayoutService
{
    public IReadOnlyList<OcrBlock> NormalizeAndOrder(
        IEnumerable<OcrBlock> blocks,
        PixelPoint captureOrigin = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var candidates = blocks
            .Select((block, index) => new Candidate(
                block with { Text = NormalizeWhitespace(block.Text) },
                index))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Block.Text))
            .OrderBy(candidate => CenterY(candidate.Block.BoundsInCapturePixels))
            .ThenBy(candidate => candidate.Block.BoundsInCapturePixels.X)
            .ThenBy(candidate => candidate.Block.ReadingOrder)
            .ThenBy(candidate => candidate.OriginalIndex)
            .ToArray();

        var lines = new List<Line>();
        foreach (var candidate in candidates)
        {
            var matchingLine = lines.FirstOrDefault(line => IsOnSameLine(line, candidate.Block));
            if (matchingLine is null)
            {
                lines.Add(new Line(candidate));
            }
            else
            {
                matchingLine.Add(candidate);
            }
        }

        var ordered = lines
            .OrderBy(line => line.CenterY)
            .ThenBy(line => line.Left)
            .SelectMany(line => line.Candidates
                .OrderBy(candidate => candidate.Block.BoundsInCapturePixels.X)
                .ThenBy(candidate => candidate.OriginalIndex))
            .Select((candidate, readingOrder) => candidate.Block with
            {
                BoundsInCapturePixels = candidate.Block.BoundsInCapturePixels.Translate(captureOrigin),
                ReadingOrder = readingOrder,
            })
            .ToArray();

        return ordered;
    }

    public IReadOnlyList<OcrBlock> Layout(
        IEnumerable<OcrBlock> blocks,
        PixelRect captureBoundsInVirtualDesktopPixels) =>
        NormalizeAndOrder(
            blocks,
            new PixelPoint(
                captureBoundsInVirtualDesktopPixels.X,
                captureBoundsInVirtualDesktopPixels.Y));

    public static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(text.Trim(), " ");
    }

    private static bool IsOnSameLine(Line line, OcrBlock block)
    {
        var blockBounds = block.BoundsInCapturePixels;
        var smallerHeight = Math.Min(line.MinimumHeight, blockBounds.Height);
        return smallerHeight > 0
            && Math.Abs(line.CenterY - CenterY(blockBounds)) < smallerHeight * 0.4;
    }

    private static double CenterY(PixelRect bounds) => bounds.Y + (bounds.Height / 2d);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record Candidate(OcrBlock Block, int OriginalIndex);

    private sealed class Line
    {
        private double _centerTotal;

        public Line(Candidate candidate)
        {
            Candidates.Add(candidate);
            _centerTotal = CenterY(candidate.Block.BoundsInCapturePixels);
            MinimumHeight = candidate.Block.BoundsInCapturePixels.Height;
            Left = candidate.Block.BoundsInCapturePixels.X;
        }

        public List<Candidate> Candidates { get; } = [];

        public double CenterY => _centerTotal / Candidates.Count;

        public int MinimumHeight { get; private set; }

        public int Left { get; private set; }

        public void Add(Candidate candidate)
        {
            Candidates.Add(candidate);
            _centerTotal += TextLayoutService.CenterY(candidate.Block.BoundsInCapturePixels);
            MinimumHeight = Math.Min(MinimumHeight, candidate.Block.BoundsInCapturePixels.Height);
            Left = Math.Min(Left, candidate.Block.BoundsInCapturePixels.X);
        }
    }
}
