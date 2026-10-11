namespace ScreenTranslator.App.Infrastructure;

public enum MaintenanceAction
{
    None,
    RegisterBrowserHost,
    UnregisterBrowserHost,
}

public static class MaintenanceCommand
{
    public const string RegisterArgument = "--register-browser-host";
    public const string UnregisterArgument = "--unregister-browser-host";

    public static MaintenanceAction Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument =>
                string.Equals(
                    argument,
                    RegisterArgument,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return MaintenanceAction.RegisterBrowserHost;
        }

        if (arguments.Any(argument =>
                string.Equals(
                    argument,
                    UnregisterArgument,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return MaintenanceAction.UnregisterBrowserHost;
        }

        return MaintenanceAction.None;
    }
}
