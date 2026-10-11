using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Layout;

public static class ComicReadingOrder
{
    public static IReadOnlyList<TextRegionGroup> Sort(IReadOnlyList<TextRegionGroup> groups, IReadOnlyList<PixelRect> panels)
    {
        if (panels.Count < 2 || groups.Count(group => group.IsVertical) * 2 < groups.Count) return groups;
        var remaining = panels.ToList();
        var orderedPanels = new List<PixelRect>();
        while (remaining.Count > 0)
        {
            var first = remaining.MinBy(panel => panel.Y);
            var row = remaining.Where(panel => Math.Abs(panel.Y - first.Y) <= Math.Min(panel.Height, first.Height) * .25).ToArray();
            orderedPanels.AddRange(row.OrderByDescending(panel => panel.Right));
            foreach (var panel in row) remaining.Remove(panel);
        }
        var assigned = groups.Select((group, index) => (Group: group, Index: index, Panel: orderedPanels.FindIndex(panel =>
            Contains(panel, group.CropBounds.Center)))).ToArray();
        if (assigned.Any(item => item.Panel < 0)) return groups;
        // Reading each panel completely avoids taking a lower caption from the right panel after the left panel.
        return assigned.GroupBy(item => item.Panel).OrderBy(group => group.Key).SelectMany(panel =>
        {
            var vertical = panel.Where(item => item.Group.IsVertical).OrderByDescending(item => item.Group.CropBounds.Right).ThenBy(item => item.Group.CropBounds.Y);
            var horizontal = panel.Where(item => !item.Group.IsVertical).OrderBy(item => item.Group.CropBounds.Y).ThenBy(item => item.Group.CropBounds.X);
            return vertical.Concat(horizontal).Select(item => item.Group);
        }).ToArray();
    }

    private static bool Contains(PixelRect bounds, PixelPoint point) => point.X >= bounds.X && point.X < bounds.Right && point.Y >= bounds.Y && point.Y < bounds.Bottom;
}
