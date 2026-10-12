using System.Text.Json;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_Match_Product_Decisions()
    {
        var settings = new AppSettings();

        Assert.Equal("zh-CN", settings.TargetLanguage);
        Assert.Equal("deepseek-flash", settings.DeepSeekModel);
        Assert.Equal(ThinkingLevel.None, settings.ThinkingLevel);
        Assert.True(settings.BriefThinkingGuidanceEnabled);
        Assert.True(settings.BoundedLowThinkingEnabled);
        Assert.Equal(TranslationInputMode.VisionOnly, settings.InputMode);
        Assert.Equal(DisplayMode.SidePanel, settings.DisplayMode);
        Assert.False(settings.SaveHistory);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.StartSilently);
        Assert.Equal("Alt+Shift+T", settings.Hotkey);
        Assert.True(settings.HotkeyEnabled);
        Assert.False(settings.BrowserFollowingEnabled);
        Assert.False(settings.BrowserAutoImageTranslationEnabled);
        Assert.False(settings.BrowserAutoPageTranslationEnabled);
        Assert.Equal(BrowserPageJudgementMode.Rules, settings.BrowserPageJudgementMode);
        Assert.Equal(10, settings.BrowserPageTranslationConcurrency);
        Assert.Equal(50, settings.BrowserChineseProseShareThresholdPercent);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.ShowTrayIcon);
        Assert.Null(settings.SidePanelPlacement);
        Assert.DoesNotContain(
            settings.GetType().GetProperties(),
            property => property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Version_One_Settings_Use_Backward_Compatible_Defaults()
    {
        const string json = """
            {
              "Version": 1,
              "TargetLanguage": "ja",
              "Hotkey": "invalid"
            }
            """;

        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        var migrated = restored.Migrate(out var hotkeyWasReset);

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.Equal("ja", migrated.TargetLanguage);
        Assert.Equal("Alt+Shift+T", migrated.Hotkey);
        Assert.True(migrated.HotkeyEnabled);
        Assert.False(migrated.BrowserFollowingEnabled);
        Assert.False(migrated.BrowserAutoImageTranslationEnabled);
        Assert.False(migrated.BrowserAutoPageTranslationEnabled);
        Assert.Equal(BrowserPageJudgementMode.Rules, migrated.BrowserPageJudgementMode);
        Assert.Equal(10, migrated.BrowserPageTranslationConcurrency);
        Assert.Equal(50, migrated.BrowserChineseProseShareThresholdPercent);
        Assert.True(migrated.BriefThinkingGuidanceEnabled);
        Assert.True(migrated.BoundedLowThinkingEnabled);
        Assert.True(migrated.MinimizeToTray);
        Assert.False(migrated.StartSilently);
        Assert.True(migrated.ShowTrayIcon);
        Assert.Null(migrated.SidePanelPlacement);
        Assert.True(hotkeyWasReset);
    }

    [Fact]
    public void Settings_RoundTrip_With_SystemTextJson()
    {
        var settings = new AppSettings
        {
            TargetLanguage = "ja-JP",
            DeepSeekModel = "deepseek-v4-pro",
            DisplayMode = DisplayMode.Overlay,
            SaveHistory = true,
            BrowserAutoImageTranslationEnabled = true,
            BrowserAutoPageTranslationEnabled = true,
            BrowserPageJudgementMode = BrowserPageJudgementMode.Rules,
            BrowserPageTranslationConcurrency = 6,
            BrowserChineseProseShareThresholdPercent = 70,
            BriefThinkingGuidanceEnabled = false,
            BoundedLowThinkingEnabled = false,
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal(settings, restored);
    }

    [Fact]
    public void Migration_Preserves_Automatic_Browser_Translation_Preference()
    {
        var settings = new AppSettings
        {
            Version = 7,
            BrowserAutoImageTranslationEnabled = true,
        };

        var migrated = settings.Migrate(out _);

        Assert.True(migrated.BrowserAutoImageTranslationEnabled);
    }

    [Fact]
    public void Migration_Preserves_Explicit_Vision_And_Thinking_Preferences()
    {
        var settings = new AppSettings
        {
            Version = 7,
            BriefThinkingGuidanceEnabled = false,
            BoundedLowThinkingEnabled = false,
        };

        var migrated = settings.Migrate(out _);

        Assert.False(migrated.BriefThinkingGuidanceEnabled);
        Assert.False(migrated.BoundedLowThinkingEnabled);
    }

    [Theory]
    [InlineData(BrowserPageJudgementMode.Rules)]
    public void Page_Preferences_RoundTrip_And_Migrate_Without_Resetting_Valid_Choices(BrowserPageJudgementMode mode)
    {
        var settings = new AppSettings
        {
            Version = 7, BrowserAutoPageTranslationEnabled = true,
            BrowserPageJudgementMode = mode, BrowserPageTranslationConcurrency = 4,
            BrowserChineseProseShareThresholdPercent = 60,
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        var migrated = restored.Migrate(out _);

        Assert.True(migrated.BrowserAutoPageTranslationEnabled);
        Assert.Equal(mode, migrated.BrowserPageJudgementMode);
        Assert.Equal(4, migrated.BrowserPageTranslationConcurrency);
        Assert.Equal(60, migrated.BrowserChineseProseShareThresholdPercent);
        Assert.False(migrated.BrowserAutoImageTranslationEnabled);
    }

    [Theory]
    [InlineData(-12, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(6, 6)]
    [InlineData(10, 10)]
    [InlineData(99, 10)]
    public void Migration_Clamps_Page_Concurrency_And_Repairs_Unknown_Judgement_Mode(int concurrency, int expected)
    {
        var settings = new AppSettings
        {
            BrowserPageTranslationConcurrency = concurrency,
            BrowserPageJudgementMode = (BrowserPageJudgementMode)99,
        };

        var migrated = settings.Migrate(out _);

        Assert.Equal(expected, migrated.BrowserPageTranslationConcurrency);
        Assert.Equal(BrowserPageJudgementMode.Rules, migrated.BrowserPageJudgementMode);
    }

    [Theory]
    [InlineData(-10, 10)]
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(90, 90)]
    [InlineData(100, 90)]
    public void Migration_Clamps_Chinese_Prose_Share_Threshold(int threshold, int expected)
    {
        var migrated = new AppSettings { BrowserChineseProseShareThresholdPercent = threshold }.Migrate(out _);

        Assert.Equal(expected, migrated.BrowserChineseProseShareThresholdPercent);
    }
}
