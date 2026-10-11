using System.Text;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class OcrReadingOrder
{
    // Recognize crops independently for spatial identity, then align them to Ovis's full-page reading order.
    // Coordinates never come from generated Markdown (its image tags describe illustrations, not text).
    public static IReadOnlyList<OcrBlock> Align(string document, IReadOnlyList<OcrBlock> crops)
    {
        var text = Normalize(document);
        var occupied = new List<(int Start, int End)>();
        var positions = new Dictionary<string, int>();
        foreach (var crop in crops.OrderByDescending(crop => Normalize(crop.Text).Length))
        {
            var query = Normalize(crop.Text);
            if (query.Length == 0) continue;
            var position = Find(query, text, occupied);
            if (position < 0) continue;
            occupied.Add((position, position + query.Length));
            positions.Add(crop.Id, position);
        }
        // Only use model ordering when every region is aligned: partial matches must not move omitted paragraphs to the end.
        if (positions.Count != crops.Count) return crops.OrderBy(crop => crop.ReadingOrder).ToArray();
        return crops.OrderBy(crop => positions[crop.Id]).Select((crop, index) => crop with { ReadingOrder = index }).ToArray();
    }

    private static int Find(string query, string document, IReadOnlyList<(int Start, int End)> occupied)
    {
        for (var start = 0; start < document.Length;)
        {
            var found = document.IndexOf(query, start, StringComparison.Ordinal);
            if (found < 0) break;
            if (!occupied.Any(span => found < span.End && found + query.Length > span.Start)) return found;
            start = found + 1;
        }
        // OCR on a crop may differ slightly from full-page OCR. Require two long matching anchors in order.
        if (query.Length < 20) return -1;
        var anchorLength = Math.Min(16, query.Length / 3);
        var left = query[..anchorLength]; var right = query[^anchorLength..];
        var candidate = document.IndexOf(left, StringComparison.Ordinal);
        if (candidate < 0 || occupied.Any(span => candidate >= span.Start && candidate < span.End)) return -1;
        var end = document.IndexOf(right, candidate + anchorLength, StringComparison.Ordinal);
        return end >= 0 && end - candidate <= query.Length * 1.25 ? candidate : -1;
    }

    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit));
}
