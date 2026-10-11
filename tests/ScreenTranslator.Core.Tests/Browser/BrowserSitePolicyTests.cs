using System.Text.Json;
using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Tests.Browser;

public sealed class BrowserSitePolicyTests
{
    [Theory]
    [InlineData(" HTTPS://WWW.Example.com/path?private=ignored ", "www.example.com")]
    [InlineData("*.Example.com", "example.com")]
    [InlineData("例え.jp", "xn--r8jz45g.jp")]
    [InlineData("example.com.", "example.com")]
    [InlineData("localhost:8080", "localhost")]
    [InlineData("[::1]", "::1")]
    [InlineData("https://user:password@example.com", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://", null)]
    [InlineData("bad domain.com", null)]
    public void Normalization_Saves_Only_A_Valid_Host(string value, string? expected) => Assert.Equal(expected, BrowserSitePolicy.NormalizeDomain(value));

    [Fact]
    public void Most_Specific_Subdomain_Wins_And_An_Equal_Blacklist_Takes_Precedence()
    {
        var settings = new AppSettings { BrowserTranslationBlacklist = ["example.com"], BrowserTranslationWhitelist = ["news.example.com"] };
        Assert.Equal("block", BrowserSitePolicy.Resolve("shop.example.com", settings).Mode);
        Assert.Equal("allow", BrowserSitePolicy.Resolve("a.news.example.com", settings).Mode);
        Assert.Equal("default", BrowserSitePolicy.Resolve("notexample.com", settings).Mode);
        Assert.Equal("default", BrowserSitePolicy.Resolve("example.com.other.org", settings).Mode);
        Assert.Equal("block", BrowserSitePolicy.Resolve("news.example.com", settings with { BrowserTranslationBlacklist = ["example.com", "news.example.com"] }).Mode);
    }

    [Fact]
    public void Quick_Rule_Updates_Only_The_Current_Host_And_Default_Reveals_Inherited_Rules()
    {
        var initial = new AppSettings { BrowserTranslationBlacklist = ["example.com", "other.org"] };
        var allowed = BrowserSitePolicy.SetRule(initial, "news.example.com", "allow");
        Assert.Equal("allow", BrowserSitePolicy.Resolve("news.example.com", allowed).Mode);
        Assert.Equal(initial.BrowserTranslationBlacklist, allowed.BrowserTranslationBlacklist);
        var reset = BrowserSitePolicy.SetRule(allowed, "news.example.com", "default");
        Assert.Empty(reset.BrowserTranslationWhitelist);
        Assert.Equal(new BrowserSiteRule("block", "example.com"), BrowserSitePolicy.Resolve("news.example.com", reset));
    }

    [Fact]
    public void Settings_Migration_Deduplicates_Normalizes_And_Handles_Null_Lists()
    {
        var settings = new AppSettings { BrowserTranslationWhitelist = ["Example.COM", "https://example.com/secret?key=discard", "bad domain", "例え.jp"], BrowserTranslationBlacklist = null! }.Migrate(out _);
        Assert.Equal(["example.com", "xn--r8jz45g.jp"], settings.BrowserTranslationWhitelist);
        Assert.Empty(settings.BrowserTranslationBlacklist);
    }

    [Theory]
    [InlineData("{\"apiKey\":\"not-a-real-key\"}")]
    [InlineData("{\"concurrency\":11}")]
    [InlineData("{\"concurrency\":\"10\"}")]
    [InlineData("{\"autoPage\":1}")]
    [InlineData("{\"targetLanguage\":\"unknown\"}")]
    [InlineData("{\"siteMode\":\"invalid\"}")]
    [InlineData("{\"autoPage\":true,\"autoPage\":false}")]
    [InlineData("{}")]
    public void Quick_Patch_Rejects_Unexpected_Fields_And_Invalid_Values(string json)
    {
        using var document = JsonDocument.Parse(json); Assert.False(BrowserQuickSettingsPatch.TryRead(document.RootElement, out _));
    }

    [Fact]
    public void Quick_Patch_Changes_Only_Allowed_Preferences()
    {
        using var document = JsonDocument.Parse("{\"autoPage\":true,\"autoImages\":false,\"targetLanguage\":\"ja\",\"concurrency\":4,\"siteMode\":\"block\"}");
        Assert.True(BrowserQuickSettingsPatch.TryRead(document.RootElement, out var patch));
        var settings = new AppSettings { TranslationContext = "keep this context", BrowserTranslationWhitelist = ["example.com"], StartSilently = true };
        var updated = patch!.Apply(settings, "example.com");
        Assert.True(updated.BrowserAutoPageTranslationEnabled); Assert.False(updated.BrowserAutoImageTranslationEnabled);
        Assert.Equal("ja", updated.TargetLanguage); Assert.Equal(4, updated.BrowserPageTranslationConcurrency);
        Assert.Empty(updated.BrowserTranslationWhitelist); Assert.Equal(["example.com"], updated.BrowserTranslationBlacklist);
        Assert.Equal(settings.TranslationContext, updated.TranslationContext); Assert.True(updated.StartSilently);
    }
}
