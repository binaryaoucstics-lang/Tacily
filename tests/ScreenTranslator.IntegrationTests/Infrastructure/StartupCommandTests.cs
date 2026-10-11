using ScreenTranslator.App.Infrastructure;

namespace ScreenTranslator.IntegrationTests.Infrastructure;

public sealed class StartupCommandTests
{
    [Theory]
    [InlineData("--startup-silent")]
    [InlineData("--STARTUP-SILENT")]
    public void Recognizes_Silent_Startup_Argument(string argument)
    {
        Assert.True(StartupCommand.IsSilentStartup([argument]));
    }

    [Fact]
    public void Ignores_Normal_Startup_Arguments()
    {
        Assert.False(StartupCommand.IsSilentStartup([]));
        Assert.False(StartupCommand.IsSilentStartup(["--other"]));
    }
}
