using System.Windows;
using System.Windows.Media;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.App.Services.Overlays;
using ScreenTranslator.App.ViewModels;
using ScreenTranslator.Core.Models;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using WpfRect = System.Windows.Rect;

namespace ScreenTranslator.App.Windows;

public partial class TextOverlayWindow : Window, ITrackedOverlay, IOverlayFocusTarget
{
    private readonly TranslationResultViewModel _viewModel;
    private readonly OverlayVisibilityState _visibility = new();
    private DipRect _trackingBounds;
    private bool _closed;

    public TextOverlayWindow()
        : this(new TranslationResultViewModel())
    {
    }

    public TextOverlayWindow(TranslationResultViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CopyRequested += ViewModel_OnCopyRequested;
        _viewModel.CloseRequested += ViewModel_OnCloseRequested;
    }

    public TranslationResultViewModel ViewModel => _viewModel;

    public DipRect TrackingBounds => _trackingBounds;

    public void SetBounds(WpfRect boundsDips)
    {
        Left = boundsDips.Left;
        Top = boundsDips.Top;
        Width = Math.Max(MinWidth, boundsDips.Width);
        Height = Math.Max(MinHeight, boundsDips.Height);
        _trackingBounds = new DipRect(
            boundsDips.Left,
            boundsDips.Top,
            boundsDips.Width,
            boundsDips.Height);
    }

    public void SetTextStyle(double fontSize, Brush? foreground = null, Brush? background = null)
    {
        TranslationText.FontSize = Math.Clamp(fontSize, 12, 32);
        if (foreground is not null)
        {
            TranslationText.Foreground = foreground;
        }

        if (background is not null)
        {
            RootCard.Background = background;
        }
    }

    public void SetInteractive(bool interactive)
    {
        ActionBar.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
        ApplyVisibility();
    }

    public void MoveTo(DipRect bounds)
    {
        _trackingBounds = bounds;
        Left = bounds.X;
        Top = bounds.Y;
    }

    public void SetUserVisibility(bool visible)
    {
        _visibility.UserVisible = visible;
        ApplyVisibility();
    }

    public void SetSourceWindowActive(bool active)
    {
        _visibility.SourceWindowActive = active;
        ApplyVisibility();
    }

    public void SetTrackingVisibility(bool visible)
    {
        _visibility.TrackingVisible = visible;
        ApplyVisibility();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _viewModel.CopyRequested -= ViewModel_OnCopyRequested;
        _viewModel.CloseRequested -= ViewModel_OnCloseRequested;
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

    private void OverlayContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        _visibility.ContextMenuOpen = true;
        ApplyVisibility();
    }

    private void OverlayContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
        _visibility.ContextMenuOpen = false;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (_closed)
        {
            return;
        }

        if (_visibility.ShouldShow)
        {
            RootCard.Visibility = Visibility.Visible;
            RootCard.IsHitTestVisible = ActionBar.Visibility == Visibility.Visible;
            if (IsLoaded && !IsVisible)
            {
                Show();
            }
        }
        else
        {
            RootCard.IsHitTestVisible = false;
            Hide();
        }
    }
}
