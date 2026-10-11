using System.Xml.Linq;
using ScreenTranslator.App.Services.Appearance;
using Wpf.Ui.Appearance;

namespace ScreenTranslator.IntegrationTests.Windows;

public sealed class VisualStyleTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace WpfUi =
        "http://schemas.lepo.co/wpfui/2022/xaml";

    [Fact]
    public void Badges_Use_Compact_Rounded_Rectangle_Style()
    {
        var application = LoadXaml("src", "ScreenTranslator.App", "App.xaml");
        var badgeStyle = Assert.Single(
            application.Descendants(Presentation + "Style"),
            element =>
                (string?)element.Attribute(Xaml + "Key")
                == "CompactBadgeStyle");
        var cornerSetter = Assert.Single(
            badgeStyle.Elements(Presentation + "Setter"),
            element =>
                (string?)element.Attribute("Property")
                == "CornerRadius");

        Assert.Equal("5", (string?)cornerSetter.Attribute("Value"));
        AssertUsesCompactBadge(
            LoadXaml("src", "ScreenTranslator.App", "Pages", "GeneralPage.xaml"));
        AssertUsesCompactBadge(
            LoadXaml("src", "ScreenTranslator.App", "Pages", "AppearancePage.xaml"));
    }

    [Fact]
    public void Reported_Accent_Labels_Use_Theme_Appropriate_Foreground()
    {
        var generalPage = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Pages",
            "GeneralPage.xaml");
        var translationPage = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Pages",
            "TranslationPage.xaml");
        var appearancePage = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Pages",
            "AppearancePage.xaml");

        AssertAccentForeground(generalPage, "PrimaryCaptureButtonText");
        AssertAccentForeground(generalPage, "HotkeyBadgeText");
        AssertAccentForeground(translationPage, "SaveButtonText");
        AssertAccentForeground(appearancePage, "PreviewBadgeText");
    }

    [Fact]
    public void Saved_Api_Key_Mask_Uses_OneWay_Text_Binding()
    {
        var translationPage = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Pages",
            "TranslationPage.xaml");
        var textBox = Assert.Single(
            translationPage.Descendants(Presentation + "TextBox"),
            element =>
                ((string?)element.Attribute("Text"))
                    ?.Contains("SavedApiKeyMask", StringComparison.Ordinal)
                == true);

        Assert.Equal("True", (string?)textBox.Attribute("IsReadOnly"));
        Assert.Contains(
            "Mode=OneWay",
            (string?)textBox.Attribute("Text"));
    }

    [Fact]
    public void Side_Panel_Headers_Have_Full_Drag_Hit_Areas()
    {
        var sidePanel = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Windows",
            "SidePanelWindow.xaml");
        var continuousPanel = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Windows",
            "ContinuousSidePanelWindow.xaml");

        AssertDragHeader(FindNamedElement(sidePanel, "HeaderArea"));
        AssertDragHeader(FindNamedElement(continuousPanel, "HeaderArea"));

        var badge = FindNamedElement(sidePanel, "AiTranslationBadge");
        Assert.Equal("5", (string?)badge.Attribute("CornerRadius"));
    }

    [Fact]
    public void Startup_And_Tray_Options_Are_Bound_On_General_Page()
    {
        var generalPage = LoadXaml(
            "src",
            "ScreenTranslator.App",
            "Pages",
            "GeneralPage.xaml");

        AssertToggleBinding(generalPage, "StartWithWindows");
        AssertToggleBinding(generalPage, "StartSilently");
        AssertToggleBinding(generalPage, "ShowTrayIcon");
        Assert.Contains(
            generalPage.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "自启动时静默运行");
        Assert.Contains(
            generalPage.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "显示托盘图标");
    }

    [Fact]
    public void Translation_Surfaces_Are_Opaque_At_Full_Window_Opacity()
    {
        Assert.Equal(
            byte.MaxValue,
            TranslationSurfacePalette
                .CreateBrush(ApplicationTheme.Light)
                .Color
                .A);
        Assert.Equal(
            byte.MaxValue,
            TranslationSurfacePalette
                .CreateBrush(ApplicationTheme.Dark)
                .Color
                .A);

        AssertOpaqueTranslationSurface(
            LoadXaml(
                "src",
                "ScreenTranslator.App",
                "Windows",
                "SidePanelWindow.xaml"));
        AssertOpaqueTranslationSurface(
            LoadXaml(
                "src",
                "ScreenTranslator.App",
                "Windows",
                "TextOverlayWindow.xaml"));
    }

    private static void AssertUsesCompactBadge(XDocument document)
    {
        Assert.Contains(
            document.Descendants(Presentation + "Border"),
            element =>
                (string?)element.Attribute("Style")
                == "{StaticResource CompactBadgeStyle}");
    }

    private static void AssertOpaqueTranslationSurface(XDocument document)
    {
        var rootCard = Assert.Single(
            document.Descendants(Presentation + "Border"),
            element =>
                (string?)element.Attribute(Xaml + "Name")
                == "RootCard");

        Assert.Equal("1", (string?)rootCard.Attribute("Opacity"));
        Assert.Equal(
            "{DynamicResource TranslationSurfaceBrush}",
            (string?)rootCard.Attribute("Background"));
    }

    private static void AssertAccentForeground(
        XDocument document,
        string elementName)
    {
        var element = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            candidate =>
                (string?)candidate.Attribute(Xaml + "Name") == elementName);

        Assert.Equal(
            "{DynamicResource TextOnAccentFillColorPrimaryBrush}",
            (string?)element.Attribute("Foreground"));
    }

    private static void AssertToggleBinding(XDocument document, string propertyName)
    {
        Assert.Contains(
            document.Descendants(WpfUi + "ToggleSwitch"),
            element =>
                (string?)element.Attribute("IsChecked")
                == $"{{Binding {propertyName}}}");
    }

    private static void AssertDragHeader(XElement header)
    {
        Assert.Equal("Transparent", (string?)header.Attribute("Background"));
        Assert.Equal("SizeAll", (string?)header.Attribute("Cursor"));
        Assert.Equal(
            "Header_OnMouseLeftButtonDown",
            (string?)header.Attribute("MouseLeftButtonDown"));
    }

    private static XElement FindNamedElement(
        XDocument document,
        string elementName) =>
        Assert.Single(
            document.Descendants(),
            element =>
                (string?)element.Attribute(Xaml + "Name") == elementName);

    private static XDocument LoadXaml(params string[] pathParts)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
        return XDocument.Load(
            Path.Combine([repositoryRoot, .. pathParts]));
    }
}
