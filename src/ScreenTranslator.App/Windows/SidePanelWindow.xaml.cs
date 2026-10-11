using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Media.Animation;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Layout;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.App.Services.Capture;
using Clipboard = System.Windows.Clipboard;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ScreenTranslator.App.Windows;

public partial class SidePanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcHitTest = 0x0084;
    private const int WmExitSizeMove = 0x0232;
    private const int HtCaption = 2;
    private const int ResizeBorderPixels = 8;

    private readonly TranslationResultViewModel _viewModel;
    private readonly DispatcherTimer _placementTimer;
    private HwndSource? _source;
    private DipRect _workArea = new(0, 0, 1920, 1080);
    private bool _isApplyingBounds;
    private bool _motionBusy, _streamRevealed;
    private double _motionWidth;
    private PixelRect? _physicalWorkArea;
    private double _physicalScale = 1;
    private PixelRect? _imageAnchor;
    private SelectionOutlineWindow? _segmentOutline;
    private TranslationSegmentViewModel? _activeSegment;
    private readonly DispatcherTimer _highlightTimer;
    private bool _refreshingImageMapping;
    private DateTime _lastImageMappingRefresh;
    private InPlaceTranslationWindow? _inPlaceWindow;
    private Services.Overlays.BrowserInPlaceController? _browserInPlace;

    public SidePanelWindow()
        : this(new TranslationResultViewModel())
    {
    }

    public SidePanelWindow(TranslationResultViewModel viewModel)
    {
        InitializeComponent();
        Loaded += (_, _) => { Behaviors.TranslationMotion.Reveal(RootCard, .94, 150); UpdateBusyMotion(); };
        IsVisibleChanged += (_, _) => UpdateBusyMotion();
        SizeChanged += (_, _) => UpdateBusyMotion();
        SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
        Behaviors.TranslationMotion.Changed += AppMotionPreferenceChanged;
        Closed += (_, _) => { SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged; Behaviors.TranslationMotion.Changed -= AppMotionPreferenceChanged; MotionRibbonOffset.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null); };
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CopyRequested += ViewModel_OnCopyRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
        _placementTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _placementTimer.Tick += PlacementTimer_OnTick;
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _highlightTimer.Tick += (_, _) => { RefreshImageMapping(); PollSourcePointer(); UpdateSourceHighlight(); };
        SourceInitialized += Window_OnSourceInitialized;
        LocationChanged += Window_OnPlacementChanged;
        SizeChanged += Window_OnPlacementChanged;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
        _viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { SetActiveSegment(null); _inPlaceWindow?.Hide(); } };
        Loaded += (_, _) => ConfigureInPlace();
        ApplyExpansion();
    }

    public TranslationResultViewModel ViewModel => _viewModel;

    public event EventHandler<WindowPlacement>? PlacementChanged;

    private void Segment_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: TranslationSegmentViewModel segment }) SetActiveSegment(segment);
    }

    private void Segment_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs args) => SetActiveSegment(null);

    private void SourcePreview_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (_viewModel.SourceImage is not System.Windows.Media.Imaging.BitmapSource image) return;
        var scale = Math.Min(SourcePreviewImage.ActualWidth / image.PixelWidth, SourcePreviewImage.ActualHeight / image.PixelHeight);
        if (scale <= 0) return;
        var point = args.GetPosition(SourcePreviewImage);
        var x = (point.X - (SourcePreviewImage.ActualWidth - image.PixelWidth * scale) / 2) / scale;
        var y = (point.Y - (SourcePreviewImage.ActualHeight - image.PixelHeight * scale) / 2) / scale;
        SetActiveSegment(_viewModel.Segments.Where(segment => segment.Contains(x, y))
            .OrderBy(segment => (long)segment.Bounds.Width * segment.Bounds.Height).FirstOrDefault());
    }

    private void SetActiveSegment(TranslationSegmentViewModel? segment)
    {
        if (_viewModel.CanRenderInPlace)
        {
            _segmentOutline?.Hide(); PreviewHighlights.Children.Clear(); _highlightTimer.Stop(); return;
        }
        if (ReferenceEquals(_activeSegment, segment)) { UpdatePreviewHighlight(); return; }
        if (_activeSegment is not null) _activeSegment.IsActive = false;
        _activeSegment = segment;
        if (segment is null)
        {
            if (_viewModel.HasSegments && _viewModel.IsExpanded) _highlightTimer.Start();
            else _highlightTimer.Stop();
            _segmentOutline?.Hide();
            PreviewHighlights.Children.Clear();
            return;
        }
        segment.IsActive = true;
        UpdatePreviewHighlight();
        UpdateSourceHighlight();
        _highlightTimer.Start();
    }

    private void SourcePreview_OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePreviewHighlight();

    private void UpdatePreviewHighlight()
    {
        PreviewHighlights.Children.Clear();
        if (_activeSegment is not { } segment || !_viewModel.IsSourceVisible || !SourcePreviewImage.IsVisible) return;
        if (_viewModel.SourceImage is System.Windows.Media.Imaging.BitmapSource image)
        {
            var origin = SourcePreviewImage.TranslatePoint(new System.Windows.Point(0, 0), PreviewHighlights);
            foreach (var text in segment.TextBounds)
            {
                var bounds = ImageTextMapping.ToPreview(text, new PixelSize(image.PixelWidth, image.PixelHeight),
                    new DipSize(SourcePreviewImage.ActualWidth, SourcePreviewImage.ActualHeight));
                var outline = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(0, bounds.Width), Height = Math.Max(0, bounds.Height), StrokeThickness = 1.5,
                    Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(65, 135, 245)),
                    Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 65, 135, 245)),
                };
                System.Windows.Controls.Canvas.SetLeft(outline, origin.X + bounds.X);
                System.Windows.Controls.Canvas.SetTop(outline, origin.Y + bounds.Y);
                PreviewHighlights.Children.Add(outline);
            }
        }
    }

    private void PollSourcePointer()
    {
        if (IsMouseOver) return;
        if (!IsVisible || !_viewModel.IsExpanded || !_viewModel.HasSegments)
        { SetActiveSegment(null); return; }
        if (_viewModel.SourceWindowBounds is not { } original
            || AccessibleTextResolver.GetWindowBounds(_viewModel.SourceWindowHandle) is not { } current
            || original.Width != current.Width || original.Height != current.Height)
        { SetActiveSegment(null); return; }
        var point = MouseSelectionService.CursorPosition;
        if (AccessibleTextResolver.FindWindowAt(point) != _viewModel.SourceWindowHandle)
        { SetActiveSegment(null); return; }
        double x = point.X - _viewModel.SourceRegion.X - current.X + original.X;
        double y = point.Y - _viewModel.SourceRegion.Y - current.Y + original.Y;
        if (_viewModel.RefreshSourceImageMapping is not null)
        {
            var imagePoint = _viewModel.SourceImageMapping?.ToImage(point, new PixelPoint(current.X - original.X, current.Y - original.Y));
            if (imagePoint is null) { SetActiveSegment(null); return; }
            x = imagePoint.Value.X; y = imagePoint.Value.Y;
        }
        var segment = _viewModel.Segments.Where(item => item.Contains(x, y))
            .OrderBy(item => (long)item.Bounds.Width * item.Bounds.Height).FirstOrDefault();
        if (ReferenceEquals(segment, _activeSegment)) return;
        SetActiveSegment(segment);
        if (segment is not null && TranslationSegmentsList.ItemContainerGenerator.ContainerFromItem(segment) is FrameworkElement item)
            item.BringIntoView();
    }

    private void UpdateSourceHighlight()
    {
        if (_activeSegment is null) return;
        var handle = _viewModel.SourceWindowHandle;
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        var current = AccessibleTextResolver.GetWindowBounds(handle);
        if (!IsVisible || !_viewModel.IsExpanded || handle == nint.Zero || !IsWindowVisible(handle) || IsIconic(handle)
            || (foreground != handle && process != Environment.ProcessId)
            || current is null || _viewModel.SourceWindowBounds is not { } original
            || current.Value.Width != original.Width || current.Value.Height != original.Height)
        {
            _segmentOutline?.Hide();
            return;
        }
        var source = _activeSegment.TextBounds.Select(bounds => _viewModel.RefreshSourceImageMapping is null
            ? ImageTextMapping.ToScreen(bounds, _viewModel.SourceRegion, original, current.Value)
            : _viewModel.SourceImageMapping?.ToScreen(bounds, new PixelPoint(current.Value.X - original.X, current.Value.Y - original.Y)))
            .OfType<PixelRect>().Where(bounds => bounds.IsUsable).ToArray();
        if (source.Length == 0) { _segmentOutline?.Hide(); return; }
        _segmentOutline ??= new SelectionOutlineWindow();
        _segmentOutline.Update(source);
    }

    private async void RefreshImageMapping()
    {
        if (_viewModel.RefreshSourceImageMapping is not { } refresh || _refreshingImageMapping
            || !IsVisible || !_viewModel.IsExpanded || DateTime.UtcNow - _lastImageMappingRefresh < TimeSpan.FromMilliseconds(250)) return;
        _refreshingImageMapping = true;
        _lastImageMappingRefresh = DateTime.UtcNow;
        try { _viewModel.SourceImageMapping = await refresh(); }
        catch (Exception) { _viewModel.SourceImageMapping = null; }
        finally { _refreshingImageMapping = false; }
        if (!IsVisible) { _segmentOutline?.Hide(); return; }
        PollSourcePointer(); UpdateSourceHighlight();
    }

    public void PlaceNearPhysicalPoint(PixelPoint point, ScreenMonitor monitor)
    {
        _physicalWorkArea = monitor.WorkArea;
        _physicalScale = monitor.ScaleX;
        _workArea = new DipRect(monitor.WorkArea.X / monitor.ScaleX, monitor.WorkArea.Y / monitor.ScaleY,
            monitor.WorkArea.Width / monitor.ScaleX, monitor.WorkArea.Height / monitor.ScaleY);
        var width = (int)Math.Round(Width * monitor.ScaleX);
        var height = (int)Math.Round(Height * monitor.ScaleY);
        var x = Math.Clamp(point.X + 18, monitor.WorkArea.X, Math.Max(monitor.WorkArea.X, monitor.WorkArea.Right - width));
        var y = Math.Clamp(point.Y + 18, monitor.WorkArea.Y, Math.Max(monitor.WorkArea.Y, monitor.WorkArea.Bottom - height));
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero) SetWindowPos(handle, nint.Zero, x, y, width, height, 0x0010 | 0x0004);
    }

    public void PlaceBesideImage(PixelRect image, ScreenMonitor monitor)
    {
        _imageAnchor = image;
        _physicalWorkArea = monitor.WorkArea;
        _physicalScale = monitor.ScaleX;
        _workArea = new DipRect(monitor.WorkArea.X / monitor.ScaleX, monitor.WorkArea.Y / monitor.ScaleY,
            monitor.WorkArea.Width / monitor.ScaleX, monitor.WorkArea.Height / monitor.ScaleY);
        PositionBesideImage();
    }

    private void PositionBesideImage()
    {
        if (_imageAnchor is not { } image || _physicalWorkArea is not { } work) return;
        var size = new PixelSize((int)Math.Round((_viewModel.IsExpanded ? 448 : 320) * _physicalScale),
            (int)Math.Round((_viewModel.IsExpanded ? Math.Min(520, _workArea.Height * 0.75) : 88) * _physicalScale));
        var bounds = ImagePopupPlacement.Place(image, work, size, (int)Math.Round(260 * _physicalScale),
            (int)Math.Round((_viewModel.IsExpanded ? 200 : 64) * _physicalScale), (int)Math.Round(12 * _physicalScale));
        _isApplyingBounds = true;
        try
        {
            Width = bounds.Width / _physicalScale;
            Height = bounds.Height / _physicalScale;
            SetWindowPos(new WindowInteropHelper(this).Handle, nint.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0004);
        }
        finally { _isApplyingBounds = false; }
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TranslationResultViewModel.IsBusy)) {
            if (_viewModel.IsBusy) _streamRevealed = false;
            UpdateBusyMotion();
        }
        if (args.PropertyName is nameof(TranslationResultViewModel.SourceBitmap) or nameof(TranslationResultViewModel.IsInPlaceEnabled)) ConfigureInPlace();
        if (args.PropertyName is nameof(TranslationResultViewModel.IsExpanded) or nameof(TranslationResultViewModel.HasSegments)) SetActiveSegment(null);
        if (args.PropertyName is nameof(TranslationResultViewModel.IsExpanded) or nameof(TranslationResultViewModel.HasSegments))
        {
            if (!_viewModel.CanRenderInPlace && _viewModel.IsExpanded && _viewModel.HasSegments) _highlightTimer.Start();
            else _highlightTimer.Stop();
        }
        if (args.PropertyName == nameof(TranslationResultViewModel.IsExpanded)) {
            ApplyExpansion();
            if (_viewModel.IsExpanded && !_streamRevealed) { _streamRevealed = true; Behaviors.TranslationMotion.Reveal(ContentScrollViewer); }
        }
        if (args.PropertyName is nameof(TranslationResultViewModel.IsSourceVisible) or nameof(TranslationResultViewModel.SourceImage))
            Dispatcher.BeginInvoke(UpdatePreviewHighlight);
        if (args.PropertyName == nameof(TranslationResultViewModel.TranslatedText) && _viewModel.IsBusy)
            Dispatcher.BeginInvoke(() => ContentScrollViewer.ScrollToEnd());
    }

    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(UpdateBusyMotion);
    }
    private void AppMotionPreferenceChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() => {
        if (!Behaviors.TranslationMotion.Enabled) { RootCard.BeginAnimation(OpacityProperty, null); ContentScrollViewer.BeginAnimation(OpacityProperty, null); }
        UpdateBusyMotion();
    });

    private void UpdateBusyMotion()
    {
        if (!IsLoaded) return;
        var busy = IsVisible && _viewModel.IsBusy;
        var width = Math.Max(110, MotionTrack.ActualWidth);
        if (!busy || !Behaviors.TranslationMotion.Enabled) {
            MotionRibbonOffset.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            MotionRibbonOffset.X = Math.Max(0, (width - 110) / 2); _motionBusy = false; return;
        }
        if (_motionBusy && Math.Abs(width - _motionWidth) < 1) return;
        _motionWidth = width; _motionBusy = true;
        MotionRibbonOffset.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(-110, width,
            TimeSpan.FromMilliseconds(1900)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ConfigureInPlace()
    {
        if (!IsLoaded || !_viewModel.CanRenderInPlace || _viewModel.SourceWindowHandle == 0) return;
        _highlightTimer.Stop(); _segmentOutline?.Hide(); PreviewHighlights.Children.Clear();
        if(_viewModel.BrowserOverlay is not null)
        {
            _inPlaceWindow?.Close(); _inPlaceWindow = null;
            _browserInPlace??=new(_viewModel,Dispatcher);return;
        }
        _inPlaceWindow ??= new InPlaceTranslationWindow(_viewModel, () => IsVisible);
        if (!_viewModel.IsInPlaceEnabled) _inPlaceWindow.Hide();
    }

    private void ApplyExpansion()
    {
        ResizeMode = _viewModel.IsExpanded ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        Width = _viewModel.IsExpanded ? 448 : 320;
        Height = _viewModel.IsExpanded ? Math.Min(520, Math.Max(280, _workArea.Height * 0.75)) : 88;
        if (IsLoaded) { if (_imageAnchor is not null) PositionBesideImage(); else ClampAndPublishPlacement(); }
    }

    public void ApplyPlacement(DipRect bounds, DipRect workArea)
    {
        _workArea = workArea;
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.8);
        ApplyBounds(bounds);
        ApplyExpansion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _highlightTimer.Stop();
        _inPlaceWindow?.Close(); _inPlaceWindow = null;
        _browserInPlace?.Dispose();_browserInPlace=null;
        _segmentOutline?.Close();
        if (_placementTimer.IsEnabled)
        {
            ClampAndPublishPlacement();
        }

        _placementTimer.Stop();
        _placementTimer.Tick -= PlacementTimer_OnTick;
        LocationChanged -= Window_OnPlacementChanged;
        SizeChanged -= Window_OnPlacementChanged;
        SourceInitialized -= Window_OnSourceInitialized;
        _source?.RemoveHook(WindowProcedure);
        _viewModel.CopyRequested -= ViewModel_OnCopyRequested;
        _viewModel.CloseRequested -= ViewModel_OnCloseRequested;
        _viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        base.OnClosed(e);
    }

    private static void ViewModel_OnCopyRequested(object? sender, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
        }
    }

    private void ViewModel_OnCloseRequested(object? sender, EventArgs e) => Close();

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _ = ReleaseCapture();
        _imageAnchor = null; // An explicit user drag overrides automatic placement.
        _ = SendMessage(
            new WindowInteropHelper(this).Handle,
            WmNcLButtonDown,
            new IntPtr(HtCaption),
            IntPtr.Zero);
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WindowProcedure);

        var extendedStyle = GetWindowLongPtr(helper.Handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(
            helper.Handle,
            GwlExStyle,
            new IntPtr((extendedStyle & ~WsExToolWindow) | WsExNoActivate));
    }

    private void Window_OnPlacementChanged(object? sender, EventArgs e)
    {
        if (_isApplyingBounds)
        {
            return;
        }

        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void PlacementTimer_OnTick(object? sender, EventArgs e)
    {
        _placementTimer.Stop();
        ClampAndPublishPlacement();
    }

    private void ClampAndPublishPlacement()
    {
        _placementTimer.Stop();
        if (_imageAnchor is not null) { PositionBesideImage(); return; }
        if (_physicalWorkArea is { } work && GetWindowRect(new WindowInteropHelper(this).Handle, out var actual))
        {
            var width = Math.Min((int)Math.Round(Width * _physicalScale), work.Width);
            var height = Math.Min((int)Math.Round(Height * _physicalScale), work.Height);
            var x = Math.Clamp(actual.Left, work.X, Math.Max(work.X, work.Right - width));
            var y = Math.Clamp(actual.Top, work.Y, Math.Max(work.Y, work.Bottom - height));
            SetWindowPos(new WindowInteropHelper(this).Handle, nint.Zero, x, y, width, height, 0x0010 | 0x0004);
            return;
        }
        var candidate = new DipRect(
            Left,
            Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        var clamped = SidePanelBoundsService.Clamp(candidate, _workArea);
        ApplyBounds(clamped);
        PlacementChanged?.Invoke(
            this,
            new WindowPlacement(
                clamped.X,
                clamped.Y,
                clamped.Width,
                clamped.Height));
    }

    private void ApplyBounds(DipRect bounds)
    {
        _isApplyingBounds = true;
        try
        {
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
        }
        finally
        {
            _isApplyingBounds = false;
        }
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmExitSizeMove)
        {
            Dispatcher.BeginInvoke(ClampAndPublishPlacement);
            return IntPtr.Zero;
        }

        if (!_viewModel.IsExpanded || message != WmNcHitTest || !GetWindowRect(hwnd, out var rect))
        {
            return IntPtr.Zero;
        }

        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));
        var left = x >= rect.Left && x < rect.Left + ResizeBorderPixels;
        var right = x <= rect.Right && x > rect.Right - ResizeBorderPixels;
        var top = y >= rect.Top && y < rect.Top + ResizeBorderPixels;
        var bottom = y <= rect.Bottom && y > rect.Bottom - ResizeBorderPixels;
        var hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13,
            (_, true, true, _) => 14,
            (true, _, _, true) => 16,
            (_, true, _, true) => 17,
            (true, _, _, _) => 10,
            (_, true, _, _) => 11,
            (_, _, true, _) => 12,
            (_, _, _, true) => 15,
            _ => 0,
        };

        if (hit != 0)
        {
            handled = true;
            return new IntPtr(hit);
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : GetWindowLong32(windowHandle, index);

    private static IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, newValue)
            : SetWindowLong32(windowHandle, index, newValue);
}
