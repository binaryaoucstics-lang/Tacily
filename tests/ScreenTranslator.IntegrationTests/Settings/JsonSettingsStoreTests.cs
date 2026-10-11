using ScreenTranslator.App.Services.Settings;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.IntegrationTests.TestInfrastructure;

namespace ScreenTranslator.IntegrationTests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ScreenTranslator.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_Then_Load_RoundTrips_Settings()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonSettingsStore(path);
        var expected = new AppSettings
        {
            TargetLanguage = "ja-JP",
            DeepSeekModel = "deepseek-v4-pro",
            SaveHistory = true,
            BrowserAutoImageTranslationEnabled = true,
            BrowserAutoPageTranslationEnabled = true,
            BrowserPageJudgementMode = BrowserPageJudgementMode.Rules,
            BrowserPageTranslationConcurrency = 5,
            BriefThinkingGuidanceEnabled = false,
            BoundedLowThinkingEnabled = false,
        };

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Quick_Site_Rules_And_Preferences_Survive_A_Reload()
    {
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
        var expected = BrowserSitePolicy.SetRule(new AppSettings { BrowserAutoPageTranslationEnabled = true, BrowserPageTranslationConcurrency = 4 }, "news.example.com", "allow");
        expected = BrowserSitePolicy.SetRule(expected, "example.com", "block");
        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();
        Assert.Equal(expected.BrowserTranslationWhitelist, actual.BrowserTranslationWhitelist);
        Assert.Equal(expected.BrowserTranslationBlacklist, actual.BrowserTranslationBlacklist);
        Assert.Equal("allow", BrowserSitePolicy.Resolve("news.example.com", actual).Mode);
        Assert.Equal(4, actual.BrowserPageTranslationConcurrency);
    }

    [Fact]
    public async Task Load_Corrupt_File_Backs_It_Up_And_Returns_Defaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new JsonSettingsStore(path);

        var actual = await store.LoadAsync();

        Assert.Equal(new AppSettings(), actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_directory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Save_Does_Not_Deadlock_When_Shutdown_Blocks_The_Ui_Thread()
    {
        var path = Path.Combine(_directory, "shutdown-settings.json");
        var store = new JsonSettingsStore(path);

        NonPumpingContextTest.Run(
            () => store.SaveAsync(new AppSettings()).GetAwaiter().GetResult(),
            TimeSpan.FromSeconds(3));

        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
