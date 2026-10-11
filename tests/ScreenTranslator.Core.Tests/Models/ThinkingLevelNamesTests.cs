using ScreenTranslator.Core.Models;

namespace ScreenTranslator.Core.Tests.Models;

public sealed class ThinkingLevelNamesTests
{
    [Fact]
    public void Existing_Settings_Values_Are_Not_Renumbered()
    {
        Assert.Equal(0, (int)ThinkingLevel.None); Assert.Equal(1, (int)ThinkingLevel.Low);
        Assert.Equal(2, (int)ThinkingLevel.High); Assert.Equal(3, (int)ThinkingLevel.Max);
    }

    [Fact]
    public void Labels_And_Efforts_Roundtrip_All_Persisted_Levels()
    {
        foreach (var level in Enum.GetValues<ThinkingLevel>())
        {
            Assert.Equal(level, ThinkingLevelNames.FromLabel(ThinkingLevelNames.Label(level)));
            Assert.True(ThinkingLevelNames.TryParseEffort(ThinkingLevelNames.Effort(level), out var parsed));
            Assert.Equal(level, parsed);
        }
        Assert.False(ThinkingLevelNames.TryParseEffort("ultra", out _));
    }
}
