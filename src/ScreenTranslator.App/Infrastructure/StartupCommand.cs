namespace ScreenTranslator.App.Infrastructure;

public static class StartupCommand
{
    public const string SilentArgument = "--startup-silent";

    public static bool IsSilentStartup(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, SilentArgument, StringComparison.OrdinalIgnoreCase));
    }
}
