using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ScreenTranslator.Core.Models;
using Point = System.Windows.Point;

namespace ScreenTranslator.App.Services.Capture;

public sealed record AccessibleTextSelection(PixelRect Bounds, string Text, nint SourceWindowHandle);

public static class AccessibleTextResolver
{
    public static async Task<PixelRect?> ResolveDocumentBoundsAsync(PixelPoint point, nint window, CancellationToken token)
    {
        if (window == nint.Zero) return null;
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var documents = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    PixelRect? largest = null;
                    foreach (AutomationElement document in documents)
                    {
                        var current = document.Current;
                        if (current.IsOffscreen || current.BoundingRectangle.IsEmpty
                            || !current.BoundingRectangle.Contains(new Point(point.X, point.Y))) continue;
                        var bounds = ToPixelRect(current.BoundingRectangle);
                        if (bounds.IsUsable && (largest is null || (long)bounds.Width * bounds.Height > (long)largest.Value.Width * largest.Value.Height)) largest = bounds;
                    }
                    return largest;
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException) { return null; }
            }, token).WaitAsync(TimeSpan.FromMilliseconds(1200), token);
        }
        catch (TimeoutException) { return null; }
    }

    public static async Task<PixelRect?> ResolveImageBoundsAsync(PixelPoint point, nint window, CancellationToken token)
    {
        if (window == nint.Zero) return null;
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var hit = AutomationElement.FromPoint(new Point(point.X, point.Y));
                    for (var depth = 0; hit is not null && depth < 40; depth++)
                    {
                        if (hit.Current.ProcessId == Environment.ProcessId) break;
                        if (TryImage(hit, point, out var hitImage)) return hitImage;
                        if (hit.Current.NativeWindowHandle == window.ToInt64()) break;
                        hit = TreeWalker.RawViewWalker.GetParent(hit);
                    }
                    var legacyImage = FindLegacyImageAt(point, window);
                    if (legacyImage is not null) return legacyImage;
                    PixelRect? image = null;
                    var pending = new Stack<(AutomationElement Element, int Depth)>();
                    pending.Push((AutomationElement.FromHandle(window), 0));
                    for (var visited = 0; pending.Count > 0 && visited < 1500; visited++)
                    {
                        var (element, depth) = pending.Pop();
                        var current = element.Current;
                        if (current.IsPassword || current.ProcessId == Environment.ProcessId || current.IsOffscreen) continue;
                        var bounds = current.BoundingRectangle;
                        if (!bounds.IsEmpty && bounds.Width > 0 && !bounds.Contains(new Point(point.X, point.Y))) continue;
                        if (TryImage(element, point, out var candidate))
                        {
                            if (candidate.IsUsable && (image is null || (long)candidate.Width * candidate.Height < (long)image.Value.Width * image.Value.Height))
                                image = candidate;
                        }
                        if (depth >= 40) continue;
                        var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                        for (var count = 0; child is not null && count < 1500; count++)
                        {
                            pending.Push((child, depth + 1));
                            child = TreeWalker.RawViewWalker.GetNextSibling(child);
                        }
                    }
                    return image;
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or NotSupportedException)
                { return null; }
            }, token).WaitAsync(TimeSpan.FromMilliseconds(2500), token);
        }
        catch (TimeoutException) { return null; }
    }

    private static bool TryImage(AutomationElement element, PixelPoint point, out PixelRect bounds)
    {
        bounds = default;
        var current = element.Current;
        if (current.IsPassword || current.IsOffscreen || current.BoundingRectangle.IsEmpty
            || !current.BoundingRectangle.Contains(new Point(point.X, point.Y))) return false;
        var role = element.GetCurrentPropertyValue(AutomationElement.ItemTypeProperty)?.ToString();
        var image = current.ControlType == ControlType.Image || role is "img" or "image"
            || ((current.ControlType == ControlType.Custom || current.ControlType == ControlType.Pane)
                && (current.ClassName.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || current.ClassName.Contains("photo", StringComparison.OrdinalIgnoreCase)
                    || current.ClassName.Contains("canvas", StringComparison.OrdinalIgnoreCase))
                && !element.TryGetCurrentPattern(TextPattern.Pattern, out _));
        if (!image) return false;
        bounds = ToPixelRect(current.BoundingRectangle);
        return bounds.IsUsable;
    }

    private static PixelRect? FindLegacyImageAt(PixelPoint point, nint sourceWindow)
    {
        try
        {
            if (AccessibleObjectFromPoint(new NativePoint { X = point.X, Y = point.Y }, out var accessible, out var child) != 0 || accessible is null) return null;
            for (var depth = 0; depth < 24; depth++)
            {
                if (WindowFromAccessibleObject(accessible, out var handle) == 0 && handle != nint.Zero && GetAncestor(handle, 2) != sourceWindow) return null;
                if (Convert.ToInt32(accessible.get_accRole(child)) == 0x28) // ROLE_SYSTEM_GRAPHIC
                {
                    accessible.accLocation(out var x, out var y, out var width, out var height, child);
                    var image = new PixelRect(x, y, width, height);
                    if (image.IsUsable && point.X >= image.X && point.X < image.Right && point.Y >= image.Y && point.Y < image.Bottom) return image;
                }
                if (accessible.accParent is not Accessibility.IAccessible parent) break;
                accessible = parent; child = 0;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException or FormatException)
        { }
        return null;
    }

    public static async Task<AccessibleTextSelection?> ResolveAsync(PixelPoint point, CancellationToken token)
    {
        // UIA calls must run away from the WPF dispatcher: slow providers must not freeze the selector.
        try
        {
            return await Task.Run(() => Resolve(point), token)
                .WaitAsync(TimeSpan.FromMilliseconds(1500), token);
        }
        catch (TimeoutException) { return null; }
    }

    public static async Task<AccessibleTextSelection?> ResolveInWindowAsync(PixelPoint point, nint window, CancellationToken token,
        Action<string>? trace = null)
    {
        try
        {
            return await Task.Run(() => ResolveInWindow(point, window, trace), token)
                .WaitAsync(TimeSpan.FromMilliseconds(1500), token);
        }
        catch (TimeoutException) { return null; }
    }

    private static AccessibleTextSelection? Resolve(PixelPoint point) => ResolveInWindow(point, FindWindowAt(point));

    private static AccessibleTextSelection? ResolveInWindow(PixelPoint point, nint window, Action<string>? trace = null)
    {
        try
        {
            if (window == nint.Zero) return null;
            var pending = new Stack<(AutomationElement Element, int Depth)>();
            pending.Push((AutomationElement.FromHandle(window), 0));
            var visited = 0;
            while (pending.Count > 0 && visited++ < 180)
            {
                var (element, depth) = pending.Pop();
                var current = element.Current;
                trace?.Invoke($"depth={depth} type={current.ControlType.ProgrammaticName} offscreen={current.IsOffscreen} bounds={current.BoundingRectangle}");
                if (current.IsPassword || current.ProcessId == Environment.ProcessId) continue;
                var elementBounds = current.BoundingRectangle;
                if (current.IsOffscreen || (!elementBounds.IsEmpty && elementBounds.Width > 0 &&
                    !elementBounds.Contains(new Point(point.X, point.Y)))) continue;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject))
                {
                    var paragraph = ((TextPattern)patternObject).RangeFromPoint(new Point(point.X, point.Y));
                    paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                    var text = paragraph.GetText(5000).Trim();
                    var rectangles = paragraph.GetBoundingRectangles().Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0).ToArray();
                    trace?.Invoke($"textLength={text.Length} rectangles={string.Join(';', rectangles.Select(rect => rect.ToString()))}");
                    if (text.Length is > 0 and < 5000 && rectangles.Any(r =>
                        point.X >= r.Left - 12 && point.X <= r.Right + 12 &&
                        point.Y >= r.Top - 10 && point.Y <= r.Bottom + 10))
                    {
                        var bounds = rectangles.Select(ToPixelRect).Aggregate((a, b) => a.Union(b));
                        return new AccessibleTextSelection(Pad(bounds), text, window);
                    }
                }

                var type = current.ControlType;
                if ((type == ControlType.Text || type == ControlType.Button || type == ControlType.Hyperlink || type == ControlType.MenuItem)
                    && !string.IsNullOrWhiteSpace(current.Name) && current.Name.Length < 2000
                    && current.BoundingRectangle.Contains(new Point(point.X, point.Y)))
                    return new AccessibleTextSelection(Pad(ToPixelRect(current.BoundingRectangle)), current.Name.Trim(), window);

                if (depth >= 16) continue;
                var children = new List<AutomationElement>();
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                for (var count = 0; child is not null && count < 100; count++)
                {
                    children.Add(child);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
                for (var index = children.Count - 1; index >= 0; index--) pending.Push((children[index], depth + 1));
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or NotSupportedException)
        {
            trace?.Invoke($"exception={exception.GetType().Name}: {exception.Message}");
            // An app without a usable accessibility provider goes through the screenshot path.
        }
        return null;
    }

    private static PixelRect ToPixelRect(System.Windows.Rect rect) => new(
        (int)Math.Floor(rect.X), (int)Math.Floor(rect.Y),
        (int)Math.Ceiling(rect.Right) - (int)Math.Floor(rect.X),
        (int)Math.Ceiling(rect.Bottom) - (int)Math.Floor(rect.Y));

    private static PixelRect Pad(PixelRect rect) => new(rect.X - 6, rect.Y - 6, rect.Width + 12, rect.Height + 12);

    public static PixelRect? GetWindowBounds(nint window) => window != nint.Zero && GetWindowRect(window, out var bounds)
        ? new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top) : null;

    public static nint FindWindowAt(PixelPoint point)
    {
        // Native hit testing ignores invisible/transparent surfaces that can still appear in EnumWindows.
        var hit = GetAncestor(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), 2);
        if (hit != nint.Zero)
        {
            GetWindowThreadProcessId(hit, out var hitProcess);
            if (hitProcess != Environment.ProcessId) return hit;
        }
        // Enumerate in Z order, skipping our selection surfaces. No overlay is hidden and no mouse event
        // is delivered to the application underneath while its accessibility provider is queried.
        nint result = nint.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var rect) ||
                point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId) return true;
            result = window;
            return false;
        }, nint.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(NativePoint point,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("oleacc.dll")] private static extern int WindowFromAccessibleObject(Accessibility.IAccessible accessible, out nint window);

    private delegate bool EnumWindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
