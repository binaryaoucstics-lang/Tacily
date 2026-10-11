using System.Text.Json;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Settings;

public sealed class ChatGptFastModeSettingsTests
{
    private const string FirstPresetId = "11111111111111111111111111111111";
    private const string SecondPresetId = "22222222222222222222222222222222";

    [Fact]
    public void NewSettingsPresetsAndRequestOptionsDefaultToFastModeOff()
    {
        Assert.False(new AppSettings().ChatGptFastModeEnabled);
        Assert.False(new TranslationProviderPreset().ChatGptFastModeEnabled);
        Assert.False(new TranslationApiOptions
        {
            Provider = TranslationProviderKind.OpenAI,
            Model = TranslationProviderConfiguration.DefaultOpenAiModel,
            BaseUrl = TranslationProviderConfiguration.DefaultOpenAiBaseUrl,
            UseChatGptPlan = true,
        }.ChatGptFastModeEnabled);
        Assert.False(TranslationProviderConfiguration.ChatGptFastModeEnabled(OAuthSettings()));
    }

    [Fact]
    public void Version15JsonWithoutFastModeFieldsMigratesWithDefaultAndPresetOff()
    {
        const string json = """
            {
              "Version": 15,
              "TranslationProvider": 1,
              "OpenAiAuthenticationMode": 1,
              "SelectedTranslationPresetId": "11111111111111111111111111111111",
              "TranslationProviderPresets": [
                {
                  "Id": "11111111111111111111111111111111",
                  "Name": "Legacy OAuth",
                  "Provider": 1,
                  "AuthenticationMode": 1,
                  "Model": "gpt-6.1-sol"
                }
              ]
            }
            """;

        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(15, restored.Version);
        var migrated = restored.Migrate(out var hotkeyWasReset);

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.False(hotkeyWasReset);
        Assert.Equal(FirstPresetId, migrated.SelectedTranslationPresetId);
        Assert.True(TranslationProviderConfiguration.UsesChatGptPlan(migrated));
        Assert.False(migrated.ChatGptFastModeEnabled);
        Assert.False(Assert.Single(migrated.TranslationProviderPresets).ChatGptFastModeEnabled);
        Assert.False(TranslationProviderConfiguration.ChatGptFastModeEnabled(migrated));
        var defaultSettings = migrated with { SelectedTranslationPresetId = "" };
        Assert.True(TranslationProviderConfiguration.UsesChatGptPlan(defaultSettings));
        Assert.False(TranslationProviderConfiguration.ChatGptFastModeEnabled(defaultSettings));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JsonRoundTripAndMigrationPreserveIndependentDefaultAndPresetSwitches(bool globalFast, bool presetFast)
    {
        var first = OAuthPreset(FirstPresetId) with { ChatGptFastModeEnabled = presetFast };
        var second = OAuthPreset(SecondPresetId) with { ChatGptFastModeEnabled = !presetFast };
        var settings = OAuthSettings() with
        {
            ChatGptFastModeEnabled = globalFast,
            TranslationProviderPresets = [first, second],
            SelectedTranslationPresetId = first.Id,
        };

        var json = JsonSerializer.Serialize(settings);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(globalFast, document.RootElement.GetProperty("ChatGptFastModeEnabled").GetBoolean());
        var presetsJson = document.RootElement.GetProperty("TranslationProviderPresets");
        Assert.Equal(presetFast, presetsJson[0].GetProperty("ChatGptFastModeEnabled").GetBoolean());
        Assert.Equal(!presetFast, presetsJson[1].GetProperty("ChatGptFastModeEnabled").GetBoolean());

        var restored = JsonSerializer.Deserialize<AppSettings>(json)!.Migrate(out _);

        Assert.Equal(globalFast, restored.ChatGptFastModeEnabled);
        Assert.Equal(2, restored.TranslationProviderPresets.Count);
        Assert.Equal(first, restored.TranslationProviderPresets[0]);
        Assert.Equal(second, restored.TranslationProviderPresets[1]);
        Assert.Equal(first.Id, restored.SelectedTranslationPresetId);
        Assert.Equal(presetFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(restored));
        Assert.Equal(!presetFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(
            restored with { SelectedTranslationPresetId = second.Id }));
        Assert.Equal(globalFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(
            restored with { SelectedTranslationPresetId = "" }));
        Assert.Equal(globalFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(
            restored with { SelectedTranslationPresetId = "33333333333333333333333333333333" }));
    }

    public static IEnumerable<object[]> ProviderAndAuthenticationModes()
    {
        foreach (var provider in Enum.GetValues<TranslationProviderKind>())
        foreach (var authentication in Enum.GetValues<TranslationAuthenticationMode>())
        foreach (var usePreset in new[] { false, true })
            yield return [provider, authentication, usePreset];
    }

    [Theory]
    [MemberData(nameof(ProviderAndAuthenticationModes))]
    public void OnlyEffectiveOpenAiOAuthEnablesFastMode(
        TranslationProviderKind provider, TranslationAuthenticationMode authentication, bool usePreset)
    {
        var settings = OAuthSettings() with
        {
            TranslationProvider = provider,
            OpenAiAuthenticationMode = authentication,
            ChatGptFastModeEnabled = true,
        };
        if (usePreset)
        {
            var preset = OAuthPreset(FirstPresetId) with
            {
                Provider = provider,
                AuthenticationMode = authentication,
                ChatGptFastModeEnabled = true,
            };
            // Opposing global values ensure the selected preset supplies both decisions.
            settings = settings with
            {
                TranslationProvider = provider == TranslationProviderKind.OpenAI
                    ? TranslationProviderKind.Claude : TranslationProviderKind.OpenAI,
                OpenAiAuthenticationMode = authentication == TranslationAuthenticationMode.ChatGptOAuth
                    ? TranslationAuthenticationMode.ApiKey : TranslationAuthenticationMode.ChatGptOAuth,
                TranslationProviderPresets = [preset],
                SelectedTranslationPresetId = preset.Id,
            };
        }

        var expected = provider == TranslationProviderKind.OpenAI
            && authentication == TranslationAuthenticationMode.ChatGptOAuth;

        Assert.Equal(expected, TranslationProviderConfiguration.UsesChatGptPlan(settings));
        Assert.Equal(expected, TranslationProviderConfiguration.ChatGptFastModeEnabled(settings));
        Assert.Equal(expected, TranslationProviderConfiguration.ChatGptFastModeEnabled(settings.Migrate(out _)));
        var disabled = usePreset
            ? settings with { TranslationProviderPresets = [settings.TranslationProviderPresets[0] with { ChatGptFastModeEnabled = false }] }
            : settings with { ChatGptFastModeEnabled = false };
        Assert.False(TranslationProviderConfiguration.ChatGptFastModeEnabled(disabled));
        Assert.Equal(TranslationProviderConfiguration.TransportIdentity(disabled), TranslationProviderConfiguration.TransportIdentity(settings));
        var disabledConnection = TranslationProviderConfiguration.ConnectionIdentity(disabled);
        var enabledConnection = TranslationProviderConfiguration.ConnectionIdentity(settings);
        Assert.DoesNotContain("service_tier:", disabledConnection);
        Assert.DoesNotContain("service_tier:fast", enabledConnection);
        Assert.DoesNotContain("service_tier:", TranslationProviderConfiguration.TransportIdentity(settings));
        if (expected)
        {
            Assert.Contains("service_tier:priority", enabledConnection);
            Assert.Equal(disabledConnection + "\nservice_tier:priority", enabledConnection);
        }
        else
        {
            Assert.DoesNotContain("service_tier:", enabledConnection);
            Assert.Equal(disabledConnection, enabledConnection);
        }
        Assert.Equal(enabledConnection, TranslationProviderConfiguration.ConnectionIdentity(settings.Migrate(out _)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingGlobalSwitchWhilePresetIsSelectedPreservesEffectiveModeAndIdentities(bool presetFast)
    {
        var preset = OAuthPreset(FirstPresetId) with { ChatGptFastModeEnabled = presetFast };
        var original = OAuthSettings() with
        {
            TranslationProviderPresets = [preset],
            SelectedTranslationPresetId = preset.Id,
        };
        var changed = original with { ChatGptFastModeEnabled = true };

        Assert.Equal(presetFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(original));
        Assert.Equal(presetFast, TranslationProviderConfiguration.ChatGptFastModeEnabled(changed));
        Assert.Equal(TranslationProviderConfiguration.ConnectionIdentity(original), TranslationProviderConfiguration.ConnectionIdentity(changed));
        Assert.Equal(TranslationProviderConfiguration.TransportIdentity(original), TranslationProviderConfiguration.TransportIdentity(changed));
    }

    private static AppSettings OAuthSettings() => new()
    {
        TranslationProvider = TranslationProviderKind.OpenAI,
        OpenAiAuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth,
    };

    private static TranslationProviderPreset OAuthPreset(string id) => new()
    {
        Id = id,
        Name = "Synthetic OAuth preset",
        Provider = TranslationProviderKind.OpenAI,
        AuthenticationMode = TranslationAuthenticationMode.ChatGptOAuth,
        Model = TranslationProviderConfiguration.DefaultOpenAiModel,
    };
}
