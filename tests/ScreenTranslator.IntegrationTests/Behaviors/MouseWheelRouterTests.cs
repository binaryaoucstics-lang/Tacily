using System.Windows;
using System.Windows.Controls;
using ScreenTranslator.App.Behaviors;
using ScreenTranslator.IntegrationTests.TestInfrastructure;
using Size = System.Windows.Size;

namespace ScreenTranslator.IntegrationTests.Behaviors;

public sealed class MouseWheelRouterTests
{
    [Fact]
    public Task FindTarget_Returns_Inner_Viewer_Until_Boundary() =>
        StaTest.RunAsync(() =>
        {
            var outerPanel = new StackPanel();
            var outer = new ScrollViewer { Height = 100, Content = outerPanel };
            var inner = new ScrollViewer
            {
                Height = 50,
                Content = new Border { Height = 400 },
            };
            outerPanel.Children.Add(inner);
            outerPanel.Children.Add(new Border { Height = 400 });
            MeasureAndArrange(outer, new Size(200, 100));

            Assert.Same(inner, MouseWheelRouter.FindTarget(inner, delta: -120));

            inner.ScrollToEnd();
            inner.UpdateLayout();

            Assert.Same(outer, MouseWheelRouter.FindTarget(inner, delta: -120));
        });

    [Fact]
    public Task FindTarget_Returns_Null_When_Content_Does_Not_Overflow() =>
        StaTest.RunAsync(() =>
        {
            var viewer = new ScrollViewer
            {
                Height = 100,
                Content = new Border { Height = 50 },
            };
            MeasureAndArrange(viewer, new Size(200, 100));

            Assert.Null(MouseWheelRouter.FindTarget(viewer, delta: -120));
        });

    [Fact]
    public Task FindTarget_Does_Not_Steal_Wheel_From_Open_ComboBox() =>
        StaTest.RunAsync(() =>
        {
            var comboBox = new ComboBox
            {
                IsDropDownOpen = true,
                ItemsSource = new[] { "A", "B", "C" },
            };
            var panel = new StackPanel();
            panel.Children.Add(comboBox);
            panel.Children.Add(new Border { Height = 400 });
            var viewer = new ScrollViewer { Height = 100, Content = panel };
            MeasureAndArrange(viewer, new Size(200, 100));

            Assert.Null(MouseWheelRouter.FindTarget(comboBox, delta: -120));
        });

    [Fact]
    public Task FindTarget_Does_Not_Steal_Wheel_From_Slider() =>
        StaTest.RunAsync(() =>
        {
            var slider = new Slider();
            var panel = new StackPanel();
            panel.Children.Add(slider);
            panel.Children.Add(new Border { Height = 400 });
            var viewer = new ScrollViewer { Height = 100, Content = panel };
            MeasureAndArrange(viewer, new Size(200, 100));

            Assert.Null(MouseWheelRouter.FindTarget(slider, delta: -120));
        });

    private static void MeasureAndArrange(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(new Point(0, 0), size));
        element.UpdateLayout();
    }
}
