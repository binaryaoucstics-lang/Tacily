using System.Text.Json;
using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Settings;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class TranslationThinkingCatalogTests
{
    public static TheoryData<string, ThinkingLevel[]> PublicModels => new()
    {
        { "gpt-6.1-sol", [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-6-astra", [ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-6-sol", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-6-luna", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-5.6-luna", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-5.6-terra", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-5.6-sol", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh, ThinkingLevel.Max] },
        { "gpt-5.5", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh] },
        { "gpt-5.4", [ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh] },
        { "gpt-4.1", [ThinkingLevel.None] },
        { "gpt-4.1-mini", [ThinkingLevel.None] },
        { "gpt-4.1-nano", [ThinkingLevel.None] },
        { "gpt-4o", [ThinkingLevel.None] },
        { "gpt-4o-mini", [ThinkingLevel.None] },
    };

    [Theory]
    [MemberData(nameof(PublicModels))]
    public void PublicApiLevelsMatchVerifiedModelsAndDatedSnapshots(string model, ThinkingLevel[] expected)
    {
        foreach (var id in new[] { model, model + "-2026-10-11", model + "-20261011" })
        {
            Assert.Equal(expected, TranslationThinkingCatalog.SupportedLevels(TranslationProviderKind.OpenAI, id));
            foreach (var requested in Enum.GetValues<ThinkingLevel>())
            {
                var config = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, id, requested);
                Assert.Contains(config.EffectiveLevel, expected);
                if (expected.Length == 1) Assert.Null(config.Effort);
                else Assert.Equal(ThinkingLevelNames.Effort(config.EffectiveLevel), config.Effort);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PublicModels))]
    public void OAuthFallbackUsesLowestLowForReasoningModels(string model, ThinkingLevel[] apiLevels)
    {
        var expected = apiLevels.Length == 1 ? apiLevels : apiLevels.Where(level => level != ThinkingLevel.None).ToArray();
        Assert.Equal(expected, TranslationThinkingCatalog.SupportedLevels(TranslationProviderKind.OpenAI, model, true));
    }

    [Theory]
    [InlineData("future-model")]
    [InlineData("gpt-6-sol-pro")]
    [InlineData("gpt-6-luna-custom")]
    [InlineData("gpt-5.6")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("gpt-4o-mini-20261399")]
    public void UnverifiedModelsHaveNoInventedCapabilities(string model)
    {
        Assert.Empty(TranslationThinkingCatalog.SupportedLevels(TranslationProviderKind.OpenAI, model));
        Assert.Empty(TranslationThinkingCatalog.SupportedLevels(TranslationProviderKind.OpenAI, model, true));
        var config = TranslationProviderConfiguration.Thinking(TranslationProviderKind.OpenAI, model, ThinkingLevel.High);
        Assert.Equal("default", config.Mode);
        Assert.Null(config.Effort);
        Assert.Equal(ThinkingLevel.High, config.EffectiveLevel);
    }

    [Fact]
    public void NormalizationUsesEffortOrderRatherThanEnumNumbersOrMetadataOrder()
    {
        ThinkingLevel[] levels = [ThinkingLevel.Max, ThinkingLevel.High, ThinkingLevel.Low, ThinkingLevel.Medium];
        Assert.Equal(ThinkingLevel.Low, TranslationThinkingCatalog.Normalize(ThinkingLevel.None, levels));
        Assert.Equal(ThinkingLevel.Low, TranslationThinkingCatalog.Normalize(ThinkingLevel.Minimal, levels));
        Assert.Equal(ThinkingLevel.High, TranslationThinkingCatalog.Normalize(ThinkingLevel.XHigh, levels));
        Assert.Equal(ThinkingLevel.Medium, TranslationThinkingCatalog.Normalize(ThinkingLevel.Medium, levels));
        Assert.Equal(ThinkingLevel.XHigh, TranslationThinkingCatalog.Normalize(ThinkingLevel.Max, [ThinkingLevel.Low, ThinkingLevel.XHigh]));
        Assert.Equal(ThinkingLevel.None, TranslationThinkingCatalog.Normalize(ThinkingLevel.High, []));
        Assert.Throws<TranslationConfigurationException>(() => TranslationThinkingCatalog.Normalize((ThinkingLevel)99, levels));
    }

    [Theory]
    [InlineData(TranslationProviderKind.DeepSeek)]
    [InlineData(TranslationProviderKind.Claude)]
    [InlineData(TranslationProviderKind.ChatCompletions)]
    public void NonOpenAiProvidersRetainTheirFourLevels(TranslationProviderKind provider) =>
        Assert.Equal([ThinkingLevel.None, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max],
            TranslationThinkingCatalog.SupportedLevels(provider, "any-model"));

    [Theory]
    [InlineData(0, ThinkingLevel.None)]
    [InlineData(1, ThinkingLevel.Low)]
    [InlineData(2, ThinkingLevel.High)]
    [InlineData(3, ThinkingLevel.Max)]
    [InlineData(4, ThinkingLevel.Medium)]
    [InlineData(5, ThinkingLevel.XHigh)]
    [InlineData(6, ThinkingLevel.Minimal)]
    public void PersistedNumericSettingsAndPresetsRemainCompatible(int number, ThinkingLevel level)
    {
        Assert.Equal(number, (int)level);
        var id = Guid.NewGuid().ToString("N");
        var settings = JsonSerializer.Deserialize<AppSettings>($$"""
            {"Version":{{AppSettings.CurrentVersion}},"ThinkingLevel":{{number}},"SelectedTranslationPresetId":"{{id}}",
             "TranslationProviderPresets":[{"Id":"{{id}}","ThinkingLevel":{{number}}}]}
            """)!.Migrate(out _);
        Assert.Equal(level, settings.ThinkingLevel);
        Assert.Equal(level, TranslationProviderConfiguration.ActivePreset(settings)!.ThinkingLevel);
        Assert.Equal(level, ThinkingLevelNames.FromLabel(ThinkingLevelNames.Label(level)));
        Assert.True(ThinkingLevelNames.TryParseEffort(ThinkingLevelNames.Effort(level), out var parsed));
        Assert.Equal(level, parsed);
    }
}
