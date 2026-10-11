using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class TextRegionGrouper
{
    public static IReadOnlyList<TextRegion> Deduplicate(IEnumerable<TextRegion> regions)
    {
        var kept = new List<TextRegion>();
        foreach (var region in regions.Where(region => region.Bounds.IsUsable && double.IsFinite(region.Confidence))
                     .OrderByDescending(region => region.Confidence).ThenByDescending(region => Area(region.Bounds)))
        {
            // Containment alone is not duplication: small ruby text/characters may be real text.
            if (kept.Any(item =>
            {
                var overlap = Area(item.Bounds.Intersect(region.Bounds));
                var edgeDifference = Math.Max(Math.Max(Math.Abs(item.Bounds.X - region.Bounds.X), Math.Abs(item.Bounds.Y - region.Bounds.Y)),
                    Math.Max(Math.Abs(item.Bounds.Right - region.Bounds.Right), Math.Abs(item.Bounds.Bottom - region.Bounds.Bottom)));
                var thickness = Math.Min(Math.Min(item.Bounds.Width, item.Bounds.Height), Math.Min(region.Bounds.Width, region.Bounds.Height));
                return overlap / (double)(Area(item.Bounds) + Area(region.Bounds) - overlap) >= 0.65
                    && edgeDifference <= Math.Max(3, thickness * .3);
            })) continue;
            var merged = region;
            for (var index = kept.Count - 1; index >= 0; index--)
            {
                if (!SameTextLine(kept[index].Bounds, merged.Bounds)) continue;
                merged = new TextRegion(kept[index].Bounds.Union(merged.Bounds), Math.Max(kept[index].Confidence, merged.Confidence));
                kept.RemoveAt(index);
                index = kept.Count; // Restart: joining one fragment can connect another tile fragment.
            }
            kept.Add(merged);
        }
        return kept.OrderBy(region => region.Bounds.Y).ThenBy(region => region.Bounds.X).ToArray();
    }

    public static IReadOnlyList<PixelRect> Group(IEnumerable<TextRegion> regions, PixelSize image) =>
        GroupDetailed(regions, image).Select(group => group.CropBounds).ToArray();

    public static IReadOnlyList<TextRegionGroup> GroupDetailed(IEnumerable<TextRegion> regions, PixelSize image,
        TextReadingDirection direction = TextReadingDirection.Auto)
    {
        var limits = new PixelRect(0, 0, image.Width, image.Height);
        var boxes = Deduplicate(regions).Select(region => region.Bounds.Intersect(limits)).Where(rect => rect.IsUsable).ToArray();
        var vertical = boxes.Where(box => direction == TextReadingDirection.JapaneseVertical
            || direction == TextReadingDirection.Auto && IsVertical(box, boxes)).ToHashSet();
        var groups = new List<List<PixelRect>>();
        var orientations = new List<bool>();
        foreach (var box in boxes.OrderBy(box => box.Y).ThenByDescending(box => box.X))
        {
            var isVertical = vertical.Contains(box);
            var found = -1; var nearest = double.MaxValue;
            for (var index = 0; index < groups.Count; index++)
            {
                if (orientations[index] != isVertical) continue;
                foreach (var last in groups[index])
                {
                    var gap = box.Y - last.Bottom;
                    var overlap = Math.Min(box.Right, last.Right) - Math.Max(box.X, last.X);
                    var thickness = Math.Min(isVertical ? box.Width : box.Height, isVertical ? last.Width : last.Height);
                    var similar = Math.Max(isVertical ? box.Width : box.Height, isVertical ? last.Width : last.Height) <= thickness * 1.7;
                    var aligned = isVertical ? overlap >= Math.Min(box.Width, last.Width) * 0.65
                        : overlap >= Math.Min(box.Width, last.Width) * 0.55 || Math.Abs(box.X - last.X) <= Math.Max(10, last.Height * 0.4);
                    if (similar && aligned && gap >= -thickness * 0.3 && gap <= Math.Max(8, thickness * 0.75) && Math.Abs(gap) < nearest)
                    { found = index; nearest = Math.Abs(gap); }
                }
            }
            if (found < 0) { groups.Add([box]); orientations.Add(isVertical); }
            else groups[found].Add(box);
        }
        // Preserve neighboring vertical columns together in their original right-to-left layout.
        // Never move individual glyphs or rotate crops; punctuation and ruby placement must survive.
        for (var index = 0; index < groups.Count; index++)
        {
            if (!orientations[index]) continue;
            for (var other = index + 1; other < groups.Count; other++)
            {
                if (!orientations[other]) continue;
                if (!groups[index].Any(left => groups[other].Any(right => NeighborColumns(left, right)))) continue;
                groups[index].AddRange(groups[other]); groups.RemoveAt(other); orientations.RemoveAt(other);
                other = index;
            }
        }
        var result = groups.Select((group, index) =>
        {
            var bounds = group.Aggregate((left, right) => left.Union(right));
            return new TextRegionGroup(new PixelRect(bounds.X - 6, bounds.Y - 6, bounds.Width + 12, bounds.Height + 12).Intersect(limits),
                (orientations[index] ? group.OrderByDescending(box => box.X).ThenBy(box => box.Y)
                    : group.OrderBy(box => box.Y).ThenBy(box => box.X)).ToArray(), orientations[index]);
        }).ToList();
        // Separate paragraphs/bubbles form top-to-bottom rows; vertical rows read right-to-left.
        var ordered = new List<TextRegionGroup>();
        while (result.Count > 0)
        {
            var first = result.MinBy(group => group.CropBounds.Y)!;
            var bandHeight = Math.Min(80, Math.Max(16, first.TextBounds.Min(box => Math.Min(box.Width, box.Height)) * 2));
            var row = result.Where(group => group.CropBounds.Y <= first.CropBounds.Y + bandHeight).ToArray();
            ordered.AddRange(row.All(group => group.IsVertical)
                ? row.OrderByDescending(group => group.CropBounds.Right) : row.OrderBy(group => group.CropBounds.X));
            foreach (var group in row) result.Remove(group);
        }
        return ordered;
    }

    private static bool IsVertical(PixelRect box, IReadOnlyList<PixelRect> boxes)
    {
        if (box.Height >= box.Width * 1.7) return true;
        if (box.Width > box.Height * 1.4) return false;
        var aligned = boxes.Where(other => Math.Min(box.Right, other.Right) - Math.Max(box.X, other.X) >= Math.Min(box.Width, other.Width) * 0.65
            && Math.Abs(box.Width - other.Width) <= Math.Max(box.Width, other.Width) * 0.45
            && Math.Max(box.Y, other.Y) - Math.Min(box.Bottom, other.Bottom) <= Math.Max(box.Width, other.Width) * 2).ToArray();
        return aligned.Length >= 3 || boxes.Any(other => other.Height >= other.Width * 2 && NeighborColumns(box, other));
    }

    private static bool SameTextLine(PixelRect a, PixelRect b)
    {
        // A long full-image detection and its contained tile fragments have low IoU, but represent
        // the same column. Width/centre checks distinguish them from adjacent columns and ruby.
        return SameAxis(a.Width, b.Width, a.Center.X, b.Center.X,
                   Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X),
                   Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y),
                   a.Height >= a.Width * 1.7 || b.Height >= b.Width * 1.7)
               || SameAxis(a.Height, b.Height, a.Center.Y, b.Center.Y,
                   Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y),
                   Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X),
                   a.Width >= a.Height * 1.7 || b.Width >= b.Height * 1.7);

        static bool SameAxis(int thicknessA, int thicknessB, int centreA, int centreB, int crossOverlap, int lengthOverlap, bool elongated)
        {
            var smaller = Math.Min(thicknessA, thicknessB);
            var larger = Math.Max(thicknessA, thicknessB);
            return elongated && smaller >= larger * .65 && Math.Abs(centreA - centreB) <= larger * .25
                   && crossOverlap >= smaller * .75 && lengthOverlap >= Math.Max(2, smaller * .25);
        }
    }

    private static bool NeighborColumns(PixelRect left, PixelRect right)
    {
        var gap = Math.Max(left.X, right.X) - Math.Min(left.Right, right.Right);
        var overlap = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y);
        var distinctColumns = Math.Abs(left.Center.X - right.Center.X) >= Math.Min(left.Width, right.Width) * 0.4;
        return distinctColumns && gap >= -Math.Min(left.Width, right.Width) * 0.8 && gap <= Math.Max(10, Math.Min(left.Width, right.Width) * 0.9)
            && overlap >= Math.Min(left.Height, right.Height) * 0.6;
    }

    private static long Area(PixelRect bounds) => (long)Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
}
