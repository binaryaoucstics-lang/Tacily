using ScreenTranslator.App.Infrastructure;

namespace ScreenTranslator.IntegrationTests.Infrastructure;

public sealed class MaintenanceCommandTests
{
    [Theory]
    [InlineData(
        MaintenanceCommand.RegisterArgument,
        MaintenanceAction.RegisterBrowserHost)]
    [InlineData(
        MaintenanceCommand.UnregisterArgument,
        MaintenanceAction.UnregisterBrowserHost)]
    public void Parses_Maintenance_Arguments(
        string argument,
        MaintenanceAction expected)
    {
        Assert.Equal(
            expected,
            MaintenanceCommand.Parse([argument]));
    }

    [Fact]
    public void Ignores_Normal_Startup()
    {
        Assert.Equal(
            MaintenanceAction.None,
            MaintenanceCommand.Parse([]));
    }
}
