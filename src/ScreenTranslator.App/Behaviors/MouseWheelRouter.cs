using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ComboBox = System.Windows.Controls.ComboBox;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Slider = System.Windows.Controls.Slider;

namespace ScreenTranslator.App.Behaviors;

public static class MouseWheelRouter
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(MouseWheelRouter),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static ScrollViewer? FindTarget(DependencyObject? origin, int delta)
    {
        if (origin is null || delta == 0 || IsDirectWheelConsumer(origin))
        {
            return null;
        }

        for (var current = origin; current is not null; current = GetParent(current))
        {
            if (current is ScrollViewer viewer &&
                viewer.ScrollableHeight > 0 &&
                CanScroll(viewer, delta))
            {
                return viewer;
            }
        }

        return null;
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            element.AddHandler(
                Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnPreviewMouseWheel),
                handledEventsToo: true);
        }
        else
        {
            element.RemoveHandler(
                Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnPreviewMouseWheel));
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled ||
            IsDirectWheelConsumer(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var target = FindTarget(e.OriginalSource as DependencyObject, e.Delta);
        if (target is null)
        {
            return;
        }

        target.ScrollToVerticalOffset(
            Math.Clamp(
                target.VerticalOffset - (e.Delta / 3.0),
                0,
                target.ScrollableHeight));
        e.Handled = true;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0
            ? viewer.VerticalOffset > 0
            : viewer.VerticalOffset < viewer.ScrollableHeight;

    private static bool IsDirectWheelConsumer(DependencyObject? origin)
    {
        if (origin is null)
        {
            return false;
        }

        for (var current = origin; current is not null; current = GetParent(current))
        {
            if (current is Slider or ScrollBar)
            {
                return true;
            }

            if (current is ComboBox)
            {
                return true;
            }

            if (current is ScrollViewer)
            {
                return false;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(child);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }

        if (child is ContentElement contentElement)
        {
            var contentParent = ContentOperations.GetParent(contentElement);
            if (contentParent is not null)
            {
                return contentParent;
            }

            if (contentElement is FrameworkContentElement frameworkContent)
            {
                return frameworkContent.Parent;
            }
        }

        return LogicalTreeHelper.GetParent(child);
    }
}
