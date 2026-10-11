namespace ScreenTranslator.App.Services.Overlays;

public interface IOverlayFocusTarget
{
    void SetSourceWindowActive(bool active);
}

public sealed class OverlayFocusCoordinator
{
    private readonly Dictionary<IntPtr, List<IOverlayFocusTarget>> _groups = [];

    public OverlayFocusCoordinator(
        IntPtr sourceWindow,
        IEnumerable<IOverlayFocusTarget> targets)
    {
        if (sourceWindow == IntPtr.Zero)
        {
            throw new ArgumentException(
                "Source window is required.",
                nameof(sourceWindow));
        }

        ArgumentNullException.ThrowIfNull(targets);

        AddGroup(sourceWindow, targets);
    }

    public int Count => _groups.Values.Sum(targets => targets.Count);

    public void AddGroup(
        IntPtr sourceWindow,
        IEnumerable<IOverlayFocusTarget> targets)
    {
        if (sourceWindow == IntPtr.Zero)
        {
            throw new ArgumentException(
                "Source window is required.",
                nameof(sourceWindow));
        }

        ArgumentNullException.ThrowIfNull(targets);
        if (!_groups.TryGetValue(sourceWindow, out var group))
        {
            group = [];
            _groups.Add(sourceWindow, group);
        }

        foreach (var target in targets)
        {
            if (!group.Contains(target))
            {
                group.Add(target);
            }
        }
    }

    public bool Remove(IOverlayFocusTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in _groups.ToArray())
        {
            if (!entry.Value.Remove(target))
            {
                continue;
            }

            if (entry.Value.Count == 0)
            {
                _groups.Remove(entry.Key);
            }

            return true;
        }

        return false;
    }

    public void HandleForegroundChanged(IntPtr foregroundWindow)
    {
        foreach (var entry in _groups)
        {
            var active = foregroundWindow == entry.Key;
            foreach (var target in entry.Value.ToArray())
            {
                target.SetSourceWindowActive(active);
            }
        }
    }
}
