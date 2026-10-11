using System.Windows.Media;
using Wpf.Ui.Appearance;
using MediaColor = System.Windows.Media.Color;

namespace ScreenTranslator.App.Services.Appearance;

public static class TranslationSurfacePalette
{
    public static SolidColorBrush CreateBrush(ApplicationTheme theme)
    {
        var color = theme == ApplicationTheme.Dark
            ? MediaColor.FromRgb(32, 32, 32)
            : MediaColor.FromRgb(250, 250, 250);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
