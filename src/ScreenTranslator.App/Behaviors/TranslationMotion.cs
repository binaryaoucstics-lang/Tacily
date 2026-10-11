using System.Windows;
using System.Windows.Media.Animation;

namespace ScreenTranslator.App.Behaviors;

/// <summary>Short presentation-only effects; never changes translation state or coordinates.</summary>
public static class TranslationMotion
{
    private static bool _appEnabled = true;
    public static event EventHandler? Changed;
    public static bool AppEnabled {
        get => _appEnabled;
        set { if (_appEnabled == value) return; _appEnabled = value; Changed?.Invoke(null, EventArgs.Empty); }
    }
    public static bool Enabled => AppEnabled && SystemParameters.ClientAreaAnimation;

    public static void Reveal(UIElement element, double from = .88, int milliseconds = 180)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, 1,
            TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
