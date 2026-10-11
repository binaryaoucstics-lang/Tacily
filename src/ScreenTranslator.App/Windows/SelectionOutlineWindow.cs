using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenTranslator.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ScreenTranslator.App.Windows;

/// <summary>Physical-pixel, click-through text outlines; no dim layer.</summary>
public sealed class SelectionOutlineWindow : Window
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private IReadOnlyList<PixelRect> _regions = [];
    private PixelRect _bounds;
    public SelectionOutlineWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = false;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new nint(style | 0x20 | 0x08000000));
        };
    }

    public void Update(PixelRect bounds) => Update([bounds]);

    public void Update(IReadOnlyList<PixelRect> bounds)
    {
        var usable = bounds.Where(region => region.IsUsable).ToArray();
        if (usable.Length == 0) { Hide(); return; }
        if (IsVisible && _regions.SequenceEqual(usable)) return;
        _regions = usable;
        _bounds = usable.Aggregate((left, right) => left.Union(right));
        if (!IsVisible) Show();
        var handle = new WindowInteropHelper(this).Handle;
        // Move first so GetDpiForWindow reflects the target monitor, then synchronize WPF layout.
        Position(handle);
        var scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        Width = _bounds.Width / scale; Height = _bounds.Height / scale;
        _canvas.Children.Clear();
        foreach (var region in usable)
        {
            var outline = new Rectangle
            {
                Width = region.Width / scale, Height = region.Height / scale,
                Stroke = new SolidColorBrush(Color.FromRgb(65, 135, 245)), StrokeThickness = Math.Min(2, Math.Min(region.Width, region.Height) / 2.0) / scale,
                Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(outline, (region.X - _bounds.X) / scale);
            Canvas.SetTop(outline, (region.Y - _bounds.Y) / scale);
            _canvas.Children.Add(outline);
        }
        UpdateLayout();
        Position(handle);
    }

    private void Position(nint handle) => SetWindowPos(handle, new nint(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0010);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
