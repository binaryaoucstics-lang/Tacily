using ScreenTranslator.Core.Browser;
using ScreenTranslator.Core.Settings;

namespace ScreenTranslator.Core.Tests.Browser;

public sealed class BrowserAutoConfigurationPolicyTests
{
    [Fact]
    public void PureMotionPreferenceCanBeBroadcastWithoutRetiringTranslationScope()
    {
        var before = new AppSettings { BrowserAutoPageTranslationEnabled = true };
        Assert.True(BrowserAutoConfigurationPolicy.OnlyAnimationPreferenceChanged(before, before with { TranslationAnimationsEnabled = false }));
        Assert.False(BrowserAutoConfigurationPolicy.OnlyAnimationPreferenceChanged(before, before));
        Assert.False(BrowserAutoConfigurationPolicy.OnlyAnimationPreferenceChanged(before, before with { TranslationAnimationsEnabled = false, TargetLanguage = "fr" }));
    }

    [Fact]
    public void MotionPreferenceSurvivesSaveReloadAndOlderSettingsDefaultToEnabled()
    {
        var saved = new AppSettings { TranslationAnimationsEnabled = false };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(saved))!.Migrate(out _);
        Assert.False(restored.TranslationAnimationsEnabled);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.Migrate(out _).TranslationAnimationsEnabled);
    }
    [Fact]
    public void SwitchingAutomaticAdmissionKeepsManualRequestIdentity()
    {
        var settings = new AppSettings { BrowserAutoPageTranslationEnabled = true, BrowserAutoImageTranslationEnabled = true };
        Assert.True(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with
        { BrowserAutoPageTranslationEnabled = false, BrowserAutoImageTranslationEnabled = false }));
        Assert.True(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with { }));
    }

    [Fact]
    public void ChangedInputsOrTransportStillInvalidateAcceptedIdentities()
    {
        var settings = new AppSettings();
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with { TargetLanguage = "fr" }));
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with { TranslationContext = "new terminology" }));
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with { OpenAiModel = "different-model" }));
        Assert.False(BrowserAutoConfigurationPolicy.PreserveManualGeneration(settings, settings with { BrowserPageCacheEnabled = false }));
    }
}
