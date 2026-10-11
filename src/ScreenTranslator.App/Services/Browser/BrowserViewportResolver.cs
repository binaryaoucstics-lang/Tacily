using System.Runtime.InteropServices;
using System.Text;
using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.App.Services.Browser;

/// <summary>Chrome's render widget gives the physical page origin even when its UIA document tree is unavailable.</summary>
public static class BrowserViewportResolver
{
    public static async Task<PixelRect?> ResolveAsync(BrowserWindowSnapshot window, PixelPoint point,
        CssSize viewport, double devicePixelRatio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PixelRect? native = null;
        EnumChildWindows(window.Handle, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            var name = new StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND") return true;
            var bounds = AccessibleTextResolver.GetWindowBounds(child);
            if (bounds is { } candidate && IsViewport(candidate, window.Bounds, point, viewport, devicePixelRatio))
                if (native is null || Area(candidate) > Area(native.Value)) native = candidate;
            return true;
        }, 0);
        if (native is not null) return native;

        var accessible = await AccessibleTextResolver.ResolveDocumentBoundsAsync(point, window.Handle, token);
        return accessible is { } document && IsViewport(document, window.Bounds, point, viewport, devicePixelRatio)
            ? document : null;
    }

    public static bool IsViewport(PixelRect candidate, PixelRect window, PixelPoint point, CssSize viewport, double dpr)
        => candidate.IsUsable && candidate.Intersect(window) == candidate
           && point.X >= candidate.X && point.X < candidate.Right && point.Y >= candidate.Y && point.Y < candidate.Bottom
           && double.IsFinite(dpr) && dpr is >= .5 and <= 8
           && double.IsFinite(viewport.Width) && double.IsFinite(viewport.Height)
           && viewport.Width > 0 && viewport.Height > 0
           && Math.Abs(candidate.Width - viewport.Width * dpr) <= 32
           && Math.Abs(candidate.Height - viewport.Height * dpr) <= 32;

    // Chrome window API coordinates and Win32 bounds can use different DPI conventions.
    // Accept a corroborated physical or DIP rectangle; preserve global negative monitor coordinates.
    public static double WindowMatchError(BrowserWindowSnapshot expected, CssRect actual)
    {
        if (!new[] { actual.Left, actual.Top, actual.Width, actual.Height }.All(double.IsFinite)
            || actual.Width <= 0 || actual.Height <= 0) return double.PositiveInfinity;
        var scale = Math.Max(96, expected.Dpi) / 96d;
        double Error(double factor) => Math.Abs(expected.Bounds.X / factor - actual.Left)
            + Math.Abs(expected.Bounds.Y / factor - actual.Top)
            + Math.Abs(expected.Bounds.Width / factor - actual.Width)
            + Math.Abs(expected.Bounds.Height / factor - actual.Height);
        return Math.Min(Error(1), Error(scale));
    }

    private static long Area(PixelRect rectangle) => (long)rectangle.Width * rectangle.Height;
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maximum);
}
